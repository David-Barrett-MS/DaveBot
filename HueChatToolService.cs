using System.Text.Json;
using Betalgo.Ranul.OpenAI.ObjectModels.RequestModels;
using Betalgo.Ranul.OpenAI.ObjectModels.SharedModels;

internal sealed class HueChatToolService
{
    private const string ConnectBridgeToolName = "connect_hue_bridge";
    private const string ControlByNameToolName = "control_hue_by_name";
    private const string ListScenesToolName = "list_hue_scenes";
    private const string ActivateSceneToolName = "activate_hue_scene";
    private const string ListLightsToolName = "list_hue_lights";
    private const string SetLightStateToolName = "set_hue_light_state";
    private const string ListRoomsToolName = "list_hue_rooms";
    private const string SetRoomStateToolName = "set_hue_room_state";

    private readonly HueMcpTools _hueMcpTools;
    private readonly ILogger<HueChatToolService> _logger;

    public HueChatToolService(HueMcpTools hueMcpTools, ILogger<HueChatToolService> logger)
    {
        _hueMcpTools = hueMcpTools;
        _logger = logger;
    }

    public IReadOnlyList<ToolDefinition> ToolDefinitions { get; } =
    [
        ToolDefinition.DefineFunction(new FunctionDefinition
        {
            Name = ConnectBridgeToolName,
            Description = "Connects to the configured Philips Hue bridge and saves an application key. Ask the user to press the physical bridge link button before calling this tool.",
            Parameters = new PropertyDefinition
            {
                Type = "object",
                Properties = new Dictionary<string, PropertyDefinition>
                {
                    ["deviceType"] = new() { Type = "string", Description = "Optional Hue device type, such as davebot#local." }
                },
                AdditionalProperties = false
            }
        }),
        ToolDefinition.DefineFunction(new FunctionDefinition
        {
            Name = ControlByNameToolName,
            Description = "The only tool for controlling Philips Hue rooms or lights. Always use this tool for any request to turn lights on/off, change brightness, or change color - never invent a light ID or call any other Hue tool. Pass the room or light name exactly as mentioned by the user (e.g. 'lounge'); it is resolved and applied automatically in one call. To control every light, pass name as 'all lights'. Provide at least one of on, brightness, or colorName.",
            Parameters = new PropertyDefinition
            {
                Type = "object",
                Properties = new Dictionary<string, PropertyDefinition>
                {
                    ["name"] = new() { Type = "string", Description = "The room or light name mentioned by the user, e.g. 'lounge' or 'kitchen counter'. Use 'all lights' to target every light. Never a resource ID." },
                    ["on"] = new() { Type = "boolean", Description = "Power state - use this for on/off requests, not colorName." },
                    ["brightness"] = new() { Type = "number", Description = "Brightness percentage from 1 to 100.", Minimum = 1, Maximum = 100 },
                    ["colorName"] = new() { Type = "string", Description = "Color name, e.g. white, warm white, red, green, blue, yellow, orange, pink, purple, cyan, teal. Never provide XY coordinates directly, and never put 'on'/'off' here - use the on parameter instead." }
                },
                Required = ["name"],
                AdditionalProperties = false
            }
        }),
        ToolDefinition.DefineFunction(new FunctionDefinition
        {
            Name = ListScenesToolName,
            Description = "Lists available Philips Hue scenes, optionally filtered to a specific room by name. Use this to discover what scenes exist (e.g. in the lounge) before activating one, or when the user asks what scenes are available.",
            Parameters = new PropertyDefinition
            {
                Type = "object",
                Properties = new Dictionary<string, PropertyDefinition>
                {
                    ["roomName"] = new() { Type = "string", Description = "Optional room name to filter scenes to, e.g. 'lounge'. Omit to list all scenes." }
                },
                AdditionalProperties = false
            }
        }),
        ToolDefinition.DefineFunction(new FunctionDefinition
        {
            Name = ActivateSceneToolName,
            Description = "Activates a named Philips Hue scene within a named room. Use this whenever the user asks to set/apply/switch to a scene (e.g. 'set lounge to Relax'). Both names are resolved automatically.",
            Parameters = new PropertyDefinition
            {
                Type = "object",
                Properties = new Dictionary<string, PropertyDefinition>
                {
                    ["roomName"] = new() { Type = "string", Description = "The room name mentioned by the user, e.g. 'lounge'." },
                    ["sceneName"] = new() { Type = "string", Description = "The scene name mentioned by the user, e.g. 'Relax' or 'Bright'." }
                },
                Required = ["roomName", "sceneName"],
                AdditionalProperties = false
            }
        })
    ];

    public Task<string> ListLightsAsync(CancellationToken cancellationToken) =>
        _hueMcpTools.ListLightsAsync(cancellationToken);

    public Task<string> ConnectBridgeAsync(string? deviceType, CancellationToken cancellationToken) =>
        _hueMcpTools.ConnectBridgeAsync(deviceType, cancellationToken);

    public Task<string> ControlByNameAsync(
        string name,
        bool? on,
        double? brightness,
        string? colorName,
        CancellationToken cancellationToken) =>
        _hueMcpTools.ControlByNameAsync(name, on, brightness, colorName, cancellationToken);

    public Task<string> ListScenesAsync(string? roomName, CancellationToken cancellationToken) =>
        _hueMcpTools.ListScenesAsync(roomName, cancellationToken);

    public Task<string> ActivateSceneAsync(string roomName, string sceneName, CancellationToken cancellationToken) =>
        _hueMcpTools.ActivateSceneAsync(roomName, sceneName, cancellationToken);

    public Task<string> SetLightStateAsync(
        string lightId,
        bool? on,
        double? brightness,
        double? colorX,
        double? colorY,
        CancellationToken cancellationToken) =>
        _hueMcpTools.SetLightStateAsync(lightId, on, brightness, colorX, colorY, cancellationToken);

    public Task<string> ListRoomsAsync(CancellationToken cancellationToken) =>
        _hueMcpTools.ListRoomsAsync(cancellationToken);

    public Task<string> SetRoomStateAsync(
        string groupedLightId,
        bool? on,
        double? brightness,
        double? colorX,
        double? colorY,
        CancellationToken cancellationToken) =>
        _hueMcpTools.SetRoomStateAsync(groupedLightId, on, brightness, colorX, colorY, cancellationToken);

    public async Task<string> ExecuteAsync(ToolCall toolCall, CancellationToken cancellationToken)
    {
        var function = toolCall.FunctionCall;
        if (function is null || string.IsNullOrWhiteSpace(function.Name))
        {
            return SerializeError("Tool call did not include a function name.");
        }

        _logger.LogInformation("Executing Hue MCP tool {ToolName}. Arguments={Arguments}", function.Name, function.Arguments);

        try
        {
            using var arguments = JsonDocument.Parse(string.IsNullOrWhiteSpace(function.Arguments) ? "{}" : function.Arguments);
            var root = arguments.RootElement;

            return function.Name switch
            {
                ConnectBridgeToolName => await _hueMcpTools.ConnectBridgeAsync(
                    GetOptionalString(root, "deviceType"),
                    cancellationToken),
                ControlByNameToolName => await _hueMcpTools.ControlByNameAsync(
                    GetRequiredString(root, "name"),
                    GetOptionalBoolean(root, "on"),
                    GetOptionalDouble(root, "brightness"),
                    GetOptionalString(root, "colorName"),
                    cancellationToken),
                ListScenesToolName => await _hueMcpTools.ListScenesAsync(
                    GetOptionalString(root, "roomName"),
                    cancellationToken),
                ActivateSceneToolName => await _hueMcpTools.ActivateSceneAsync(
                    GetRequiredString(root, "roomName"),
                    GetRequiredString(root, "sceneName"),
                    cancellationToken),
                // Legacy/low-level tool names are no longer advertised to the model, but if a model still
                // emits one (e.g. from prior conversation memory), redirect it through control_hue_by_name
                // instead of failing, since these calls are frequently made with a name instead of a real ID.
                // Raw XY color values from a legacy call are not translated (no reliable XY-to-name mapping);
                // only on/brightness are honored in that fallback path.
                SetLightStateToolName => await _hueMcpTools.ControlByNameAsync(
                    GetRequiredString(root, "lightId"),
                    GetOptionalBoolean(root, "on"),
                    GetOptionalDouble(root, "brightness"),
                    null,
                    cancellationToken),
                SetRoomStateToolName => await _hueMcpTools.ControlByNameAsync(
                    GetRequiredString(root, "groupedLightId"),
                    GetOptionalBoolean(root, "on"),
                    GetOptionalDouble(root, "brightness"),
                    null,
                    cancellationToken),
                ListLightsToolName => await _hueMcpTools.ListLightsAsync(cancellationToken),
                ListRoomsToolName => await _hueMcpTools.ListRoomsAsync(cancellationToken),
                _ => SerializeError($"Unsupported Hue MCP tool '{function.Name}'.")
            };
        }
        catch (JsonException)
        {
            return SerializeError("Tool arguments must be valid JSON.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Hue MCP tool {ToolName} failed.", function.Name);
            return SerializeError(ex.Message);
        }
    }

    private static string GetRequiredString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidOperationException($"Tool argument '{name}' is required.");
        }

        return value.GetString()!;
    }

    private static bool? GetOptionalBoolean(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static string? GetOptionalString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double? GetOptionalDouble(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetDouble(out var number)
            ? number
            : null;

    private static string SerializeError(string message) => JsonSerializer.Serialize(new { error = message });
}
