using DaveHome;
using DaveIdentity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DaveBrain.Pages.Hubs;

[Authorize(Policy = DaveIdentityPolicies.HubManagement)]
public sealed class ImportModel(
    HueHubService hueHubs,
    IHomeStateStore homeStateStore) : PageModel
{
    public sealed class HueDeviceImportSelection
    {
        public bool Selected { get; set; }

        public string ResourceId { get; set; } = string.Empty;

        public string DeviceType { get; set; } = string.Empty;

        public string DisplayName { get; set; } = string.Empty;

        public Guid? LocationId { get; set; }
    }

    public Guid HubId { get; private set; }

    public IReadOnlyList<HueLightResource> Candidates { get; private set; } =
        Array.Empty<HueLightResource>();

    public IReadOnlyList<HueSwitchResource> SwitchCandidates { get; private set; } =
        Array.Empty<HueSwitchResource>();

    public IReadOnlyList<HueMotionSensorResource> MotionSensorCandidates { get; private set; } =
        Array.Empty<HueMotionSensorResource>();

    public IReadOnlyList<Location> Locations { get; private set; } = Array.Empty<Location>();

    public bool RetrievedCandidates { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid hubId, CancellationToken cancellationToken)
    {
        HubId = hubId;
        await LoadAsync(cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostImportSelectedAsync(
        Guid hubId,
        List<HueDeviceImportSelection>? selections,
        CancellationToken cancellationToken)
    {
        HubId = hubId;
        var selected = (selections ?? []).Where(selection => selection.Selected).ToArray();
        if (selected.Length == 0)
        {
            ModelState.AddModelError(string.Empty, "Select at least one Hue device to import.");
            await LoadAsync(cancellationToken);
            return Page();
        }

        var imported = 0;
        var failed = 0;
        foreach (var selection in selected)
        {
            var result = await ImportAsync(hubId, selection, cancellationToken);
            if (result.Succeeded)
            {
                imported++;
            }
            else
            {
                failed++;
                ModelState.AddModelError(
                    string.Empty,
                    $"{selection.DisplayName}: {result.Detail ?? result.Status.ToString()}");
            }
        }

        if (failed > 0)
        {
            ModelState.AddModelError(
                string.Empty,
                $"Imported {imported} of {selected.Length} selected Hue device(s).");
            await LoadAsync(cancellationToken);
            return Page();
        }

        TempData["StatusMessage"] = $"Imported {imported} Hue device(s).";
        return RedirectToPage("/Devices/Index");
    }

    public async Task<IActionResult> OnPostImportSingleAsync(
        Guid hubId,
        List<HueDeviceImportSelection>? selections,
        int selectionIndex,
        CancellationToken cancellationToken)
    {
        HubId = hubId;
        if (selections is null || selectionIndex < 0 || selectionIndex >= selections.Count)
        {
            ModelState.AddModelError(string.Empty, "The selected Hue device could not be found.");
            await LoadAsync(cancellationToken);
            return Page();
        }

        var selection = selections[selectionIndex];
        var result = await ImportAsync(hubId, selection, cancellationToken);
        if (!result.Succeeded)
        {
            ModelState.AddModelError(string.Empty, result.Detail ?? result.Status.ToString());
            await LoadAsync(cancellationToken);
            return Page();
        }

        TempData["StatusMessage"] = $"Imported {selection.DisplayName}.";
        return RedirectToPage("/Devices/Index");
    }

    private async Task<DeviceOperationResult> ImportAsync(
        Guid hubId,
        HueDeviceImportSelection selection,
        CancellationToken cancellationToken)
    {
        try
        {
            return selection.DeviceType switch
            {
                "hue.switch" => await hueHubs.ImportSwitchAsync(
                    hubId,
                    selection.ResourceId,
                    selection.DisplayName,
                    cancellationToken),
                "hue.motion-sensor" => await hueHubs.ImportMotionSensorAsync(
                    hubId,
                    selection.ResourceId,
                    selection.DisplayName,
                    selection.LocationId,
                    cancellationToken),
                "hue.light" => await hueHubs.ImportLightAsync(
                    hubId,
                    selection.ResourceId,
                    selection.DisplayName,
                    selection.LocationId,
                    cancellationToken),
                _ => new DeviceOperationResult(
                    DeviceOperationStatus.InvalidState,
                    Detail: "The selected Hue device type is not supported."),
            };
        }
        catch (InvalidOperationException exception)
        {
            return new DeviceOperationResult(DeviceOperationStatus.InvalidState, Detail: exception.Message);
        }
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var lights = await hueHubs.RefreshMappedLightsAsync(HubId, cancellationToken);
        var switches = await hueHubs.GetUnmappedSwitchImportCandidatesAsync(HubId, cancellationToken);
        var motionSensors = await hueHubs.GetUnmappedMotionSensorImportCandidatesAsync(
            HubId,
            cancellationToken);
        var mappedLightResourceIds = homeStateStore.Current.Document.Devices
            .Where(device => device.HubId == HubId && device.DeviceType == "hue.light")
            .Select(device => device.ExternalResourceId)
            .Where(resourceId => !string.IsNullOrWhiteSpace(resourceId))
            .ToHashSet(StringComparer.Ordinal);
        Candidates = lights.Lights
            .Where(light => !mappedLightResourceIds.Contains(light.ResourceId))
            .ToArray();
        SwitchCandidates = switches.Switches;
        MotionSensorCandidates = motionSensors.MotionSensors;
        RetrievedCandidates = lights.Succeeded && switches.Succeeded && motionSensors.Succeeded;
        if (!lights.Succeeded && lights.Detail is { } detail)
        {
            ModelState.AddModelError(string.Empty, detail);
        }
        if (!switches.Succeeded && switches.Detail is { } switchDetail)
        {
            ModelState.AddModelError(string.Empty, switchDetail);
        }
        if (!motionSensors.Succeeded && motionSensors.Detail is { } motionSensorDetail)
        {
            ModelState.AddModelError(string.Empty, motionSensorDetail);
        }

        Locations = homeStateStore.Current.LocationsById.Values
            .OrderBy(location => location.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
