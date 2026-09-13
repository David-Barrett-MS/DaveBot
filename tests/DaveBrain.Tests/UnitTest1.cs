using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace DaveBrain.Tests;

public sealed class MtlsListenerConfigurationTests
{
    [Fact]
    public void ProductionConfigurationRequiresDedicatedMtlsListener()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["Kestrel:Endpoints:Https:Url"] = "https://192.168.0.113:443",
        });

        var exception = Assert.Throws<InvalidOperationException>(() =>
            MtlsListenerConfiguration.Validate(
                configuration, isDevelopment: false, new Uri("https://brain.daves.tips"), 8443));

        Assert.Contains("DaveEarMtls:Url", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionConfigurationAcceptsSeparatedListeners()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["Kestrel:Endpoints:Https:Url"] = "https://192.168.0.113:443",
            ["Kestrel:Endpoints:Https:Certificate:Subject"] = "brain.daves.tips",
            ["Kestrel:Endpoints:Https:Certificate:Store"] = "My",
            ["Kestrel:Endpoints:Https:Certificate:Location"] = "LocalMachine",
            ["Kestrel:Endpoints:Https:Certificate:AllowInvalid"] = "false",
            ["Kestrel:Endpoints:DaveEarMtls:Url"] = "https://192.168.0.113:8443",
            ["Kestrel:Endpoints:DaveEarMtls:ClientCertificateMode"] = "RequireCertificate",
            ["Kestrel:Endpoints:DaveEarMtls:Certificate:Subject"] = "brain.daves.tips",
            ["Kestrel:Endpoints:DaveEarMtls:Certificate:Store"] = "My",
            ["Kestrel:Endpoints:DaveEarMtls:Certificate:Location"] = "LocalMachine",
            ["Kestrel:Endpoints:DaveEarMtls:Certificate:AllowInvalid"] = "false",
        });

        MtlsListenerConfiguration.Validate(
            configuration, isDevelopment: false, new Uri("https://brain.daves.tips"), 8443);
    }

    [Fact]
    public void ProductionConfigurationRejectsDifferentListenerCertificates()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["Kestrel:Endpoints:Https:Url"] = "https://192.168.0.113:443",
            ["Kestrel:Endpoints:Https:Certificate:Subject"] = "other.daves.tips",
            ["Kestrel:Endpoints:Https:Certificate:Store"] = "My",
            ["Kestrel:Endpoints:Https:Certificate:Location"] = "LocalMachine",
            ["Kestrel:Endpoints:Https:Certificate:AllowInvalid"] = "false",
            ["Kestrel:Endpoints:DaveEarMtls:Url"] = "https://192.168.0.113:8443",
            ["Kestrel:Endpoints:DaveEarMtls:ClientCertificateMode"] = "RequireCertificate",
            ["Kestrel:Endpoints:DaveEarMtls:Certificate:Subject"] = "brain.daves.tips",
            ["Kestrel:Endpoints:DaveEarMtls:Certificate:Store"] = "My",
            ["Kestrel:Endpoints:DaveEarMtls:Certificate:Location"] = "LocalMachine",
            ["Kestrel:Endpoints:DaveEarMtls:Certificate:AllowInvalid"] = "false",
        });

        var exception = Assert.Throws<InvalidOperationException>(() =>
            MtlsListenerConfiguration.Validate(
                configuration, isDevelopment: false, new Uri("https://brain.daves.tips"), 8443));

        Assert.Contains("same server TLS certificate", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(443, false)]
    [InlineData(8443, true)]
    public void EarRequestsAreLimitedToTheMtlsListener(int localPort, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Connection.LocalPort = localPort;

        Assert.Equal(expected, EarRequestAuthorization.IsMtlsListener(context, 8443));
    }

    private static IConfiguration Configuration(
        IReadOnlyDictionary<string, string?> values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
}
