using System.Security.Cryptography.X509Certificates;

namespace DaveIdentity;

public sealed class MtlsCertificateBindingValidator(EarEnrollmentService enrollmentService)
{
    public async Task<bool> IsValidAsync(
        Guid earId,
        X509Certificate2 certificate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return await enrollmentService.IsBoundCertificateAsync(
            earId,
            certificate,
            cancellationToken);
    }
}
