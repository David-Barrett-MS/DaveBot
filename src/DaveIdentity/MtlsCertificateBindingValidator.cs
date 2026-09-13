using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace DaveIdentity;

public sealed class MtlsCertificateBindingValidator(EarEnrollmentService enrollmentService)
{
    public async Task<bool> IsValidAsync(
        Guid earId,
        ClaimsPrincipal principal,
        X509Certificate2 certificate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(certificate);
        var confirmation = principal.FindFirst("cnf")?.Value;
        if (string.IsNullOrWhiteSpace(confirmation))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(confirmation);
            if (!document.RootElement.TryGetProperty("x5t#S256", out var thumbprint) ||
                thumbprint.ValueKind != JsonValueKind.String ||
                !string.Equals(
                    thumbprint.GetString(),
                    EarCertificateAuthority.CalculateThumbprint(certificate),
                    StringComparison.Ordinal))
            {
                return false;
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return await enrollmentService.IsBoundCertificateAsync(
            earId,
            certificate,
            cancellationToken);
    }
}
