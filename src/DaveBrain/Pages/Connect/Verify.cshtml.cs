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

    public async Task<IActionResult> OnPostAsync(bool approved)
    {
        if (!approved)
        {
            return Forbid(
                new AuthenticationProperties(),
                OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        var subject = User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new InvalidOperationException("The authenticated user does not have an identifier.");
        var result = await HttpContext.AuthenticateAsync(
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        var verificationPrincipal = result.Principal
            ?? throw new InvalidOperationException("The device verification principal is unavailable.");
        var verificationIdentity = verificationPrincipal.Identity as ClaimsIdentity
            ?? throw new InvalidOperationException("The device verification identity is unavailable.");
        var deviceCodeId = verificationPrincipal.GetClaim(
            OpenIddictConstants.Claims.Private.DeviceCodeId)
            ?? throw new InvalidOperationException(
                "The device verification principal does not contain a device-code identifier.");
        var identity = new ClaimsIdentity(
            verificationIdentity.Claims,
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
            ClaimTypes.Name,
            ClaimTypes.Role);
        var principal = new ClaimsPrincipal(identity);

        // The authenticated principal carries OpenIddict's internal device-code identifier.
        identity.SetClaim(OpenIddictConstants.Claims.Private.DeviceCodeId, deviceCodeId);
        identity.SetClaim(OpenIddictConstants.Claims.Subject, subject);
        identity.SetClaim(OpenIddictConstants.Claims.Name, User.Identity?.Name ?? subject);
        identity.SetDestinations(claim => claim.Type is
            OpenIddictConstants.Claims.Subject
                ? [OpenIddictConstants.Destinations.AccessToken]
                : Array.Empty<string>());
        principal.SetScopes(verificationPrincipal.GetScopes());
        return SignIn(
            principal,
            new AuthenticationProperties(),
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }
}
