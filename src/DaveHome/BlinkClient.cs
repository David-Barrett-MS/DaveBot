using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("DaveHome.Tests")]

namespace DaveHome;

public sealed record BlinkOptions
{
    public Uri OAuthBaseUri { get; init; } = new("https://api.oauth.blink.com/");
    public Uri ApiBaseUri { get; init; } = new("https://rest-prod.immedia-semi.com/");
    public string OAuthClientId { get; init; } = "ios";
    public string OAuthRedirectUri { get; init; } = "immedia-blink://applinks.blink.com/signin/callback";
    public string OAuthScope { get; init; } = "client";
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public int MaximumSnapshotBytes { get; init; } = 5 * 1024 * 1024;
    public int SnapshotCommandPollAttempts { get; init; } = 10;

    public void Validate()
    {
        if (OAuthBaseUri.Scheme != Uri.UriSchemeHttps ||
            ApiBaseUri.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrWhiteSpace(OAuthClientId) ||
            string.IsNullOrWhiteSpace(OAuthRedirectUri) ||
            string.IsNullOrWhiteSpace(OAuthScope) ||
            RequestTimeout <= TimeSpan.Zero ||
            RequestTimeout > TimeSpan.FromMinutes(2) ||
            MaximumSnapshotBytes is < 1 or > 20 * 1024 * 1024 ||
            SnapshotCommandPollAttempts is < 1 or > 30)
        {
            throw new InvalidOperationException("Blink configuration is invalid.");
        }
    }
}

public sealed record BlinkAccessToken
{
    public BlinkAccessToken()
    {
    }

    internal BlinkAccessToken(
        string accessToken,
        string refreshToken,
        DateTimeOffset expiresAtUtc,
        string hardwareId,
        string accountId,
        string apiBaseAddress)
    {
        AccessToken = accessToken;
        RefreshToken = refreshToken;
        ExpiresAtUtc = expiresAtUtc;
        HardwareId = hardwareId;
        AccountId = accountId;
        ApiBaseAddress = apiBaseAddress;
    }

    [JsonInclude] internal string AccessToken { get; init; } = string.Empty;
    [JsonInclude] internal string RefreshToken { get; init; } = string.Empty;
    [JsonInclude] internal DateTimeOffset ExpiresAtUtc { get; init; }
    [JsonInclude] internal string HardwareId { get; init; } = string.Empty;
    [JsonInclude] internal string AccountId { get; init; } = string.Empty;
    [JsonInclude] internal string ApiBaseAddress { get; init; } = string.Empty;
}

public sealed class BlinkConnectionTicket
{
    internal BlinkConnectionTicket(CookieContainer cookies, string csrfToken, string codeVerifier, string hardwareId)
    {
        Cookies = cookies;
        CsrfToken = csrfToken;
        CodeVerifier = codeVerifier;
        HardwareId = hardwareId;
    }

    internal CookieContainer Cookies { get; }
    internal string CsrfToken { get; }
    internal string CodeVerifier { get; }
    internal string HardwareId { get; }
}

public sealed record BlinkConnectionResult(
    DeviceOperationStatus Status,
    BlinkAccessToken? Token = null,
    BlinkConnectionTicket? VerificationTicket = null,
    string? Detail = null)
{
    public bool Succeeded => Status == DeviceOperationStatus.Succeeded;
}

public sealed record BlinkApiResult<T>(
    DeviceOperationStatus Status,
    T? Value = default,
    string? Detail = null)
{
    public bool Succeeded => Status == DeviceOperationStatus.Succeeded;
}

public sealed record BlinkRemoteCamera(
    string CameraId,
    string Name,
    string? Model,
    string? NetworkId = null,
    string? Thumbnail = null,
    string? CameraKind = null,
    string? SerialNumber = null);

public sealed record BlinkSnapshotResult(
    DeviceOperationStatus Status,
    DeviceSnapshot? Snapshot = null,
    string? Detail = null)
{
    public bool Succeeded => Status == DeviceOperationStatus.Succeeded;
}

public interface IBlinkRegistrationHttpClientFactory
{
    HttpClient CreateClient(CookieContainer cookies);
}

public sealed class BlinkRegistrationHttpClientFactory : IBlinkRegistrationHttpClientFactory
{
    public HttpClient CreateClient(CookieContainer cookies) => new(new HttpClientHandler
    {
        CookieContainer = cookies,
        UseCookies = true,
        AllowAutoRedirect = false,
    }, disposeHandler: true)
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };
}

public interface IBlinkClient
{
    Task<BlinkConnectionResult> ConnectAsync(
        string email,
        string password,
        CancellationToken cancellationToken = default);

    Task<BlinkConnectionResult> CompleteVerificationAsync(
        string code,
        BlinkConnectionTicket verificationTicket,
        CancellationToken cancellationToken = default);

    Task<BlinkConnectionResult> RefreshAsync(
        BlinkAccessToken token,
        CancellationToken cancellationToken = default);

    Task<BlinkApiResult<IReadOnlyList<BlinkRemoteCamera>>> GetCamerasAsync(
        BlinkAccessToken token,
        CancellationToken cancellationToken = default);

    Task<BlinkSnapshotResult> GetSnapshotAsync(
        BlinkAccessToken token,
        string cameraId,
        CancellationToken cancellationToken = default);

    Task<BlinkLiveViewStartResult> StartLiveViewAsync(
        BlinkAccessToken token,
        string cameraId,
        CancellationToken cancellationToken = default);
}

public sealed class BlinkClient(
    HttpClient httpClient,
    BlinkOptions options,
    IBlinkRegistrationHttpClientFactory registrationHttpClientFactory) : IBlinkClient
{
    private const string OAuthUserAgent =
        "Mozilla/5.0 (iPhone; CPU iPhone OS 18_7 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/26.1 Mobile/15E148 Safari/604.1";
    private const string TokenUserAgent = "Blink/2511191620 CFNetwork/3860.200.71 Darwin/25.1.0";
    private const string ApiUserAgent = "27.0ANDROID_28373244";
    private static readonly Regex CsrfTokenPattern = new(
        """<script\s+[^>]*id=["']oauth-args["'][^>]*>(?<json>.*?)</script>""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    public async Task<BlinkConnectionResult> ConnectAsync(
        string email,
        string password,
        CancellationToken cancellationToken = default)
    {
        options.Validate();
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password) ||
            email.Length > 320 || password.Length > 1_000)
        {
            return new BlinkConnectionResult(DeviceOperationStatus.InvalidState);
        }

        var cookies = new CookieContainer();
        using var client = registrationHttpClientFactory.CreateClient(cookies);
        var hardwareId = Guid.NewGuid().ToString().ToUpperInvariant();
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        try
        {
            using var timeout = CreateTimeout(cancellationToken);
            var authorize = BuildAuthorizeUri(challenge, hardwareId);
            using var authorizeRequest = new HttpRequestMessage(HttpMethod.Get, authorize);
            AddNavigationHeaders(authorizeRequest);
            using var authorizeResponse = await client.SendAsync(authorizeRequest, timeout.Token);
            if (!authorizeResponse.IsSuccessStatusCode && !IsRedirect(authorizeResponse.StatusCode))
            {
                return Failed(authorizeResponse.StatusCode, "Blink authorization could not be started.");
            }

            var csrfToken = await GetCsrfTokenAsync(client, timeout.Token);
            if (csrfToken is null)
            {
                return new BlinkConnectionResult(DeviceOperationStatus.InvalidState,
                    Detail: "Blink did not provide a sign-in challenge.");
            }

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri(options.OAuthBaseUri, "oauth/v2/signin"))
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["username"] = email.Trim(),
                    ["password"] = password,
                    ["csrf-token"] = csrfToken,
                }),
            };
            AddWebHeaders(request, "https://api.oauth.blink.com/oauth/v2/signin");
            using var response = await client.SendAsync(request, timeout.Token);
            if (response.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Accepted)
            {
                return new BlinkConnectionResult(
                    DeviceOperationStatus.VerificationRequired,
                    VerificationTicket: new BlinkConnectionTicket(cookies, csrfToken, verifier, hardwareId),
                    Detail: "Blink requires the verification code sent to the account.");
            }

            if (!IsRedirect(response.StatusCode))
            {
                return Failed(response.StatusCode, "Blink account sign-in was rejected.");
            }

            return await CompleteAuthorizationAsync(client, verifier, hardwareId, timeout.Token);
        }
        catch (HttpRequestException)
        {
            return new BlinkConnectionResult(DeviceOperationStatus.BridgeUnavailable, Detail: "Blink could not be reached.");
        }
        catch (JsonException)
        {
            return new BlinkConnectionResult(DeviceOperationStatus.InvalidState, Detail: "The Blink response was invalid.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new BlinkConnectionResult(DeviceOperationStatus.TimedOut, Detail: "Blink sign-in timed out.");
        }
    }

    public async Task<BlinkConnectionResult> CompleteVerificationAsync(
        string code,
        BlinkConnectionTicket verificationTicket,
        CancellationToken cancellationToken = default)
    {
        options.Validate();
        if (verificationTicket is null || string.IsNullOrWhiteSpace(code) || code.Trim().Length > 100)
        {
            return new BlinkConnectionResult(DeviceOperationStatus.InvalidState);
        }

        using var client = registrationHttpClientFactory.CreateClient(verificationTicket.Cookies);
        try
        {
            using var timeout = CreateTimeout(cancellationToken);
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri(options.OAuthBaseUri, "oauth/v2/2fa/verify"))
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["2fa_code"] = code.Trim(),
                    ["csrf-token"] = verificationTicket.CsrfToken,
                    ["remember_me"] = "false",
                }),
            };
            AddWebHeaders(request, "https://api.oauth.blink.com/oauth/v2/signin");
            using var response = await client.SendAsync(request, timeout.Token);
            if (response.StatusCode != HttpStatusCode.Created)
            {
                return Failed(response.StatusCode, "Blink verification code was rejected.");
            }

            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(timeout.Token),
                cancellationToken: timeout.Token);
            if (!TryGetString(document.RootElement, "status", out var status) ||
                !string.Equals(status, "auth-completed", StringComparison.OrdinalIgnoreCase))
            {
                return new BlinkConnectionResult(
                    DeviceOperationStatus.InvalidState,
                    Detail: "Blink did not complete verification.");
            }

            return await CompleteAuthorizationAsync(
                client,
                verificationTicket.CodeVerifier,
                verificationTicket.HardwareId,
                timeout.Token);
        }
        catch (HttpRequestException)
        {
            return new BlinkConnectionResult(DeviceOperationStatus.BridgeUnavailable, Detail: "Blink could not be reached.");
        }
        catch (JsonException)
        {
            return new BlinkConnectionResult(DeviceOperationStatus.InvalidState, Detail: "The Blink response was invalid.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new BlinkConnectionResult(DeviceOperationStatus.TimedOut, Detail: "Blink verification timed out.");
        }
    }

    public async Task<BlinkConnectionResult> RefreshAsync(
        BlinkAccessToken token,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token.RefreshToken) || string.IsNullOrWhiteSpace(token.HardwareId))
        {
            return new BlinkConnectionResult(DeviceOperationStatus.InvalidState);
        }

        try
        {
            using var timeout = CreateTimeout(cancellationToken);
            var refreshed = await RequestTokenAsync(
                httpClient,
                new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = token.RefreshToken,
                    ["client_id"] = options.OAuthClientId,
                    ["scope"] = options.OAuthScope,
                    ["hardware_id"] = token.HardwareId,
                },
                timeout.Token);
            if (!refreshed.Succeeded || refreshed.Token is null)
            {
                return refreshed;
            }

            return new BlinkConnectionResult(
                DeviceOperationStatus.Succeeded,
                refreshed.Token with
                {
                    HardwareId = token.HardwareId,
                    AccountId = token.AccountId,
                    ApiBaseAddress = token.ApiBaseAddress,
                });
        }
        catch (HttpRequestException)
        {
            return new BlinkConnectionResult(DeviceOperationStatus.BridgeUnavailable, Detail: "Blink could not be reached.");
        }
        catch (JsonException)
        {
            return new BlinkConnectionResult(DeviceOperationStatus.InvalidState, Detail: "The Blink token response was invalid.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new BlinkConnectionResult(DeviceOperationStatus.TimedOut, Detail: "Blink token refresh timed out.");
        }
    }

    public async Task<BlinkApiResult<IReadOnlyList<BlinkRemoteCamera>>> GetCamerasAsync(
        BlinkAccessToken token,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(token.ApiBaseAddress, UriKind.Absolute, out var baseAddress) ||
            string.IsNullOrWhiteSpace(token.AccountId))
        {
            return new BlinkApiResult<IReadOnlyList<BlinkRemoteCamera>>(DeviceOperationStatus.InvalidState);
        }

        try
        {
            using var timeout = CreateTimeout(cancellationToken);
            using var primaryRequest = CreateCamerasRequest(baseAddress, token, "Bearer");
            using var primaryResponse = await httpClient.SendAsync(primaryRequest, timeout.Token);
            if (primaryResponse.StatusCode == HttpStatusCode.Unauthorized)
            {
                using var fallbackRequest = CreateCamerasRequest(baseAddress, token, "Token");
                using var fallbackResponse = await httpClient.SendAsync(fallbackRequest, timeout.Token);
                return await ParseCamerasResponseAsync(fallbackResponse, timeout.Token);
            }

            return await ParseCamerasResponseAsync(primaryResponse, timeout.Token);
        }
        catch (HttpRequestException)
        {
            return new BlinkApiResult<IReadOnlyList<BlinkRemoteCamera>>(
                DeviceOperationStatus.BridgeUnavailable, Detail: "Blink could not be reached.");
        }
        catch (JsonException)
        {
            return new BlinkApiResult<IReadOnlyList<BlinkRemoteCamera>>(
                DeviceOperationStatus.InvalidState, Detail: "The Blink camera response was invalid.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new BlinkApiResult<IReadOnlyList<BlinkRemoteCamera>>(
                DeviceOperationStatus.TimedOut, Detail: "Blink camera discovery timed out.");
        }
    }

    public async Task<BlinkSnapshotResult> GetSnapshotAsync(
        BlinkAccessToken token,
        string cameraId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cameraId))
        {
            return new BlinkSnapshotResult(DeviceOperationStatus.InvalidState);
        }

        var cameras = await GetCamerasAsync(token, cancellationToken);
        if (!cameras.Succeeded || cameras.Value is null)
        {
            return new BlinkSnapshotResult(cameras.Status, Detail: cameras.Detail);
        }

        var camera = cameras.Value.SingleOrDefault(candidate =>
            string.Equals(candidate.CameraId, cameraId, StringComparison.Ordinal));
        if (camera is null)
        {
            return new BlinkSnapshotResult(
                DeviceOperationStatus.ResourceNotFound,
                Detail: "The Blink camera could not be found.");
        }

        var requested = await RequestThumbnailAsync(token, camera, cancellationToken);
        if (!requested.Succeeded)
        {
            return new BlinkSnapshotResult(requested.Status, Detail: requested.Detail);
        }

        var refreshed = await GetCamerasAsync(token, cancellationToken);
        if (!refreshed.Succeeded || refreshed.Value is null)
        {
            return new BlinkSnapshotResult(refreshed.Status, Detail: refreshed.Detail);
        }

        camera = refreshed.Value.SingleOrDefault(candidate =>
            string.Equals(candidate.CameraId, cameraId, StringComparison.Ordinal));
        if (camera is null || !TryCreateThumbnailUri(token, camera, out var thumbnailUri))
        {
            return new BlinkSnapshotResult(
                DeviceOperationStatus.InvalidState,
                Detail: "Blink did not provide a camera thumbnail after requesting one.");
        }

        try
        {
            using var timeout = CreateTimeout(cancellationToken);
            var uncachedThumbnailUri = CreateUncachedThumbnailUri(thumbnailUri);
            using var primaryRequest = CreateSnapshotGetRequest(
                uncachedThumbnailUri,
                token.AccessToken,
                "Bearer");
            using var primaryResponse = await httpClient.SendAsync(primaryRequest, timeout.Token);
            if (primaryResponse.StatusCode == HttpStatusCode.Unauthorized)
            {
                using var fallbackRequest = CreateSnapshotGetRequest(
                    uncachedThumbnailUri,
                    token.AccessToken,
                    "Token");
                using var fallbackResponse = await httpClient.SendAsync(fallbackRequest, timeout.Token);
                return await ParseSnapshotResponseAsync(fallbackResponse, timeout.Token);
            }

            return await ParseSnapshotResponseAsync(primaryResponse, timeout.Token);
        }
        catch (HttpRequestException)
        {
            return new BlinkSnapshotResult(
                DeviceOperationStatus.BridgeUnavailable,
                Detail: "Blink could not be reached.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new BlinkSnapshotResult(
                DeviceOperationStatus.TimedOut,
                Detail: "Blink snapshot retrieval timed out.");
        }
    }

    public async Task<BlinkLiveViewStartResult> StartLiveViewAsync(
        BlinkAccessToken token,
        string cameraId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cameraId))
        {
            return new BlinkLiveViewStartResult(DeviceOperationStatus.InvalidState);
        }

        var cameras = await GetCamerasAsync(token, cancellationToken);
        if (!cameras.Succeeded || cameras.Value is null)
        {
            return new BlinkLiveViewStartResult(cameras.Status, Detail: cameras.Detail);
        }

        var camera = cameras.Value.SingleOrDefault(candidate =>
            string.Equals(candidate.CameraId, cameraId, StringComparison.Ordinal));
        if (camera is null)
        {
            return new BlinkLiveViewStartResult(DeviceOperationStatus.ResourceNotFound);
        }

        if (!TryCreateLiveViewUri(token, camera, out var liveViewUri) ||
            string.IsNullOrWhiteSpace(camera.SerialNumber))
        {
            return new BlinkLiveViewStartResult(
                DeviceOperationStatus.InvalidState,
                Detail: "Blink did not provide the camera details required for live view.");
        }

        try
        {
            using var timeout = CreateTimeout(cancellationToken);
            using var primaryRequest = CreateLiveViewRequest(liveViewUri, token.AccessToken, "Bearer");
            using var primaryResponse = await httpClient.SendAsync(primaryRequest, timeout.Token);
            WriteLiveViewAudit("Start response", primaryResponse.StatusCode);
            if (primaryResponse.StatusCode == HttpStatusCode.Unauthorized)
            {
                using var fallbackRequest = CreateLiveViewRequest(liveViewUri, token.AccessToken, "Token");
                using var fallbackResponse = await httpClient.SendAsync(fallbackRequest, timeout.Token);
                WriteLiveViewAudit("Start fallback response", fallbackResponse.StatusCode);
                return await ParseLiveViewResponseAsync(
                    fallbackResponse,
                    token,
                    camera,
                    timeout.Token);
            }

            return await ParseLiveViewResponseAsync(primaryResponse, token, camera, timeout.Token);
        }
        catch (HttpRequestException)
        {
            return new BlinkLiveViewStartResult(
                DeviceOperationStatus.BridgeUnavailable,
                Detail: "Blink could not be reached.");
        }
        catch (JsonException)
        {
            return new BlinkLiveViewStartResult(
                DeviceOperationStatus.InvalidState,
                Detail: "Blink returned an invalid live view response.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new BlinkLiveViewStartResult(
                DeviceOperationStatus.TimedOut,
                Detail: "Blink live view setup timed out.");
        }
    }

    private static HttpRequestMessage CreateCamerasRequest(
        Uri baseAddress,
        BlinkAccessToken token,
        string authorizationScheme)
        => CreateApiGetRequest(
            new Uri(baseAddress, $"api/v3/accounts/{Uri.EscapeDataString(token.AccountId)}/homescreen"),
            token.AccessToken,
            authorizationScheme);

    private static HttpRequestMessage CreateApiGetRequest(
        Uri requestUri,
        string accessToken,
        string authorizationScheme)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, requestUri)
        {
            Content = new ByteArrayContent([]),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue(authorizationScheme, accessToken);
        return request;
    }

    private static HttpRequestMessage CreateApiPostRequest(
        Uri requestUri,
        string accessToken,
        string authorizationScheme)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = new ByteArrayContent([]),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue(authorizationScheme, accessToken);
        return request;
    }

    private static HttpRequestMessage CreateLiveViewRequest(
        Uri requestUri,
        string accessToken,
        string authorizationScheme)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = new StringContent(
                """{"intent":"liveview","motion_event_start_time":null}""",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(authorizationScheme, accessToken);
        request.Options.Set(new HttpRequestOptionsKey<bool>("DaveBot.SuppressDebugLogging"), true);
        return request;
    }

    private static HttpRequestMessage CreateSnapshotGetRequest(
        Uri requestUri,
        string accessToken,
        string authorizationScheme)
    {
        var request = CreateApiGetRequest(requestUri, accessToken, authorizationScheme);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        request.Headers.Pragma.Add(new NameValueHeaderValue("no-cache"));
        return request;
    }

    private static async Task<BlinkApiResult<IReadOnlyList<BlinkRemoteCamera>>> ParseCamerasResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            return new BlinkApiResult<IReadOnlyList<BlinkRemoteCamera>>(
                ToStatus(response.StatusCode), Detail: "Blink rejected the camera discovery request.");
        }

        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        var cameras = GetCameras(document.RootElement)
            .GroupBy(camera => camera.CameraId, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(camera => camera.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new BlinkApiResult<IReadOnlyList<BlinkRemoteCamera>>(
            DeviceOperationStatus.Succeeded, cameras);
    }

    private async Task<BlinkSnapshotResult> ParseSnapshotResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            return new BlinkSnapshotResult(
                ToStatus(response.StatusCode),
                Detail: "Blink rejected the camera snapshot request.");
        }

        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (contentType is not ("image/jpeg" or "image/png"))
        {
            return new BlinkSnapshotResult(
                DeviceOperationStatus.InvalidState,
                Detail: "Blink returned an unsupported snapshot format.");
        }

        if (response.Content.Headers.ContentLength > options.MaximumSnapshotBytes)
        {
            return new BlinkSnapshotResult(
                DeviceOperationStatus.InvalidState,
                Detail: "The Blink snapshot is too large to display.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var image = new MemoryStream();
        var buffer = new byte[81_920];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (image.Length + read > options.MaximumSnapshotBytes)
            {
                return new BlinkSnapshotResult(
                    DeviceOperationStatus.InvalidState,
                    Detail: "The Blink snapshot is too large to display.");
            }

            await image.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        if (image.Length == 0)
        {
            return new BlinkSnapshotResult(
                DeviceOperationStatus.InvalidState,
                Detail: "Blink returned an empty snapshot.");
        }

        return new BlinkSnapshotResult(
            DeviceOperationStatus.Succeeded,
            new DeviceSnapshot(contentType, image.ToArray()));
    }

    private async Task<BlinkConnectionResult> CompleteAuthorizationAsync(
        HttpClient client,
        string verifier,
        string hardwareId,
        CancellationToken cancellationToken)
    {
        var code = await GetAuthorizationCodeAsync(client, cancellationToken);
        if (code is null)
        {
            return new BlinkConnectionResult(DeviceOperationStatus.InvalidState,
                Detail: "Blink did not provide an authorization code.");
        }

        var token = await RequestTokenAsync(
            client,
            new Dictionary<string, string>
            {
                ["app_brand"] = "blink",
                ["client_id"] = options.OAuthClientId,
                ["code"] = code,
                ["code_verifier"] = verifier,
                ["grant_type"] = "authorization_code",
                ["hardware_id"] = hardwareId,
                ["redirect_uri"] = options.OAuthRedirectUri,
                ["scope"] = options.OAuthScope,
            },
            cancellationToken);
        if (!token.Succeeded || token.Token is null)
        {
            return token;
        }

        var account = await GetAccountAsync(token.Token.AccessToken, cancellationToken);
        if (!account.Succeeded)
        {
            return new BlinkConnectionResult(account.Status, Detail: account.Detail);
        }

        return new BlinkConnectionResult(
            DeviceOperationStatus.Succeeded,
            token.Token with
            {
                HardwareId = hardwareId,
                AccountId = account.Value.AccountId,
                ApiBaseAddress = account.Value.ApiBaseAddress,
            });
    }

    private async Task<BlinkConnectionResult> RequestTokenAsync(
        HttpClient client,
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(options.OAuthBaseUri, "oauth/token"))
        {
            Content = new FormUrlEncodedContent(values),
        };
        request.Headers.UserAgent.ParseAdd(TokenUserAgent);
        request.Headers.Accept.ParseAdd("*/*");
        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return Failed(response.StatusCode, "Blink token exchange was rejected.");
        }

        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        if (!TryGetString(document.RootElement, "access_token", out var accessToken) ||
            !TryGetString(document.RootElement, "refresh_token", out var refreshToken) ||
            !document.RootElement.TryGetProperty("expires_in", out var expiresIn) ||
            !expiresIn.TryGetInt32(out var seconds) || seconds <= 0)
        {
            return new BlinkConnectionResult(DeviceOperationStatus.InvalidState,
                Detail: "The Blink token response was incomplete.");
        }

        return new BlinkConnectionResult(
            DeviceOperationStatus.Succeeded,
            new BlinkAccessToken(
                accessToken,
                refreshToken,
                DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, seconds - 60)),
                string.Empty,
                string.Empty,
                string.Empty));
    }

    private async Task<BlinkApiResult<(string AccountId, string ApiBaseAddress)>> GetAccountAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var primaryRequest = CreateTierRequest(accessToken, "Bearer");
        using var primaryResponse = await httpClient.SendAsync(primaryRequest, cancellationToken);
        if (primaryResponse.StatusCode == HttpStatusCode.Unauthorized)
        {
            using var fallbackRequest = CreateTierRequest(accessToken, "Token");
            using var fallbackResponse = await httpClient.SendAsync(fallbackRequest, cancellationToken);
            return await ParseAccountResponseAsync(fallbackResponse, cancellationToken);
        }

        return await ParseAccountResponseAsync(primaryResponse, cancellationToken);
    }

    private HttpRequestMessage CreateTierRequest(string accessToken, string authorizationScheme)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(options.ApiBaseUri, "api/v1/users/tier_info"));
        request.Content = new ByteArrayContent([]);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
        request.Headers.Authorization = new AuthenticationHeaderValue(authorizationScheme, accessToken);
        request.Headers.UserAgent.ParseAdd(ApiUserAgent);
        return request;
    }

    private static async Task<BlinkApiResult<(string AccountId, string ApiBaseAddress)>> ParseAccountResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            return new BlinkApiResult<(string AccountId, string ApiBaseAddress)>(
                ToStatus(response.StatusCode), Detail: "Blink account details could not be read.");
        }

        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        if (!TryGetString(document.RootElement, "tier", out var tier) ||
            !TryGetString(document.RootElement, "account_id", out var accountId) ||
            !Regex.IsMatch(tier, "^[a-z0-9]{1,20}$", RegexOptions.CultureInvariant))
        {
            return new BlinkApiResult<(string AccountId, string ApiBaseAddress)>(
                DeviceOperationStatus.InvalidState, Detail: "The Blink account response was incomplete.");
        }

        return new BlinkApiResult<(string AccountId, string ApiBaseAddress)>(
            DeviceOperationStatus.Succeeded,
            (accountId, $"https://rest-{tier}.immedia-semi.com/"));
    }

    private async Task<string?> GetCsrfTokenAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(options.OAuthBaseUri, "oauth/v2/signin"));
        AddNavigationHeaders(request);
        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        var match = CsrfTokenPattern.Match(html);
        if (!match.Success)
        {
            return null;
        }

        using var document = JsonDocument.Parse(match.Groups["json"].Value);
        return TryGetString(document.RootElement, "csrf-token", out var csrfToken) ? csrfToken : null;
    }

    private async Task<string?> GetAuthorizationCodeAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(options.OAuthBaseUri, "oauth/v2/authorize"));
        request.Headers.UserAgent.ParseAdd(OAuthUserAgent);
        request.Headers.Accept.ParseAdd("*/*");
        request.Headers.Referrer = new Uri(options.OAuthBaseUri, "oauth/v2/signin");
        using var response = await client.SendAsync(request, cancellationToken);
        if (!IsRedirect(response.StatusCode) || response.Headers.Location is not { } location)
        {
            return null;
        }

        var query = location.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in query)
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2 && pair[0] == "code")
            {
                return Uri.UnescapeDataString(pair[1]);
            }
        }

        return null;
    }

    private Uri BuildAuthorizeUri(string challenge, string hardwareId)
    {
        var parameters = new Dictionary<string, string>
        {
            ["app_brand"] = "blink",
            ["app_version"] = "50.1",
            ["client_id"] = options.OAuthClientId,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["device_brand"] = "Apple",
            ["device_model"] = "iPhone16,1",
            ["device_os_version"] = "26.1",
            ["hardware_id"] = hardwareId,
            ["redirect_uri"] = options.OAuthRedirectUri,
            ["response_type"] = "code",
            ["scope"] = options.OAuthScope,
        };
        var query = string.Join(
            "&",
            parameters.Select(pair =>
                $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        return new Uri(new Uri(options.OAuthBaseUri, "oauth/v2/authorize"), $"?{query}");
    }

    private static IReadOnlyList<BlinkRemoteCamera> GetCameras(JsonElement root)
    {
        var cameras = new List<BlinkRemoteCamera>();
        foreach (var (propertyName, defaultModel, cameraKind) in new[]
                 {
                     ("cameras", "camera", "default"),
                     ("owls", "mini", "mini"),
                     ("doorbells", "doorbell", "doorbell"),
                 })
        {
            if (!root.TryGetProperty(propertyName, out var items) || items.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var camera in items.EnumerateArray())
            {
                if (TryGetString(camera, "id", out var id))
                {
                    cameras.Add(new BlinkRemoteCamera(
                        id,
                        TryGetString(camera, "name", out var name) ? name : $"Blink camera {id}",
                        TryGetString(camera, "type", out var model) ? model : defaultModel,
                        TryGetString(camera, "network_id", out var networkId) ? networkId : null,
                        TryGetString(camera, "thumbnail", out var thumbnail) ? thumbnail : null,
                        cameraKind,
                        TryGetString(camera, "serial", out var serial) ? serial :
                        TryGetString(camera, "serial_number", out var serialNumber) ? serialNumber : null));
                }
            }
        }

        return cameras;
    }

    private static bool TryCreateLiveViewUri(
        BlinkAccessToken token,
        BlinkRemoteCamera camera,
        out Uri liveViewUri)
    {
        liveViewUri = null!;
        if (!Uri.TryCreate(token.ApiBaseAddress, UriKind.Absolute, out var apiBaseAddress) ||
            apiBaseAddress.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrWhiteSpace(token.AccountId) ||
            string.IsNullOrWhiteSpace(camera.NetworkId))
        {
            return false;
        }

        var account = Uri.EscapeDataString(token.AccountId);
        var network = Uri.EscapeDataString(camera.NetworkId);
        var cameraId = Uri.EscapeDataString(camera.CameraId);
        var path = camera.CameraKind switch
        {
            "mini" =>
                $"api/v2/accounts/{account}/networks/{network}/owls/{cameraId}/liveview",
            "doorbell" =>
                $"api/v2/accounts/{account}/networks/{network}/doorbells/{cameraId}/liveview",
            _ =>
                $"api/v6/accounts/{account}/networks/{network}/cameras/{cameraId}/liveview",
        };
        liveViewUri = new Uri(apiBaseAddress, path);
        return true;
    }

    private async Task<BlinkLiveViewStartResult> ParseLiveViewResponseAsync(
        HttpResponseMessage response,
        BlinkAccessToken token,
        BlinkRemoteCamera camera,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            return new BlinkLiveViewStartResult(
                ToStatus(response.StatusCode),
                Detail: "Blink rejected the live view request.");
        }

        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        var root = document.RootElement;
        var hasCommandId = TryGetString(root, "command_id", out var commandId) &&
            commandId.Length <= 200;
        var hasServer = TryGetString(root, "server", out var server);
        var hasDuration = TryGetInt32(root, "duration", out var durationSeconds) &&
            durationSeconds >= 1;
        var pollingInterval = TryGetInt32(root, "polling_interval", out var pollingIntervalSeconds) &&
            pollingIntervalSeconds is >= 5 and <= 60
            ? TimeSpan.FromSeconds(pollingIntervalSeconds)
            : TimeSpan.FromSeconds(15);
        BlinkImmiEndpoint? endpoint = null;
        if (hasServer && BlinkLiveViewProtocol.TryParseImmiEndpoint(server, out var parsedEndpoint))
        {
            endpoint = parsedEndpoint;
        }

        var hasEndpoint = endpoint is not null;
        var hasHandshake = endpoint is not null &&
            BlinkLiveViewProtocol.TryCreateGreeting(camera.SerialNumber, endpoint, out _);
        if (!hasCommandId ||
            string.IsNullOrWhiteSpace(camera.NetworkId) ||
            !hasHandshake)
        {
            RuntimeAuditContext.Write(
                "Blink live view",
                "Start response",
                $"Compatible session: no; command: {hasCommandId}; duration: {hasDuration}; " +
                $"IMMI endpoint: {hasEndpoint}; handshake: {hasHandshake}.");
            return new BlinkLiveViewStartResult(
                DeviceOperationStatus.InvalidState,
                Detail: "Blink did not make live view available for this camera.");
        }

        var stream = new BlinkLiveViewStream(
            endpoint!,
            camera.SerialNumber!,
            hasDuration
                ? TimeSpan.FromSeconds(Math.Min(durationSeconds, 3_600))
                : TimeSpan.FromMinutes(10),
            pollingInterval,
            completeCancellationToken => IsLiveViewActiveAsync(
                token,
                camera.NetworkId,
                commandId,
                completeCancellationToken),
            completeCancellationToken => CompleteLiveViewAsync(
                token,
                camera.NetworkId,
                commandId,
                completeCancellationToken));
        return new BlinkLiveViewStartResult(DeviceOperationStatus.Succeeded, stream);
    }

    private static void WriteLiveViewAudit(string request, HttpStatusCode status) =>
        RuntimeAuditContext.Write("Blink live view", request, $"HTTP {(int)status} ({status})");

    private async Task<bool> IsLiveViewActiveAsync(
        BlinkAccessToken token,
        string networkId,
        string commandId,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(token.ApiBaseAddress, UriKind.Absolute, out var apiBaseAddress))
        {
            return false;
        }

        var commandUri = new Uri(
            apiBaseAddress,
            $"network/{Uri.EscapeDataString(networkId)}/command/{Uri.EscapeDataString(commandId)}");
        using var primaryRequest = CreateLiveViewStatusRequest(commandUri, token.AccessToken, "Bearer");
        using var primaryResponse = await httpClient.SendAsync(primaryRequest, cancellationToken);
        if (primaryResponse.StatusCode == HttpStatusCode.Unauthorized)
        {
            using var fallbackRequest = CreateLiveViewStatusRequest(commandUri, token.AccessToken, "Token");
            using var fallbackResponse = await httpClient.SendAsync(fallbackRequest, cancellationToken);
            return await ParseLiveViewStatusAsync(fallbackResponse, commandId, cancellationToken);
        }

        return await ParseLiveViewStatusAsync(primaryResponse, commandId, cancellationToken);
    }

    private static HttpRequestMessage CreateLiveViewStatusRequest(
        Uri requestUri,
        string accessToken,
        string authorizationScheme)
    {
        var request = CreateApiGetRequest(requestUri, accessToken, authorizationScheme);
        request.Options.Set(new HttpRequestOptionsKey<bool>("DaveBot.SuppressDebugLogging"), true);
        return request;
    }

    private static async Task<bool> ParseLiveViewStatusAsync(
        HttpResponseMessage response,
        string commandId,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        var root = document.RootElement;
        if (!TryGetString(root, "status_code", out var statusCode) ||
            statusCode != "908" ||
            !root.TryGetProperty("commands", out var commands) ||
            commands.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var command in commands.EnumerateArray())
        {
            if (TryGetString(command, "id", out var candidateId) &&
                string.Equals(candidateId, commandId, StringComparison.Ordinal) &&
                TryGetString(command, "state_condition", out var condition))
            {
                return condition is "new" or "running";
            }
        }

        return false;
    }

    private async Task CompleteLiveViewAsync(
        BlinkAccessToken token,
        string networkId,
        string commandId,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(token.ApiBaseAddress, UriKind.Absolute, out var apiBaseAddress))
        {
            return;
        }

        var commandUri = new Uri(
            apiBaseAddress,
            $"network/{Uri.EscapeDataString(networkId)}/command/" +
            $"{Uri.EscapeDataString(commandId)}/done/");
        using var primaryRequest = CreateLiveViewCompletionRequest(commandUri, token.AccessToken, "Bearer");
        using var primaryResponse = await httpClient.SendAsync(primaryRequest, cancellationToken);
        if (primaryResponse.StatusCode == HttpStatusCode.Unauthorized)
        {
            using var fallbackRequest = CreateLiveViewCompletionRequest(commandUri, token.AccessToken, "Token");
            using var fallbackResponse = await httpClient.SendAsync(fallbackRequest, cancellationToken);
        }
    }

    private static HttpRequestMessage CreateLiveViewCompletionRequest(
        Uri requestUri,
        string accessToken,
        string authorizationScheme)
    {
        var request = CreateApiPostRequest(requestUri, accessToken, authorizationScheme);
        request.Options.Set(new HttpRequestOptionsKey<bool>("DaveBot.SuppressDebugLogging"), true);
        return request;
    }

    private async Task<BlinkApiResult<bool>> RequestThumbnailAsync(
        BlinkAccessToken token,
        BlinkRemoteCamera camera,
        CancellationToken cancellationToken)
    {
        if (!TryCreateThumbnailCommandUri(token, camera, out var commandUri))
        {
            return new BlinkApiResult<bool>(
                DeviceOperationStatus.InvalidState,
                Detail: "Blink did not provide the camera details required to request a snapshot.");
        }

        try
        {
            using var timeout = CreateTimeout(cancellationToken);
            using var primaryRequest = CreateApiPostRequest(commandUri, token.AccessToken, "Bearer");
            using var primaryResponse = await httpClient.SendAsync(primaryRequest, timeout.Token);
            if (primaryResponse.StatusCode == HttpStatusCode.Unauthorized)
            {
                using var fallbackRequest = CreateApiPostRequest(commandUri, token.AccessToken, "Token");
                using var fallbackResponse = await httpClient.SendAsync(fallbackRequest, timeout.Token);
                return await WaitForThumbnailCommandAsync(
                    token,
                    fallbackResponse,
                    timeout.Token);
            }

            return await WaitForThumbnailCommandAsync(token, primaryResponse, timeout.Token);
        }
        catch (HttpRequestException)
        {
            return new BlinkApiResult<bool>(
                DeviceOperationStatus.BridgeUnavailable,
                Detail: "Blink could not be reached.");
        }
        catch (JsonException)
        {
            return new BlinkApiResult<bool>(
                DeviceOperationStatus.InvalidState,
                Detail: "Blink returned an invalid snapshot command response.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new BlinkApiResult<bool>(
                DeviceOperationStatus.TimedOut,
                Detail: "Blink snapshot capture timed out.");
        }
    }

    private async Task<BlinkApiResult<bool>> WaitForThumbnailCommandAsync(
        BlinkAccessToken token,
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            return new BlinkApiResult<bool>(
                ToStatus(response.StatusCode),
                Detail: "Blink rejected the camera snapshot request.");
        }

        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        if (!TryGetString(document.RootElement, "id", out var commandId) ||
            !TryGetString(document.RootElement, "network_id", out var networkId))
        {
            return new BlinkApiResult<bool>(
                DeviceOperationStatus.InvalidState,
                Detail: "Blink did not confirm the camera snapshot request.");
        }

        for (var attempt = 0; attempt < options.SnapshotCommandPollAttempts; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            var status = await GetCommandStatusAsync(token, networkId, commandId, cancellationToken);
            if (!status.Succeeded || status.Value)
            {
                return status;
            }
        }

        return new BlinkApiResult<bool>(
            DeviceOperationStatus.TimedOut,
            Detail: "Blink did not complete the camera snapshot request in time.");
    }

    private async Task<BlinkApiResult<bool>> GetCommandStatusAsync(
        BlinkAccessToken token,
        string networkId,
        string commandId,
        CancellationToken cancellationToken)
    {
        var commandUri = new Uri(
            new Uri(token.ApiBaseAddress, UriKind.Absolute),
            $"network/{Uri.EscapeDataString(networkId)}/command/{Uri.EscapeDataString(commandId)}");
        using var primaryRequest = CreateApiGetRequest(commandUri, token.AccessToken, "Bearer");
        using var primaryResponse = await httpClient.SendAsync(primaryRequest, cancellationToken);
        if (primaryResponse.StatusCode == HttpStatusCode.Unauthorized)
        {
            using var fallbackRequest = CreateApiGetRequest(commandUri, token.AccessToken, "Token");
            using var fallbackResponse = await httpClient.SendAsync(fallbackRequest, cancellationToken);
            return await ParseCommandStatusResponseAsync(fallbackResponse, cancellationToken);
        }

        return await ParseCommandStatusResponseAsync(primaryResponse, cancellationToken);
    }

    private static async Task<BlinkApiResult<bool>> ParseCommandStatusResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            return new BlinkApiResult<bool>(
                ToStatus(response.StatusCode),
                Detail: "Blink rejected the camera snapshot status request.");
        }

        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        if (document.RootElement.TryGetProperty("complete", out var complete) &&
            complete.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            if (complete.GetBoolean())
            {
                return new BlinkApiResult<bool>(DeviceOperationStatus.Succeeded, true);
            }

            if (TryGetString(document.RootElement, "status_code", out var statusCode) &&
                statusCode != "908")
            {
                return new BlinkApiResult<bool>(
                    DeviceOperationStatus.InvalidState,
                    Detail: "Blink could not complete the camera snapshot request.");
            }

            return new BlinkApiResult<bool>(DeviceOperationStatus.Succeeded, false);
        }

        return new BlinkApiResult<bool>(
            DeviceOperationStatus.InvalidState,
            Detail: "Blink returned an invalid camera snapshot status.");
    }

    private static bool TryCreateThumbnailCommandUri(
        BlinkAccessToken token,
        BlinkRemoteCamera camera,
        out Uri commandUri)
    {
        commandUri = null!;
        if (!Uri.TryCreate(token.ApiBaseAddress, UriKind.Absolute, out var apiBaseAddress) ||
            string.IsNullOrWhiteSpace(token.AccountId) ||
            string.IsNullOrWhiteSpace(camera.NetworkId))
        {
            return false;
        }

        var path = camera.CameraKind switch
        {
            "mini" =>
                $"api/v1/accounts/{Uri.EscapeDataString(token.AccountId)}/networks/" +
                $"{Uri.EscapeDataString(camera.NetworkId)}/owls/{Uri.EscapeDataString(camera.CameraId)}/thumbnail",
            "doorbell" =>
                $"api/v1/accounts/{Uri.EscapeDataString(token.AccountId)}/networks/" +
                $"{Uri.EscapeDataString(camera.NetworkId)}/doorbells/{Uri.EscapeDataString(camera.CameraId)}/thumbnail",
            _ => $"network/{Uri.EscapeDataString(camera.NetworkId)}/camera/{Uri.EscapeDataString(camera.CameraId)}/thumbnail",
        };
        commandUri = new Uri(apiBaseAddress, path);
        return true;
    }

    private static bool TryCreateThumbnailUri(
        BlinkAccessToken token,
        BlinkRemoteCamera camera,
        out Uri thumbnailUri)
    {
        thumbnailUri = null!;
        if (!Uri.TryCreate(token.ApiBaseAddress, UriKind.Absolute, out var apiBaseAddress))
        {
            return false;
        }

        if (TryGetNumericThumbnailTimestamp(camera.Thumbnail, out var timestamp) &&
            !string.IsNullOrWhiteSpace(camera.NetworkId) &&
            !string.IsNullOrWhiteSpace(camera.Model))
        {
            thumbnailUri = new Uri(
                apiBaseAddress,
                $"api/v3/media/accounts/{Uri.EscapeDataString(token.AccountId)}/networks/" +
                $"{Uri.EscapeDataString(camera.NetworkId)}/{Uri.EscapeDataString(camera.Model)}/" +
                $"{Uri.EscapeDataString(camera.CameraId)}/thumbnail/thumbnail.jpg?ts={timestamp}&ext=");
            return true;
        }

        if (string.IsNullOrWhiteSpace(camera.Thumbnail) ||
            !camera.Thumbnail.StartsWith("/", StringComparison.Ordinal) ||
            camera.Thumbnail.StartsWith("//", StringComparison.Ordinal))
        {
            return false;
        }

        var legacyPath = camera.Thumbnail.EndsWith("&ext=", StringComparison.Ordinal)
            ? camera.Thumbnail
            : $"{camera.Thumbnail}.jpg";
        thumbnailUri = new Uri(apiBaseAddress, legacyPath);
        return thumbnailUri.Scheme == Uri.UriSchemeHttps &&
            string.Equals(thumbnailUri.Host, apiBaseAddress.Host, StringComparison.OrdinalIgnoreCase);
    }

    private static Uri CreateUncachedThumbnailUri(Uri thumbnailUri)
    {
        var builder = new UriBuilder(thumbnailUri);
        var query = builder.Query.TrimStart('?');
        builder.Query = string.IsNullOrEmpty(query)
            ? $"cache_bust={Guid.NewGuid():N}"
            : $"{query}&cache_bust={Guid.NewGuid():N}";
        return builder.Uri;
    }

    private static bool TryGetNumericThumbnailTimestamp(string? value, out long timestamp) =>
        long.TryParse(
            value,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out timestamp) &&
        timestamp >= 0;

    private static void AddWebHeaders(HttpRequestMessage request, string referer)
    {
        request.Headers.UserAgent.ParseAdd(OAuthUserAgent);
        request.Headers.Accept.ParseAdd("*/*");
        request.Headers.TryAddWithoutValidation("Origin", "https://api.oauth.blink.com");
        request.Headers.Referrer = new Uri(referer);
    }

    private static void AddNavigationHeaders(HttpRequestMessage request)
    {
        request.Headers.UserAgent.ParseAdd(OAuthUserAgent);
        request.Headers.Accept.ParseAdd(
            "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        request.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
    }

    private CancellationTokenSource CreateTimeout(CancellationToken cancellationToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.RequestTimeout);
        return timeout;
    }

    private static BlinkConnectionResult Failed(HttpStatusCode status, string detail) =>
        new(ToStatus(status), Detail: $"{detail} HTTP {(int)status} ({status}).");

    private static DeviceOperationStatus ToStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => DeviceOperationStatus.Unauthorized,
        HttpStatusCode.NotFound => DeviceOperationStatus.ResourceNotFound,
        _ => DeviceOperationStatus.BridgeUnavailable,
    };

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or
            HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or
            HttpStatusCode.PermanentRedirect;

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(name, out var property))
        {
            return false;
        }

        value = property.ValueKind switch
        {
            JsonValueKind.String => property.GetString() ?? string.Empty,
            JsonValueKind.Number => property.GetRawText(),
            _ => string.Empty,
        };
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryGetBoolean(JsonElement element, string name, out bool value)
    {
        value = false;
        if (!element.TryGetProperty(name, out var property) ||
            property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        value = property.GetBoolean();
        return true;
    }

    private static bool TryGetInt32(JsonElement element, string name, out int value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt32(out value);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
