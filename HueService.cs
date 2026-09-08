using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net.Http.Headers;
using Microsoft.Extensions.Options;

internal sealed class HueOptions
{
    public string? BridgeBaseUrl { get; set; }

    public string? ApplicationKey { get; set; }

    public bool IgnoreCertificateErrors { get; set; }
}

internal sealed record HueLightDto(
    string Id,
    string Name,
    bool IsOn,
    double? Brightness,
    double? ColorX,
    double? ColorY);

internal sealed record HueLightStateRequest(
    bool? On,
    double? Brightness,
    HueXyColorRequest? Color);

internal sealed record HueXyColorRequest(double X, double Y);

internal sealed record HueRoomDto(
    string Id,
    string Name,
    string? GroupedLightId,
    IReadOnlyList<string> LightIds);

internal sealed record HueSceneDto(
    string Id,
    string Name,
    string? GroupId,
    string? GroupName);

internal sealed record HueCreateAppKeyResult(string ApplicationKey, string? ClientKey);

internal sealed record HueControlResult(
    string MatchedTargetName,
    string MatchedTargetType,
    bool Applied,
    IReadOnlyList<string> AffectedLightNames);

internal sealed record HueSceneActivationResult(
    IReadOnlyList<string> RoomNames,
    string SceneName,
    bool Applied);

internal sealed class HueApiException : Exception
{
    public HueApiException(string message) : base(message)
    {
    }
}

internal sealed class HueService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IOptionsMonitor<HueOptions> _options;
    private readonly IHostEnvironment _hostEnvironment;
    private readonly ILogger<HueService> _logger;
    private readonly SemaphoreSlim _configurationLock = new(1, 1);
    private string? _runtimeApplicationKey;

    public HueService(IOptionsMonitor<HueOptions> options, IHostEnvironment hostEnvironment, ILogger<HueService> logger)
    {
        _options = options;
        _hostEnvironment = hostEnvironment;
        _logger = logger;
    }

    public Task<IReadOnlyList<HueLightDto>> GetLightsAsync(CancellationToken cancellationToken)
    {
        return ExecuteWithClientAsync(async (client, options, ct) =>
        {
            var endpoint = new Uri(client.BaseAddress!, "resource/light");
            _logger.LogInformation("Requesting Hue lights from {Endpoint}", endpoint);

            using var response = await client.GetAsync("resource/light", ct);
            var root = await ParseJsonAsync(response, ct);
            EnsureHueSuccess(response, root, "Failed to read Hue lights.");

            var lights = root?["data"]?.AsArray()
                .Select(ParseLight)
                .Where(static l => l is not null)
                .Cast<HueLightDto>()
                .ToArray() ?? [];

            _logger.LogInformation("Hue returned {LightCount} lights from bridge {BridgeBaseUrl}", lights.Length, options.BridgeBaseUrl);
            return (IReadOnlyList<HueLightDto>)lights;
        }, cancellationToken);
    }

    public Task<HueLightDto> UpdateLightStateAsync(string lightId, HueLightStateRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(lightId))
        {
            throw new HueApiException("Hue light id is required.");
        }

        if (request.On is null && request.Brightness is null && request.Color is null)
        {
            throw new HueApiException("At least one light state value is required (on, brightness, or color). ");
        }

        return ExecuteWithClientAsync(async (client, _, ct) =>
        {
            var payload = BuildLightPayload(request);
            var payloadJson = payload.ToJsonString(JsonOptions);

            _logger.LogInformation("Updating Hue light {LightId} with payload {Payload}", lightId, payloadJson);

            using var putRequest = new HttpRequestMessage(HttpMethod.Put, $"resource/light/{Uri.EscapeDataString(lightId)}")
            {
                Content = new StringContent(payloadJson)
            };
            putRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            using var putResponse = await client.SendAsync(putRequest, ct);
            var updateRoot = await ParseJsonAsync(putResponse, ct);
            EnsureHueSuccess(putResponse, updateRoot, $"Failed to update Hue light '{lightId}'.");

            return await GetLightByIdAsync(client, lightId, ct);
        }, cancellationToken);
    }

    public Task<IReadOnlyList<HueRoomDto>> GetRoomsAsync(CancellationToken cancellationToken)
    {
        return ExecuteWithClientAsync(async (client, options, ct) =>
        {
            _logger.LogInformation("Requesting Hue rooms and zones from bridge {BridgeBaseUrl}", options.BridgeBaseUrl);

            using var roomResponse = await client.GetAsync("resource/room", ct);
            var roomRoot = await ParseJsonAsync(roomResponse, ct);
            EnsureHueSuccess(roomResponse, roomRoot, "Failed to read Hue rooms.");

            using var zoneResponse = await client.GetAsync("resource/zone", ct);
            var zoneRoot = await ParseJsonAsync(zoneResponse, ct);
            EnsureHueSuccess(zoneResponse, zoneRoot, "Failed to read Hue zones.");

            using var lightResponse = await client.GetAsync("resource/light", ct);
            var lightRoot = await ParseJsonAsync(lightResponse, ct);
            EnsureHueSuccess(lightResponse, lightRoot, "Failed to read Hue lights.");

            var deviceIdToLightId = (lightRoot?["data"]?.AsArray() ?? [])
                .Select(node => node as JsonObject)
                .Where(static light => light is not null)
                .Select(light => (
                    LightId: light!["id"]?.GetValue<string>(),
                    DeviceId: light["owner"]?["rid"]?.GetValue<string>()))
                .Where(pair => !string.IsNullOrWhiteSpace(pair.LightId) && !string.IsNullOrWhiteSpace(pair.DeviceId))
                .ToLookup(pair => pair.DeviceId!, pair => pair.LightId!);

            var rooms = (roomRoot?["data"]?.AsArray() ?? [])
                .Select(node => ParseRoom(node, deviceIdToLightId))
                .Where(static r => r is not null)
                .Cast<HueRoomDto>();

            // Zones (e.g. a "Lounge" zone grouping multiple physical rooms/lights) are a distinct Hue
            // resource type from rooms, but are controlled and scoped for scenes the same way (via a
            // grouped_light service), so they're merged into the same collection used throughout for
            // free-text name resolution, control, and scene activation.
            var zones = (zoneRoot?["data"]?.AsArray() ?? [])
                .Select(node => ParseRoom(node, deviceIdToLightId))
                .Where(static z => z is not null)
                .Cast<HueRoomDto>();

            var groups = rooms.Concat(zones).ToArray();

            _logger.LogInformation("Hue returned {RoomCount} room(s) and zone(s) from bridge {BridgeBaseUrl}", groups.Length, options.BridgeBaseUrl);
            return (IReadOnlyList<HueRoomDto>)groups;
        }, cancellationToken);
    }

    public Task<IReadOnlyList<HueLightDto>> SetRoomStateAsync(string groupedLightId, HueLightStateRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(groupedLightId))
        {
            throw new HueApiException("Hue grouped_light id is required. Use the GroupedLightId returned by list_hue_rooms.");
        }

        if (request.On is null && request.Brightness is null && request.Color is null)
        {
            throw new HueApiException("At least one light state value is required (on, brightness, or color). ");
        }

        return ExecuteWithClientAsync(async (client, _, ct) =>
        {
            var payload = BuildLightPayload(request);
            var payloadJson = payload.ToJsonString(JsonOptions);

            _logger.LogInformation("Updating Hue grouped_light {GroupedLightId} with payload {Payload}", groupedLightId, payloadJson);

            using var putRequest = new HttpRequestMessage(HttpMethod.Put, $"resource/grouped_light/{Uri.EscapeDataString(groupedLightId)}")
            {
                Content = new StringContent(payloadJson)
            };
            putRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            using var putResponse = await client.SendAsync(putRequest, ct);
            var updateRoot = await ParseJsonAsync(putResponse, ct);
            EnsureHueSuccess(putResponse, updateRoot, $"Failed to update Hue room (grouped_light '{groupedLightId}').");

            return Array.Empty<HueLightDto>() as IReadOnlyList<HueLightDto>;
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<HueSceneDto>> GetScenesAsync(string? roomName, CancellationToken cancellationToken)
    {
        var rooms = await GetRoomsAsync(cancellationToken);
        var roomIdToName = rooms.ToDictionary(r => r.Id, r => r.Name, StringComparer.OrdinalIgnoreCase);

        var scenes = await ExecuteWithClientAsync(async (client, options, ct) =>
        {
            _logger.LogInformation("Requesting Hue scenes from bridge {BridgeBaseUrl}", options.BridgeBaseUrl);

            using var sceneResponse = await client.GetAsync("resource/scene", ct);
            var sceneRoot = await ParseJsonAsync(sceneResponse, ct);
            EnsureHueSuccess(sceneResponse, sceneRoot, "Failed to read Hue scenes.");

            var parsedScenes = (sceneRoot?["data"]?.AsArray() ?? [])
                .Select(node => ParseScene(node, roomIdToName))
                .Where(static s => s is not null)
                .Cast<HueSceneDto>()
                .ToArray();

            _logger.LogInformation("Hue returned {SceneCount} scene(s) from bridge {BridgeBaseUrl}", parsedScenes.Length, options.BridgeBaseUrl);
            return (IReadOnlyList<HueSceneDto>)parsedScenes;
        }, cancellationToken);

        if (string.IsNullOrWhiteSpace(roomName))
        {
            return scenes;
        }

        var matchedRooms = FindAllNameMatches(rooms, r => r.Name, roomName.Trim());
        if (matchedRooms.Count == 0)
        {
            return [];
        }

        var matchedRoomIds = matchedRooms.Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return scenes.Where(s => s.GroupId is not null && matchedRoomIds.Contains(s.GroupId)).ToArray();
    }

    /// <summary>
    /// Resolves a room by free-text name and a scene by free-text name within that room, then recalls
    /// ("activates") the scene via PUT /resource/scene/{id} with {"recall":{"action":"active"}}.
    /// </summary>
    public async Task<HueSceneActivationResult> ActivateSceneAsync(string roomName, string sceneName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(roomName))
        {
            throw new HueApiException("A room name is required to activate a scene.");
        }

        if (string.IsNullOrWhiteSpace(sceneName))
        {
            throw new HueApiException("A scene name is required.");
        }

        var rooms = await GetRoomsAsync(cancellationToken);
        var matchedRooms = FindAllNameMatches(rooms, r => r.Name, roomName.Trim());
        if (matchedRooms.Count == 0)
        {
            throw new HueApiException($"No room matching '{roomName}' was found. Known rooms: {string.Join(", ", rooms.Select(r => r.Name))}");
        }

        var activatedRoomNames = new List<string>();
        string? resolvedSceneName = null;

        foreach (var matchedRoom in matchedRooms)
        {
            var scenes = await GetScenesAsync(matchedRoom.Name, cancellationToken);
            var matchedScene = FindAllNameMatches(scenes, s => s.Name, sceneName.Trim()).FirstOrDefault();
            if (matchedScene is null)
            {
                _logger.LogWarning("No scene matching '{SceneName}' was found in room '{RoomName}'; skipping.", sceneName, matchedRoom.Name);
                continue;
            }

            resolvedSceneName = matchedScene.Name;

            await ExecuteWithClientAsync(async (client, _, ct) =>
            {
                var payloadJson = new JsonObject
                {
                    ["recall"] = new JsonObject
                    {
                        ["action"] = "active"
                    }
                }.ToJsonString(JsonOptions);

                _logger.LogInformation("Activating Hue scene {SceneId} ('{SceneName}') in room '{RoomName}'.", matchedScene.Id, matchedScene.Name, matchedRoom.Name);

                using var putRequest = new HttpRequestMessage(HttpMethod.Put, $"resource/scene/{Uri.EscapeDataString(matchedScene.Id)}")
                {
                    Content = new StringContent(payloadJson)
                };
                putRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

                using var putResponse = await client.SendAsync(putRequest, ct);
                var updateRoot = await ParseJsonAsync(putResponse, ct);
                EnsureHueSuccess(putResponse, updateRoot, $"Failed to activate Hue scene '{matchedScene.Name}'.");

                return true;
            }, cancellationToken);

            activatedRoomNames.Add(matchedRoom.Name);
        }

        if (activatedRoomNames.Count == 0)
        {
            throw new HueApiException($"No scene matching '{sceneName}' was found in any room matching '{roomName}'.");
        }

        return new HueSceneActivationResult(activatedRoomNames, resolvedSceneName ?? sceneName.Trim(), true);
    }

    private static HueSceneDto? ParseScene(JsonNode? node, IReadOnlyDictionary<string, string> roomIdToName)
    {
        if (node is not JsonObject scene)
        {
            return null;
        }

        var id = scene["id"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var name = scene["metadata"]?["name"]?.GetValue<string>() ?? id;
        var groupId = scene["group"]?["rid"]?.GetValue<string>();
        var groupName = groupId is not null && roomIdToName.TryGetValue(groupId, out var resolvedName)
            ? resolvedName
            : null;

        return new HueSceneDto(id, name, groupId, groupName);
    }

    internal static readonly IReadOnlyDictionary<string, (double X, double Y)> ColorNameToXy =
        new Dictionary<string, (double X, double Y)>(StringComparer.OrdinalIgnoreCase)
        {
            ["white"] = (0.3227, 0.3290),
            ["warm white"] = (0.4325, 0.4090),
            ["cool white"] = (0.3115, 0.3233),
            ["daylight"] = (0.3455, 0.3538),
            ["red"] = (0.6750, 0.3220),
            ["green"] = (0.4091, 0.5180),
            ["blue"] = (0.1670, 0.0400),
            ["yellow"] = (0.4317, 0.4996),
            ["orange"] = (0.6115, 0.3850),
            ["pink"] = (0.4507, 0.2426),
            ["purple"] = (0.2664, 0.1225),
            ["magenta"] = (0.3787, 0.1724),
            ["cyan"] = (0.1710, 0.3520),
            ["turquoise"] = (0.1938, 0.4030),
            ["lime"] = (0.3610, 0.5860),
            ["gold"] = (0.4808, 0.4308),
            ["amber"] = (0.5614, 0.4156),
            ["lavender"] = (0.2980, 0.2010),
            ["teal"] = (0.1620, 0.3350)
        };

    /// <summary>
    /// Free-text names that mean "every light", used so a request like "turn all lights off" is honored
    /// directly instead of failing to match any single room/light name.
    /// </summary>
    private static readonly string[] AllLightsAliases = ["all", "all lights", "every light", "everything", "all the lights"];

    /// <summary>
    /// Resolves a free-text room or light name and applies a state to it in a single call.
    /// Rooms are matched first (controlling all lights in the room via grouped_light), falling back to an individual light.
    /// A name meaning "all lights" (see AllLightsAliases) controls every light on the bridge directly.
    /// Color names (e.g. "white", "red") are resolved to CIE XY server-side so the model does not need to supply coordinates.
    /// </summary>
    public async Task<HueControlResult> ControlByNameAsync(
        string name,
        bool? on,
        double? brightness,
        string? colorName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new HueApiException("A room or light name is required.");
        }

        // Models occasionally misuse colorName for a power state (e.g. "off"/"on") instead of the
        // dedicated `on` parameter. Treat that defensively as the intended on/off request rather than
        // failing with a confusing "unknown color" error.
        if (!string.IsNullOrWhiteSpace(colorName) && !ColorNameToXy.ContainsKey(colorName.Trim()))
        {
            var trimmedColorName = colorName.Trim();
            if (string.Equals(trimmedColorName, "off", StringComparison.OrdinalIgnoreCase))
            {
                on ??= false;
                colorName = null;
            }
            else if (string.Equals(trimmedColorName, "on", StringComparison.OrdinalIgnoreCase))
            {
                on ??= true;
                colorName = null;
            }
        }

        if (on is null && brightness is null && string.IsNullOrWhiteSpace(colorName))
        {
            throw new HueApiException("At least one of on, brightness, or colorName is required.");
        }

        HueXyColorRequest? color = null;
        if (!string.IsNullOrWhiteSpace(colorName))
        {
            if (!ColorNameToXy.TryGetValue(colorName.Trim(), out var xy))
            {
                throw new HueApiException(
                    $"Unknown color name '{colorName}'. Supported colors: {string.Join(", ", ColorNameToXy.Keys)}");
            }

            color = new HueXyColorRequest(xy.X, xy.Y);
            on ??= true;
        }

        var request = new HueLightStateRequest(on, brightness, color);
        var normalizedName = name.Trim();

        if (AllLightsAliases.Contains(normalizedName, StringComparer.OrdinalIgnoreCase))
        {
            var allLights = await GetLightsAsync(cancellationToken);
            var affectedAllNames = new List<string>();

            foreach (var light in allLights)
            {
                try
                {
                    await UpdateLightStateAsync(light.Id, request, cancellationToken);
                    affectedAllNames.Add(light.Name);
                }
                catch (HueApiException ex)
                {
                    _logger.LogWarning("Skipping light '{LightName}' ({LightId}) while controlling all lights: {Message}", light.Name, light.Id, ex.Message);
                }
            }

            return new HueControlResult(
                "All lights",
                "light",
                affectedAllNames.Count > 0,
                affectedAllNames);
        }

        var rooms = await GetRoomsAsync(cancellationToken);
        var matchedRooms = FindAllNameMatches(rooms, room => room.Name, normalizedName);

        if (matchedRooms.Count > 0)
        {
            var affectedNames = new List<string>();

            foreach (var room in matchedRooms)
            {
                if (string.IsNullOrWhiteSpace(room.GroupedLightId))
                {
                    _logger.LogWarning("Room '{RoomName}' has no controllable grouped light service; skipping.", room.Name);
                    continue;
                }

                try
                {
                    await SetRoomStateAsync(room.GroupedLightId, request, cancellationToken);
                    affectedNames.Add(room.Name);
                }
                catch (HueApiException ex)
                {
                    _logger.LogWarning("Skipping room '{RoomName}' due to error: {Message}", room.Name, ex.Message);
                }
            }

            if (affectedNames.Count == 0)
            {
                throw new HueApiException($"Matched room(s) for '{name}' could not be updated (no controllable grouped light service, or all lights unreachable).");
            }

            return new HueControlResult(
                string.Join(", ", affectedNames),
                "room",
                true,
                affectedNames);
        }

        var lights = await GetLightsAsync(cancellationToken);
        var matchedLights = FindAllNameMatches(lights, light => light.Name, normalizedName);

        if (matchedLights.Count > 0)
        {
            var affectedNames = new List<string>();

            foreach (var light in matchedLights)
            {
                try
                {
                    await UpdateLightStateAsync(light.Id, request, cancellationToken);
                    affectedNames.Add(light.Name);
                }
                catch (HueApiException ex)
                {
                    _logger.LogWarning("Skipping light '{LightName}' ({LightId}) due to error: {Message}", light.Name, light.Id, ex.Message);
                }
            }

            if (affectedNames.Count == 0)
            {
                throw new HueApiException($"Light(s) matching '{name}' could not be updated (unreachable).");
            }

            return new HueControlResult(
                string.Join(", ", affectedNames),
                "light",
                true,
                affectedNames);
        }

        var allNames = string.Join(", ", rooms.Select(r => r.Name).Concat(lights.Select(l => l.Name)));
        throw new HueApiException($"No room or light matching '{name}' was found. Known names: {allNames}");
    }

    private static IReadOnlyList<T> FindAllNameMatches<T>(IReadOnlyList<T> items, Func<T, string> nameSelector, string targetName)
        where T : class
    {
        var exact = items.Where(item =>
            string.Equals(nameSelector(item), targetName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (exact.Length > 0)
        {
            return exact;
        }

        var startsWith = items.Where(item =>
            nameSelector(item).StartsWith(targetName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (startsWith.Length > 0)
        {
            return startsWith;
        }

        var contains = items.Where(item =>
            nameSelector(item).Contains(targetName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (contains.Length > 0)
        {
            return contains;
        }

        var targetWords = targetName.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return items.Where(item =>
            targetWords.Any(word => word.Length > 2 && nameSelector(item).Contains(word, StringComparison.OrdinalIgnoreCase))).ToArray();
    }

    public Task<HueCreateAppKeyResult> CreateApplicationKeyAsync(string? deviceType, CancellationToken cancellationToken)
    {
        var deviceTypeValue = string.IsNullOrWhiteSpace(deviceType)
            ? "davebot#local"
            : deviceType;

        return ExecuteWithClientAsync(async (client, options, ct) =>
        {
            var payloadJson = new JsonObject
            {
                ["devicetype"] = deviceTypeValue,
                ["generateclientkey"] = true
            }.ToJsonString(JsonOptions);

            using var request = new HttpRequestMessage(HttpMethod.Post, options.BridgeBaseUrl!.TrimEnd('/') + "/api")
            {
                Content = new StringContent(payloadJson)
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            using var response = await client.SendAsync(request, ct);
            var root = await ParseJsonAsync(response, ct);

            if (!response.IsSuccessStatusCode)
            {
                throw new HueApiException($"Hue bridge key creation failed with status {(int)response.StatusCode}.");
            }

            var firstEntry = root?.AsArray().FirstOrDefault();
            var errorDescription = firstEntry?["error"]?["description"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(errorDescription))
            {
                throw new HueApiException($"Hue bridge key creation failed: {errorDescription}");
            }

            var username = firstEntry?["success"]?["username"]?.GetValue<string>();
            var clientKey = firstEntry?["success"]?["clientkey"]?.GetValue<string>();

            if (string.IsNullOrWhiteSpace(username))
            {
                throw new HueApiException("Hue bridge did not return an application key. Press bridge link button and retry.");
            }

            _runtimeApplicationKey = username;
            await PersistApplicationKeyAsync(username, ct);
            _logger.LogInformation("Created Hue application key for bridge {BridgeBaseUrl}.", options.BridgeBaseUrl);
            return new HueCreateAppKeyResult(username, clientKey);
        }, cancellationToken, includeApplicationKey: false);
    }

    public HttpMessageHandler CreateMessageHandler()
    {
        var handler = new HttpClientHandler();

        if (_options.CurrentValue.IgnoreCertificateErrors)
        {
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
        }

        return handler;
    }

    private async Task<T> ExecuteWithClientAsync<T>(
        Func<HttpClient, HueOptions, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken,
        bool includeApplicationKey = true)
    {
        var options = _options.CurrentValue;
        ValidateOptions(options);

        using var handler = CreateMessageHandler();
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

        if (!string.IsNullOrWhiteSpace(options.BridgeBaseUrl))
        {
            var bridgeUrl = options.BridgeBaseUrl.TrimEnd('/');
            client.BaseAddress = includeApplicationKey
                ? new Uri(bridgeUrl + "/clip/v2/")
                : new Uri(bridgeUrl + "/");
        }

        if (includeApplicationKey)
        {
            var effectiveApplicationKey = string.IsNullOrWhiteSpace(options.ApplicationKey)
                ? _runtimeApplicationKey
                : options.ApplicationKey;

            if (string.IsNullOrWhiteSpace(effectiveApplicationKey))
            {
                throw new HueApiException("Hue:ApplicationKey is required. Connect to bridge first to obtain and store the key.");
            }

            client.DefaultRequestHeaders.Add("hue-application-key", effectiveApplicationKey);
        }

        return await operation(client, options, cancellationToken);
    }

    private static async Task<JsonNode?> ParseJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        return JsonNode.Parse(content);
    }

    private static JsonObject BuildLightPayload(HueLightStateRequest request)
    {
        var payload = new JsonObject();

        if (request.On is not null)
        {
            payload["on"] = new JsonObject
            {
                ["on"] = request.On.Value
            };
        }

        if (request.Brightness is not null)
        {
            var brightness = Math.Clamp(request.Brightness.Value, 1, 100);
            payload["dimming"] = new JsonObject
            {
                ["brightness"] = brightness
            };
        }

        if (request.Color is not null)
        {
            var x = Math.Clamp(request.Color.X, 0, 1);
            var y = Math.Clamp(request.Color.Y, 0, 1);

            payload["color"] = new JsonObject
            {
                ["xy"] = new JsonObject
                {
                    ["x"] = x,
                    ["y"] = y
                }
            };
        }

        return payload;
    }

    private async Task<HueLightDto> GetLightByIdAsync(HttpClient client, string lightId, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync($"resource/light/{Uri.EscapeDataString(lightId)}", cancellationToken);
        var root = await ParseJsonAsync(response, cancellationToken);
        EnsureHueSuccess(response, root, $"Failed to load Hue light '{lightId}'.");

        var light = root?["data"]?.AsArray().Select(ParseLight).FirstOrDefault(static l => l is not null);
        return light ?? throw new HueApiException($"Hue light '{lightId}' was not found.");
    }

    private static HueLightDto? ParseLight(JsonNode? node)
    {
        if (node is not JsonObject light)
        {
            return null;
        }

        var id = light["id"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var name = light["metadata"]?["name"]?.GetValue<string>()
            ?? light["id_v1"]?.GetValue<string>()
            ?? id;

        var isOn = light["on"]?["on"]?.GetValue<bool>() ?? false;
        var brightness = light["dimming"]?["brightness"]?.GetValue<double?>();
        var colorX = light["color"]?["xy"]?["x"]?.GetValue<double?>();
        var colorY = light["color"]?["xy"]?["y"]?.GetValue<double?>();

        return new HueLightDto(id, name, isOn, brightness, colorX, colorY);
    }

    /// <summary>
    /// Parses a Hue "room" or "zone" resource into a HueRoomDto. Both resource types expose a
    /// grouped_light service and a children list the same way, but zone children can reference
    /// individual "light" resources directly (in addition to "device", as rooms always do), since
    /// a zone can group specific lights across different physical rooms.
    /// </summary>
    private static HueRoomDto? ParseRoom(JsonNode? node, ILookup<string, string> deviceIdToLightId)
    {
        if (node is not JsonObject room)
        {
            return null;
        }

        var id = room["id"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var name = room["metadata"]?["name"]?.GetValue<string>() ?? id;

        var services = room["services"]?.AsArray() ?? [];
        var groupedLightId = services
            .Select(service => service as JsonObject)
            .Where(service => service is not null && string.Equals(service["rtype"]?.GetValue<string>(), "grouped_light", StringComparison.OrdinalIgnoreCase))
            .Select(service => service!["rid"]?.GetValue<string>())
            .FirstOrDefault(rid => !string.IsNullOrWhiteSpace(rid));

        var children = room["children"]?.AsArray() ?? [];
        var childObjects = children
            .Select(child => child as JsonObject)
            .Where(static child => child is not null)
            .ToArray();

        var deviceIds = childObjects
            .Where(child => string.Equals(child!["rtype"]?.GetValue<string>(), "device", StringComparison.OrdinalIgnoreCase))
            .Select(child => child!["rid"]?.GetValue<string>())
            .Where(rid => !string.IsNullOrWhiteSpace(rid))
            .Select(rid => rid!);

        var directLightIds = childObjects
            .Where(child => string.Equals(child!["rtype"]?.GetValue<string>(), "light", StringComparison.OrdinalIgnoreCase))
            .Select(child => child!["rid"]?.GetValue<string>())
            .Where(rid => !string.IsNullOrWhiteSpace(rid))
            .Select(rid => rid!);

        var lightIds = deviceIds
            .SelectMany(deviceId => deviceIdToLightId[deviceId])
            .Concat(directLightIds)
            .Distinct()
            .ToArray();

        return new HueRoomDto(id, name, groupedLightId, lightIds);
    }

    /// <summary>
    /// Hue error codes that represent a soft warning rather than a real failure - the bridge still accepted
    /// and queued the command (HTTP 200), it's just informing the caller a device may be unreachable right
    /// now (e.g. powered off at the wall) so the command might not visibly take effect immediately.
    /// </summary>
    private static readonly HashSet<string> SoftWarningErrorCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "attribute_may_have_no_effect"
    };

    private static void EnsureHueSuccess(HttpResponseMessage response, JsonNode? root, string fallbackMessage)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new HueApiException($"{fallbackMessage} HTTP {(int)response.StatusCode}.");
        }

        var errors = root?["errors"]?.AsArray();
        if (errors is not { Count: > 0 })
        {
            return;
        }

        var hardErrors = errors
            .Where(e => !SoftWarningErrorCodes.Contains(e?["error_code"]?.GetValue<string>() ?? string.Empty))
            .Select(static e => e?["description"]?.GetValue<string>())
            .Where(static d => !string.IsNullOrWhiteSpace(d))
            .ToArray();

        if (hardErrors.Length > 0)
        {
            throw new HueApiException(string.Join("; ", hardErrors));
        }
    }

    private static void ValidateOptions(HueOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.BridgeBaseUrl))
        {
            throw new HueApiException("Hue:BridgeBaseUrl is required. Example: https://192.168.1.10");
        }
    }

    private async Task PersistApplicationKeyAsync(string applicationKey, CancellationToken cancellationToken)
    {
        var primarySettingsFileName = _hostEnvironment.IsDevelopment()
            ? "appsettings.Development.json"
            : "appsettings.json";
        var primarySettingsPath = Path.Combine(_hostEnvironment.ContentRootPath, primarySettingsFileName);
        var baseSettingsPath = Path.Combine(_hostEnvironment.ContentRootPath, "appsettings.json");

        await _configurationLock.WaitAsync(cancellationToken);

        try
        {
            await PersistApplicationKeyToFileAsync(primarySettingsPath, primarySettingsFileName, applicationKey, cancellationToken);

            if (!string.Equals(primarySettingsPath, baseSettingsPath, StringComparison.OrdinalIgnoreCase) && File.Exists(baseSettingsPath))
            {
                await PersistApplicationKeyToFileAsync(baseSettingsPath, "appsettings.json", applicationKey, cancellationToken);
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new HueApiException($"Hue application key was created but could not be saved to config: {ex.Message}");
        }
        catch (IOException ex)
        {
            throw new HueApiException($"Hue application key was created but could not be saved to config: {ex.Message}");
        }
        finally
        {
            _configurationLock.Release();
        }
    }

    private static async Task PersistApplicationKeyToFileAsync(
        string settingsPath,
        string settingsFileName,
        string applicationKey,
        CancellationToken cancellationToken)
    {
        var content = await File.ReadAllTextAsync(settingsPath, cancellationToken);
        var root = JsonNode.Parse(content)?.AsObject()
            ?? throw new HueApiException($"Unable to parse '{settingsFileName}'.");

        var hue = root["Hue"]?.AsObject() ?? new JsonObject();
        hue["ApplicationKey"] = applicationKey;
        root["Hue"] = hue;

        var formattedJson = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(settingsPath, formattedJson + Environment.NewLine, cancellationToken);
    }
}
