using System.Security.Claims;
using DaveIdentity;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;

namespace DaveBrain.Pages.Connect;

[Authorize(Policy = DaveIdentityPolicies.EarManagement)]
public sealed class VerifyModel : PageModel
{
    [BindProperty(SupportsGet = true, Name = "user_code")]
    public string UserCode { get; init; } = string.Empty;

    public void OnGet()
    {
    }

    public IActionResult OnPost(bool approved)
    {
        var request = HttpContext.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The device verification request is unavailable.");
        if (!approved)
        {
            return Forbid(
                new AuthenticationProperties(),
                OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        var subject = User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new InvalidOperationException("The authenticated user does not have an identifier.");
        var identity = new ClaimsIdentity(
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
            ClaimTypes.Name,
            ClaimTypes.Role);
        identity.AddClaim(OpenIddictConstants.Claims.Subject, subject);
        identity.AddClaim(OpenIddictConstants.Claims.Name, User.Identity?.Name ?? subject);
        identity.AddClaim("client_id", request.ClientId ?? string.Empty);
        identity.SetDestinations(claim => claim.Type is
            OpenIddictConstants.Claims.Subject or "client_id"
                ? [OpenIddictConstants.Destinations.AccessToken]
                : Array.Empty<string>());
        var principal = new ClaimsPrincipal(identity);
        principal.SetScopes(request.GetScopes());
        return SignIn(
            principal,
            new AuthenticationProperties(),
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }
}
