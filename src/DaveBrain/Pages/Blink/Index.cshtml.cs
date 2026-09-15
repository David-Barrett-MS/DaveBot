using System.Security.Claims;
using DaveHome;
using DaveIdentity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DaveBrain.Pages.Blink;

[Authorize(Policy = DaveIdentityPolicies.HubManagement)]
public sealed class IndexModel(BlinkHubService blink, IHomeStateStore homeStateStore) : PageModel
{
    public sealed class CameraSelection
    {
        public bool Selected { get; set; }
        public string CameraId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public Guid? LocationId { get; set; }
    }

    public IReadOnlyList<Hub> Hubs { get; private set; } = Array.Empty<Hub>();
    public IReadOnlyList<Device> Cameras { get; private set; } = Array.Empty<Device>();
    public IReadOnlyList<Location> Locations { get; private set; } = Array.Empty<Location>();
    public IReadOnlyList<BlinkCameraCandidate> Candidates { get; private set; } = Array.Empty<BlinkCameraCandidate>();
    public Guid? CandidateHubId { get; private set; }
    public string? VerificationId { get; private set; }
    public DeviceOperationResult? Result { get; private set; }

    public void OnGet() => Load();

    public async Task<IActionResult> OnPostConnectAsync(
        string hubName,
        string email,
        string password,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
        {
            return Challenge();
        }

        var result = await blink.ConnectAsync(userId, email, password, hubName, cancellationToken);
        if (result.RequiresVerification && result.VerificationId is not null)
        {
            VerificationId = result.VerificationId;
            Load();
            return Page();
        }

        if (result.Status != DeviceOperationStatus.Succeeded)
        {
            ModelState.AddModelError(string.Empty, result.Detail ?? result.Status.ToString());
            Load();
            return Page();
        }

        TempData["StatusMessage"] = "Blink account linked.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostCompleteVerificationAsync(
        string verificationId,
        string code,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
        {
            return Challenge();
        }

        var result = await blink.CompleteVerificationAsync(userId, verificationId, code, cancellationToken);
        if (result.Status != DeviceOperationStatus.Succeeded)
        {
            ModelState.AddModelError(string.Empty, result.Detail ?? result.Status.ToString());
            VerificationId = verificationId;
            Load();
            return Page();
        }

        TempData["StatusMessage"] = "Blink account linked.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostCheckCamerasAsync(Guid hubId, CancellationToken cancellationToken)
    {
        CandidateHubId = hubId;
        Load();
        await LoadCandidatesAsync(hubId, cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostImportSelectedAsync(
        Guid hubId,
        List<CameraSelection>? selections,
        CancellationToken cancellationToken)
    {
        var selected = (selections ?? []).Where(selection => selection.Selected).ToArray();
        if (selected.Length == 0)
        {
            ModelState.AddModelError(string.Empty, "Select at least one Blink camera to import.");
            CandidateHubId = hubId;
            Load();
            await LoadCandidatesAsync(hubId, cancellationToken);
            return Page();
        }

        var imported = 0;
        foreach (var selection in selected)
        {
            var result = await blink.ImportCameraAsync(
                hubId, selection.CameraId, selection.DisplayName, selection.LocationId, cancellationToken);
            if (result.Succeeded)
            {
                imported++;
                continue;
            }

            ModelState.AddModelError(
                string.Empty,
                $"{selection.DisplayName}: {result.Detail ?? result.Status.ToString()}");
        }

        if (imported != selected.Length)
        {
            CandidateHubId = hubId;
            Load();
            await LoadCandidatesAsync(hubId, cancellationToken);
            return Page();
        }

        TempData["StatusMessage"] = $"Imported {imported} Blink camera(s).";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostUnlinkAsync(Guid hubId, CancellationToken cancellationToken)
    {
        try
        {
            await blink.RemoveHubAsync(hubId, cancellationToken);
            TempData["StatusMessage"] = "Blink account unlinked.";
            return RedirectToPage();
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            Load();
            return Page();
        }
    }

    private async Task LoadCandidatesAsync(Guid hubId, CancellationToken cancellationToken)
    {
        var result = await blink.GetUnmappedCameraCandidatesAsync(hubId, cancellationToken);
        if (!result.Succeeded)
        {
            ModelState.AddModelError(string.Empty, result.Detail ?? result.Status.ToString());
            return;
        }

        Candidates = result.Cameras;
    }

    private void Load()
    {
        Hubs = blink.GetHubs();
        Cameras = blink.GetMappedDevices();
        Locations = homeStateStore.Current.LocationsById.Values
            .OrderBy(location => location.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private bool TryGetUserId(out string userId)
    {
        userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        return !string.IsNullOrWhiteSpace(userId);
    }
}
