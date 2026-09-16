using System.Security.Cryptography.X509Certificates;

namespace DaveEar;

internal static class EarClientCertificateLoader
{
    public static X509Certificate2 Load(string certificatePem, string privateKeyPem)
    {
        var certificate = X509Certificate2.CreateFromPem(certificatePem, privateKeyPem);
        if (!OperatingSystem.IsWindows())
        {
            return certificate;
        }

        try
        {
            return X509CertificateLoader.LoadPkcs12(
                certificate.Export(X509ContentType.Pkcs12),
                password: null,
                X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
        }
        finally
        {
            certificate.Dispose();
        }
    }
}
