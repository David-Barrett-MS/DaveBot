using System.Security.Claims;
using DaveHome;
using DaveIdentity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DaveBrain.Pages.AirPlus;

[Authorize(Policy = DaveIdentityPolicies.HubManagement)]
public sealed class IndexModel(
    PhilipsAirPlusHubService airPlus,
    PhilipsAirPlusOptions options,
    IHomeStateStore homeStateStore) : PageModel
{
    private const string ProviderType = "philips.airplus";

    public IReadOnlyList<Hub> Hubs { get; private set; } = Array.Empty<Hub>();

    public IReadOnlyList<Device> Devices { get; private set; } = Array.Empty<Device>();

    public IReadOnlyList<Location> Locations { get; private set; } = Array.Empty<Location>();

    public IReadOnlyList<PhilipsAirPlusDeviceCandidate> Candidates { get; private set; } =
        Array.Empty<PhilipsAirPlusDeviceCandidate>();

    public Guid? CandidateHubId { get; private set; }

    public string? AuthorizationUri { get; private set; }

    public string? AuthorizationState { get; private set; }

    public string? PendingHubName { get; private set; }

    public DeviceOperationResult? Result { get; private set; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(options.ClientId) &&
        !string.IsNullOrWhiteSpace(options.ClientSecret) &&
        !string.IsNullOrWhiteSpace(options.RedirectUri);

    public void OnGet() => Load();

    public async Task<IActionResult> OnPostBeginAuthorizationAsync(
        string hubName,
        CancellationToken cancellationToken)
    {
        Load();
        if (!TryGetAuthenticatedUserId(out var userId))
        {
            return Challenge();
        }

        try
        {
            var result = await airPlus.BeginAuthorizationAsync(userId, cancellationToken);
            if (!result.Status.Equals(DeviceOperationStatus.Succeeded) ||
                result.AuthorizationUri is null ||
                string.IsNullOrWhiteSpace(result.State))
            {
                ModelState.AddModelError(string.Empty, result.Detail ?? result.Status.ToString());
                return Page();
            }

            AuthorizationUri = result.AuthorizationUri.AbsoluteUri;
            AuthorizationState = result.State;
            PendingHubName = hubName;
            return Page();
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostCompleteAuthorizationAsync(
        string hubName,
        string redirectUrlOrCode,
        string state,
        CancellationToken cancellationToken)
    {
        if (!TryGetAuthenticatedUserId(out var userId))
        {
            return Challenge();
        }

        try
        {
            var result = await airPlus.CompleteAuthorizationAsync(
                userId,
                redirectUrlOrCode,
                state,
                hubName,
                cancellationToken);
            if (!result.Status.Equals(DeviceOperationStatus.Succeeded))
            {
                ModelState.AddModelError(string.Empty, result.Detail ?? result.Status.ToString());
                Load();
                return Page();
            }

            TempData["StatusMessage"] = "Philips Air+ account linked.";
            return RedirectToPage();
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            Load();
            return Page();
        }
    }

    public async Task<IActionResult> OnPostCheckDevicesAsync(
        Guid hubId,
        CancellationToken cancellationToken)
    {
        Load();
        CandidateHubId = hubId;
        var result = await airPlus.GetUnmappedImportCandidatesAsync(hubId, cancellationToken);
        if (!result.Succeeded)
        {
            ModelState.AddModelError(string.Empty, result.Detail ?? result.Status.ToString());
            return Page();
        }

        Candidates = result.Devices;
        return Page();
    }

    public async Task<IActionResult> OnPostHubActionAsync(
        Guid hubId,
        string action,
        CancellationToken cancellationToken)
    {
        try
        {
            switch (action)
            {
                case "disable":
                    await airPlus.SetHubEnabledAsync(hubId, false, cancellationToken);
                    Result = new DeviceOperationResult(DeviceOperationStatus.Succeeded);
                    break;
                case "enable":
                    await airPlus.SetHubEnabledAsync(hubId, true, cancellationToken);
                    Result = new DeviceOperationResult(DeviceOperationStatus.Succeeded);
                    break;
                case "remove":
                    await airPlus.RemoveHubAsync(hubId, cancellationToken);
                    TempData["StatusMessage"] = "Philips Air+ account unlinked.";
                    return RedirectToPage();
                default:
                    Result = new DeviceOperationResult(DeviceOperationStatus.InvalidState);
                    break;
            }
        }
        catch (InvalidOperationException exception)
        {
            Result = new DeviceOperationResult(DeviceOperationStatus.InvalidState, Detail: exception.Message);
        }

        Load();
        return Page();
    }

    public async Task<IActionResult> OnPostImportAsync(
        Guid hubId,
        string deviceId,
        string displayName,
        Guid? locationId,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await airPlus.ImportDeviceAsync(
                hubId,
                deviceId,
                displayName,
                locationId,
                cancellationToken);
            if (!result.Succeeded)
            {
                ModelState.AddModelError(string.Empty, result.Detail ?? result.Status.ToString());
                Load();
                CandidateHubId = hubId;
                return Page();
            }

            TempData["StatusMessage"] = $"Imported {displayName.Trim()}.";
            return RedirectToPage();
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            Load();
            CandidateHubId = hubId;
            return Page();
        }
    }

    public async Task<IActionResult> OnPostDeviceActionAsync(
        Guid deviceId,
        string action,
        int? fanSpeed,
        CancellationToken cancellationToken)
    {
        var device = airPlus.GetMappedDevices().SingleOrDefault(candidate => candidate.Id == deviceId);
        if (device is null)
        {
            ModelState.AddModelError(string.Empty, "The Philips Air+ device does not exist.");
            Load();
            return Page();
        }

        try
        {
            Result = action switch
            {
                "read" => await airPlus.ReadDeviceStateAsync(device, cancellationToken),
                "set-fan-speed" when fanSpeed is not null => await airPlus.SetFanSpeedAsync(
                    device,
                    fanSpeed.Value,
                    cancellationToken),
                "disable" => await SetDeviceEnabledAsync(deviceId, false, cancellationToken),
                "enable" => await SetDeviceEnabledAsync(deviceId, true, cancellationToken),
                "remove" => await RemoveDeviceAsync(deviceId, cancellationToken),
                _ => new DeviceOperationResult(DeviceOperationStatus.InvalidState),
            };
        }
        catch (InvalidOperationException exception)
        {
            Result = new DeviceOperationResult(DeviceOperationStatus.InvalidState, Detail: exception.Message);
        }

        Load();
        return Page();
    }

    public async Task<IActionResult> OnPostUpdateLocationAsync(
        Guid deviceId,
        Guid? locationId,
        CancellationToken cancellationToken)
    {
        try
        {
            await airPlus.SetDeviceLocationAsync(deviceId, locationId, cancellationToken);
            Result = new DeviceOperationResult(DeviceOperationStatus.Succeeded);
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
        }

        Load();
        return Page();
    }

    public string FormatState(DeviceState? state)
    {
        if (state is null)
        {
            return "Unknown";
        }

        var readings = new List<string>();
        if (state.IsOn is { } isOn)
        {
            readings.Add(isOn ? "On" : "Off");
        }

        if (state.Attributes is { } attributes)
        {
            foreach (var attribute in attributes.OrderBy(attribute => attribute.Key, StringComparer.Ordinal))
            {
                readings.Add($"{GetSensorLabel(attribute.Key)}: {attribute.Value}");
            }
        }

        return readings.Count == 0 ? "No readings reported" : string.Join("; ", readings);
    }

    private async Task<DeviceOperationResult> SetDeviceEnabledAsync(
        Guid deviceId,
        bool enabled,
        CancellationToken cancellationToken)
    {
        await airPlus.SetDeviceEnabledAsync(deviceId, enabled, cancellationToken);
        return new DeviceOperationResult(DeviceOperationStatus.Succeeded);
    }

    private async Task<DeviceOperationResult> RemoveDeviceAsync(
        Guid deviceId,
        CancellationToken cancellationToken)
    {
        await airPlus.RemoveDeviceAsync(deviceId, cancellationToken);
        return new DeviceOperationResult(DeviceOperationStatus.Succeeded);
    }

    private bool TryGetAuthenticatedUserId(out string userId)
    {
        userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        return !string.IsNullOrWhiteSpace(userId);
    }

    private void Load()
    {
        Hubs = airPlus.GetHubs();
        Devices = airPlus.GetMappedDevices();
        Locations = homeStateStore.Current.LocationsById.Values
            .OrderBy(location => location.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string GetSensorLabel(string attribute) => attribute switch
    {
        "pm25UgM3" => "PM2.5 (ug/m3)",
        "allergenIndex" => "Allergen index",
        "airQualityIndex" => "Air quality index",
        "airQualityIndexTarget" => "Air quality target",
        "gasLevel" => "Gas level",
        "temperatureC" => "Temperature (C)",
        "humidityPercent" => "Humidity (%)",
        "humiditySetpointPercent" => "Humidity setpoint (%)",
        "filterStatus0" => "Filter 0 (%)",
        "filterStatus1" => "Filter 1 (%)",
        "filterStatus2" => "Filter 2 (%)",
        "productState" => "Product state",
        "productError" => "Product error",
        "fanSpeed" => "Fan speed",
        _ => attribute,
    };
}
