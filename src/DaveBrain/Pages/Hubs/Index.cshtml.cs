using System.ComponentModel.DataAnnotations;
using DaveHome;
using DaveIdentity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DaveBrain.Pages.Hubs;

[Authorize(Policy = DaveIdentityPolicies.HubManagement)]
public sealed class IndexModel(
    HueHubService hueHubs,
    HueListenerDebugLog listenerDebugLog) : PageModel
{
    public IReadOnlyList<Hub> Hubs { get; private set; } = Array.Empty<Hub>();

    public IReadOnlyList<HueBridgeDiscovery> Discovered { get; private set; } =
        Array.Empty<HueBridgeDiscovery>();

    [BindProperty]
    [Required]
    [StringLength(200)]
    [Display(Name = "Bridge name")]
    public string BridgeName { get; set; } = string.Empty;

    [BindProperty]
    [Required]
    [Url]
    [Display(Name = "Bridge HTTPS address")]
    public string BridgeAddress { get; set; } = string.Empty;

    [BindProperty]
    public string? BridgeId { get; set; }

    public void OnGet() => LoadHubs();

    public IActionResult OnGetListenerEvents(long after = 0)
    {
        Response.Headers.CacheControl = "no-store";
        return new JsonResult(listenerDebugLog.ReadAfter(Math.Max(0, after)));
    }

    public async Task<IActionResult> OnPostDiscoverAsync(CancellationToken cancellationToken)
    {
        ModelState.Clear();
        Discovered = await hueHubs.DiscoverAsync(cancellationToken);
        LoadHubs();
        return Page();
    }

    public async Task<IActionResult> OnPostLinkAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid || !Uri.TryCreate(BridgeAddress, UriKind.Absolute, out var address) ||
            address.Scheme != Uri.UriSchemeHttps)
        {
            ModelState.AddModelError(string.Empty, "A valid bridge HTTPS address is required.");
            LoadHubs();
            return Page();
        }

        var result = await hueHubs.LinkAsync(BridgeName, address, BridgeId, cancellationToken);
        if (!result.Status.Equals(DeviceOperationStatus.Succeeded))
        {
            ModelState.AddModelError(string.Empty, result.Detail ?? result.Status.ToString());
            LoadHubs();
            return Page();
        }

        TempData["StatusMessage"] =
            $"Bridge linked. Imported {result.ImportedLightCount} new Hue light(s) and " +
            $"{result.ImportedSwitchCount} Hue switch button(s).";
        return RedirectToPage();
    }

    private void LoadHubs() => Hubs = hueHubs.GetHubs();
}
