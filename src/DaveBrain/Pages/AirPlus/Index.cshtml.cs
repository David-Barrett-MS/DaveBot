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
    IHomeStateStore homeStateStore) : PageModel
{
    private const string ProviderType = "philips.airplus";

    public sealed class AirPlusDeviceImportSelection
    {
        public bool Selected { get; set; }

        public string DeviceId { get; set; } = string.Empty;

        public string DisplayName { get; set; } = string.Empty;

        public Guid? LocationId { get; set; }
    }

    public IReadOnlyList<Hub> Hubs { get; private set; } = Array.Empty<Hub>();

    public IReadOnlyList<Device> Devices { get; private set; } = Array.Empty<Device>();

    public IReadOnlyList<Location> Locations { get; private set; } = Array.Empty<Location>();

    public IReadOnlyList<PhilipsAirPlusDeviceCandidate> Candidates { get; private set; } =
        Array.Empty<PhilipsAirPlusDeviceCandidate>();

    public Guid? CandidateHubId { get; private set; }

    public string? VerificationId { get; private set; }

    public string? PendingHubName { get; private set; }

    public DeviceOperationResult? Result { get; private set; }

    public void OnGet() => Load();

    public async Task<IActionResult> OnPostRequestEmailVerificationAsync(
        string hubName,
        string email,
        CancellationToken cancellationToken)
    {
        Load();
        if (!TryGetAuthenticatedUserId(out var userId))
        {
            return Challenge();
        }

        try
        {
            var result = await airPlus.RequestEmailVerificationAsync(
                userId,
                email,
                cancellationToken);
            if (!result.Status.Equals(DeviceOperationStatus.Succeeded) ||
                string.IsNullOrWhiteSpace(result.VerificationId))
            {
                ModelState.AddModelError(string.Empty, result.Detail ?? result.Status.ToString());
                return Page();
            }

            VerificationId = result.VerificationId;
            PendingHubName = hubName;
            return Page();
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostCompleteEmailVerificationAsync(
        string hubName,
        string verificationId,
        string code,
        CancellationToken cancellationToken)
    {
        if (!TryGetAuthenticatedUserId(out var userId))
        {
            return Challenge();
        }

        try
        {
            var result = await airPlus.CompleteEmailVerificationAsync(
                userId,
                verificationId,
                code,
                hubName,
                cancellationToken);
            if (!result.Status.Equals(DeviceOperationStatus.Succeeded))
            {
                ModelState.AddModelError(string.Empty, result.Detail ?? result.Status.ToString());
                Load();
                VerificationId = verificationId;
                PendingHubName = hubName;
                return Page();
            }

            TempData["StatusMessage"] = "Philips Air+ account linked.";
            return RedirectToPage();
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            Load();
            VerificationId = verificationId;
            PendingHubName = hubName;
            return Page();
        }
    }

    public async Task<IActionResult> OnPostCheckDevicesAsync(
        Guid hubId,
        CancellationToken cancellationToken)
    {
        Load();
        CandidateHubId = hubId;
        await LoadCandidatesAsync(hubId, cancellationToken);
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

    public async Task<IActionResult> OnPostImportSelectedAsync(
        Guid hubId,
        List<AirPlusDeviceImportSelection>? selections,
        CancellationToken cancellationToken)
    {
        var selected = (selections ?? []).Where(selection => selection.Selected).ToArray();
        if (selected.Length == 0)
        {
            ModelState.AddModelError(string.Empty, "Select at least one Philips Air+ device to import.");
            Load();
            CandidateHubId = hubId;
            await LoadCandidatesAsync(hubId, cancellationToken);
            return Page();
        }

        var imported = 0;
        var failed = 0;
        foreach (var selection in selected)
        {
            var result = await ImportAsync(hubId, selection, cancellationToken);
            if (!result.Succeeded)
            {
                failed++;
                ModelState.AddModelError(
                    string.Empty,
                    $"{selection.DisplayName}: {result.Detail ?? result.Status.ToString()}");
                continue;
            }

            imported++;
        }

        if (failed > 0)
        {
            ModelState.AddModelError(
                string.Empty,
                $"Imported {imported} of {selected.Length} selected Philips Air+ device(s).");
            Load();
            CandidateHubId = hubId;
            await LoadCandidatesAsync(hubId, cancellationToken);
            return Page();
        }

        TempData["StatusMessage"] = $"Imported {imported} Philips Air+ device(s).";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostImportSingleAsync(
        Guid hubId,
        List<AirPlusDeviceImportSelection>? selections,
        int selectionIndex,
        CancellationToken cancellationToken)
    {
        if (selections is null || selectionIndex < 0 || selectionIndex >= selections.Count)
        {
            ModelState.AddModelError(string.Empty, "The selected Philips Air+ device could not be found.");
            Load();
            CandidateHubId = hubId;
            await LoadCandidatesAsync(hubId, cancellationToken);
            return Page();
        }

        var selection = selections[selectionIndex];
        var result = await ImportAsync(hubId, selection, cancellationToken);
        if (!result.Succeeded)
        {
            ModelState.AddModelError(string.Empty, result.Detail ?? result.Status.ToString());
            Load();
            CandidateHubId = hubId;
            await LoadCandidatesAsync(hubId, cancellationToken);
            return Page();
        }

        TempData["StatusMessage"] = $"Imported {selection.DisplayName.Trim()}.";
        return RedirectToPage();
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

    public string FormatState(DeviceState? state) =>
        PhilipsAirPlusSensorFormatter.FormatState(state);

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

    private async Task<DeviceOperationResult> ImportAsync(
        Guid hubId,
        AirPlusDeviceImportSelection selection,
        CancellationToken cancellationToken)
    {
        try
        {
            return await airPlus.ImportDeviceAsync(
                hubId,
                selection.DeviceId,
                selection.DisplayName,
                selection.LocationId,
                cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            return new DeviceOperationResult(DeviceOperationStatus.InvalidState, Detail: exception.Message);
        }
    }

    private async Task LoadCandidatesAsync(Guid hubId, CancellationToken cancellationToken)
    {
        var result = await airPlus.GetUnmappedImportCandidatesAsync(hubId, cancellationToken);
        if (!result.Succeeded)
        {
            ModelState.AddModelError(string.Empty, result.Detail ?? result.Status.ToString());
            return;
        }

        Candidates = result.Devices;
    }

    private void Load()
    {
        Hubs = airPlus.GetHubs();
        Devices = airPlus.GetMappedDevices();
        Locations = homeStateStore.Current.LocationsById.Values
            .OrderBy(location => location.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

}
