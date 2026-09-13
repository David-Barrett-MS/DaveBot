using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using DaveIdentity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DaveBrain.Pages.Identity;

[Authorize]
public sealed class ChangePasswordModel(LocalUserService users) : PageModel
{
    [BindProperty]
    [Required]
    [DataType(DataType.Password)]
    [Display(Name = "Initial password")]
    public string CurrentPassword { get; set; } = string.Empty;

    [BindProperty]
    [Required]
    [StringLength(128, MinimumLength = 8)]
    [DataType(DataType.Password)]
    [Display(Name = "New password")]
    public string NewPassword { get; set; } = string.Empty;

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var userIdText = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdText, out var userId))
        {
            return Forbid();
        }

        try
        {
            await users.CompleteInitialPasswordChangeAsync(
                userId,
                CurrentPassword,
                NewPassword,
                cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            return Page();
        }

        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToPage("/Identity/Login");
    }
}
