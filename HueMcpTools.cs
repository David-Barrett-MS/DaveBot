using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

[McpServerToolType]
internal sealed class HueMcpTools
{
    private readonly HueService _hueService;
    private readonly ILogger<HueMcpTools> _logger;

    public HueMcpTools(HueService hueService, ILogger<HueMcpTools> logger)
    {
        _hueService = hueService;
        _logger = logger;
    }

    [McpServerTool(Name = "connect_hue_bridge")]
    [Description("Connects DaveBot to the configured Philips Hue bridge. Tell the user to press the physical bridge link button first, then call this tool. The generated Hue application key is stored in the active appsettings file.")]
    public async Task<string> ConnectBridgeAsync(
        [Description("Optional Hue device type, such as davebot#local.")] string? deviceType = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _hueService.CreateApplicationKeyAsync(deviceType, cancellationToken);
            return JsonSerializer.Serialize(new
            {
                connected = true,
                applicationKeySaved = true,
                result.ClientKey
            });
        }
        catch (HueApiException ex)
        {
            _logger.LogWarning(ex, "MCP Hue bridge connection failed.");
            return JsonSerializer.Serialize(new { connected = false, error = ex.Message });
        }
    }

    [McpServerTool(Name = "control_hue_by_name")]
    [Description("Controls a Philips Hue room or light by name in a single call. Prefer this tool for any user request to turn lights on/off, change brightness, or change color. Automatically matches the given name to a room first, then an individual light, and applies the change immediately - no need to call list_hue_rooms or list_hue_lights first. Pass name as 'all lights' to control every light. Provide at least one of on, brightness, or colorName.")]
    public async Task<string> ControlByNameAsync(
        [Description("The room or light name mentioned by the user, e.g. 'lounge' or 'kitchen counter'. Use 'all lights' to target every light.")] string name,
        [Description("Optional on/off state - use this for on/off requests, not colorName.")] bool? on = null,
        [Description("Optional brightness percentage, from 1 to 100.")] double? brightness = null,
        [Description("Optional color name, e.g. white, warm white, red, green, blue, yellow, orange, pink, purple, cyan, teal. Do not put 'on'/'off' here.")] string? colorName = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _hueService.ControlByNameAsync(name, on, brightness, colorName, cancellationToken);
            return JsonSerializer.Serialize(result);
        }
        catch (HueApiException ex)
        {
            _logger.LogWarning(ex, "MCP Hue control_hue_by_name failed for '{Name}'.", name);
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    [McpServerTool(Name = "list_hue_scenes")]
    [Description("Lists Philips Hue scenes, optionally filtered to a specific room by name. Each scene includes its name and the room it belongs to. Use this to discover what scenes are available before activating one.")]
    public async Task<string> ListScenesAsync(
        [Description("Optional room name to filter scenes to, e.g. 'lounge'. Omit to list all scenes.")] string? roomName = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var scenes = await _hueService.GetScenesAsync(roomName, cancellationToken);
            return JsonSerializer.Serialize(scenes);
        }
        catch (HueApiException ex)
        {
            _logger.LogWarning(ex, "MCP Hue scene listing failed for room '{RoomName}'.", roomName);
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    [McpServerTool(Name = "activate_hue_scene")]
    [Description("Activates (recalls) a named Philips Hue scene within a named room, e.g. room 'lounge', scene 'Relax'. Resolves both names automatically and applies the scene immediately.")]
    public async Task<string> ActivateSceneAsync(
        [Description("The room name mentioned by the user, e.g. 'lounge'.")] string roomName,
        [Description("The scene name mentioned by the user, e.g. 'Relax' or 'Bright'.")] string sceneName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _hueService.ActivateSceneAsync(roomName, sceneName, cancellationToken);
            return JsonSerializer.Serialize(result);
        }
        catch (HueApiException ex)
        {
            _logger.LogWarning(ex, "MCP Hue activate_hue_scene failed for room '{RoomName}', scene '{SceneName}'.", roomName, sceneName);
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    [McpServerTool(Name = "list_hue_lights")]
    [Description("Lists Philips Hue lights with their IDs, names, on/off state, brightness, and available XY color state. Use this before controlling a named light.")]
    public async Task<string> ListLightsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var lights = await _hueService.GetLightsAsync(cancellationToken);
            return JsonSerializer.Serialize(lights);
        }
        catch (HueApiException ex)
        {
            _logger.LogWarning(ex, "MCP Hue light listing failed.");
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    [McpServerTool(Name = "set_hue_light_state")]
    [Description("Changes a Philips Hue light. Provide the light ID returned by list_hue_lights. Set on to true/false, brightness from 1 to 100, and optionally XY color coordinates from 0 to 1.")]
    public async Task<string> SetLightStateAsync(
        [Description("The Hue light resource ID returned by list_hue_lights.")] string lightId,
        [Description("Optional on/off state.")] bool? on = null,
        [Description("Optional brightness percentage, from 1 to 100.")] double? brightness = null,
        [Description("Optional CIE XY color X coordinate, from 0 to 1.")] double? colorX = null,
        [Description("Optional CIE XY color Y coordinate, from 0 to 1.")] double? colorY = null,
        CancellationToken cancellationToken = default)
    {
        if ((colorX is null) != (colorY is null))
        {
            return JsonSerializer.Serialize(new { error = "Both colorX and colorY are required when setting a color." });
        }

        try
        {
            var color = colorX is not null && colorY is not null
                ? new HueXyColorRequest(colorX.Value, colorY.Value)
                : null;

            var light = await _hueService.UpdateLightStateAsync(
                lightId,
                new HueLightStateRequest(on, brightness, color),
                cancellationToken);

            return JsonSerializer.Serialize(light);
        }
        catch (HueApiException ex)
        {
            _logger.LogWarning(ex, "MCP Hue light update failed for {LightId}.", lightId);
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }
    [McpServerTool(Name = "list_hue_rooms")]
    [Description("Lists Philips Hue rooms with their IDs, names, contained light IDs, and the grouped_light service ID used to control all lights in the room together. Use this before controlling lights by room name.")]
    public async Task<string> ListRoomsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var rooms = await _hueService.GetRoomsAsync(cancellationToken);
            return JsonSerializer.Serialize(rooms);
        }
        catch (HueApiException ex)
        {
            _logger.LogWarning(ex, "MCP Hue room listing failed.");
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    [McpServerTool(Name = "set_hue_room_state")]
    [Description("Changes all lights in a Philips Hue room at once. Provide the groupedLightId returned by list_hue_rooms (not the room id). Set on to true/false, brightness from 1 to 100, and optionally XY color coordinates from 0 to 1.")]
    public async Task<string> SetRoomStateAsync(
        [Description("The grouped_light resource ID for the room, returned as groupedLightId by list_hue_rooms.")] string groupedLightId,
        [Description("Optional on/off state.")] bool? on = null,
        [Description("Optional brightness percentage, from 1 to 100.")] double? brightness = null,
        [Description("Optional CIE XY color X coordinate, from 0 to 1.")] double? colorX = null,
        [Description("Optional CIE XY color Y coordinate, from 0 to 1.")] double? colorY = null,
        CancellationToken cancellationToken = default)
    {
        if ((colorX is null) != (colorY is null))
        {
            return JsonSerializer.Serialize(new { error = "Both colorX and colorY are required when setting a color." });
        }

        try
        {
            var color = colorX is not null && colorY is not null
                ? new HueXyColorRequest(colorX.Value, colorY.Value)
                : null;

            await _hueService.SetRoomStateAsync(
                groupedLightId,
                new HueLightStateRequest(on, brightness, color),
                cancellationToken);

            return JsonSerializer.Serialize(new { success = true, groupedLightId });
        }
        catch (HueApiException ex)
        {
            _logger.LogWarning(ex, "MCP Hue room update failed for {GroupedLightId}.", groupedLightId);
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }
}
