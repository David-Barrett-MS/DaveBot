using System.ComponentModel.DataAnnotations;
using DaveHome;
using DaveIdentity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DaveBrain.Pages.Groups;

[Authorize(Policy = DaveIdentityPolicies.DashboardRead)]
public sealed class IndexModel(
    IHomeStateStore homeStateStore,
    IAuthorizationService authorization) : PageModel
{
    public IReadOnlyList<DeviceGroup> Groups { get; private set; } = Array.Empty<DeviceGroup>();

    [BindProperty]
    [StringLength(200)]
    [Display(Name = "Group name")]
    public string NewGroupName { get; init; } = string.Empty;

    public void OnGet() => LoadGroups();

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!await IsAdministratorAsync())
        {
            return Forbid();
        }

        if (!ModelState.IsValid)
        {
            LoadGroups();
            return Page();
        }

        try
        {
            await homeStateStore.UpdateAsync(state => state.Document with
            {
                Groups = state.Document.Groups.Append(
                    new DeviceGroup(Guid.NewGuid(), NewGroupName.Trim())).ToArray(),
            }, homeStateStore.Current.Revision, cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            LoadGroups();
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

        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 200)
        {
            ModelState.AddModelError(string.Empty, "A group name from 1 to 200 characters is required.");
            LoadGroups();
            return Page();
        }

        try
        {
            await homeStateStore.UpdateAsync(state =>
            {
                if (!state.GroupsById.ContainsKey(id))
                {
                    throw new InvalidOperationException("The group does not exist.");
                }

                return state.Document with
                {
                    Groups = state.Document.Groups.Select(group => group.Id == id
                        ? group with { Name = name.Trim() }
                        : group).ToArray(),
                };
            }, homeStateStore.Current.Revision, cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            LoadGroups();
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
                if (!state.GroupsById.ContainsKey(id))
                {
                    throw new InvalidOperationException("The group does not exist.");
                }

                if (state.Document.Devices.Any(device => device.GroupIds?.Contains(id) == true))
                {
                    throw new InvalidOperationException(
                        "Groups with device members cannot be removed. Remove all device memberships first.");
                }

                return state.Document with
                {
                    Groups = state.Document.Groups.Where(group => group.Id != id).ToArray(),
                };
            }, homeStateStore.Current.Revision, cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            LoadGroups();
            return Page();
        }

        return RedirectToPage();
    }

    public int MemberCount(DeviceGroup group) =>
        homeStateStore.Current.Document.Devices.Count(device => device.GroupIds?.Contains(group.Id) == true);

    private void LoadGroups() =>
        Groups = homeStateStore.Current.GroupsById.Values
            .OrderBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private async Task<bool> IsAdministratorAsync() =>
        (await authorization.AuthorizeAsync(User, DaveIdentityPolicies.ConfigurationManagement)).Succeeded;
}
