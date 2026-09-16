using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace DaveIdentity;

public sealed record EarEnrollmentResult(EarRecord Ear, string CertificatePem);

public sealed class EarEnrollmentService(
    IdentityStateStore stateStore,
    EarCertificateAuthority certificateAuthority)
{
    public async Task<EarEnrollmentResult> EnrollAsync(
        string name,
        Guid locationId,
        string oauthIssuer,
        string oauthSubject,
        string certificateSigningRequestPem,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200)
        {
            throw new ArgumentException("An Ear name from 1 to 200 characters is required.", nameof(name));
        }

        if (locationId == Guid.Empty || string.IsNullOrWhiteSpace(oauthIssuer) ||
            string.IsNullOrWhiteSpace(oauthSubject))
        {
            throw new ArgumentException("A location, OAuth issuer, and OAuth subject are required.");
        }

        var request = CertificateRequest.LoadSigningRequestPem(
            certificateSigningRequestPem,
            HashAlgorithmName.SHA256,
            CertificateRequestLoadOptions.Default,
            RSASignaturePadding.Pkcs1);
        using var certificate = certificateAuthority.IssueClientCertificate(
            request,
            DateTimeOffset.UtcNow.AddDays(90));
        var ear = new EarRecord(
            Guid.NewGuid(),
            name.Trim(),
            locationId,
            OAuthIssuer: oauthIssuer,
            OAuthSubject: oauthSubject,
            OAuthClientId: "DaveEar",
            MtlsCertificateThumbprint: EarCertificateAuthority.CalculateThumbprint(certificate),
            CreatedUtc: DateTimeOffset.UtcNow,
            LastSeenUtc: null,
            Enabled: true);
        await stateStore.UpdateAsync(document => document with
        {
            Ears = document.Ears.Append(ear).ToArray(),
        }, cancellationToken);

        return new EarEnrollmentResult(ear, certificate.ExportCertificatePem());
    }

    public async Task<bool> IsBoundCertificateAsync(
        Guid earId,
        X509Certificate2 certificate,
        CancellationToken cancellationToken = default)
    {
        var ear = await stateStore.FindEarAsync(earId, cancellationToken);
        return ear is { Enabled: true } &&
            certificateAuthority.ValidateClientCertificate(
                certificate,
                ear.MtlsCertificateThumbprint);
    }

    public async Task<bool> IsBoundCertificateForOAuthSubjectAsync(
        string clientId,
        string subject,
        X509Certificate2 certificate,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentNullException.ThrowIfNull(certificate);

        foreach (var ear in GetEars())
        {
            if (!string.Equals(ear.OAuthClientId, clientId, StringComparison.Ordinal) ||
                !string.Equals(ear.OAuthSubject, subject, StringComparison.Ordinal))
            {
                continue;
            }

            if (await IsBoundCertificateAsync(ear.Id, certificate, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    public IReadOnlyList<EarRecord> GetEars() => stateStore.GetEars();

    public async Task RecordHeartbeatAsync(Guid earId, CancellationToken cancellationToken = default)
    {
        await stateStore.UpdateAsync(document => document with
        {
            Ears = document.Ears.Select(ear => ear.Id == earId
                ? ear with { LastSeenUtc = DateTimeOffset.UtcNow }
                : ear).ToArray(),
        }, cancellationToken);
    }

    public async Task RevokeAsync(Guid earId, CancellationToken cancellationToken = default)
    {
        await stateStore.UpdateAsync(document =>
        {
            if (document.Ears.All(ear => ear.Id != earId))
            {
                throw new InvalidOperationException("The Ear does not exist.");
            }

            return document with
            {
                Ears = document.Ears.Select(ear => ear.Id == earId
                    ? ear with { Enabled = false }
                    : ear).ToArray(),
            };
        }, cancellationToken);
    }

    public async Task<EarEnrollmentResult> RenewAsync(
        Guid earId,
        string certificateSigningRequestPem,
        CancellationToken cancellationToken = default)
    {
        var ear = await stateStore.FindEarAsync(earId, cancellationToken)
            ?? throw new InvalidOperationException("The Ear does not exist.");
        if (!ear.Enabled)
        {
            throw new InvalidOperationException("A revoked Ear cannot renew its certificate.");
        }

        var request = CertificateRequest.LoadSigningRequestPem(
            certificateSigningRequestPem,
            HashAlgorithmName.SHA256,
            CertificateRequestLoadOptions.Default,
            RSASignaturePadding.Pkcs1);
        using var certificate = certificateAuthority.IssueClientCertificate(
            request,
            DateTimeOffset.UtcNow.AddDays(90));
        var renewed = ear with
        {
            MtlsCertificateThumbprint = EarCertificateAuthority.CalculateThumbprint(certificate),
        };
        await stateStore.UpdateAsync(document => document with
        {
            Ears = document.Ears.Select(candidate => candidate.Id == earId
                ? renewed
                : candidate).ToArray(),
        }, cancellationToken);
        return new EarEnrollmentResult(renewed, certificate.ExportCertificatePem());
    }
}
