using DaveHome;
using DaveIdentity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DaveBrain.Pages.Devices;

[Authorize(Policy = DaveIdentityPolicies.DiagnosticCommands)]
public sealed class IndexModel(
    HueHubService hueHubs,
    PhilipsAirPlusHubService airPlus,
    IHomeStateStore homeStateStore) : PageModel
{
    public sealed record ColourPickerValue(string Hex, int Brightness, double X, double Y);

    private HashSet<string>? selectedLocationFilters;
    private HashSet<string>? selectedTypeFilters;

    public IReadOnlyList<Device> Devices { get; private set; } = Array.Empty<Device>();

    public IReadOnlyList<Location> Locations { get; private set; } = Array.Empty<Location>();

    public IReadOnlyList<string> DeviceTypes { get; private set; } = Array.Empty<string>();

    public DeviceOperationResult? Result { get; private set; }

    public void OnGet(string? locationFilter, string? typeFilter)
    {
        SetFilters(locationFilter, typeFilter);
        LoadDevices();
    }

    public async Task<IActionResult> OnPostAsync(
        Guid deviceId,
        string action,
        int? brightness,
        double? colourX,
        double? colourY,
        bool updateColour,
        bool updateBrightness,
        int? fanSpeed,
        string? locationFilter,
        string? typeFilter,
        CancellationToken cancellationToken)
    {
        SetFilters(locationFilter, typeFilter);
        var device = GetManagedDevice(deviceId);
        if (device is null)
        {
            ModelState.AddModelError(string.Empty, "The device does not exist.");
            LoadDevices();
            return Page();
        }

        try
        {
            Result = action == "set-colour-brightness"
                ? await SetColourAndBrightnessAsync(
                    device,
                    updateColour,
                    colourX,
                    colourY,
                    updateBrightness,
                    brightness,
                    cancellationToken)
                : action switch
                {
                    "read" when device.DeviceType == "hue.light" =>
                        await hueHubs.ReadLightStateAsync(device, cancellationToken),
                    "on" when device.DeviceType == "hue.light" &&
                        device.Capabilities.HasFlag(DeviceCapabilities.OnOff) => await hueHubs.SetLightStateAsync(
                        device,
                        new DeviceStateChange(IsOn: true),
                        cancellationToken),
                    "off" when device.DeviceType == "hue.light" &&
                        device.Capabilities.HasFlag(DeviceCapabilities.OnOff) => await hueHubs.SetLightStateAsync(
                        device,
                        new DeviceStateChange(IsOn: false),
                        cancellationToken),
                    "brightness" when device.DeviceType == "hue.light" &&
                        device.Capabilities.HasFlag(DeviceCapabilities.Brightness) &&
                        brightness is >= 0 and <= 100 =>
                        await hueHubs.SetLightStateAsync(
                        device,
                        new DeviceStateChange(Brightness: brightness),
                        cancellationToken),
                    "read" when device.DeviceType == "philips.airplus" => await airPlus.ReadDeviceStateAsync(
                        device,
                        cancellationToken),
                    "set-fan-speed" when device.DeviceType == "philips.airplus" && fanSpeed is not null =>
                        await airPlus.SetFanSpeedAsync(device, fanSpeed.Value, cancellationToken),
                    "disable" => await SetEnabledAsync(device, false, cancellationToken),
                    "enable" => await SetEnabledAsync(device, true, cancellationToken),
                    "remove" => await RemoveAsync(device, cancellationToken),
                    _ => new DeviceOperationResult(DeviceOperationStatus.InvalidState),
                };
        }
        catch (InvalidOperationException exception)
        {
            Result = new DeviceOperationResult(DeviceOperationStatus.InvalidState, Detail: exception.Message);
        }

        LoadDevices();
        return Page();
    }

    public async Task<IActionResult> OnPostUpdateLocationAsync(
        Guid deviceId,
        Guid? locationId,
        string? locationFilter,
        string? typeFilter,
        CancellationToken cancellationToken)
    {
        SetFilters(locationFilter, typeFilter);
        try
        {
            await homeStateStore.UpdateAsync(state =>
            {
                if (!state.DevicesById.TryGetValue(deviceId, out var device) ||
                    !IsManagedDevice(device))
                {
                    throw new InvalidOperationException("The device does not exist.");
                }

                if (locationId is { } assignedLocationId &&
                    !state.LocationsById.ContainsKey(assignedLocationId))
                {
                    throw new InvalidOperationException("The selected location does not exist.");
                }

                return state.Document with
                {
                    Devices = state.Document.Devices.Select(candidate => candidate.Id == deviceId
                        ? candidate with { LocationId = locationId }
                        : candidate).ToArray(),
                };
            }, homeStateStore.Current.Revision, cancellationToken);
            Result = new DeviceOperationResult(DeviceOperationStatus.Succeeded);
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
        }

        LoadDevices();
        return Page();
    }

    public async Task<IActionResult> OnPostSetEnabledAsync(
        Guid deviceId,
        bool enabled,
        string? locationFilter,
        string? typeFilter,
        CancellationToken cancellationToken)
    {
        SetFilters(locationFilter, typeFilter);
        var device = GetManagedDevice(deviceId);
        if (device is null)
        {
            ModelState.AddModelError(string.Empty, "The device does not exist.");
        }
        else
        {
            try
            {
                Result = await SetEnabledAsync(device, enabled, cancellationToken);
            }
            catch (InvalidOperationException exception)
            {
                Result = new DeviceOperationResult(DeviceOperationStatus.InvalidState, Detail: exception.Message);
            }
        }

        LoadDevices();
        return Page();
    }

    public string FormatState(Device device)
    {
        if (device.DeviceType == "philips.airplus")
        {
            return PhilipsAirPlusSensorFormatter.FormatState(device.State);
        }

        var state = device.State;
        return state is null
            ? "Unknown"
            : state.Attributes is { } attributes &&
              attributes.TryGetValue("event", out var eventName)
                ? $"{eventName} (button {attributes.GetValueOrDefault("button", "unknown")})"
                : state.Attributes is { } motionAttributes &&
                  (motionAttributes.ContainsKey("motion") ||
                   motionAttributes.ContainsKey("motionValid") ||
                   motionAttributes.ContainsKey("lightLevelAvailable"))
                    ? $"Motion: {motionAttributes.GetValueOrDefault("motion", "Unknown")}; " +
                      $"light level: {motionAttributes.GetValueOrDefault("lightLevel", "Unavailable")}"
                : $"{(state.IsOn is true ? "On" : "Off")}, {state.Brightness?.ToString() ?? "unknown"}%";
    }

    public ColourPickerValue GetColourPickerValue(DeviceState? state)
    {
        var x = state?.Colour?.X is >= 0 and <= 1 ? state.Colour.X : 0.3127;
        var y = state?.Colour?.Y is > 0 and <= 1 ? state.Colour.Y : 0.3290;
        var brightness = Math.Clamp(state?.Brightness ?? 100, 0, 100);
        return new ColourPickerValue(ToRgbHex(x, y), brightness, x, y);
    }

    public string? GetCachedColourHex(Device device) =>
        device.Capabilities.HasFlag(DeviceCapabilities.Colour) &&
        device.State?.Colour is { } colour &&
        IsValidColour(colour.X, colour.Y)
            ? ToRgbHex(colour.X, colour.Y)
            : null;

    public bool IsLocationFilterSelected(string locationFilter) =>
        selectedLocationFilters is null || selectedLocationFilters.Contains(locationFilter);

    public bool IsTypeFilterSelected(string typeFilter) =>
        selectedTypeFilters is null || selectedTypeFilters.Contains(typeFilter);

    public bool AreAllLocationsSelected() =>
        selectedLocationFilters is null ||
        (selectedLocationFilters.Contains("unassigned") &&
         Locations.All(location => selectedLocationFilters.Contains(location.Id.ToString())));

    public bool AreAllDeviceTypesSelected() =>
        selectedTypeFilters is null ||
        DeviceTypes.All(type => selectedTypeFilters.Contains(type));

    public int GetFanSpeed(DeviceState? state) =>
        state?.Attributes?.TryGetValue("fanSpeed", out var value) == true &&
        int.TryParse(value, out var fanSpeed) &&
        fanSpeed is >= 1 and <= 10 or 81 or 82
            ? fanSpeed
            : 1;

    public bool CanRead(Device device) =>
        device.DeviceType is "hue.light" or "philips.airplus";

    private async Task<DeviceOperationResult> SetEnabledAsync(
        Device device,
        bool enabled,
        CancellationToken cancellationToken)
    {
        switch (device.DeviceType)
        {
            case "hue.light":
            case "hue.switch":
            case "hue.motion-sensor":
                await hueHubs.SetDeviceEnabledAsync(device.Id, enabled, cancellationToken);
                break;
            case "philips.airplus":
                await airPlus.SetDeviceEnabledAsync(device.Id, enabled, cancellationToken);
                break;
            default:
                throw new InvalidOperationException("The device provider is not supported.");
        }

        return new DeviceOperationResult(DeviceOperationStatus.Succeeded);
    }

    private async Task<DeviceOperationResult> SetColourAndBrightnessAsync(
        Device device,
        bool updateColour,
        double? colourX,
        double? colourY,
        bool updateBrightness,
        int? brightness,
        CancellationToken cancellationToken)
    {
        if (device.DeviceType != "hue.light" || (!updateColour && !updateBrightness) ||
            (updateColour &&
             (!device.Capabilities.HasFlag(DeviceCapabilities.Colour) ||
              !IsValidColour(colourX, colourY))) ||
            (updateBrightness &&
             (!device.Capabilities.HasFlag(DeviceCapabilities.Brightness) ||
              brightness is < 0 or > 100)))
        {
            return new DeviceOperationResult(DeviceOperationStatus.InvalidState);
        }

        return await hueHubs.SetLightStateAsync(
            device,
            new DeviceStateChange(
                Brightness: updateBrightness ? brightness : null,
                Colour: updateColour
                    ? new DeviceColour(colourX!.Value, colourY!.Value)
                    : null),
            cancellationToken);
    }

    private async Task<DeviceOperationResult> RemoveAsync(
        Device device,
        CancellationToken cancellationToken)
    {
        switch (device.DeviceType)
        {
            case "hue.light":
            case "hue.switch":
            case "hue.motion-sensor":
                await hueHubs.RemoveDeviceAsync(device.Id, cancellationToken);
                break;
            case "philips.airplus":
                await airPlus.RemoveDeviceAsync(device.Id, cancellationToken);
                break;
            default:
                throw new InvalidOperationException("The device provider is not supported.");
        }

        return new DeviceOperationResult(DeviceOperationStatus.Succeeded);
    }

    private void LoadDevices()
    {
        Devices = hueHubs.GetMappedHueDevices()
            .Concat(airPlus.GetMappedDevices())
            .OrderBy(device => device.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Locations = homeStateStore.Current.LocationsById.Values
            .OrderBy(location => location.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        DeviceTypes = Devices
            .Select(device => device.DeviceType)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(type => type, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void SetFilters(string? locationFilter, string? typeFilter)
    {
        selectedLocationFilters = locationFilter is null
            ? null
            : locationFilter
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.Ordinal);
        selectedTypeFilters = typeFilter is null
            ? null
            : typeFilter
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private Device? GetManagedDevice(Guid deviceId) =>
        hueHubs.GetMappedHueDevices()
            .Concat(airPlus.GetMappedDevices())
            .SingleOrDefault(candidate => candidate.Id == deviceId);

    private static bool IsManagedDevice(Device device) =>
        device.DeviceType is "hue.light" or "hue.switch" or "hue.motion-sensor" or "philips.airplus";

    private static bool IsValidColour(double? x, double? y) =>
        x is >= 0 and <= 1 &&
        y is > 0 and <= 1 &&
        x + y <= 1;

    private static string ToRgbHex(double x, double y)
    {
        var xTristimulus = x / y;
        const double yTristimulus = 1;
        var zTristimulus = (1 - x - y) / y;
        var red = 3.2406 * xTristimulus - 1.5372 * yTristimulus - 0.4986 * zTristimulus;
        var green = -0.9689 * xTristimulus + 1.8758 * yTristimulus + 0.0415 * zTristimulus;
        var blue = 0.0557 * xTristimulus - 0.2040 * yTristimulus + 1.0570 * zTristimulus;
        var largest = Math.Max(red, Math.Max(green, blue));
        if (largest > 1)
        {
            red /= largest;
            green /= largest;
            blue /= largest;
        }

        return $"#{ToSrgbComponent(red):X2}{ToSrgbComponent(green):X2}{ToSrgbComponent(blue):X2}";
    }

    private static int ToSrgbComponent(double component)
    {
        var linear = Math.Max(0, component);
        var gammaCorrected = linear <= 0.0031308
            ? linear * 12.92
            : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055;
        return (int)Math.Round(Math.Clamp(gammaCorrected, 0, 1) * 255);
    }
}
