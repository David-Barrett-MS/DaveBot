using System.ComponentModel.DataAnnotations;
using DaveIdentity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DaveBrain.Pages.Identity;

[Authorize(Policy = DaveIdentityPolicies.ConfigurationManagement)]
public sealed class UsersModel(LocalUserService users) : PageModel
{
    public IReadOnlyList<IdentityUserRecord> Users { get; private set; } = Array.Empty<IdentityUserRecord>();

    [BindProperty]
    [Required]
    [StringLength(256)]
    [Display(Name = "Username")]
    public string NewUserName { get; init; } = string.Empty;

    [BindProperty]
    [Required]
    [DataType(DataType.Password)]
    [Display(Name = "Password")]
    public string NewPassword { get; init; } = string.Empty;

    [BindProperty]
    [Required]
    [Display(Name = "Role")]
    public string NewUserRole { get; init; } = DaveRoles.User;

    public void OnGet() => LoadUsers();

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            LoadUsers();
            return Page();
        }

        try
        {
            await users.CreateUserAsync(NewUserName, NewPassword, [NewUserRole], cancellationToken);
        }
        catch (ArgumentException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            LoadUsers();
            return Page();
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            LoadUsers();
            return Page();
        }

        return RedirectToPage();
    }

    private void LoadUsers() => Users = users.GetUsers();
}
