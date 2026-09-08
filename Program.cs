using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Betalgo.Ranul.OpenAI.ObjectModels.RequestModels;
using Microsoft.AI.Foundry.Local;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.Configure<HueOptions>(builder.Configuration.GetSection("Hue"));
builder.Services.AddSingleton<HueService>();
builder.Services.AddSingleton<HueMcpTools>();
builder.Services.AddSingleton<HueChatToolService>();
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithTools<HueMcpTools>();
builder.Services.AddSingleton<FoundryChatService>();
builder.Services.AddHostedService<FoundryModelWarmupService>();
builder.Services.AddSingleton<SpeechSessionStore>();
builder.Services.AddSingleton<SpeechChatService>();

var app = builder.Build();

await FoundryLocalManager.CreateAsync(
    new Configuration { AppName = "DaveBot" },
    app.Logger);

var discoveredEps = FoundryLocalManager.Instance.DiscoverEps();
Console.WriteLine($"Foundry Local discovered {discoveredEps.Length} execution provider(s):");
foreach (var ep in discoveredEps)
{
    Console.WriteLine($"  {ep.Name} (registered: {ep.IsRegistered})");
}

// Registering more than one GPU execution provider at once causes model load failures for
// "generic-gpu" variants ("Running a model with multiple providers is not supported"), so only
// register the single best available one instead of all discovered EPs.
string[] epPreferenceOrder = ["CUDAExecutionProvider", "NvTensorRTRTXExecutionProvider", "OpenVINOExecutionProvider", "WebGpuExecutionProvider"];
var bestEpName = epPreferenceOrder.FirstOrDefault(name =>
    discoveredEps.Any(ep => string.Equals(ep.Name, name, StringComparison.OrdinalIgnoreCase)));

if (bestEpName is not null)
{
    var epDownloadResult = await FoundryLocalManager.Instance.DownloadAndRegisterEpsAsync(
        [bestEpName],
        CreateEpProgressLogger());
    Console.WriteLine($"Execution provider registration result for '{bestEpName}': Success={epDownloadResult.Success}, Status={epDownloadResult.Status}");
}
else
{
    Console.WriteLine("No known GPU execution provider was discovered; models will run on CPU.");
}

var startupCatalog = await FoundryLocalManager.Instance.GetCatalogAsync();
var startupModels = await startupCatalog.ListModelsAsync();
Console.WriteLine($"Foundry Local catalog '{startupCatalog.Name}' has {startupModels.Count} model(s) available:");
foreach (var startupModel in startupModels)
{
    Console.WriteLine($"  {startupModel.Alias} (id: {startupModel.Id})");
    foreach (var variant in startupModel.Variants)
    {
        Console.WriteLine($"    variant: {variant.Alias} (id: {variant.Id})");
    }
}

if (startupModels.Count > 0)
{
    var infoType = startupModels[0].Info?.GetType();
    if (infoType is not null)
    {
        Console.WriteLine($"Sample ModelInfo properties (from '{startupModels[0].Alias}'):");
        foreach (var property in infoType.GetProperties())
        {
            object? value;
            try
            {
                value = property.GetValue(startupModels[0].Info);
            }
            catch (Exception ex)
            {
                value = $"<error: {ex.Message}>";
            }

            Console.WriteLine($"  {property.Name} = {value}");
        }
    }
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.Lifetime.ApplicationStopping.Register(() =>
{
    if (FoundryLocalManager.IsInitialized)
    {
        FoundryLocalManager.Instance.Dispose();
    }
});

app.UseHttpsRedirection();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapMcp("/mcp/hue");

app.MapGet("/api/models", () =>
{
    return Results.Ok(new ModelCapabilitiesResponse(
        FoundryChatService.SupportedChatModels,
        "whisper-small",
        "piper",
        "Chat and STT endpoints are available now. TTS endpoint is planned for a later update."));
});

app.MapPost("/api/hue/appkey", async (HueAppKeyRequest? request, HueService hueService, ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
{
    var logger = loggerFactory.CreateLogger("HueApi");
    var deviceType = request?.DeviceType;

    logger.LogInformation("Creating Hue application key. DeviceType={DeviceType}", deviceType ?? "davebot#local");

    try
    {
        var key = await hueService.CreateApplicationKeyAsync(deviceType, cancellationToken);
        return Results.Ok(key);
    }
    catch (HueApiException ex)
    {
        logger.LogWarning(ex, "Hue app key creation failed.");
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapGet("/api/hue/lights", async (HueService hueService, ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
{
    var logger = loggerFactory.CreateLogger("HueApi");

    try
    {
        var lights = await hueService.GetLightsAsync(cancellationToken);
        return Results.Ok(lights);
    }
    catch (HueApiException ex)
    {
        logger.LogWarning(ex, "Hue lights query failed.");
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPut("/api/hue/lights/{lightId}", async (string lightId, HueLightStateRequest request, HueService hueService, ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
{
    var logger = loggerFactory.CreateLogger("HueApi");

    try
    {
        var light = await hueService.UpdateLightStateAsync(lightId, request, cancellationToken);
        return Results.Ok(light);
    }
    catch (HueApiException ex)
    {
        logger.LogWarning(ex, "Hue light update failed for {LightId}", lightId);
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/chat", async (ChatRequest request, FoundryChatService chatService, ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
{
    var logger = loggerFactory.CreateLogger("ChatApi");
    var requestId = Guid.NewGuid().ToString("N");
    var stopwatch = Stopwatch.StartNew();

    logger.LogInformation("[{RequestId}] Incoming chat request. Model={Model}, PromptLength={PromptLength}",
        requestId,
        request.Model ?? "(default)",
        request.Message?.Length ?? 0);

    if (string.IsNullOrWhiteSpace(request.Message))
    {
        logger.LogWarning("[{RequestId}] Chat request rejected: empty message.", requestId);
        return Results.BadRequest(new { error = "Message is required.", requestId });
    }

    try
    {
        var completion = await chatService.CompleteAsync(request, cancellationToken);
        stopwatch.Stop();

        logger.LogInformation("[{RequestId}] Chat request completed in {ElapsedMs}ms. ResponseLength={ResponseLength}",
            requestId,
            stopwatch.ElapsedMilliseconds,
            completion.Message.Length);

        return Results.Ok(completion);
    }
    catch (Exception ex)
    {
        stopwatch.Stop();
        logger.LogError(ex, "[{RequestId}] Chat request failed after {ElapsedMs}ms.", requestId, stopwatch.ElapsedMilliseconds);
        return Results.Problem(ex.Message, extensions: new Dictionary<string, object?> { ["requestId"] = requestId });
    }
});

app.MapPost("/api/speech/sessions", (SpeechSessionStartRequest? request, SpeechChatService speechChatService, ILoggerFactory loggerFactory) =>
{
    var logger = loggerFactory.CreateLogger("SpeechApi");
    var sampleRate = request?.SampleRate is > 0 ? request.SampleRate.Value : 16000;
    var channels = request?.Channels is > 0 ? request.Channels.Value : 1;

    var session = speechChatService.CreateSession(sampleRate, channels);
    logger.LogInformation("Created speech session {SessionId}. SampleRate={SampleRate}, Channels={Channels}",
        session.SessionId,
        session.SampleRate,
        session.Channels);

    return Results.Ok(session);
});

app.MapPost("/api/speech/sessions/{sessionId}/chunks", async (string sessionId, HttpRequest httpRequest, SpeechChatService speechChatService, ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
{
    var logger = loggerFactory.CreateLogger("SpeechApi");

    if ((httpRequest.ContentLength ?? 0) <= 0)
    {
        return Results.BadRequest(new { error = "Chunk payload is required." });
    }

    try
    {
        await speechChatService.AppendChunkAsync(sessionId, httpRequest.Body, cancellationToken);
        logger.LogDebug("Speech chunk accepted for session {SessionId}. Bytes={Bytes}", sessionId, httpRequest.ContentLength);
        return Results.Ok();
    }
    catch (KeyNotFoundException ex)
    {
        logger.LogWarning(ex, "Speech chunk rejected for missing session {SessionId}.", sessionId);
        return Results.NotFound(new { error = ex.Message });
    }
});

app.MapPost("/api/speech/sessions/{sessionId}/complete", async (string sessionId, SpeechFinalizeRequest request, SpeechChatService speechChatService, ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
{
    var logger = loggerFactory.CreateLogger("SpeechApi");
    var requestId = Guid.NewGuid().ToString("N");
    var stopwatch = Stopwatch.StartNew();

    logger.LogInformation("[{RequestId}] Finalizing speech session {SessionId}. ChatModel={ChatModel}",
        requestId,
        sessionId,
        request.ChatModel ?? "(default)");

    try
    {
        var result = await speechChatService.CompleteAsync(sessionId, request, cancellationToken);
        stopwatch.Stop();
        logger.LogInformation("[{RequestId}] Speech session {SessionId} completed in {ElapsedMs}ms. TranscriptLength={TranscriptLength}",
            requestId,
            sessionId,
            stopwatch.ElapsedMilliseconds,
            result.Transcript.Length);
        return Results.Ok(result);
    }
    catch (KeyNotFoundException ex)
    {
        logger.LogWarning(ex, "[{RequestId}] Speech finalize failed: session {SessionId} not found.", requestId, sessionId);
        return Results.NotFound(new { error = ex.Message, requestId });
    }
    catch (Exception ex)
    {
        stopwatch.Stop();
        logger.LogError(ex, "[{RequestId}] Speech session {SessionId} failed after {ElapsedMs}ms.", requestId, sessionId, stopwatch.ElapsedMilliseconds);
        return Results.Problem(ex.Message, extensions: new Dictionary<string, object?> { ["requestId"] = requestId });
    }
});

app.MapDelete("/api/speech/sessions/{sessionId}", async (string sessionId, SpeechChatService speechChatService, ILoggerFactory loggerFactory) =>
{
    var logger = loggerFactory.CreateLogger("SpeechApi");
    await speechChatService.CancelSessionAsync(sessionId);
    logger.LogInformation("Speech session {SessionId} canceled.", sessionId);
    return Results.Ok();
});

app.Run();

static Action<string, double> CreateEpProgressLogger()
{
    var lastLoggedPercentByEp = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    return (epName, percent) =>
    {
        var roundedPercent = (int)Math.Round(percent);
        if (lastLoggedPercentByEp.TryGetValue(epName, out var lastPercent) && lastPercent == roundedPercent)
        {
            return;
        }

        lastLoggedPercentByEp[epName] = roundedPercent;
        Console.WriteLine($"  Registering EP '{epName}': {roundedPercent}%");
    };
}

internal sealed class FoundryChatService
{
    internal static readonly string[] SupportedChatModels = ["qwen3-vl-8b-instruct", "qwen3.5-9b", "qwen3.5-4b", "qwen3-1.7b", "phi-4-mini"];

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _modelLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _loadedModels = new(StringComparer.OrdinalIgnoreCase);
    private readonly HueChatToolService _hueChatTools;
    private readonly ILogger<FoundryChatService> _logger;

    public FoundryChatService(HueChatToolService hueChatTools, ILogger<FoundryChatService> logger)
    {
        _hueChatTools = hueChatTools;
        _logger = logger;
    }

    public async Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        var overallStopwatch = Stopwatch.StartNew();
        var modelAlias = ResolveModelAlias(request.Model);
        _logger.LogInformation("Starting completion using model '{ModelAlias}'.", modelAlias);

        var catalog = await FoundryLocalManager.Instance.GetCatalogAsync(cancellationToken);
        _logger.LogDebug("Foundry catalog loaded.");

        var (model, resolvedModelAlias) = await ResolveCatalogModelAsync(catalog, modelAlias, cancellationToken);
        if (!string.Equals(modelAlias, resolvedModelAlias, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Resolved requested model '{RequestedModelAlias}' to Foundry catalog alias '{ResolvedModelAlias}'.",
                modelAlias,
                resolvedModelAlias);
        }

        modelAlias = resolvedModelAlias;

        var selectedRuntime = model.Info?.GetType().GetProperty("Runtime")?.GetValue(model.Info);
        _logger.LogInformation("Model '{ModelAlias}' resolved to '{ModelId}' with runtime {Runtime}.", modelAlias, model.Id, selectedRuntime);

        await EnsureModelReadyAsync(modelAlias, model, cancellationToken);

        _logger.LogInformation("Creating chat client for model '{ModelAlias}'.", modelAlias);
#pragma warning disable CS0618 // GetChatClientAsync is obsolete in favor of ChatSession, which has a different API shape incompatible with the Betalgo OpenAI message/tool types used throughout this file; migrating is a larger change tracked separately.
        var chatClient = await model.GetChatClientAsync(cancellationToken);
#pragma warning restore CS0618
        _logger.LogInformation("Submitting prompt to model '{ModelAlias}'.", modelAlias);

        var messages = new List<ChatMessage>
        {
            ChatMessage.FromSystem("You are DaveBot, a concise assistant. For any Philips Hue room or light request (turning on/off, changing brightness, changing color), call the control_hue_by_name tool directly with the room or light name extracted from the user's message - do not call list_hue_rooms or list_hue_lights first, and do not describe how you would do it in code. If the user asks what scenes are available (e.g. 'what scenes does the lounge have'), call list_hue_scenes directly. If the user asks to set/apply/switch to a specific named scene (e.g. 'set lounge to Relax', 'set lounge to Moonlight'), call activate_hue_scene directly with roomName and sceneName extracted from the user's message - do not call list_hue_scenes first, the scene name does not need to be validated beforehand. Do not use control_hue_by_name for scene requests. If Hue is not connected, ask the user to press the physical bridge link button, wait for confirmation, and then call connect_hue_bridge. Never claim a light, room, or scene changed unless a tool result confirms it.")
        };

        if (IsHueBridgeConnectionRequest(request.Message))
        {
            _logger.LogInformation("Bridge-connection prompt detected. Running required connect_hue_bridge MCP preflight.");
            var connectionResult = await _hueChatTools.ConnectBridgeAsync(null, cancellationToken);
            _logger.LogInformation("connect_hue_bridge result: {Result}", connectionResult);

            if (TryParseConnectionSucceeded(connectionResult, out var isConnected) && isConnected)
            {
                return new ChatResponse(
                    modelAlias,
                    "Hue bridge connected successfully and application key was stored in app configuration.",
                    DateTimeOffset.UtcNow);
            }

            return new ChatResponse(
                modelAlias,
                $"Hue bridge connection failed. Details: {connectionResult}",
                DateTimeOffset.UtcNow);
        }

        messages.Add(ChatMessage.FromUser(request.Message));

        // Bounded tool-calling loop: one tool-call round, then either a direct answer (for tools handled by
        // TryBuildDirectControlResponse) or one follow-up round where the model summarizes tool results in
        // plain text. Capped at 2 rounds - a well-behaved tool call should never need more than that, and
        // list-style tool results are answered directly (see TryBuildDirectControlResponse) specifically so
        // the model isn't tempted to call the same read-only tool again instead of answering.
        const int maxToolCallRounds = 2;
        var noToolCallRetryUsed = false;
        ChatResponse? directResponse = null;

        Betalgo.Ranul.OpenAI.ObjectModels.ResponseModels.ChatCompletionCreateResponse response;
        IList<ToolCall>? toolCalls;

        for (var round = 0; ; round++)
        {
            response = await chatClient.CompleteChatAsync(messages, _hueChatTools.ToolDefinitions, cancellationToken);
            toolCalls = ExtractToolCalls(response);

            if (toolCalls is null or { Count: 0 })
            {
                if (!noToolCallRetryUsed)
                {
                    // Some models occasionally narrate an action in plain text instead of calling the tool
                    // (no structured tool_calls, no inline <tool_call> block either). This is a generic
                    // reliability retry - not tied to any specific prompt wording - giving the model one
                    // more chance with an explicit reminder before falling back to its plain-text answer.
                    noToolCallRetryUsed = true;
                    _logger.LogWarning("Model '{ModelAlias}' returned no tool call for a request; retrying once with an explicit reminder.", modelAlias);

                    messages.Add(ChatMessage.FromAssistant(response.Choices?.FirstOrDefault()?.Message?.Content?.ToString() ?? string.Empty, null, null));
                    messages.Add(ChatMessage.FromSystem("You did not call a tool. If the user's message asked to control, check, or connect Hue lights/rooms/scenes, you must call the appropriate tool now with structured arguments. Do not describe or narrate the action in plain text."));
                    continue;
                }

                break;
            }

            _logger.LogInformation("Model requested {ToolCallCount} Hue MCP tool call(s) (round {Round}).", toolCalls.Count, round + 1);

            var toolResults = new List<string>();
            foreach (var toolCall in toolCalls)
            {
                var toolResult = await _hueChatTools.ExecuteAsync(toolCall, cancellationToken);
                toolResults.Add(toolResult);
            }

            // If the tool call was our authoritative Hue control/scene action and it succeeded, report the
            // result directly instead of sending it back to the model for paraphrasing - the tool result
            // already contains everything needed for a definitive confirmation, and a further model
            // round-trip only adds latency (and risk of the model mangling or repeating the outcome).
            directResponse = TryBuildDirectControlResponse(toolCalls, toolResults, modelAlias);
            if (directResponse is not null)
            {
                break;
            }

            messages.Add(ChatMessage.FromAssistant(
                response.Choices?.FirstOrDefault()?.Message?.Content?.ToString() ?? string.Empty,
                null,
                toolCalls));

            for (var i = 0; i < toolCalls.Count; i++)
            {
                messages.Add(ChatMessage.FromTool(toolResults[i], toolCalls[i].Id ?? $"toolcall_{i}"));
            }

            if (round + 1 >= maxToolCallRounds)
            {
                _logger.LogWarning("Reached max tool-call rounds ({MaxRounds}) for model '{ModelAlias}'; requesting a final plain-text answer.",
                    maxToolCallRounds,
                    modelAlias);

                messages.Add(ChatMessage.FromSystem("You have already called the necessary tools. Reply now with a final plain-text answer summarizing the result. Do not call any more tools."));
                response = await chatClient.CompleteChatAsync(messages, _hueChatTools.ToolDefinitions, cancellationToken);
                break;
            }

            _logger.LogInformation("Submitting Hue MCP tool results to model '{ModelAlias}' for round {NextRound}.", modelAlias, round + 2);
        }

        if (directResponse is not null)
        {
            overallStopwatch.Stop();
            _logger.LogInformation("Completion finished for model '{ModelAlias}' in {ElapsedMs}ms (tool result reported directly).",
                modelAlias,
                overallStopwatch.ElapsedMilliseconds);
            return directResponse;
        }

        var message = response.Choices?.FirstOrDefault()?.Message?.Content;
        overallStopwatch.Stop();

        _logger.LogInformation("Completion finished for model '{ModelAlias}' in {ElapsedMs}ms.",
            modelAlias,
            overallStopwatch.ElapsedMilliseconds);

        return new ChatResponse(
            modelAlias,
            message?.ToString() ?? string.Empty,
            DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// If a control_hue_by_name, activate_hue_scene, or list_hue_scenes tool call succeeded, builds a
    /// definitive/human-readable response directly from its structured result, avoiding an unnecessary
    /// second model round-trip for something the tool result already answers conclusively. This is what
    /// keeps the tool-calling loop to at most two rounds for these tools: one call, then a direct answer.
    /// </summary>
    private static ChatResponse? TryBuildDirectControlResponse(IList<ToolCall> toolCalls, IList<string> toolResults, string modelAlias)
    {
        for (var i = 0; i < toolCalls.Count; i++)
        {
            var toolName = toolCalls[i].FunctionCall?.Name;

            if (string.Equals(toolName, "control_hue_by_name", StringComparison.OrdinalIgnoreCase))
            {
                var response = TryBuildControlByNameResponse(toolResults[i], modelAlias);
                if (response is not null)
                {
                    return response;
                }
            }
            else if (string.Equals(toolName, "activate_hue_scene", StringComparison.OrdinalIgnoreCase))
            {
                var response = TryBuildActivateSceneResponse(toolResults[i], modelAlias);
                if (response is not null)
                {
                    return response;
                }
            }
            else if (string.Equals(toolName, "list_hue_scenes", StringComparison.OrdinalIgnoreCase))
            {
                var response = TryBuildListScenesResponse(toolResults[i], modelAlias);
                if (response is not null)
                {
                    return response;
                }
            }
        }

        return null;
    }

    private static ChatResponse? TryBuildListScenesResponse(string toolResult, string modelAlias)
    {
        try
        {
            using var doc = JsonDocument.Parse(toolResult);
            var root = doc.RootElement;

            if (GetPropertyCaseInsensitive(root, "error") is { } errorProp)
            {
                return new ChatResponse(modelAlias, $"Could not list Hue scenes: {errorProp.GetString()}", DateTimeOffset.UtcNow);
            }

            if (root.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var sceneNames = root.EnumerateArray()
                .Select(scene => GetPropertyCaseInsensitive(scene, "name")?.GetString())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .ToArray();

            if (sceneNames.Length == 0)
            {
                return new ChatResponse(modelAlias, "No scenes were found for that room.", DateTimeOffset.UtcNow);
            }

            return new ChatResponse(
                modelAlias,
                $"Available scenes: {string.Join(", ", sceneNames)}.",
                DateTimeOffset.UtcNow);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ChatResponse? TryBuildControlByNameResponse(string toolResult, string modelAlias)
    {
        try
        {
            using var doc = JsonDocument.Parse(toolResult);
            var root = doc.RootElement;

            if (GetPropertyCaseInsensitive(root, "error") is { } errorProp)
            {
                return new ChatResponse(modelAlias, $"Could not update Hue: {errorProp.GetString()}", DateTimeOffset.UtcNow);
            }

            var appliedProp = GetPropertyCaseInsensitive(root, "applied");
            if (appliedProp is not { ValueKind: JsonValueKind.True or JsonValueKind.False } || !appliedProp.Value.GetBoolean())
            {
                return null;
            }

            var targetType = GetPropertyCaseInsensitive(root, "matchedTargetType")?.GetString() ?? "target";
            var namesProp = GetPropertyCaseInsensitive(root, "affectedLightNames");
            var affectedNames = namesProp is { ValueKind: JsonValueKind.Array }
                ? namesProp.Value.EnumerateArray().Select(n => n.GetString() ?? string.Empty).Where(n => n.Length > 0).ToArray()
                : [];

            var namesText = affectedNames.Length > 0 ? string.Join(", ", affectedNames) : "the requested target";
            var verb = affectedNames.Length == 1 ? "was" : "were";

            return new ChatResponse(
                modelAlias,
                $"{namesText} ({targetType}) {verb} updated successfully.",
                DateTimeOffset.UtcNow);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ChatResponse? TryBuildActivateSceneResponse(string toolResult, string modelAlias)
    {
        try
        {
            using var doc = JsonDocument.Parse(toolResult);
            var root = doc.RootElement;

            if (GetPropertyCaseInsensitive(root, "error") is { } errorProp)
            {
                return new ChatResponse(modelAlias, $"Could not activate Hue scene: {errorProp.GetString()}", DateTimeOffset.UtcNow);
            }

            var appliedProp = GetPropertyCaseInsensitive(root, "applied");
            if (appliedProp is not { ValueKind: JsonValueKind.True or JsonValueKind.False } || !appliedProp.Value.GetBoolean())
            {
                return null;
            }

            var roomNames = GetPropertyCaseInsensitive(root, "roomNames") is { ValueKind: JsonValueKind.Array } namesProp
                ? namesProp.EnumerateArray().Select(n => n.GetString() ?? string.Empty).Where(n => n.Length > 0).ToArray()
                : [];
            var roomNamesText = roomNames.Length > 0 ? string.Join(", ", roomNames) : "the room";
            var sceneName = GetPropertyCaseInsensitive(root, "sceneName")?.GetString() ?? "the scene";

            return new ChatResponse(
                modelAlias,
                $"Scene '{sceneName}' was activated in {roomNamesText}.",
                DateTimeOffset.UtcNow);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement? GetPropertyCaseInsensitive(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }

    /// <summary>
    /// Some local model runtimes (e.g. Qwen/Hermes-format chat templates) do not populate the structured
    /// tool_calls field and instead emit an inline "&lt;tool_call&gt;{...}&lt;/tool_call&gt;" text block.
    /// This checks the structured field first, then falls back to parsing that inline format so tool
    /// execution works regardless of which convention the underlying model uses.
    /// </summary>
    private IList<ToolCall>? ExtractToolCalls(Betalgo.Ranul.OpenAI.ObjectModels.ResponseModels.ChatCompletionCreateResponse response)
    {
        var message = response.Choices?.FirstOrDefault()?.Message;
        if (message?.ToolCalls is { Count: > 0 } structuredToolCalls)
        {
            return structuredToolCalls;
        }

        var content = message?.Content?.ToString();
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var parsedCalls = new List<ToolCall>();
        var index = 0;
        var searchStart = 0;

        while (true)
        {
            var tagStart = content.IndexOf("<tool_call>", searchStart, StringComparison.OrdinalIgnoreCase);
            if (tagStart < 0)
            {
                break;
            }

            var jsonStart = content.IndexOf('{', tagStart);
            if (jsonStart < 0)
            {
                break;
            }

            var jsonText = ExtractBalancedJsonObject(content, jsonStart);
            if (jsonText is null)
            {
                _logger.LogWarning("Could not find a balanced JSON object in inline <tool_call> block starting at index {Index}.", jsonStart);
                break;
            }

            searchStart = jsonStart + jsonText.Length;

            try
            {
                using var doc = JsonDocument.Parse(jsonText);
                var root = doc.RootElement;

                var toolName = root.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
                if (string.IsNullOrWhiteSpace(toolName))
                {
                    continue;
                }

                var argumentsJson = root.TryGetProperty("arguments", out var argsProp)
                    ? argsProp.GetRawText()
                    : "{}";

                parsedCalls.Add(new ToolCall
                {
                    Id = $"inline_{Guid.NewGuid():N}",
                    Type = "function",
                    Index = index++,
                    FunctionCall = new FunctionCall
                    {
                        Name = toolName,
                        Arguments = argumentsJson
                    }
                });
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse inline <tool_call> JSON: {Json}", jsonText);
            }
        }

        if (parsedCalls.Count > 0)
        {
            _logger.LogInformation("Parsed {Count} inline tool_call block(s) from model text output (non-structured tool-call format).", parsedCalls.Count);
            return parsedCalls;
        }

        var plainTextCall = TryParsePlainTextToolCall(content);
        if (plainTextCall is not null)
        {
            _logger.LogInformation("Parsed a plain-text tool call ('{ToolName}') from model text output (non-JSON format).", plainTextCall.FunctionCall!.Name);
            return [plainTextCall];
        }

        return null;
    }

    /// <summary>
    /// Some small local models (observed with phi-4-mini) narrate a tool call as plain text instead of
    /// JSON, e.g. "list_hue_scenes room_name: lounge" or "activate_hue_scene roomName: lounge, sceneName: Relax".
    /// This looks for a known tool name followed by "key: value" pairs and builds a structured ToolCall from it.
    /// </summary>
    private ToolCall? TryParsePlainTextToolCall(string content)
    {
        foreach (var toolDefinition in _hueChatTools.ToolDefinitions)
        {
            var toolName = toolDefinition.Function?.Name;
            if (string.IsNullOrWhiteSpace(toolName))
            {
                continue;
            }

            var nameMatch = Regex.Match(content, $@"\b{Regex.Escape(toolName)}\b", RegexOptions.IgnoreCase);
            if (!nameMatch.Success)
            {
                continue;
            }

            var knownProperties = toolDefinition.Function?.Parameters?.Properties?.Keys
                ?? Enumerable.Empty<string>();

            var argumentsAfterName = content[(nameMatch.Index + nameMatch.Length)..];
            var arguments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (Match pairMatch in Regex.Matches(argumentsAfterName, @"(\w+)""?\s*[:=]\s*""?([^,\n""]+)""?"))
            {
                var key = pairMatch.Groups[1].Value;
                var matchedProperty = knownProperties.FirstOrDefault(p => NormalizePropertyName(p) == NormalizePropertyName(key));
                if (matchedProperty is null)
                {
                    continue;
                }

                arguments[matchedProperty] = pairMatch.Groups[2].Value.Trim();
            }

            if (arguments.Count == 0)
            {
                continue;
            }

            return new ToolCall
            {
                Id = $"plaintext_{Guid.NewGuid():N}",
                Type = "function",
                Index = 0,
                FunctionCall = new FunctionCall
                {
                    Name = toolName,
                    Arguments = JsonSerializer.Serialize(arguments)
                }
            };
        }

        return null;
    }

    /// <summary>
    /// Normalizes a property name for loose comparison between the tool's declared camelCase parameter
    /// names (e.g. "roomName") and whatever casing/separator convention a model happens to narrate in
    /// plain text (e.g. "room_name", "Room-Name"), by stripping non-alphanumeric characters and lowering.
    /// </summary>
    private static string NormalizePropertyName(string name) =>
        Regex.Replace(name, "[^a-zA-Z0-9]", string.Empty).ToLowerInvariant();

    /// <summary>
    /// Scans forward from a '{' character and returns the substring up to its matching balanced '}',
    /// correctly skipping over braces inside string literals (including escaped quotes). Returns null
    /// if no balanced closing brace is found.
    /// </summary>
    private static string? ExtractBalancedJsonObject(string content, int startIndex)
    {
        if (startIndex < 0 || startIndex >= content.Length || content[startIndex] != '{')
        {
            return null;
        }

        var depth = 0;
        var inString = false;
        var escapeNext = false;

        for (var i = startIndex; i < content.Length; i++)
        {
            var c = content[i];

            if (escapeNext)
            {
                escapeNext = false;
                continue;
            }

            if (inString)
            {
                if (c == '\\')
                {
                    escapeNext = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        return content[startIndex..(i + 1)];
                    }
                    break;
            }
        }

        return null;
    }

    public async Task PreloadChatModelsAsync(CancellationToken cancellationToken)
    {
        // Only the default model is preloaded/warmed up at startup. Other supported models are loaded
        // lazily on first use (via EnsureModelReadyAsync in CompleteAsync) to avoid paying the download/
        // load cost for models the user may never select.
        var defaultAlias = SupportedChatModels[0];
        _logger.LogInformation("Starting Foundry chat model warmup for default model '{ModelAlias}'.", defaultAlias);

        var catalog = await FoundryLocalManager.Instance.GetCatalogAsync(cancellationToken);

        try
        {
            var (model, resolvedAlias) = await ResolveCatalogModelAsync(catalog, defaultAlias, cancellationToken);
            await EnsureModelReadyAsync(resolvedAlias, model, cancellationToken);
            _logger.LogInformation("Warmup complete for default model '{ModelAlias}'.", resolvedAlias);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Model warmup skipped/failed for default model '{ModelAlias}'.", defaultAlias);
        }

        _logger.LogInformation("Foundry chat model warmup finished.");
    }

    private static string ResolveModelAlias(string? requestedModel)
    {
        if (string.IsNullOrWhiteSpace(requestedModel))
        {
            return SupportedChatModels[0];
        }

        var matched = SupportedChatModels.FirstOrDefault(m =>
            string.Equals(m, requestedModel, StringComparison.OrdinalIgnoreCase));

        return matched ?? throw new InvalidOperationException(
            $"Unsupported chat model '{requestedModel}'. Supported values: {string.Join(", ", SupportedChatModels)}");
    }

    private static bool IsHueBridgeConnectionRequest(string message) =>
        message.Contains("connect", StringComparison.OrdinalIgnoreCase) && message.Contains("bridge", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("application key", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("api key", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("link button", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("pressed the button", StringComparison.OrdinalIgnoreCase);

    private static bool TryParseConnectionSucceeded(string connectionResult, out bool connected)
    {
        connected = false;

        try
        {
            using var doc = JsonDocument.Parse(connectionResult);
            if (doc.RootElement.TryGetProperty("connected", out var connectedNode) &&
                connectedNode.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                connected = connectedNode.GetBoolean();
                return true;
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    private static async Task<(IModel Model, string Alias)> ResolveCatalogModelAsync(ICatalog catalog, string requestedAlias, CancellationToken cancellationToken)
    {
        var model = await catalog.GetModelAsync(requestedAlias, cancellationToken);
        if (model is not null)
        {
            PreferGpuVariant(model);
            return (model, requestedAlias);
        }

        string[] keywords = requestedAlias.StartsWith("qwen", StringComparison.OrdinalIgnoreCase)
            ? ["qwen", "1.7b"]
            : requestedAlias.StartsWith("phi", StringComparison.OrdinalIgnoreCase)
                ? ["phi", "4", "mini"]
                : [requestedAlias];

        var models = await catalog.ListModelsAsync(cancellationToken);
        var fallback = models.FirstOrDefault(candidate =>
            keywords.All(keyword => candidate.Alias.Contains(keyword, StringComparison.OrdinalIgnoreCase)));

        if (fallback is not null)
        {
            var fallbackModel = await catalog.GetModelAsync(fallback.Alias, cancellationToken);
            if (fallbackModel is not null)
            {
                PreferGpuVariant(fallbackModel);
                return (fallbackModel, fallback.Alias);
            }
        }

        var availableAliases = string.Join(", ", models.Select(candidate => candidate.Alias));
        throw new InvalidOperationException(
            $"Model '{requestedAlias}' was not found in the local Foundry catalog. Available aliases: {availableAliases}");
    }

    /// <summary>
    /// The Foundry Local catalog's default-selected variant for a model alias does not always
    /// pick a GPU-capable build even when one is available (observed for some aliases where the
    /// default resolves to a "-generic-cpu" variant despite "-cuda-gpu"/"-generic-gpu" siblings
    /// existing). Explicitly prefer the best available GPU variant so local hardware acceleration
    /// is actually used.
    /// </summary>
    private static void PreferGpuVariant(IModel model)
    {
        var runtimeValue = model.Info?.GetType().GetProperty("Runtime")?.GetValue(model.Info);
        var runtimeDescription = runtimeValue?.ToString() ?? string.Empty;
        if (runtimeDescription.Contains("DeviceType = GPU", StringComparison.OrdinalIgnoreCase) ||
            runtimeDescription.Contains("DeviceType = NPU", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var preferredVariant = GetGpuVariantPreferenceOrder(model).FirstOrDefault();
        if (preferredVariant is not null)
        {
            model.SelectVariant(preferredVariant);
        }
    }

    /// <summary>
    /// Returns this model's variants ordered from most to least preferred for hardware acceleration
    /// (CUDA, then TensorRT-RTX, then generic GPU/DirectML), excluding the CPU-only variant. Used to
    /// retry with the next-best variant if the currently selected one fails to load (some GPU variant
    /// packages have been observed to be broken, e.g. duplicate ONNX node name errors).
    /// </summary>
    private static IEnumerable<IModel> GetGpuVariantPreferenceOrder(IModel model)
    {
        string[] gpuVariantSuffixes = ["-cuda-gpu", "-trtrtx-gpu", "-generic-gpu"];
        foreach (var suffix in gpuVariantSuffixes)
        {
            var gpuVariant = model.Variants.FirstOrDefault(variant =>
                variant.Id.Contains(suffix, StringComparison.OrdinalIgnoreCase));
            if (gpuVariant is not null)
            {
                yield return gpuVariant;
            }
        }
    }

    private async Task EnsureModelReadyAsync(string modelAlias, IModel model, CancellationToken cancellationToken)
    {
        if (_loadedModels.ContainsKey(modelAlias))
        {
            _logger.LogDebug("Model '{ModelAlias}' already loaded.", modelAlias);
            return;
        }

        var modelLock = _modelLocks.GetOrAdd(modelAlias, _ => new SemaphoreSlim(1, 1));
        await modelLock.WaitAsync(cancellationToken);

        try
        {
            if (_loadedModels.ContainsKey(modelAlias))
            {
                _logger.LogDebug("Model '{ModelAlias}' already loaded after waiting lock.", modelAlias);
                return;
            }

            _logger.LogInformation("Model '{ModelAlias}' not loaded. Starting download if required.", modelAlias);

            await DownloadAndLoadWithFallbackAsync(modelAlias, model, cancellationToken);

            _loadedModels[modelAlias] = true;
        }
        finally
        {
            modelLock.Release();
        }
    }

    /// <summary>
    /// Downloads and loads the model's currently selected variant. If loading fails (some GPU
    /// variant packages have been observed to be broken, e.g. duplicate ONNX node name errors),
    /// falls back through the remaining GPU variants and finally the CPU variant before giving up.
    /// </summary>
    private async Task DownloadAndLoadWithFallbackAsync(string modelAlias, IModel model, CancellationToken cancellationToken)
    {
        var candidates = new List<IModel> { model };
        candidates.AddRange(GetGpuVariantPreferenceOrder(model).Where(v => v.Id != model.Id));
        var cpuVariant = model.Variants.FirstOrDefault(v => v.Id.Contains("-generic-cpu", StringComparison.OrdinalIgnoreCase));
        if (cpuVariant is not null && candidates.All(c => c.Id != cpuVariant.Id))
        {
            candidates.Add(cpuVariant);
        }

        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            if (i > 0)
            {
                _logger.LogWarning("Falling back to variant '{VariantId}' for model '{ModelAlias}'.", candidate.Id, modelAlias);
                model.SelectVariant(candidate);
            }

            try
            {
                var lastLoggedProgress = -1;
                await model.DownloadAsync(progress =>
                {
                    var currentProgress = (int)Math.Floor(progress);
                    if (currentProgress <= lastLoggedProgress || currentProgress % 10 != 0)
                    {
                        return;
                    }

                    lastLoggedProgress = currentProgress;
                    _logger.LogInformation("Model '{ModelAlias}' download progress: {Progress}%", modelAlias, currentProgress);
                }, cancellationToken);

                _logger.LogInformation("Loading model '{ModelAlias}' variant '{VariantId}' into memory.", modelAlias, candidate.Id);
                await model.LoadAsync(cancellationToken);
                _logger.LogInformation("Model '{ModelAlias}' is loaded (variant '{VariantId}').", modelAlias, candidate.Id);
                return;
            }
            catch (FoundryLocalException ex) when (i < candidates.Count - 1)
            {
                _logger.LogWarning(ex, "Failed to load model '{ModelAlias}' variant '{VariantId}'; trying next variant.", modelAlias, candidate.Id);
            }
        }
    }
}

internal sealed class FoundryModelWarmupService : IHostedService, IDisposable
{
    private readonly FoundryChatService _chatService;
    private readonly ILogger<FoundryModelWarmupService> _logger;
    private CancellationTokenSource? _warmupCancellation;

    public FoundryModelWarmupService(FoundryChatService chatService, ILogger<FoundryModelWarmupService> logger)
    {
        _chatService = chatService;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _warmupCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        _ = Task.Run(async () =>
        {
            try
            {
                await _chatService.PreloadChatModelsAsync(_warmupCancellation.Token);
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("Foundry model warmup canceled.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Foundry model warmup failed.");
            }
        }, _warmupCancellation.Token);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _warmupCancellation?.Cancel();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _warmupCancellation?.Dispose();
    }
}

internal sealed class SpeechChatService
{
    private const string SttModelAlias = "whisper-small";

    private readonly SemaphoreSlim _sttModelLock = new(1, 1);
    private readonly SpeechSessionStore _sessionStore;
    private readonly FoundryChatService _chatService;
    private readonly ILogger<SpeechChatService> _logger;
    private bool _sttModelLoaded;

    public SpeechChatService(SpeechSessionStore sessionStore, FoundryChatService chatService, ILogger<SpeechChatService> logger)
    {
        _sessionStore = sessionStore;
        _chatService = chatService;
        _logger = logger;
    }

    public SpeechSessionResponse CreateSession(int sampleRate, int channels)
    {
        var boundedSampleRate = Math.Clamp(sampleRate, 8000, 48000);
        var boundedChannels = Math.Clamp(channels, 1, 2);
        var sessionId = _sessionStore.CreateSession(boundedSampleRate, boundedChannels);
        return new SpeechSessionResponse(sessionId, boundedSampleRate, boundedChannels);
    }

    public Task AppendChunkAsync(string sessionId, Stream chunkStream, CancellationToken cancellationToken) =>
        _sessionStore.AppendChunkAsync(sessionId, chunkStream, cancellationToken);

    public async Task<SpeechChatResponse> CompleteAsync(string sessionId, SpeechFinalizeRequest request, CancellationToken cancellationToken)
    {
        var capturedAudio = await _sessionStore.CompleteSessionAsync(sessionId, cancellationToken);
        var wavPath = CreateWavFileFromPcm(capturedAudio);

        try
        {
            _logger.LogInformation("Starting transcription for session {SessionId} using model '{ModelAlias}'.", sessionId, SttModelAlias);

            var catalog = await FoundryLocalManager.Instance.GetCatalogAsync(cancellationToken);
            var sttModel = await catalog.GetModelAsync(SttModelAlias, cancellationToken)
                ?? throw new InvalidOperationException($"Model '{SttModelAlias}' was not found in the local Foundry catalog.");

            await EnsureSttModelReadyAsync(sttModel, cancellationToken);

            #pragma warning disable CS0618 // GetAudioClientAsync is obsolete in favor of AudioSession; migrating is a larger change tracked separately.
                        var audioClient = await sttModel.GetAudioClientAsync(cancellationToken);
            #pragma warning restore CS0618
            var sttResult = await audioClient.TranscribeAudioAsync(wavPath, cancellationToken);
            var transcript = sttResult.Text?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(transcript))
            {
                throw new InvalidOperationException("No speech was detected in the recorded audio.");
            }

            _logger.LogInformation("Transcription complete for session {SessionId}. TranscriptLength={TranscriptLength}",
                sessionId,
                transcript.Length);

            var chatResponse = await _chatService.CompleteAsync(new ChatRequest(transcript, request.ChatModel), cancellationToken);

            return new SpeechChatResponse(
                sessionId,
                transcript,
                chatResponse.Message,
                chatResponse.Model,
                chatResponse.GeneratedAtUtc);
        }
        finally
        {
            TryDeleteFile(capturedAudio.FilePath);
            TryDeleteFile(wavPath);
        }
    }

    public Task CancelSessionAsync(string sessionId) => _sessionStore.CancelSessionAsync(sessionId);

    private async Task EnsureSttModelReadyAsync(IModel sttModel, CancellationToken cancellationToken)
    {
        if (_sttModelLoaded)
        {
            _logger.LogDebug("STT model '{ModelAlias}' already loaded.", SttModelAlias);
            return;
        }

        await _sttModelLock.WaitAsync(cancellationToken);

        try
        {
            if (_sttModelLoaded)
            {
                return;
            }

            _logger.LogInformation("Preparing STT model '{ModelAlias}'.", SttModelAlias);

            var lastLoggedProgress = -1;
            await sttModel.DownloadAsync(progress =>
            {
                var currentProgress = (int)Math.Floor(progress);
                if (currentProgress <= lastLoggedProgress || currentProgress % 10 != 0)
                {
                    return;
                }

                lastLoggedProgress = currentProgress;
                _logger.LogInformation("STT model '{ModelAlias}' download progress: {Progress}%", SttModelAlias, currentProgress);
            }, cancellationToken);

            await sttModel.LoadAsync(cancellationToken);
            _sttModelLoaded = true;

            _logger.LogInformation("STT model '{ModelAlias}' is loaded.", SttModelAlias);
        }
        finally
        {
            _sttModelLock.Release();
        }
    }

    private static void TryDeleteFile(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch
        {
            // keep best effort cleanup silent
        }
    }

    private static string CreateWavFileFromPcm(SpeechAudioPayload payload)
    {
        if (payload.ByteLength <= 0)
        {
            throw new InvalidOperationException("No microphone audio was captured.");
        }

        if (payload.ByteLength > int.MaxValue)
        {
            throw new InvalidOperationException("Audio payload is too large.");
        }

        var wavPath = Path.ChangeExtension(payload.FilePath, ".wav");
        var dataLength = (int)payload.ByteLength;
        var byteRate = payload.SampleRate * payload.Channels * 2;
        var blockAlign = (short)(payload.Channels * 2);

        using (var source = new FileStream(payload.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var destination = new FileStream(wavPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new BinaryWriter(destination, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + dataLength);
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)payload.Channels);
            writer.Write(payload.SampleRate);
            writer.Write(byteRate);
            writer.Write(blockAlign);
            writer.Write((short)16);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(dataLength);

            source.CopyTo(destination);
        }

        return wavPath;
    }
}

internal sealed class SpeechSessionStore
{
    private readonly ConcurrentDictionary<string, SpeechUploadSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _storageFolder = Path.Combine(Path.GetTempPath(), "DaveBot", "speech-sessions");

    public string CreateSession(int sampleRate, int channels)
    {
        Directory.CreateDirectory(_storageFolder);
        var sessionId = Guid.NewGuid().ToString("N");
        var filePath = Path.Combine(_storageFolder, $"{sessionId}.pcm");

        var session = new SpeechUploadSession(filePath, sampleRate, channels);
        if (!_sessions.TryAdd(sessionId, session))
        {
            session.Dispose();
            throw new InvalidOperationException("Failed to create speech session.");
        }

        return sessionId;
    }

    public async Task AppendChunkAsync(string sessionId, Stream chunkStream, CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            throw new KeyNotFoundException($"Speech session '{sessionId}' was not found.");
        }

        await session.AppendAsync(chunkStream, cancellationToken);
    }

    public async Task<SpeechAudioPayload> CompleteSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (!_sessions.TryRemove(sessionId, out var session))
        {
            throw new KeyNotFoundException($"Speech session '{sessionId}' was not found.");
        }

        return await session.CompleteAsync(cancellationToken);
    }

    public async Task CancelSessionAsync(string sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var session))
        {
            return;
        }

        var filePath = session.FilePath;
        await session.DisposeAsync();

        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
    }
}

internal sealed class SpeechUploadSession : IAsyncDisposable, IDisposable
{
    private readonly FileStream _stream;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private long _bytesWritten;
    private bool _isClosed;

    public SpeechUploadSession(string filePath, int sampleRate, int channels)
    {
        FilePath = filePath;
        SampleRate = sampleRate;
        Channels = channels;
        _stream = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
    }

    public string FilePath { get; }

    public int SampleRate { get; }

    public int Channels { get; }

    public async Task AppendAsync(Stream chunkStream, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);

        try
        {
            if (_isClosed)
            {
                throw new InvalidOperationException("Speech session is already completed.");
            }

            await chunkStream.CopyToAsync(_stream, cancellationToken);
            await _stream.FlushAsync(cancellationToken);
            _bytesWritten = _stream.Length;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<SpeechAudioPayload> CompleteAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);

        try
        {
            if (_isClosed)
            {
                return new SpeechAudioPayload(FilePath, SampleRate, Channels, _bytesWritten);
            }

            await _stream.FlushAsync(cancellationToken);
            await _stream.DisposeAsync();
            _isClosed = true;

            return new SpeechAudioPayload(FilePath, SampleRate, Channels, _bytesWritten);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lock.WaitAsync();

        try
        {
            if (_isClosed)
            {
                return;
            }

            await _stream.DisposeAsync();
            _isClosed = true;
        }
        finally
        {
            _lock.Release();
            _lock.Dispose();
        }
    }

    public void Dispose()
    {
        if (_isClosed)
        {
            return;
        }

        _stream.Dispose();
        _isClosed = true;
        _lock.Dispose();
    }
}

internal sealed record ChatRequest(string Message, string? Model);

internal sealed record HueAppKeyRequest(string? DeviceType);

internal sealed record ChatResponse(string Model, string Message, DateTimeOffset GeneratedAtUtc);

internal sealed record SpeechSessionStartRequest(int? SampleRate, int? Channels);

internal sealed record SpeechSessionResponse(string SessionId, int SampleRate, int Channels);

internal sealed record SpeechFinalizeRequest(string? ChatModel);

internal sealed record SpeechChatResponse(
    string SessionId,
    string Transcript,
    string Message,
    string Model,
    DateTimeOffset GeneratedAtUtc);

internal sealed record SpeechAudioPayload(string FilePath, int SampleRate, int Channels, long ByteLength);

internal sealed record ModelCapabilitiesResponse(
    IReadOnlyList<string> ChatModels,
    string SttModel,
    string TtsModel,
    string Notes);
