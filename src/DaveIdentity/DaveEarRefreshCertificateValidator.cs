using OpenIddict.Abstractions;
using OpenIddict.Server;

namespace DaveIdentity;

public sealed class DaveEarRefreshCertificateValidator(
    EarEnrollmentService enrollmentService)
    : IOpenIddictServerHandler<OpenIddictServerEvents.ProcessSignInContext>
{
    public async ValueTask HandleAsync(OpenIddictServerEvents.ProcessSignInContext context)
    {
        if (!string.Equals(context.ClientId, "DaveEar", StringComparison.Ordinal) ||
            !string.Equals(
                context.Request.GrantType,
                OpenIddictConstants.GrantTypes.RefreshToken,
                StringComparison.Ordinal))
        {
            return;
        }

        var certificate = context.Transaction.RemoteCertificate;
        if (certificate is null)
        {
            context.Reject(
                OpenIddictConstants.Errors.InvalidGrant,
                "A client certificate is required when refreshing DaveEar tokens.");
            return;
        }

        var principal = context.Principal
            ?? throw new InvalidOperationException(
                "The DaveEar refresh token principal is unavailable.");
        var subject = principal.GetClaim(OpenIddictConstants.Claims.Subject);
        if (string.IsNullOrWhiteSpace(subject) ||
            !await enrollmentService.IsBoundCertificateForOAuthSubjectAsync(
                "DaveEar",
                subject,
                certificate,
                context.CancellationToken))
        {
            context.Reject(
                OpenIddictConstants.Errors.InvalidGrant,
                "The DaveEar client certificate is not valid for this refresh token.");
            return;
        }

    }
}
