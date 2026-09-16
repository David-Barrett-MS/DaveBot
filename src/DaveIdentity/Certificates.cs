using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace DaveIdentity;

public sealed class EarCertificateAuthority
{
    private readonly X509Certificate2 _certificateAuthority;

    public EarCertificateAuthority(X509Certificate2 certificateAuthority)
    {
        ArgumentNullException.ThrowIfNull(certificateAuthority);
        if (!certificateAuthority.HasPrivateKey || !certificateAuthority.Subject.Contains("DaveBot Ear Client CA", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The Ear certificate authority must have its private key and expected subject.",
                nameof(certificateAuthority));
        }

        _certificateAuthority = certificateAuthority;
    }

    public string Thumbprint => CalculateThumbprint(_certificateAuthority);

    public bool ValidateClientCertificate(X509Certificate2 certificate, string expectedThumbprint)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        if (!string.Equals(CalculateThumbprint(certificate), expectedThumbprint, StringComparison.Ordinal))
        {
            return false;
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(_certificateAuthority);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
        return chain.Build(certificate);
    }

    public X509Certificate2 IssueClientCertificate(
        RSA publicKey,
        string commonName,
        DateTimeOffset notAfter)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        if (string.IsNullOrWhiteSpace(commonName) || notAfter <= DateTimeOffset.UtcNow)
        {
            throw new ArgumentException("A certificate subject and future expiry are required.", nameof(commonName));
        }

        var request = new CertificateRequest(
            $"CN={commonName.Trim()}",
            publicKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(
            certificateAuthority: false,
            hasPathLengthConstraint: false,
            pathLengthConstraint: 0,
            critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature,
            critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.2")],
            critical: true));
        return request.Create(
            _certificateAuthority,
            DateTimeOffset.UtcNow.AddMinutes(-5),
            notAfter,
            RandomNumberGenerator.GetBytes(16));
    }

    public X509Certificate2 IssueClientCertificate(
        CertificateRequest signingRequest,
        DateTimeOffset notAfter)
    {
        ArgumentNullException.ThrowIfNull(signingRequest);
        if (notAfter <= DateTimeOffset.UtcNow ||
            signingRequest.PublicKey.Oid?.Value != "1.2.840.113549.1.1.1")
        {
            throw new ArgumentException(
                "The certificate request must contain an RSA public key and a future expiry.",
                nameof(signingRequest));
        }

        var request = new CertificateRequest(
            signingRequest.SubjectName,
            signingRequest.PublicKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(
            certificateAuthority: false,
            hasPathLengthConstraint: false,
            pathLengthConstraint: 0,
            critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature,
            critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.2")],
            critical: true));
        return request.Create(
            _certificateAuthority,
            DateTimeOffset.UtcNow.AddMinutes(-5),
            notAfter,
            RandomNumberGenerator.GetBytes(16));
    }

    public static EarCertificateAuthority CreateEphemeralForDevelopment()
    {
        using var key = RSA.Create(3072);
        var request = new CertificateRequest(
            "CN=DaveBot Ear Client CA",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(
            certificateAuthority: true,
            hasPathLengthConstraint: false,
            pathLengthConstraint: 0,
            critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
            critical: true));
        return new EarCertificateAuthority(request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(5)));
    }

    public static string CalculateThumbprint(X509Certificate2 certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate.RawData));
}

public static class CertificateStoreLoader
{
    public static X509Certificate2 LoadLocalMachineCertificate(string thumbprint)
    {
        if (string.IsNullOrWhiteSpace(thumbprint))
        {
            throw new ArgumentException("A certificate thumbprint is required.", nameof(thumbprint));
        }

        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        var certificates = store.Certificates.Find(
            X509FindType.FindByThumbprint,
            thumbprint.Replace(" ", string.Empty, StringComparison.Ordinal),
            validOnly: false);
        if (certificates.Count != 1 || !certificates[0].HasPrivateKey)
        {
            throw new InvalidOperationException(
                $"A single certificate with private key was not found for thumbprint '{thumbprint}'.");
        }

        return certificates[0];
    }
}
