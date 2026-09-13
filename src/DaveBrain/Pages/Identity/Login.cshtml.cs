using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using DaveIdentity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DaveBrain.Pages.Identity;

[AllowAnonymous]
public sealed class LoginModel(LocalUserService users) : PageModel
{
    [BindProperty]
    public LoginInput Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var attempt = await users.ValidatePasswordAsync(
            Input.UserName,
            Input.Password,
            cancellationToken);
        if (attempt.Result is not DaveIdentity.SignInResult.Succeeded || attempt.User is null)
        {
            ModelState.AddModelError(string.Empty, "The username or password is incorrect.");
            return Page();
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, attempt.User.Id.ToString()),
            new(ClaimTypes.Name, attempt.User.UserName),
            new("security_stamp", attempt.User.SecurityStamp),
            new("must_change_password", attempt.User.MustChangePassword.ToString().ToLowerInvariant()),
        };
        claims.AddRange(attempt.User.Roles.Select(role => new Claim(ClaimTypes.Role, role)));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            claims,
            CookieAuthenticationDefaults.AuthenticationScheme));
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties { IsPersistent = false });

        return LocalRedirect(attempt.User.MustChangePassword
            ? "/Identity/ChangePassword"
            : Url.IsLocalUrl(ReturnUrl) ? ReturnUrl! : "/");
    }

    public sealed class LoginInput
    {
        [Required]
        [StringLength(256)]
        [Display(Name = "Username")]
        public string UserName { get; set; } = string.Empty;

        [Required]
        [DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;
    }
}
