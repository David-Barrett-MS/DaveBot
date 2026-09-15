using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using DaveHome;

namespace DaveHome.Tests;

public sealed class BlinkClientTests
{
    [Fact]
    public async Task CameraDiscoveryUsesBearerAuthorizationAndParsesAllCameraTypes()
    {
        var handler = new RecordingHandler("""
            {
              "cameras":[{"id":1,"name":"Front","type":"xt"}],
              "owls":[{"id":2,"name":"Kitchen","type":"mini"}],
              "doorbells":[{"id":3,"name":"Door","type":"doorbell"}]
            }
            """);
        var client = CreateClient(handler);
        var token = new BlinkAccessToken(
            "access", "refresh", DateTimeOffset.UtcNow.AddHours(1), "hardware", "account", "https://rest-e002.immedia-semi.com/");

        var result = await client.GetCamerasAsync(token);

        Assert.True(result.Succeeded);
        Assert.Equal("/api/v3/accounts/account/homescreen", handler.Path);
        Assert.Equal("Bearer", handler.Authorization!.Scheme);
        Assert.Equal("access", handler.Authorization.Parameter);
        Assert.Equal(string.Empty, handler.UserAgent);
        Assert.Equal("application/json", handler.ContentType);
        Assert.Equal(["Door", "Front", "Kitchen"], result.Value!.Select(camera => camera.Name));
    }

    [Fact]
    public async Task CameraDiscoveryRetriesWithTokenOnlyAfterBearerUnauthorized()
    {
        var handler = new TokenFallbackCameraHandler();
        var client = CreateClient(handler);
        var token = new BlinkAccessToken(
            "access", "refresh", DateTimeOffset.UtcNow.AddHours(1), "hardware", "account", "https://rest-e002.immedia-semi.com/");

        var result = await client.GetCamerasAsync(token);

        Assert.True(result.Succeeded);
        Assert.Equal(["Bearer", "Token"], handler.AuthorizationSchemes);
        Assert.Equal(["Front"], result.Value!.Select(camera => camera.Name));
    }

    [Fact]
    public async Task SnapshotRetrievalRequestsAFreshThumbnailBeforeDownloadingIt()
    {
        var handler = new SnapshotHandler();
        var client = CreateClient(handler);
        var token = new BlinkAccessToken(
            "access", "refresh", DateTimeOffset.UtcNow.AddHours(1), "hardware", "account", "https://rest-e002.immedia-semi.com/");

        var result = await client.GetSnapshotAsync(token, "1");

        Assert.True(result.Succeeded);
        Assert.Equal("image/jpeg", result.Snapshot!.ContentType);
        Assert.Equal([1, 2, 3], result.Snapshot.Content);
        Assert.Equal(
            [
                "/api/v3/accounts/account/homescreen",
                "/network/2/camera/1/thumbnail",
                "/network/2/command/3",
                "/api/v3/accounts/account/homescreen",
                "/api/v3/media/accounts/account/networks/2/xt/1/thumbnail/thumbnail.jpg",
            ],
            handler.Paths);
        Assert.Equal(["Bearer", "Bearer", "Bearer", "Bearer", "Bearer"], handler.AuthorizationSchemes);
        Assert.StartsWith("?ts=123&ext=&cache_bust=", handler.ThumbnailQuery);
        Assert.Equal("no-cache", handler.MediaCacheControl);
        Assert.Equal("no-cache", handler.MediaPragma);
    }

    [Fact]
    public async Task SnapshotRetrievalRejectsOversizedImages()
    {
        var handler = new SnapshotHandler([1, 2, 3]);
        var client = CreateClient(handler, new BlinkOptions { MaximumSnapshotBytes = 2 });
        var token = new BlinkAccessToken(
            "access", "refresh", DateTimeOffset.UtcNow.AddHours(1), "hardware", "account", "https://rest-e002.immedia-semi.com/");

        var result = await client.GetSnapshotAsync(token, "1");

        Assert.Equal(DeviceOperationStatus.InvalidState, result.Status);
        Assert.Equal("The Blink snapshot is too large to display.", result.Detail);
    }

    [Fact]
    public async Task SnapshotRetrievalSupportsLegacyRelativeThumbnailPaths()
    {
        var handler = new SnapshotHandler(thumbnailJson: "\"/media/production/account/account/network/2/camera/1/thumb_001\"");
        var client = CreateClient(handler);
        var token = new BlinkAccessToken(
            "access", "refresh", DateTimeOffset.UtcNow.AddHours(1), "hardware", "account", "https://rest-e002.immedia-semi.com/");

        var result = await client.GetSnapshotAsync(token, "1");

        Assert.True(result.Succeeded);
        Assert.Equal(
            "/media/production/account/account/network/2/camera/1/thumb_001.jpg",
            handler.Paths[4]);
    }

    [Fact]
    public async Task SnapshotRetrievalReportsWhenCameraDetailsCannotRequestASnapshot()
    {
        var handler = new SnapshotHandler(thumbnailJson: "\"\"", networkJson: "null");
        var client = CreateClient(handler);
        var token = new BlinkAccessToken(
            "access", "refresh", DateTimeOffset.UtcNow.AddHours(1), "hardware", "account", "https://rest-e002.immedia-semi.com/");

        var result = await client.GetSnapshotAsync(token, "1");

        Assert.Equal(DeviceOperationStatus.InvalidState, result.Status);
        Assert.Equal(
            "Blink did not provide the camera details required to request a snapshot.",
            result.Detail);
        Assert.Single(handler.Paths);
    }

    [Fact]
    public async Task SnapshotRetrievalRequestsAndWaitsForADoorbellThumbnailWhenNoCacheExists()
    {
        var handler = new SnapshotCaptureHandler();
        var client = CreateClient(
            handler,
            new BlinkOptions { SnapshotCommandPollAttempts = 1 });
        var token = new BlinkAccessToken(
            "access", "refresh", DateTimeOffset.UtcNow.AddHours(1), "hardware", "account", "https://rest-e002.immedia-semi.com/");

        var result = await client.GetSnapshotAsync(token, "1");

        Assert.True(result.Succeeded);
        Assert.Equal([1, 2, 3], result.Snapshot!.Content);
        Assert.Equal(
            [
                "/api/v3/accounts/account/homescreen",
                "/api/v1/accounts/account/networks/2/doorbells/1/thumbnail",
                "/network/2/command/3",
                "/api/v3/accounts/account/homescreen",
                "/api/v3/media/accounts/account/networks/2/lotus/1/thumbnail/thumbnail.jpg",
            ],
            handler.Paths);
        Assert.Equal(["Bearer", "Bearer", "Bearer", "Bearer", "Bearer"], handler.AuthorizationSchemes);
    }

    [Fact]
    public async Task SnapshotRetrievalRetriesMediaDownloadWithTokenAfterBearerUnauthorized()
    {
        var handler = new SnapshotHandler(requireTokenForImage: true);
        var client = CreateClient(handler);
        var token = new BlinkAccessToken(
            "access", "refresh", DateTimeOffset.UtcNow.AddHours(1), "hardware", "account", "https://rest-e002.immedia-semi.com/");

        var result = await client.GetSnapshotAsync(token, "1");

        Assert.True(result.Succeeded);
        Assert.Equal(["Bearer", "Bearer", "Bearer", "Bearer", "Bearer", "Token"], handler.AuthorizationSchemes);
    }

    [Fact]
    public async Task TokenRefreshPreservesBlinkAccountIdentity()
    {
        var handler = new RecordingHandler("""{"access_token":"new-access","refresh_token":"new-refresh","expires_in":3600}""");
        var client = CreateClient(handler);
        var token = new BlinkAccessToken(
            "access", "refresh", DateTimeOffset.UtcNow, "hardware", "account", "https://rest-e002.immedia-semi.com/");

        var result = await client.RefreshAsync(token);

        Assert.True(result.Succeeded);
        Assert.Equal("/oauth/token", handler.Path);
        Assert.Contains("grant_type=refresh_token", handler.RequestBody, StringComparison.Ordinal);
        Assert.Contains("hardware_id=hardware", handler.RequestBody, StringComparison.Ordinal);
        Assert.Equal("new-access", result.Token!.AccessToken);
        Assert.Equal("account", result.Token.AccountId);
        Assert.Equal("https://rest-e002.immedia-semi.com/", result.Token.ApiBaseAddress);
    }

    [Fact]
    public async Task ConnectionUsesBrowserNavigationHeadersAndCompletesOAuthFlow()
    {
        var registration = new ConnectionFlowHandler();
        var tier = new RecordingHandler("""{"tier":"e002","account_id":"account"}""");
        var client = CreateClient(tier, new TestRegistrationHttpClientFactory(registration));

        var result = await client.ConnectAsync("admin@example.test", "password");

        Assert.True(result.Succeeded);
        Assert.Equal("access", result.Token!.AccessToken);
        Assert.Contains(
            registration.InitialAuthorization.Accept,
            value => value.Contains("text/html", StringComparison.Ordinal));
        Assert.Contains(
            registration.InitialAuthorization.AcceptLanguage,
            value => value.StartsWith("en-US", StringComparison.Ordinal));
        Assert.Equal("Bearer", tier.Authorization!.Scheme);
        Assert.Equal("27.0ANDROID_28373244", tier.UserAgent);
        Assert.Equal("application/x-www-form-urlencoded", tier.ContentType);
    }

    [Fact]
    public async Task ConnectionRetriesTierLookupWithTokenOnlyAfterBearerUnauthorized()
    {
        var tier = new TokenFallbackTierHandler();
        var client = CreateClient(
            tier,
            new TestRegistrationHttpClientFactory(new ConnectionFlowHandler()));

        var result = await client.ConnectAsync("admin@example.test", "password");

        Assert.True(result.Succeeded);
        Assert.Equal(["Bearer", "Token"], tier.AuthorizationSchemes);
    }

    [Fact]
    public async Task AuthorizationRejectionIncludesOnlyStatusInResult()
    {
        var client = CreateClient(
            new RecordingHandler("{}"),
            new TestRegistrationHttpClientFactory(new RejectionHandler()));

        var result = await client.ConnectAsync("admin@example.test", "password");

        Assert.Equal(DeviceOperationStatus.Unauthorized, result.Status);
        Assert.Equal(
            "Blink authorization could not be started. HTTP 401 (Unauthorized).",
            result.Detail);
    }

    [Fact]
    public async Task VerificationRequiresCompletedStatusAndUsesOAuthContract()
    {
        var verification = new VerificationFlowHandler("auth-completed");
        var tier = new RecordingHandler("""{"tier":"e002","account_id":"account"}""");
        var client = CreateClient(tier, new TestRegistrationHttpClientFactory(verification));

        var result = await client.CompleteVerificationAsync("123456", CreateVerificationTicket());

        Assert.True(result.Succeeded);
        Assert.Equal("Mozilla/5.0 (iPhone; CPU iPhone OS 18_7 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/26.1 Mobile/15E148 Safari/604.1",
            verification.VerificationRequest!.UserAgent);
        Assert.Equal("*/*", verification.VerificationRequest.Accept);
        Assert.Equal("https://api.oauth.blink.com", verification.VerificationRequest.Origin);
        Assert.Equal("https://api.oauth.blink.com/oauth/v2/signin", verification.VerificationRequest.Referrer);
        Assert.Equal("application/x-www-form-urlencoded", verification.VerificationRequest.ContentType);
        Assert.Equal("Blink/2511191620 CFNetwork/3860.200.71 Darwin/25.1.0", verification.TokenRequest!.UserAgent);
        Assert.Equal("*/*", verification.TokenRequest.Accept);
        Assert.Equal("application/x-www-form-urlencoded", verification.TokenRequest.ContentType);
    }

    [Fact]
    public async Task VerificationDoesNotAcceptIncompleteCreatedResponse()
    {
        var client = CreateClient(
            new RecordingHandler("{}"),
            new TestRegistrationHttpClientFactory(new VerificationFlowHandler("pending")));

        var result = await client.CompleteVerificationAsync("123456", CreateVerificationTicket());

        Assert.Equal(DeviceOperationStatus.InvalidState, result.Status);
        Assert.Equal("Blink did not complete verification.", result.Detail);
    }

    [Theory]
    [InlineData("cameras", "default", "/api/v6/accounts/account/networks/2/cameras/1/liveview")]
    [InlineData("owls", "mini", "/api/v2/accounts/account/networks/2/owls/1/liveview")]
    [InlineData("doorbells", "doorbell", "/api/v2/accounts/account/networks/2/doorbells/1/liveview")]
    public async Task LiveViewUsesTheCameraSpecificPathAndDoesNotExposeUpstreamFields(
        string collection,
        string type,
        string expectedPath)
    {
        var handler = new LiveViewHandler(collection, type);
        var client = CreateClient(handler);
        var token = new BlinkAccessToken(
            "access", "refresh", DateTimeOffset.UtcNow.AddHours(1), "hardware", "account",
            "https://rest-e002.immedia-semi.com/");

        var result = await client.StartLiveViewAsync(token, "1");

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Stream);
        Assert.Equal(expectedPath, handler.LiveViewPath);
        Assert.Equal("""{"intent":"liveview","motion_event_start_time":null}""", handler.LiveViewBody);
        Assert.Equal(["Bearer"], handler.LiveViewAuthorizationSchemes);
        await result.Stream!.StopAsync();
        Assert.Equal("/network/2/command/command-1/done/", handler.CompletionPath);
        Assert.Equal("Bearer", handler.CompletionAuthorizationScheme);
    }

    [Fact]
    public async Task LiveViewRetriesWithTokenOnlyAfterBearerUnauthorized()
    {
        var handler = new LiveViewHandler("cameras", "default", rejectBearer: true);
        var client = CreateClient(handler);
        var token = new BlinkAccessToken(
            "access", "refresh", DateTimeOffset.UtcNow.AddHours(1), "hardware", "account",
            "https://rest-e002.immedia-semi.com/");

        var result = await client.StartLiveViewAsync(token, "1");

        Assert.True(result.Succeeded);
        Assert.Equal(["Bearer", "Token"], handler.LiveViewAuthorizationSchemes);
        await result.Stream!.StopAsync();
    }

    [Fact]
    public async Task LiveViewStartsWhenBlinkOmitsUnusedSessionHints()
    {
        var handler = new LiveViewHandler(
            "cameras",
            "default",
            includeSessionHints: false,
            includeLiveViewToken: false);
        var client = CreateClient(handler);
        var token = new BlinkAccessToken(
            "access", "refresh", DateTimeOffset.UtcNow.AddHours(1), "hardware", "account",
            "https://rest-e002.immedia-semi.com/");

        var result = await client.StartLiveViewAsync(token, "1");

        Assert.True(result.Succeeded);
        await result.Stream!.StopAsync();
    }

    [Fact]
    public async Task LiveViewUsesLocalLifetimeWhenBlinkOmitsDuration()
    {
        var handler = new LiveViewHandler("cameras", "default", includeDuration: false);
        var client = CreateClient(handler);
        var token = new BlinkAccessToken(
            "access", "refresh", DateTimeOffset.UtcNow.AddHours(1), "hardware", "account",
            "https://rest-e002.immedia-semi.com/");

        var result = await client.StartLiveViewAsync(token, "1");

        Assert.True(result.Succeeded);
        Assert.Equal(TimeSpan.FromMinutes(10), result.Stream!.Duration);
        await result.Stream.StopAsync();
    }

    [Fact]
    public void ImmiEndpointParserAndGreetingRejectUnsafeHandshakeValues()
    {
        Assert.True(BlinkLiveViewProtocol.TryParseImmiEndpoint(
            "immis://live.example.test:443/connection-1__IMDS?client_id=42",
            out var endpoint));
        Assert.Equal("live.example.test", endpoint.Host);
        Assert.Equal(443, endpoint.Port);
        Assert.Equal((uint)42, endpoint.ClientId);
        Assert.Equal("connection-1", endpoint.ConnectionId);

        Assert.True(BlinkLiveViewProtocol.TryCreateGreeting("SERIAL-1", endpoint, out var greeting));
        Assert.Equal(BlinkLiveViewProtocol.GreetingLength, greeting.Length);
        Assert.Equal([0, 0, 0, 40], greeting[..4]);
        Assert.Equal([0, 0, 0, 16], greeting[4..8]);
        Assert.Equal((byte)'S', greeting[8]);
        Assert.Equal([0, 0, 0, 42], greeting[24..28]);
        Assert.Equal([1, 8], greeting[28..30]);
        Assert.Equal([0, 0, 0, 16], greeting[98..102]);
        Assert.Equal([0, 0, 0, 1], greeting[118..122]);

        Assert.False(BlinkLiveViewProtocol.TryParseImmiEndpoint(
            "immis://live.example.test:444/connection-1__IMDS?client_id=42",
            out _));
        Assert.False(BlinkLiveViewProtocol.TryParseImmiEndpoint(
            "https://live.example.test/connection-1__IMDS?client_id=42",
            out _));
        Assert.False(BlinkLiveViewProtocol.TryParseImmiEndpoint(
            "immis://live.example.test/connection-1__IMDS?client_id=not-a-number",
            out _));
        Assert.False(BlinkLiveViewProtocol.TryCreateGreeting(new string('x', 257), endpoint, out _));

        Assert.True(BlinkLiveViewProtocol.TryParseImmiEndpoint(
            "immis://live.example.test/connection-identifier-is-long__IMDS?client_id=42",
            out var longIdentifierEndpoint));
        Assert.True(BlinkLiveViewProtocol.TryCreateGreeting(
            "SERIAL-1",
            longIdentifierEndpoint,
            out var truncatedGreeting));
        Assert.Equal("connection-ident", System.Text.Encoding.ASCII.GetString(truncatedGreeting[102..118]));
    }

    [Fact]
    public void ImmiTransportPayloadStartsImmediatelyAfterTheImmiHeader()
    {
        var payload = new byte[] { 0x47, 0x40, 0x00, 0x10 };

        Assert.True(BlinkLiveViewProtocol.TryExtractTransportStreamPayload(
            0x00,
            payload,
            out var transportStream));
        Assert.Equal(payload, transportStream.ToArray());

        Assert.False(BlinkLiveViewProtocol.TryExtractTransportStreamPayload(
            0x12,
            payload,
            out _));
        Assert.False(BlinkLiveViewProtocol.TryExtractTransportStreamPayload(
            0x00,
            new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x47 },
            out _));
    }

    [Theory]
    [InlineData(SslPolicyErrors.None, true)]
    [InlineData(SslPolicyErrors.RemoteCertificateChainErrors, true)]
    [InlineData(SslPolicyErrors.RemoteCertificateNameMismatch, true)]
    [InlineData(
        SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors,
        true)]
    [InlineData(SslPolicyErrors.RemoteCertificateNotAvailable, false)]
    public void ImmiTlsValidationAcceptsOnlyTheBlinkCertificateException(
        SslPolicyErrors policyErrors,
        bool expected)
    {
        using var certificate = CreateServerCertificate();

        Assert.Equal(
            expected,
            BlinkLiveViewProtocol.IsAcceptedImmiCertificate(
                certificate,
                policyErrors,
                DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ImmiTlsValidationRejectsExpiredCertificates()
    {
        using var certificate = CreateServerCertificate();

        Assert.False(BlinkLiveViewProtocol.IsAcceptedImmiCertificate(
            certificate,
            SslPolicyErrors.RemoteCertificateChainErrors,
            DateTimeOffset.UtcNow.AddYears(2)));
    }

    private static X509Certificate2 CreateServerCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=blink-live-view-test",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        var usages = new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages, false));
        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddHours(1));
    }

    private static BlinkConnectionTicket CreateVerificationTicket() =>
        new(new CookieContainer(), "csrf", "verifier", "hardware");

    private static BlinkClient CreateClient(HttpMessageHandler handler) =>
        CreateClient(handler, new BlinkOptions(), new BlinkRegistrationHttpClientFactory());

    private static BlinkClient CreateClient(HttpMessageHandler handler, BlinkOptions options) =>
        CreateClient(handler, options, new BlinkRegistrationHttpClientFactory());

    private static BlinkClient CreateClient(
        HttpMessageHandler handler,
        IBlinkRegistrationHttpClientFactory registrationFactory) =>
        CreateClient(handler, new BlinkOptions(), registrationFactory);

    private static BlinkClient CreateClient(
        HttpMessageHandler handler,
        BlinkOptions options,
        IBlinkRegistrationHttpClientFactory registrationFactory) =>
        new(new HttpClient(handler), options, registrationFactory);

    private sealed class RecordingHandler(string body) : HttpMessageHandler
    {
        public string? Path { get; private set; }
        public AuthenticationHeaderValue? Authorization { get; private set; }
        public string? UserAgent { get; private set; }
        public string? ContentType { get; private set; }
        public string RequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Path = request.RequestUri!.AbsolutePath;
            Authorization = request.Headers.Authorization;
            UserAgent = request.Headers.UserAgent.ToString();
            ContentType = request.Content?.Headers.ContentType?.MediaType;
            RequestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        }
    }

    private sealed class TokenFallbackTierHandler : HttpMessageHandler
    {
        public List<string> AuthorizationSchemes { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            AuthorizationSchemes.Add(request.Headers.Authorization!.Scheme);
            if (request.Headers.Authorization.Scheme == "Bearer")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"tier":"e002","account_id":"account"}"""),
            });
        }
    }

    private sealed class TokenFallbackCameraHandler : HttpMessageHandler
    {
        public List<string> AuthorizationSchemes { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            AuthorizationSchemes.Add(request.Headers.Authorization!.Scheme);
            if (request.Headers.Authorization.Scheme == "Bearer")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"cameras":[{"id":1,"name":"Front","type":"xt"}]}"""),
            });
        }
    }

    private sealed class SnapshotHandler(
        byte[]? image = null,
        bool requireTokenForImage = false,
        string thumbnailJson = "123",
        string networkJson = "2") : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        public List<string> AuthorizationSchemes { get; } = [];
        public string? ThumbnailQuery { get; private set; }
        public string? MediaCacheControl { get; private set; }
        public string? MediaPragma { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            AuthorizationSchemes.Add(request.Headers.Authorization!.Scheme);
            if (request.RequestUri.AbsolutePath == "/api/v3/accounts/account/homescreen")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        $$"""{"cameras":[{"id":1,"name":"Front","type":"xt","network_id":{{networkJson}},"thumbnail":{{thumbnailJson}}}]}"""),
                });
            }

            if (request.Method == HttpMethod.Post)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"id":3,"network_id":2}"""),
                });
            }

            if (request.RequestUri.AbsolutePath == "/network/2/command/3")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"complete":true,"status_code":908}"""),
                });
            }

            ThumbnailQuery = request.RequestUri.Query;
            MediaCacheControl = request.Headers.CacheControl?.ToString();
            MediaPragma = request.Headers.Pragma.ToString();
            if (requireTokenForImage && request.Headers.Authorization!.Scheme == "Bearer")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            }

            var content = new ByteArrayContent(image ?? [1, 2, 3]);
            content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
            });
        }
    }

    private sealed class SnapshotCaptureHandler : HttpMessageHandler
    {
        private int homescreenRequests;

        public List<string> Paths { get; } = [];
        public List<string> AuthorizationSchemes { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            AuthorizationSchemes.Add(request.Headers.Authorization!.Scheme);
            if (request.RequestUri.AbsolutePath == "/api/v3/accounts/account/homescreen")
            {
                homescreenRequests++;
                var thumbnail = homescreenRequests == 1 ? "\"\"" : "123";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        $$"""{"doorbells":[{"id":1,"name":"Front","type":"lotus","network_id":2,"thumbnail":{{thumbnail}}}]}"""),
                });
            }

            if (request.Method == HttpMethod.Post)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"id":3,"network_id":2}"""),
                });
            }

            if (request.RequestUri.AbsolutePath == "/network/2/command/3")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"complete":true,"status_code":908}"""),
                });
            }

            var content = new ByteArrayContent([1, 2, 3]);
            content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class LiveViewHandler(
        string collection,
        string type,
        bool rejectBearer = false,
        bool includeSessionHints = true,
        bool includeLiveViewToken = true,
        bool includeDuration = true) :
        HttpMessageHandler
    {
        public string? LiveViewPath { get; private set; }
        public string? LiveViewBody { get; private set; }
        public List<string> LiveViewAuthorizationSchemes { get; } = [];
        public string? CompletionPath { get; private set; }
        public string? CompletionAuthorizationScheme { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/api/v3/accounts/account/homescreen")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        $$"""{"{{collection}}":[{"id":1,"name":"Front","type":"{{type}}","network_id":2,"serial":"SERIAL-1"}]}"""),
                };
            }

            if (request.RequestUri.AbsolutePath.EndsWith("/liveview", StringComparison.Ordinal))
            {
                LiveViewPath = request.RequestUri.AbsolutePath;
                LiveViewBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                LiveViewAuthorizationSchemes.Add(request.Headers.Authorization!.Scheme);
                if (rejectBearer && request.Headers.Authorization.Scheme == "Bearer")
                {
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                }

                var sessionHints = includeSessionHints
                    ? ",\"join_available\":true,\"join_state\":\"available\""
                    : string.Empty;
                var liveViewToken = includeLiveViewToken
                    ? ",\"liveview_token\":\"secret\""
                    : string.Empty;
                var duration = includeDuration ? ",\"duration\":300" : string.Empty;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        $"{{\"command_id\":\"command-1\"{sessionHints},\"server\":\"immis://live.example.test:443/connection-1__IMDS?client_id=42\"{duration}{liveViewToken}}}"),
                };
            }

            CompletionPath = request.RequestUri.AbsolutePath;
            CompletionAuthorizationScheme = request.Headers.Authorization!.Scheme;
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class TestRegistrationHttpClientFactory(HttpMessageHandler handler) :
        IBlinkRegistrationHttpClientFactory
    {
        public HttpClient CreateClient(CookieContainer cookies) => new(handler, disposeHandler: false);
    }

    private sealed class RejectionHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
    }

    private sealed class ConnectionFlowHandler : HttpMessageHandler
    {
        public RequestHeaders InitialAuthorization { get; private set; } = new([], []);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/oauth/v2/authorize" &&
                request.Headers.Referrer is null)
            {
                InitialAuthorization = new RequestHeaders(
                    request.Headers.Accept.Select(value => value.ToString()).ToArray(),
                    request.Headers.AcceptLanguage.Select(value => value.ToString()).ToArray());
                return new HttpResponseMessage(HttpStatusCode.Found)
                {
                    Headers = { Location = new Uri("https://api.oauth.blink.com/oauth/v2/signin") },
                };
            }

            if (request.Method == HttpMethod.Get && path == "/oauth/v2/signin")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """<script id="oauth-args">{"csrf-token":"csrf"}</script>"""),
                };
            }

            if (request.Method == HttpMethod.Post && path == "/oauth/v2/signin")
            {
                Assert.Contains("username=admin%40example.test", await request.Content!.ReadAsStringAsync(cancellationToken));
                return new HttpResponseMessage(HttpStatusCode.Found);
            }

            if (request.Method == HttpMethod.Get && path == "/oauth/v2/authorize")
            {
                return new HttpResponseMessage(HttpStatusCode.Found)
                {
                    Headers = { Location = new Uri("immedia-blink://callback?code=authorization-code") },
                };
            }

            if (request.Method == HttpMethod.Post && path == "/oauth/token")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"access_token":"access","refresh_token":"refresh","expires_in":3600}"""),
                };
            }

            throw new InvalidOperationException($"Unexpected request: {request.Method} {path}");
        }
    }

    private sealed record RequestHeaders(
        IReadOnlyList<string> Accept,
        IReadOnlyList<string> AcceptLanguage);

    private sealed class VerificationFlowHandler(string verificationStatus) : HttpMessageHandler
    {
        public RequestContract? VerificationRequest { get; private set; }
        public RequestContract? TokenRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path == "/oauth/v2/2fa/verify")
            {
                VerificationRequest = CaptureContract(request);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent($$"""{"status":"{{verificationStatus}}"}"""),
                });
            }

            if (request.Method == HttpMethod.Get && path == "/oauth/v2/authorize")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found)
                {
                    Headers = { Location = new Uri("immedia-blink://callback?code=authorization-code") },
                });
            }

            if (request.Method == HttpMethod.Post && path == "/oauth/token")
            {
                TokenRequest = CaptureContract(request);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"access_token":"access","refresh_token":"refresh","expires_in":3600}"""),
                });
            }

            throw new InvalidOperationException($"Unexpected request: {request.Method} {path}");
        }

        private static RequestContract CaptureContract(HttpRequestMessage request) =>
            new(
                request.Headers.UserAgent.ToString(),
                request.Headers.Accept.ToString(),
                request.Headers.TryGetValues("Origin", out var origins) ? Assert.Single(origins) : null,
                request.Headers.Referrer?.AbsoluteUri,
                request.Content?.Headers.ContentType?.MediaType);
    }

    private sealed record RequestContract(
        string UserAgent,
        string Accept,
        string? Origin,
        string? Referrer,
        string? ContentType);
}
