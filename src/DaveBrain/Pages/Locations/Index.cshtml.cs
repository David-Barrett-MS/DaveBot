using System.ComponentModel.DataAnnotations;
using DaveHome;
using DaveIdentity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DaveBrain.Pages.Locations;

[Authorize(Policy = DaveIdentityPolicies.DashboardRead)]
public sealed class IndexModel(
    IHomeStateStore homeStateStore,
    IAuthorizationService authorization) : PageModel
{
    public IReadOnlyList<Location> Locations { get; private set; } = Array.Empty<Location>();

    [BindProperty]
    [StringLength(200)]
    [Display(Name = "Location name")]
    public string NewLocationName { get; init; } = string.Empty;

    public void OnGet() => LoadLocations();

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!await IsAdministratorAsync())
        {
            return Forbid();
        }

        if (!ModelState.IsValid)
        {
            LoadLocations();
            return Page();
        }

        try
        {
            await homeStateStore.UpdateAsync(state => state.Document with
            {
                Locations = state.Document.Locations.Append(
                    new Location(Guid.NewGuid(), NewLocationName.Trim(), null)).ToArray(),
            }, homeStateStore.Current.Revision, cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            LoadLocations();
            return Page();
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRenameAsync(
        Guid id,
        string name,
        CancellationToken cancellationToken)
    {
        if (!await IsAdministratorAsync())
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(name) || name.Length > 200)
        {
            ModelState.AddModelError(string.Empty, "A location name from 1 to 200 characters is required.");
            LoadLocations();
            return Page();
        }

        try
        {
            await homeStateStore.UpdateAsync(state =>
            {
                if (state.Document.Locations.All(location => location.Id != id))
                {
                    throw new InvalidOperationException("The location does not exist.");
                }

                return state.Document with
                {
                    Locations = state.Document.Locations.Select(location => location.Id == id
                        ? location with { Name = name.Trim() }
                        : location).ToArray(),
                };
            }, homeStateStore.Current.Revision, cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            LoadLocations();
            return Page();
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!await IsAdministratorAsync())
        {
            return Forbid();
        }

        try
        {
            await homeStateStore.UpdateAsync(state =>
            {
                if (state.Document.Locations.All(location => location.Id != id))
                {
                    throw new InvalidOperationException("The location does not exist.");
                }

                if (state.Document.Locations.Any(location => location.ParentLocationId == id) ||
                    state.Document.Devices.Any(device => device.LocationId == id))
                {
                    throw new InvalidOperationException(
                        "Locations with child locations or devices cannot be removed.");
                }

                return state.Document with
                {
                    Locations = state.Document.Locations
                        .Where(location => location.Id != id)
                        .ToArray(),
                };
            }, homeStateStore.Current.Revision, cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            LoadLocations();
            return Page();
        }

        return RedirectToPage();
    }

    private void LoadLocations() =>
        Locations = homeStateStore.Current.LocationsById.Values
            .OrderBy(location => location.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private async Task<bool> IsAdministratorAsync() =>
        (await authorization.AuthorizeAsync(User, DaveIdentityPolicies.ConfigurationManagement)).Succeeded;
}
