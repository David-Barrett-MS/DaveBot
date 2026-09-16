using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DaveEar;

namespace DaveEar.Tests;

public sealed class ServiceAndOidcTests
{
    [Fact]
    public async Task DiscoveryRejectsNonHttpsEndpoints()
    {
        var handler = new StubHandler(_ => Json("""{"issuer":"https://brain.example.test","device_authorization_endpoint":"http://brain.example.test/connect/device","token_endpoint":"https://brain.example.test/connect/token"}"""));
        var client = new DaveEarEnrollmentClient(new HttpClient(handler));

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.DiscoverAsync(new Uri("https://brain.example.test"), CancellationToken.None));
    }

    [Fact]
    public async Task DeviceAuthorizationRequestsOfflineAccess()
    {
        var handler = new StubHandler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            Assert.Contains("scope=offline_access+davebrain.ear.enroll+davebrain.ear.command", body);
            return Json("""
                {
                  "device_code":"device",
                  "user_code":"user",
                  "verification_uri":"https://brain.example.test/verify",
                  "expires_in":600
                }
                """);
        });
        var client = new DaveEarEnrollmentClient(new HttpClient(handler));

        var authorization = await client.StartDeviceAuthorizationAsync(
            new OidcDiscovery(
                new Uri("https://brain.example.test"),
                new Uri("https://brain.example.test/device"),
                new Uri("https://brain.example.test/token")),
            "DaveEar",
            CancellationToken.None);

        Assert.Equal("device", authorization.DeviceCode);
    }

    [Fact]
    public async Task DevicePollingHandlesPendingThenReturnsToken()
    {
        var count = 0;
        var handler = new StubHandler(_ =>
        {
            count++;
            return count == 1
                ? Json("""{"error":"authorization_pending"}""", HttpStatusCode.BadRequest)
                : Json("""{"access_token":"access","refresh_token":"refresh","expires_in":600}""");
        });
        var client = new DaveEarEnrollmentClient(new HttpClient(handler));
        var authorization = new DeviceAuthorization("device", "user", new Uri("https://brain.example.test/verify"), null, 1, DateTimeOffset.UtcNow.AddSeconds(3));

        var token = await client.AwaitDeviceAuthorizationAsync(
            new OidcDiscovery(new Uri("https://brain.example.test"), new Uri("https://brain.example.test/device"), new Uri("https://brain.example.test/token")),
            "DaveEar", authorization, _ => { }, CancellationToken.None);

        Assert.Equal("access", token.AccessToken);
        Assert.Equal("refresh", token.RefreshToken);
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task EnrollmentSendsOnlyCsrAndReturnsIssuedIdentity()
    {
        var handler = new StubHandler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("Bearer enrollment-token", request.Headers.Authorization!.ToString());
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            Assert.Contains("CERTIFICATE REQUEST", body, StringComparison.Ordinal);
            Assert.DoesNotContain("PRIVATE KEY", body, StringComparison.Ordinal);
            return Json("""{"earId":"b1cd09a6-4e57-4ee9-bbf2-7c2ecae28338","certificate":"-----BEGIN CERTIFICATE-----\nissued\n-----END CERTIFICATE-----"}""", HttpStatusCode.Created);
        });
        var client = new DaveEarEnrollmentClient(new HttpClient(handler));

        var result = await client.EnrollAsync(
            new Uri("https://brain.example.test"),
            new OAuthToken("enrollment-token", "refresh", DateTimeOffset.UtcNow.AddMinutes(10)),
            "Kitchen", Guid.NewGuid(), "-----BEGIN CERTIFICATE REQUEST-----\ncsr\n-----END CERTIFICATE REQUEST-----", CancellationToken.None);

        Assert.Equal(Guid.Parse("b1cd09a6-4e57-4ee9-bbf2-7c2ecae28338"), result.EarId);
        Assert.Contains("BEGIN CERTIFICATE", result.CertificatePem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CertificateBoundRefreshRotatesAndPersistsRefreshToken()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        var configuration = Configuration();
        Uri? refreshUri = null;
        var provider = new MtlsAccessTokenProvider(
            configuration,
            new EarCredentials(key.ExportPkcs8PrivateKeyPem(), certificate.ExportCertificatePem(), "old-refresh"),
            new OidcDiscovery(configuration.Issuer, new Uri("https://brain.example.test/device"), new Uri("https://brain.example.test/token")),
            _ => new HttpClient(new StubHandler(request =>
            {
                refreshUri = request.RequestUri;
                return Json("""{"access_token":"access","refresh_token":"new-refresh","expires_in":600}""");
            })));
        var credentials = new RecordingCredentialStore();

        Assert.Equal("access", await provider.GetAsync(CancellationToken.None));
        await provider.PersistRotatedCredentialsAsync(credentials, CancellationToken.None);

        Assert.Equal("new-refresh", credentials.Saved!.RefreshToken);
        Assert.Equal(8443, refreshUri!.Port);
    }

    [Fact]
    public void ConfigurationUsesSameHostDedicatedMtlsEndpoint()
    {
        var configuration = Configuration();

        Assert.Equal("brain.example.test", configuration.EffectiveMtlsEndpoint.Host);
        Assert.Equal(8443, configuration.EffectiveMtlsEndpoint.Port);
        Assert.Throws<InvalidOperationException>(() => (configuration with
        {
            MtlsEndpoint = new Uri("https://other.example.test:8443"),
        }).Validate());
    }

    [Fact]
    public async Task RetrySpoolLimitsBytesAndDoesNotReturnHeldUpload()
    {
        var root = Path.Combine(Directory.GetCurrentDirectory(), $"ear-test-{Guid.NewGuid():N}");
        try
        {
            var paths = new DaveEarPaths(root);
            var configuration = Configuration() with { MaximumSpoolBytes = 100_000 };
            var spool = new RetrySpool(paths, configuration);
            Assert.True(await spool.SaveForRetryAsync(Guid.NewGuid(), new byte[50_000], 1, TimeSpan.Zero, CancellationToken.None));
            Assert.False(await spool.SaveForRetryAsync(Guid.NewGuid(), new byte[60_000], 1, TimeSpan.Zero, CancellationToken.None));
            var held = Guid.NewGuid();
            Assert.True(await spool.SaveHeldAsync(held, new byte[100], CancellationToken.None));

            var due = await spool.GetDueAsync(CancellationToken.None);
            Assert.Single(due);
            Assert.NotEqual(held, due[0].Item.SegmentId);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ServiceHoldsAmbiguousUploadWithoutResending()
    {
        var root = Path.Combine(Directory.GetCurrentDirectory(), $"ear-test-{Guid.NewGuid():N}");
        try
        {
            var configuration = Configuration();
            var credentials = new EarCredentials("private", "certificate", "refresh");
            var provider = new MtlsAccessTokenProvider(
                configuration, credentials,
                new OidcDiscovery(configuration.Issuer, new Uri("https://brain.example.test/device"), new Uri("https://brain.example.test/token")));
            var service = new DaveEarService(
                configuration, new EmptyCapture(), new EnergyVoiceActivityDetector(1), new PcmWavEncoder(),
                new ResultUploader(new UploadResult(UploadDisposition.Ambiguous, "network-outcome-unknown")),
                new RetrySpool(new DaveEarPaths(root), configuration), provider, new MemoryCredentialStore());
            var segment = new AudioSegment(Guid.NewGuid(), new float[16_000], 16_000, DateTimeOffset.UtcNow, false);
            var handle = typeof(DaveEarService).GetMethod("HandleUploadAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            await (Task)handle.Invoke(service, [segment.Id, new PcmWavEncoder().Encode(segment), 0, (Action<string>)(_ => { }), CancellationToken.None])!;

            Assert.Equal(1, service.HeldSegments);
            Assert.Empty(await new RetrySpool(new DaveEarPaths(root), configuration).GetDueAsync(CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static DaveEarConfiguration Configuration() =>
        DaveEarConfiguration.Create(new Uri("https://brain.example.test"), "test", Guid.NewGuid(), Guid.NewGuid());

    private static HttpResponseMessage Json(string text, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(text) };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }

    private sealed class EmptyCapture : IAudioCapture
    {
        public async IAsyncEnumerable<AudioFrame> CaptureAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
        public Task<AudioCaptureDiagnostic> DiagnoseAsync(CancellationToken cancellationToken) => Task.FromResult(new AudioCaptureDiagnostic(true, "test"));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ResultUploader(UploadResult result) : ICommandUploader
    {
        public Task<UploadResult> UploadAsync(Guid earId, Guid segmentId, ReadOnlyMemory<byte> wav, CancellationToken cancellationToken) => Task.FromResult(result);
        public Task<HeartbeatResult> HeartbeatAsync(Guid earId, CancellationToken cancellationToken) => Task.FromResult(new HeartbeatResult(true, "enabled"));
    }

    private sealed class MemoryCredentialStore : ICredentialStore
    {
        public Task SaveAsync(EarCredentials credentials, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<EarCredentials?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult<EarCredentials?>(null);
        public Task DeleteAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingCredentialStore : ICredentialStore
    {
        public EarCredentials? Saved { get; private set; }
        public Task SaveAsync(EarCredentials credentials, CancellationToken cancellationToken = default)
        {
            Saved = credentials;
            return Task.CompletedTask;
        }
        public Task<EarCredentials?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult<EarCredentials?>(Saved);
        public Task DeleteAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
