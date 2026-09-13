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
    public Guid HubId { get; private set; }

    public IReadOnlyList<HueLightResource> Candidates { get; private set; } =
        Array.Empty<HueLightResource>();

    public IReadOnlyList<Location> Locations { get; private set; } = Array.Empty<Location>();

    public bool RetrievedCandidates { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid hubId, CancellationToken cancellationToken)
    {
        HubId = hubId;
        await LoadAsync(cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(
        Guid hubId,
        string resourceId,
        string displayName,
        Guid? locationId,
        CancellationToken cancellationToken)
    {
        HubId = hubId;
        try
        {
            var result = await hueHubs.ImportLightAsync(
                hubId,
                resourceId,
                displayName,
                locationId,
                cancellationToken);
            if (!result.Succeeded)
            {
                ModelState.AddModelError(string.Empty, result.Detail ?? result.Status.ToString());
                await LoadAsync(cancellationToken);
                return Page();
            }
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            await LoadAsync(cancellationToken);
            return Page();
        }

        return RedirectToPage("/Devices/Index");
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var result = await hueHubs.GetUnmappedLightImportCandidatesAsync(HubId, cancellationToken);
        Candidates = result.Lights;
        RetrievedCandidates = result.Succeeded;
        if (!result.Succeeded && result.Detail is { } detail)
        {
            ModelState.AddModelError(string.Empty, detail);
        }

        Locations = homeStateStore.Current.LocationsById.Values
            .OrderBy(location => location.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
