using DaveHome;
using DaveIdentity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DaveBrain.Pages.Devices;

[Authorize(Policy = DaveIdentityPolicies.DiagnosticCommands)]
public sealed class IndexModel(HueHubService hueHubs, IHomeStateStore homeStateStore) : PageModel
{
    public IReadOnlyList<Device> Devices { get; private set; } = Array.Empty<Device>();

    public IReadOnlyList<Location> Locations { get; private set; } = Array.Empty<Location>();

    public DeviceOperationResult? Result { get; private set; }

    public void OnGet() => LoadDevices();

    public async Task<IActionResult> OnPostAsync(
        Guid deviceId,
        string action,
        int? brightness,
        CancellationToken cancellationToken)
    {
        var device = hueHubs.GetMappedLights().SingleOrDefault(candidate => candidate.Id == deviceId);
        if (device is null)
        {
            ModelState.AddModelError(string.Empty, "The Hue light does not exist.");
            LoadDevices();
            return Page();
        }

        try
        {
            Result = action switch
            {
                "read" => await hueHubs.ReadLightStateAsync(device, cancellationToken),
                "on" => await hueHubs.SetLightStateAsync(
                    device,
                    new DeviceStateChange(IsOn: true),
                    cancellationToken),
                "off" => await hueHubs.SetLightStateAsync(
                    device,
                    new DeviceStateChange(IsOn: false),
                    cancellationToken),
                "brightness" when brightness is not null => await hueHubs.SetLightStateAsync(
                    device,
                    new DeviceStateChange(Brightness: brightness),
                    cancellationToken),
                "disable" => await SetEnabledAsync(deviceId, false, cancellationToken),
                "enable" => await SetEnabledAsync(deviceId, true, cancellationToken),
                "remove" => await RemoveAsync(deviceId, cancellationToken),
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
        CancellationToken cancellationToken)
    {
        try
        {
            await homeStateStore.UpdateAsync(state =>
            {
                if (!state.DevicesById.TryGetValue(deviceId, out var device) ||
                    device.DeviceType != "hue.light")
                {
                    throw new InvalidOperationException("The Hue light does not exist.");
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

    public string FormatState(DeviceState? state) =>
        state is null
            ? "Unknown"
            : $"{(state.IsOn is true ? "On" : "Off")}, {state.Brightness?.ToString() ?? "unknown"}%";

    private async Task<DeviceOperationResult> SetEnabledAsync(
        Guid deviceId,
        bool enabled,
        CancellationToken cancellationToken)
    {
        await hueHubs.SetDeviceEnabledAsync(deviceId, enabled, cancellationToken);
        return new DeviceOperationResult(DeviceOperationStatus.Succeeded);
    }

    private async Task<DeviceOperationResult> RemoveAsync(
        Guid deviceId,
        CancellationToken cancellationToken)
    {
        await hueHubs.RemoveDeviceAsync(deviceId, cancellationToken);
        return new DeviceOperationResult(DeviceOperationStatus.Succeeded);
    }

    private void LoadDevices()
    {
        Devices = hueHubs.GetMappedLights();
        Locations = homeStateStore.Current.LocationsById.Values
            .OrderBy(location => location.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
