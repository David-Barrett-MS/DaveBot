using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

[assembly: InternalsVisibleTo("DaveBrain")]
[assembly: InternalsVisibleTo("DaveBrain.Tests")]

namespace DaveHome;

public sealed record PhilipsAirPlusOptions
{
    public string ClientId { get; init; } = "-XsK7O6iEkLml77yDGDUi0ku";
    public string GigyaApiKey { get; init; } = "4_JGZWlP8eQHpEqkvQElolbA";
    public string RedirectUri { get; init; } = "com.philips.air://loginredirect";
    public Uri AuthorizationEndpoint { get; init; } =
        new("https://cdc.accounts.home.id/oidc/op/v1.0/4_JGZWlP8eQHpEqkvQElolbA/authorize");
    public Uri TokenEndpoint { get; init; } =
        new("https://cdc.accounts.home.id/oidc/op/v1.0/4_JGZWlP8eQHpEqkvQElolbA/token");
    public Uri EmailOtpSendEndpoint { get; init; } =
        new("https://cdc.accounts.home.id/accounts.auth.otp.email.sendCode");
    public Uri EmailOtpLoginEndpoint { get; init; } =
        new("https://cdc.accounts.home.id/accounts.auth.otp.email.login");
    public Uri GigyaGetIdsEndpoint { get; init; } =
        new("https://cdc.accounts.home.id/socialize.getIDs");
    public Uri ApiBaseUri { get; init; } = new("https://prod.eu-da.iot.versuni.com/");
    public Uri MqttUri { get; init; } = new("wss://ats.prod.eu-da.iot.versuni.com:443/mqtt");
    public string Scope { get; init; } =
        "openid email profile address DI.Account.read DI.Account.write " +
        "DI.AccountProfile.read DI.AccountProfile.write DI.AccountGeneralConsent.read " +
        "DI.AccountGeneralConsent.write DI.GeneralConsent.read subscriptions profile_extended " +
        "consents DI.AccountSubscription.read DI.AccountSubscription.write";
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public void Validate()
    {
        if (!Uri.TryCreate(RedirectUri, UriKind.Absolute, out var redirect) ||
            redirect.Scheme != "com.philips.air" ||
            !string.Equals(redirect.Host, "loginredirect", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "PhilipsAirPlus:RedirectUri must be the registered com.philips.air://loginredirect URI.");
        }

        if (AuthorizationEndpoint.Scheme != Uri.UriSchemeHttps ||
            TokenEndpoint.Scheme != Uri.UriSchemeHttps ||
            EmailOtpSendEndpoint.Scheme != Uri.UriSchemeHttps ||
            EmailOtpLoginEndpoint.Scheme != Uri.UriSchemeHttps ||
            GigyaGetIdsEndpoint.Scheme != Uri.UriSchemeHttps ||
            ApiBaseUri.Scheme != Uri.UriSchemeHttps ||
            MqttUri.Scheme != "wss" ||
            string.IsNullOrWhiteSpace(Scope) ||
            RequestTimeout <= TimeSpan.Zero ||
            RequestTimeout > TimeSpan.FromMinutes(2))
        {
            throw new InvalidOperationException("Philips Air+ endpoint configuration is invalid.");
        }
    }
}

// Credentials remain opaque outside the trusted home and brain assemblies.
public sealed record PhilipsAirPlusAccessToken
{
    public PhilipsAirPlusAccessToken()
    {
    }

    internal PhilipsAirPlusAccessToken(
        string accessToken, string refreshToken, DateTimeOffset expiresAtUtc)
    {
        AccessToken = accessToken;
        RefreshToken = refreshToken;
        ExpiresAtUtc = expiresAtUtc;
    }

    [JsonInclude]
    internal string AccessToken { get; init; } = string.Empty;
    [JsonInclude]
    internal string RefreshToken { get; init; } = string.Empty;
    [JsonInclude]
    internal DateTimeOffset ExpiresAtUtc { get; init; }
}

public sealed class PhilipsAirPlusRemoteDevice
{
    public PhilipsAirPlusRemoteDevice(string deviceId, string friendlyName, string? productType)
        : this(deviceId, friendlyName, productType, string.Empty)
    {
    }

    internal PhilipsAirPlusRemoteDevice(
        string deviceId, string friendlyName, string? productType, string thingName)
    {
        DeviceId = deviceId;
        FriendlyName = friendlyName;
        ProductType = productType;
        ThingName = thingName;
    }

    public string DeviceId { get; }
    public string FriendlyName { get; }
    public string? ProductType { get; }
    internal string ThingName { get; }
}

public sealed record PhilipsAirPlusRequestResult<T>(
    DeviceOperationStatus Status,
    T? Value = default,
    string? Detail = null)
{
    public bool Succeeded => Status == DeviceOperationStatus.Succeeded;
}

// The verification token is intentionally inaccessible outside this client.
public sealed class PhilipsAirPlusEmailVerificationTicket
{
    internal PhilipsAirPlusEmailVerificationTicket(string email, string verificationToken)
    {
        Email = email;
        VerificationToken = verificationToken;
    }

    public PhilipsAirPlusEmailVerificationTicket()
    {
    }

    internal string? Email { get; }
    internal string? VerificationToken { get; }
}

public interface IPhilipsAirPlusRegistrationHttpClientFactory
{
    HttpClient CreateClient();
}

public sealed class PhilipsAirPlusRegistrationHttpClientFactory :
    IPhilipsAirPlusRegistrationHttpClientFactory
{
    public HttpClient CreateClient() => new(new HttpClientHandler
    {
        UseCookies = true,
        CookieContainer = new CookieContainer(),
        AllowAutoRedirect = false,
    }, disposeHandler: true)
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };
}

public interface IPhilipsAirPlusClient
{
    Task<PhilipsAirPlusRequestResult<PhilipsAirPlusEmailVerificationTicket>> RequestEmailVerificationAsync(
        string email,
        CancellationToken cancellationToken = default);

    Task<PhilipsAirPlusRequestResult<PhilipsAirPlusAccessToken>> CompleteEmailVerificationAsync(
        string code,
        PhilipsAirPlusEmailVerificationTicket verificationTicket,
        CancellationToken cancellationToken = default);

    Task<PhilipsAirPlusRequestResult<PhilipsAirPlusAccessToken>> RefreshTokenAsync(
        PhilipsAirPlusAccessToken token,
        CancellationToken cancellationToken = default);

    Task<PhilipsAirPlusRequestResult<string>> GetAccountIdAsync(
        string accessToken,
        CancellationToken cancellationToken = default);

    Task<PhilipsAirPlusRequestResult<IReadOnlyList<PhilipsAirPlusRemoteDevice>>> GetDevicesAsync(
        string accessToken,
        CancellationToken cancellationToken = default);

    Task<PhilipsAirPlusRequestResult<JsonElement>> GetShadowAsync(
        string accessToken,
        PhilipsAirPlusRemoteDevice device,
        CancellationToken cancellationToken = default);

    Task<PhilipsAirPlusRequestResult<JsonElement>> GetDeviceDataAsync(
        string accessToken,
        PhilipsAirPlusRemoteDevice device,
        CancellationToken cancellationToken = default) =>
        GetShadowAsync(accessToken, device, cancellationToken);

    Task<PhilipsAirPlusRequestResult<JsonElement>> SetFanSpeedAsync(
        string accessToken,
        PhilipsAirPlusRemoteDevice device,
        int fanSpeed,
        CancellationToken cancellationToken = default);
}

public sealed class PhilipsAirPlusClient : IPhilipsAirPlusClient
{
    private static readonly string[] EnvironmentalNcpPorts = ["Status", "filtRd", "devRd", "Config"];

    private const int MaximumWebSocketMessageBytes = 65_536;
    private const int MaximumAuthenticationResponseBytes = 65_536;
    private readonly HttpClient httpClient;
    private readonly PhilipsAirPlusOptions options;
    private readonly IPhilipsAirPlusRegistrationHttpClientFactory registrationHttpClientFactory;

    internal PhilipsAirPlusClient(HttpClient httpClient, PhilipsAirPlusOptions options)
        : this(httpClient, options, new PhilipsAirPlusRegistrationHttpClientFactory())
    {
    }

    public PhilipsAirPlusClient(
        HttpClient httpClient,
        PhilipsAirPlusOptions options,
        IPhilipsAirPlusRegistrationHttpClientFactory registrationHttpClientFactory)
    {
        this.httpClient = httpClient;
        this.options = options;
        this.registrationHttpClientFactory = registrationHttpClientFactory;
    }

    public async Task<PhilipsAirPlusRequestResult<PhilipsAirPlusEmailVerificationTicket>>
        RequestEmailVerificationAsync(string email, CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, options.EmailOtpSendEndpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["email"] = email,
                    ["apiKey"] = options.GigyaApiKey,
                    ["format"] = "json",
                }),
            };
            using var timeout = CreateTimeout(cancellationToken);
            using var response = await httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                return new PhilipsAirPlusRequestResult<PhilipsAirPlusEmailVerificationTicket>(
                    ToStatus(response.StatusCode), Detail: "Email verification could not be requested.");
            }

            using var document = await ReadAuthenticationJsonAsync(response, timeout.Token);
            if (!HasSuccessErrorCode(document.RootElement) ||
                !TryGetString(document.RootElement, "vToken", out var verificationToken))
            {
                return new PhilipsAirPlusRequestResult<PhilipsAirPlusEmailVerificationTicket>(
                    ToStatus(response.StatusCode), Detail: "Email verification could not be requested.");
            }

            return new PhilipsAirPlusRequestResult<PhilipsAirPlusEmailVerificationTicket>(
                DeviceOperationStatus.Succeeded,
                new PhilipsAirPlusEmailVerificationTicket(email, verificationToken));
        }
        catch (HttpRequestException)
        {
            return new PhilipsAirPlusRequestResult<PhilipsAirPlusEmailVerificationTicket>(
                DeviceOperationStatus.BridgeUnavailable, Detail: "Philips Air+ could not be reached.");
        }
        catch (JsonException)
        {
            return new PhilipsAirPlusRequestResult<PhilipsAirPlusEmailVerificationTicket>(
                DeviceOperationStatus.InvalidState, Detail: "The email verification response was invalid.");
        }
        catch (InvalidDataException)
        {
            return new PhilipsAirPlusRequestResult<PhilipsAirPlusEmailVerificationTicket>(
                DeviceOperationStatus.InvalidState, Detail: "The email verification response was too large.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new PhilipsAirPlusRequestResult<PhilipsAirPlusEmailVerificationTicket>(
                DeviceOperationStatus.TimedOut, Detail: "Email verification timed out.");
        }
    }

    public async Task<PhilipsAirPlusRequestResult<PhilipsAirPlusAccessToken>>
        CompleteEmailVerificationAsync(
            string code,
            PhilipsAirPlusEmailVerificationTicket verificationTicket,
            CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(verificationTicket.Email) ||
            string.IsNullOrWhiteSpace(verificationTicket.VerificationToken))
        {
            return new PhilipsAirPlusRequestResult<PhilipsAirPlusAccessToken>(
                DeviceOperationStatus.InvalidState, Detail: "The email verification request is invalid.");
        }

        using var registrationClient = registrationHttpClientFactory.CreateClient();
        try
        {
            var sessionToken = await VerifyEmailCodeAsync(
                registrationClient, verificationTicket.Email, code,
                verificationTicket.VerificationToken, cancellationToken);
            if (!sessionToken.Succeeded || sessionToken.Value is null)
            {
                return new PhilipsAirPlusRequestResult<PhilipsAirPlusAccessToken>(
                    sessionToken.Status, Detail: sessionToken.Detail);
            }

            var authorization = await AuthorizeEmailSessionAsync(
                registrationClient, sessionToken.Value, cancellationToken);
            if (!authorization.Succeeded || authorization.Value is null)
            {
                return new PhilipsAirPlusRequestResult<PhilipsAirPlusAccessToken>(
                    authorization.Status, Detail: authorization.Detail);
            }

            return await RequestTokenAsync(
                registrationClient,
                new Dictionary<string, string>
                {
                    ["client_id"] = options.ClientId,
                    ["grant_type"] = "authorization_code",
                    ["code"] = authorization.Value.Code,
                    ["redirect_uri"] = options.RedirectUri,
                    ["code_verifier"] = authorization.Value.CodeVerifier,
                },
                cancellationToken);
        }
        catch (HttpRequestException)
        {
            return new PhilipsAirPlusRequestResult<PhilipsAirPlusAccessToken>(
                DeviceOperationStatus.BridgeUnavailable, Detail: "Philips Air+ could not be reached.");
        }
        catch (JsonException)
        {
            return new PhilipsAirPlusRequestResult<PhilipsAirPlusAccessToken>(
                DeviceOperationStatus.InvalidState, Detail: "The email verification response was invalid.");
        }
        catch (InvalidDataException)
        {
            return new PhilipsAirPlusRequestResult<PhilipsAirPlusAccessToken>(
                DeviceOperationStatus.InvalidState, Detail: "The email verification response was too large.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new PhilipsAirPlusRequestResult<PhilipsAirPlusAccessToken>(
                DeviceOperationStatus.TimedOut, Detail: "Email verification timed out.");
        }
    }

    public async Task<PhilipsAirPlusRequestResult<PhilipsAirPlusAccessToken>> RefreshTokenAsync(
        PhilipsAirPlusAccessToken token,
        CancellationToken cancellationToken = default) =>
        await RequestTokenAsync(
            httpClient,
            new Dictionary<string, string>
            {
                ["client_id"] = options.ClientId,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = token.RefreshToken,
                ["access_token"] = token.AccessToken,
            },
            cancellationToken,
            token.RefreshToken);

    public async Task<PhilipsAirPlusRequestResult<string>> GetAccountIdAsync(
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        var response = await GetJsonAsync("/api/da/user/self", accessToken, cancellationToken);
        if (!response.Succeeded || response.Value is not { } document)
        {
            return new PhilipsAirPlusRequestResult<string>(response.Status, Detail: response.Detail);
        }

        using (document)
        {
            return document.RootElement.TryGetProperty("id", out var id) &&
                   id.ValueKind == JsonValueKind.String &&
                   !string.IsNullOrWhiteSpace(id.GetString())
                ? new PhilipsAirPlusRequestResult<string>(
                    DeviceOperationStatus.Succeeded,
                    id.GetString()!)
                : new PhilipsAirPlusRequestResult<string>(
                    DeviceOperationStatus.InvalidState,
                    Detail: "The Philips Air+ account response did not contain an account ID.");
        }
    }

    public async Task<PhilipsAirPlusRequestResult<IReadOnlyList<PhilipsAirPlusRemoteDevice>>>
        GetDevicesAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        var response = await GetJsonAsync("/api/da/user/self/device", accessToken, cancellationToken);
        if (!response.Succeeded || response.Value is not { } document)
        {
            return new PhilipsAirPlusRequestResult<IReadOnlyList<PhilipsAirPlusRemoteDevice>>(
                response.Status, Detail: response.Detail);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return new PhilipsAirPlusRequestResult<IReadOnlyList<PhilipsAirPlusRemoteDevice>>(
                    DeviceOperationStatus.InvalidState,
                    Detail: "The Philips Air+ device response was invalid.");
            }

            var devices = new List<PhilipsAirPlusRemoteDevice>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !TryGetString(item, "id", out var deviceId) ||
                    !TryGetString(item, "thingName", out var thingName))
                {
                    continue;
                }

                var name = TryGetString(item, "friendlyName", out var friendlyName)
                    ? friendlyName
                    : deviceId;
                TryGetString(item, "ctn", out var productType);
                devices.Add(new PhilipsAirPlusRemoteDevice(deviceId, name, productType, thingName));
            }

            return new PhilipsAirPlusRequestResult<IReadOnlyList<PhilipsAirPlusRemoteDevice>>(
                DeviceOperationStatus.Succeeded,
                devices);
        }
    }

    public Task<PhilipsAirPlusRequestResult<JsonElement>> GetShadowAsync(
        string accessToken,
        PhilipsAirPlusRemoteDevice device,
        CancellationToken cancellationToken = default) =>
        ShadowRoundTripAsync(accessToken, device, "get", "{}", cancellationToken);

    public async Task<PhilipsAirPlusRequestResult<JsonElement>> GetDeviceDataAsync(
        string accessToken,
        PhilipsAirPlusRemoteDevice device,
        CancellationToken cancellationToken = default)
    {
        var shadow = await GetShadowAsync(accessToken, device, cancellationToken);
        if (!shadow.Succeeded)
        {
            return shadow;
        }

        var portResults = await Task.WhenAll(EnvironmentalNcpPorts.Select(
            portName => GetNcpPortAsync(accessToken, device, portName, cancellationToken)));

        var ncpPorts = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        for (var i = 0; i < EnvironmentalNcpPorts.Length; i++)
        {
            var port = portResults[i];
            if (port.Succeeded && port.Value.ValueKind == JsonValueKind.Object)
            {
                ncpPorts.Add(EnvironmentalNcpPorts[i], port.Value);
            }
        }

        return ncpPorts.Count == 0
            ? shadow
            : new PhilipsAirPlusRequestResult<JsonElement>(
                DeviceOperationStatus.Succeeded,
                AddNcpPorts(shadow.Value, ncpPorts));
    }

    public Task<PhilipsAirPlusRequestResult<JsonElement>> SetFanSpeedAsync(
        string accessToken,
        PhilipsAirPlusRemoteDevice device,
        int fanSpeed,
        CancellationToken cancellationToken = default) =>
        ShadowRoundTripAsync(
            accessToken,
            device,
            "update",
            $"{{\"state\":{{\"desired\":{{\"om\":{fanSpeed}}}}}}}",
            cancellationToken);

    private async Task<PhilipsAirPlusRequestResult<string>> VerifyEmailCodeAsync(
        HttpClient registrationClient,
        string email,
        string code,
        string verificationToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, options.EmailOtpLoginEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["email"] = email,
                ["code"] = code,
                ["vToken"] = verificationToken,
                ["apiKey"] = options.GigyaApiKey,
                ["format"] = "json",
            }),
        };
        using var timeout = CreateTimeout(cancellationToken);
        using var response = await registrationClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            return new PhilipsAirPlusRequestResult<string>(
                ToStatus(response.StatusCode), Detail: "Email verification was rejected.");
        }

        using var document = await ReadAuthenticationJsonAsync(response, timeout.Token);
        if (!HasSuccessErrorCode(document.RootElement) ||
            !document.RootElement.TryGetProperty("sessionInfo", out var sessionInfo) ||
            !TryGetString(sessionInfo, "cookieValue", out var sessionToken))
        {
            return new PhilipsAirPlusRequestResult<string>(
                DeviceOperationStatus.InvalidState, Detail: "Email verification was rejected.");
        }

        return new PhilipsAirPlusRequestResult<string>(DeviceOperationStatus.Succeeded, sessionToken);
    }

    private async Task<PhilipsAirPlusRequestResult<AuthorizationCode>> AuthorizeEmailSessionAsync(
        HttpClient registrationClient,
        string sessionToken,
        CancellationToken cancellationToken)
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var authorizeUri = AddQuery(options.AuthorizationEndpoint, new Dictionary<string, string>
        {
            ["client_id"] = options.ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = options.RedirectUri,
            ["scope"] = options.Scope,
            ["state"] = Base64Url(RandomNumberGenerator.GetBytes(32)),
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["prompt"] = "none",
        });

        using var timeout = CreateTimeout(cancellationToken);
        using var authorizeRequest = new HttpRequestMessage(HttpMethod.Get, authorizeUri);
        using var authorizeResponse = await registrationClient.SendAsync(
            authorizeRequest, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!IsRedirect(authorizeResponse.StatusCode) ||
            !TryGetLocationQueryValue(authorizeResponse, "context", out var context))
        {
            return new PhilipsAirPlusRequestResult<AuthorizationCode>(
                DeviceOperationStatus.InvalidState, Detail: "Philips Air+ authorization could not be completed.");
        }

        using var idsRequest = new HttpRequestMessage(HttpMethod.Post, options.GigyaGetIdsEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["APIKey"] = options.GigyaApiKey,
                ["includeTicket"] = "true",
                ["format"] = "json",
            }),
        };
        using var idsResponse = await registrationClient.SendAsync(
            idsRequest, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!idsResponse.IsSuccessStatusCode)
        {
            return new PhilipsAirPlusRequestResult<AuthorizationCode>(
                ToStatus(idsResponse.StatusCode), Detail: "Philips Air+ authorization could not be completed.");
        }

        using var ids = await ReadAuthenticationJsonAsync(idsResponse, timeout.Token);
        if (!TryGetString(ids.RootElement, "gmidTicket", out var gmidTicket))
        {
            return new PhilipsAirPlusRequestResult<AuthorizationCode>(
                DeviceOperationStatus.InvalidState, Detail: "Philips Air+ authorization could not be completed.");
        }

        var continueEndpoint = new Uri(options.AuthorizationEndpoint.AbsoluteUri.TrimEnd('/') + "/continue");
        using var continueRequest = new HttpRequestMessage(HttpMethod.Get, AddQuery(continueEndpoint,
            new Dictionary<string, string>
            {
                ["context"] = context,
                ["login_token"] = sessionToken,
                ["gmidTicket"] = gmidTicket,
                ["client_id"] = options.ClientId,
            }));
        using var continueResponse = await registrationClient.SendAsync(
            continueRequest, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!IsRedirect(continueResponse.StatusCode) ||
            !TryGetLocationQueryValue(continueResponse, "code", out var code))
        {
            return new PhilipsAirPlusRequestResult<AuthorizationCode>(
                DeviceOperationStatus.InvalidState, Detail: "Philips Air+ authorization could not be completed.");
        }

        return new PhilipsAirPlusRequestResult<AuthorizationCode>(
            DeviceOperationStatus.Succeeded, new AuthorizationCode(code, verifier));
    }

    private async Task<PhilipsAirPlusRequestResult<PhilipsAirPlusAccessToken>> RequestTokenAsync(
        HttpClient client,
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken,
        string? fallbackRefreshToken = null)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, options.TokenEndpoint)
            {
                Content = new FormUrlEncodedContent(values),
            };
            using var timeout = CreateTimeout(cancellationToken);
            using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                return new PhilipsAirPlusRequestResult<PhilipsAirPlusAccessToken>(
                    ToStatus(response.StatusCode), Detail: "Philips Air+ token exchange was rejected.");
            }

            using var document = await ReadAuthenticationJsonAsync(response, timeout.Token);
            if (!TryGetString(document.RootElement, "access_token", out var accessToken))
            {
                return new PhilipsAirPlusRequestResult<PhilipsAirPlusAccessToken>(
                    DeviceOperationStatus.InvalidState, Detail: "The token response was invalid.");
            }

            var refreshToken = TryGetString(document.RootElement, "refresh_token", out var refreshed)
                ? refreshed
                : fallbackRefreshToken;
            if (string.IsNullOrWhiteSpace(refreshToken) ||
                !document.RootElement.TryGetProperty("expires_in", out var expires) ||
                !expires.TryGetInt32(out var expiresSeconds) || expiresSeconds is < 61 or > 2_592_000)
            {
                return new PhilipsAirPlusRequestResult<PhilipsAirPlusAccessToken>(
                    DeviceOperationStatus.InvalidState, Detail: "The token response was incomplete.");
            }

            return new PhilipsAirPlusRequestResult<PhilipsAirPlusAccessToken>(
                DeviceOperationStatus.Succeeded,
                new PhilipsAirPlusAccessToken(
                    accessToken,
                    refreshToken,
                    DateTimeOffset.UtcNow.AddSeconds(expiresSeconds)));
        }
        catch (HttpRequestException)
        {
            return new PhilipsAirPlusRequestResult<PhilipsAirPlusAccessToken>(
                DeviceOperationStatus.BridgeUnavailable, Detail: "Philips Air+ could not be reached.");
        }
        catch (JsonException)
        {
            return new PhilipsAirPlusRequestResult<PhilipsAirPlusAccessToken>(
                DeviceOperationStatus.InvalidState, Detail: "The token response was invalid.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new PhilipsAirPlusRequestResult<PhilipsAirPlusAccessToken>(
                DeviceOperationStatus.TimedOut, Detail: "Philips Air+ token exchange timed out.");
        }
    }

    private static bool HasSuccessErrorCode(JsonElement element) =>
        element.TryGetProperty("errorCode", out var errorCode) &&
        errorCode.ValueKind == JsonValueKind.Number &&
        errorCode.TryGetInt32(out var code) && code == 0;

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or
            HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or
            HttpStatusCode.PermanentRedirect;

    private static Uri AddQuery(Uri endpoint, IReadOnlyDictionary<string, string> values)
    {
        var builder = new UriBuilder(endpoint)
        {
            Query = string.Join("&", values.Select(pair =>
                $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}")),
        };
        return builder.Uri;
    }

    private static bool TryGetLocationQueryValue(
        HttpResponseMessage response, string name, out string value)
    {
        value = string.Empty;
        if (response.Headers.Location is not { } location ||
            !Uri.TryCreate(location.IsAbsoluteUri ? location.AbsoluteUri : location.OriginalString,
                UriKind.Absolute, out var redirect))
        {
            return false;
        }

        foreach (var pair in redirect.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (string.Equals(Uri.UnescapeDataString(separator < 0 ? pair : pair[..separator]),
                    name, StringComparison.Ordinal) &&
                separator >= 0)
            {
                value = Uri.UnescapeDataString(pair[(separator + 1)..]);
                return !string.IsNullOrWhiteSpace(value) && value.Length <= 4096;
            }
        }

        return false;
    }

    private static async Task<JsonDocument> ReadAuthenticationJsonAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (output.Length + read > MaximumAuthenticationResponseBytes)
            {
                throw new InvalidDataException();
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        return JsonDocument.Parse(output.ToArray());
    }

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record AuthorizationCode(string Code, string CodeVerifier);

    private async Task<PhilipsAirPlusRequestResult<JsonDocument>> GetJsonAsync(
        string path,
        string accessToken,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                new Uri(options.ApiBaseUri, path));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var timeout = CreateTimeout(cancellationToken);
            using var response = await httpClient.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                return new PhilipsAirPlusRequestResult<JsonDocument>(
                    ToStatus(response.StatusCode), Detail: "Philips Air+ rejected the request.");
            }

            return new PhilipsAirPlusRequestResult<JsonDocument>(
                DeviceOperationStatus.Succeeded,
                await ReadAuthenticationJsonAsync(response, timeout.Token));
        }
        catch (HttpRequestException)
        {
            return new PhilipsAirPlusRequestResult<JsonDocument>(
                DeviceOperationStatus.BridgeUnavailable, Detail: "Philips Air+ could not be reached.");
        }
        catch (JsonException)
        {
            return new PhilipsAirPlusRequestResult<JsonDocument>(
                DeviceOperationStatus.InvalidState, Detail: "The Philips Air+ response was invalid.");
        }
        catch (InvalidDataException)
        {
            return new PhilipsAirPlusRequestResult<JsonDocument>(
                DeviceOperationStatus.InvalidState, Detail: "The Philips Air+ response was too large.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new PhilipsAirPlusRequestResult<JsonDocument>(
                DeviceOperationStatus.TimedOut, Detail: "Philips Air+ request timed out.");
        }
    }

    private async Task<PhilipsAirPlusRequestResult<JsonElement>> ShadowRoundTripAsync(
        string accessToken,
        PhilipsAirPlusRemoteDevice device,
        string operation,
        string payload,
        CancellationToken cancellationToken)
    {
        return await ShadowRoundTripCoreAsync(
            accessToken,
            device,
            operation,
            payload,
            cancellationToken);
    }

        private async Task<PhilipsAirPlusRequestResult<JsonElement>> GetNcpPortAsync(
            string accessToken,
            PhilipsAirPlusRemoteDevice device,
            string portName,
            CancellationToken cancellationToken)
        {
            var account = await GetAccountIdAsync(accessToken, cancellationToken);
            if (!account.Succeeded || account.Value is null)
            {
                return new PhilipsAirPlusRequestResult<JsonElement>(account.Status, Detail: account.Detail);
            }

                var signature = await GetJsonAsync("/api/da/user/self/signature", accessToken, cancellationToken);
            if (!signature.Succeeded || signature.Value is not { } signatureDocument)
            {
                return new PhilipsAirPlusRequestResult<JsonElement>(
                    signature.Status,
                    Detail: signature.Detail);
            }

            string authorizerSignature;
            using (signatureDocument)
            {
                if (!TryGetString(signatureDocument.RootElement, "signature", out authorizerSignature))
                {
                    return new PhilipsAirPlusRequestResult<JsonElement>(
                        DeviceOperationStatus.InvalidState,
                        Detail: "The Philips Air+ signature response was invalid.");
                }
            }

            var responseTopic = $"da_ctrl/{device.ThingName}/from_ncp";
            var correlationId = Guid.NewGuid().ToString();
            var payload = JsonSerializer.Serialize(new
            {
                cid = correlationId,
                time = DateTimeOffset.UtcNow.ToString(
                    "yyyy-MM-dd'T'HH:mm:ss'Z'",
                    System.Globalization.CultureInfo.InvariantCulture),
                @type = "command",
                cn = "getPort",
                ct = "mobile",
                data = new { portName },
            });
            RuntimeAuditContext.Write(
                "External MQTT request",
                $"PUBLISH da_ctrl/[redacted-device]/to_ncp\n{payload}",
                "Awaiting response.");
            try
            {
                using var socket = new ClientWebSocket();
                socket.Options.AddSubProtocol("mqtt");
                socket.Options.SetRequestHeader("x-amz-customauthorizer-name", "CustomAuthorizer");
                socket.Options.SetRequestHeader("x-amz-customauthorizer-signature", authorizerSignature);
                socket.Options.SetRequestHeader("token-header", $"Bearer {accessToken}");
                socket.Options.SetRequestHeader("content-type", "application/json");
                socket.Options.SetRequestHeader("tenant", "da");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(options.RequestTimeout);
                await socket.ConnectAsync(options.MqttUri, timeout.Token);
                await SendAsync(socket, PhilipsAirPlusMqtt311.CreateConnect(
                    $"{account.Value}_{Guid.NewGuid():N}"), timeout.Token);
                if (!PhilipsAirPlusMqtt311.IsAcceptedConnection(await ReceivePacketAsync(socket, timeout.Token)))
                {
                    WriteNcpResult(portName, "MQTT connection was rejected.");
                    return new PhilipsAirPlusRequestResult<JsonElement>(
                        DeviceOperationStatus.Unauthorized,
                        Detail: "Philips Air+ MQTT connection was rejected.");
                }

                await SendAsync(
                    socket,
                    PhilipsAirPlusMqtt311.CreateSubscribe(1, [responseTopic]),
                    timeout.Token);
                if (!PhilipsAirPlusMqtt311.IsAcceptedSubscription(
                        await ReceivePacketAsync(socket, timeout.Token), 1))
                {
                    WriteNcpResult(portName, "MQTT subscription was rejected.");
                    return new PhilipsAirPlusRequestResult<JsonElement>(
                        DeviceOperationStatus.Unauthorized,
                        Detail: "Philips Air+ MQTT subscription was rejected.");
                }

                await SendAsync(
                    socket,
                    PhilipsAirPlusMqtt311.CreatePublish(
                        $"da_ctrl/{device.ThingName}/to_ncp",
                        Encoding.UTF8.GetBytes(payload)),
                    timeout.Token);
                while (true)
                {
                    var packet = await ReceivePacketAsync(socket, timeout.Token);
                    if (packet.PacketType == 12)
                    {
                        await SendAsync(socket, [0xD0, 0x00], timeout.Token);
                        continue;
                    }

                    if (!PhilipsAirPlusMqtt311.TryParsePublish(packet, out var topic, out var body) ||
                        topic != responseTopic)
                    {
                        continue;
                    }

                    RuntimeAuditContext.Write(
                        "External MQTT response",
                        "SUBSCRIBE da_ctrl/[redacted-device]/from_ncp",
                        Encoding.UTF8.GetString(body));
                    using var response = JsonDocument.Parse(body);
                    var root = response.RootElement;
                    if (!TryGetString(root, "cn", out var commandName) || commandName != "getPort" ||
                        (TryGetString(root, "cid", out var responseCorrelationId) &&
                         !correlationId.StartsWith(responseCorrelationId, StringComparison.Ordinal) &&
                         !responseCorrelationId.StartsWith(correlationId, StringComparison.Ordinal)))
                    {
                        continue;
                    }

                    if (root.TryGetProperty("status", out var status) &&
                        (!status.TryGetInt32(out var statusCode) || statusCode != 0))
                    {
                        WriteNcpResult(portName, "The device rejected the getPort command.");
                        return new PhilipsAirPlusRequestResult<JsonElement>(
                            DeviceOperationStatus.InvalidState,
                            Detail: "Philips Air+ rejected the environmental sensor request.");
                    }

                    if (!root.TryGetProperty("data", out var data) ||
                        data.ValueKind != JsonValueKind.Object ||
                        !TryGetString(data, "portName", out var responsePortName) ||
                        !string.Equals(responsePortName, portName, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    return new PhilipsAirPlusRequestResult<JsonElement>(
                        DeviceOperationStatus.Succeeded,
                        data.Clone());
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                WriteNcpResult(portName, "Timed out while waiting for the device response.");
                return new PhilipsAirPlusRequestResult<JsonElement>(
                    DeviceOperationStatus.TimedOut,
                    Detail: "Philips Air+ environmental sensor request timed out.");
            }
            catch (OperationCanceledException)
            {
                WriteNcpResult(portName, "The request was canceled while waiting for the device response.");
                return new PhilipsAirPlusRequestResult<JsonElement>(
                    DeviceOperationStatus.TimedOut,
                    Detail: "Philips Air+ environmental sensor request was canceled.");
            }
            catch (WebSocketException exception)
            {
                WriteNcpResult(
                    portName,
                    $"MQTT {exception.WebSocketErrorCode}: {exception.Message}");
                return new PhilipsAirPlusRequestResult<JsonElement>(
                    DeviceOperationStatus.BridgeUnavailable,
                    Detail: "Philips Air+ environmental sensor request could not connect.");
            }
            catch (JsonException)
            {
                WriteNcpResult(portName, "The device response was not valid JSON.");
                return new PhilipsAirPlusRequestResult<JsonElement>(
                    DeviceOperationStatus.InvalidState,
                    Detail: "The Philips Air+ environmental sensor response was invalid.");
            }
            catch (Exception exception) when (exception is IOException or SocketException)
            {
                WriteNcpResult(portName, $"The MQTT connection was aborted: {exception.Message}");
                return new PhilipsAirPlusRequestResult<JsonElement>(
                    DeviceOperationStatus.BridgeUnavailable,
                    Detail: "Philips Air+ environmental sensor request could not connect.");
            }
        }

        private static JsonElement AddNcpPorts(
            JsonElement shadow,
            IReadOnlyDictionary<string, JsonElement> ncpPorts)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                if (shadow.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in shadow.EnumerateObject())
                    {
                        property.WriteTo(writer);
                    }
                }
                else
                {
                    writer.WritePropertyName("shadow");
                    shadow.WriteTo(writer);
                }

                writer.WritePropertyName("ncp");
                writer.WriteStartObject();
                foreach (var (portName, port) in ncpPorts)
                {
                    writer.WritePropertyName(portName);
                    port.WriteTo(writer);
                }
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            using var document = JsonDocument.Parse(stream.ToArray());
            return document.RootElement.Clone();
        }

    private async Task<PhilipsAirPlusRequestResult<JsonElement>> ShadowRoundTripCoreAsync(
        string accessToken,
        PhilipsAirPlusRemoteDevice device,
        string operation,
        string payload,
        CancellationToken cancellationToken)
    {
        var account = await GetAccountIdAsync(accessToken, cancellationToken);
        if (!account.Succeeded || account.Value is null)
        {
            return new PhilipsAirPlusRequestResult<JsonElement>(account.Status, Detail: account.Detail);
        }

        var signature = await GetJsonAsync("/api/da/user/self/signature", accessToken, cancellationToken);
        if (!signature.Succeeded || signature.Value is not { } signatureDocument)
        {
            return new PhilipsAirPlusRequestResult<JsonElement>(
                signature.Status, Detail: signature.Detail);
        }

        string authorizerSignature;
        using (signatureDocument)
        {
            if (!TryGetString(signatureDocument.RootElement, "signature", out authorizerSignature))
            {
                return new PhilipsAirPlusRequestResult<JsonElement>(
                    DeviceOperationStatus.InvalidState,
                    Detail: "The Philips Air+ signature response was invalid.");
            }
        }

        var prefix = $"$aws/things/{device.ThingName}/shadow";
        RuntimeAuditContext.Write(
            "External MQTT request",
            $"PUBLISH $aws/things/[redacted-device]/shadow/{operation}\n{payload}",
            "Awaiting response.");
        try
        {
            using var socket = new ClientWebSocket();
            socket.Options.AddSubProtocol("mqtt");
            socket.Options.SetRequestHeader("x-amz-customauthorizer-name", "CustomAuthorizer");
            socket.Options.SetRequestHeader("x-amz-customauthorizer-signature", authorizerSignature);
            socket.Options.SetRequestHeader("token-header", $"Bearer {accessToken}");
            socket.Options.SetRequestHeader("token-header", $"Bearer {accessToken}");
            socket.Options.SetRequestHeader("token-header", $"Bearer {accessToken}");
            socket.Options.SetRequestHeader("content-type", "application/json");
            socket.Options.SetRequestHeader("tenant", "da");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.RequestTimeout);
            await socket.ConnectAsync(options.MqttUri, timeout.Token);
            await SendAsync(socket, PhilipsAirPlusMqtt311.CreateConnect(
                $"{account.Value}_{Guid.NewGuid():N}"), timeout.Token);
            if (!PhilipsAirPlusMqtt311.IsAcceptedConnection(await ReceivePacketAsync(socket, timeout.Token)))
            {
                WriteShadowResult(operation, "MQTT connection was rejected.");
                return new PhilipsAirPlusRequestResult<JsonElement>(
                    DeviceOperationStatus.Unauthorized, Detail: "Philips Air+ MQTT connection was rejected.");
            }

            var acceptedTopic = $"{prefix}/{operation}/accepted";
            var rejectedTopic = $"{prefix}/{operation}/rejected";
            await SendAsync(socket, PhilipsAirPlusMqtt311.CreateSubscribe(1, [acceptedTopic, rejectedTopic]), timeout.Token);
            if (!PhilipsAirPlusMqtt311.IsAcceptedSubscription(
                    await ReceivePacketAsync(socket, timeout.Token), 1))
            {
                WriteShadowResult(operation, "MQTT subscription was rejected.");
                return new PhilipsAirPlusRequestResult<JsonElement>(
                    DeviceOperationStatus.Unauthorized,
                    Detail: "Philips Air+ MQTT subscription was rejected.");
            }

            await SendAsync(socket, PhilipsAirPlusMqtt311.CreatePublish(
                $"{prefix}/{operation}", Encoding.UTF8.GetBytes(payload)), timeout.Token);
            while (true)
            {
                var packet = await ReceivePacketAsync(socket, timeout.Token);
                if (packet.PacketType == 12)
                {
                    await SendAsync(socket, [0xD0, 0x00], timeout.Token);
                    continue;
                }

                if (!PhilipsAirPlusMqtt311.TryParsePublish(packet, out var topic, out var body))
                {
                    continue;
                }

                if (topic == acceptedTopic || topic == rejectedTopic)
                {
                    RuntimeAuditContext.Write(
                        "External MQTT response",
                        $"SUBSCRIBE $aws/things/[redacted-device]/shadow/{operation}/accepted, " +
                        $"$aws/things/[redacted-device]/shadow/{operation}/rejected",
                        Encoding.UTF8.GetString(body));
                }

                if (topic == rejectedTopic)
                {
                    WriteShadowResult(operation, "The device rejected the shadow request.");
                    return new PhilipsAirPlusRequestResult<JsonElement>(
                        DeviceOperationStatus.InvalidState,
                        Detail: "Philips Air+ rejected the requested device state.");
                }

                if (topic != acceptedTopic)
                {
                    continue;
                }

                using var response = JsonDocument.Parse(body);
                return new PhilipsAirPlusRequestResult<JsonElement>(
                    DeviceOperationStatus.Succeeded,
                    response.RootElement.Clone());
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            WriteShadowResult(operation, "Timed out while waiting for the device response.");
            return new PhilipsAirPlusRequestResult<JsonElement>(
                DeviceOperationStatus.TimedOut, Detail: "Philips Air+ MQTT request timed out.");
        }
        catch (WebSocketException exception)
        {
            WriteShadowResult(
                operation,
                $"MQTT {exception.WebSocketErrorCode}: {exception.Message}");
            return new PhilipsAirPlusRequestResult<JsonElement>(
                DeviceOperationStatus.BridgeUnavailable, Detail: "Philips Air+ MQTT could not be reached.");
        }
        catch (JsonException)
        {
            WriteShadowResult(operation, "The device response was not valid JSON.");
            return new PhilipsAirPlusRequestResult<JsonElement>(
                DeviceOperationStatus.InvalidState, Detail: "The Philips Air+ shadow response was invalid.");
        }
    }

    private static async Task SendAsync(
        ClientWebSocket socket,
        byte[] packet,
        CancellationToken cancellationToken) =>
        await socket.SendAsync(packet, WebSocketMessageType.Binary, true, cancellationToken);

    private static void WriteNcpResult(string portName, string result) =>
        RuntimeAuditContext.Write(
            "External MQTT result",
            $"NCP getPort {portName}",
            result);

    private static void WriteShadowResult(string operation, string result) =>
        RuntimeAuditContext.Write(
            "External MQTT result",
            $"Shadow {operation}",
            result);

    private static async Task<PhilipsAirPlusMqttPacket> ReceivePacketAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var data = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new WebSocketException(
                    WebSocketError.ConnectionClosedPrematurely,
                    $"The MQTT server closed the connection with status {socket.CloseStatus}.");
            }

            if (result.MessageType != WebSocketMessageType.Binary)
            {
                throw new WebSocketException(
                    WebSocketError.InvalidMessageType,
                    $"Expected a binary MQTT frame but received {result.MessageType}.");
            }

            if (data.Length + result.Count > MaximumWebSocketMessageBytes)
            {
                throw new WebSocketException(
                    WebSocketError.InvalidMessageType,
                    "The MQTT frame exceeded the configured maximum size.");
            }

            await data.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken);
        }
        while (!result.EndOfMessage);

        if (!PhilipsAirPlusMqtt311.TryParsePacket(data.GetBuffer().AsSpan(0, checked((int)data.Length)), out var packet))
        {
            throw new WebSocketException(
                WebSocketError.Faulted,
                "The MQTT frame was malformed or contained more than one MQTT packet.");
        }

        return packet;
    }

    private static bool TryGetString(JsonElement element, string property, out string value)
    {
        value = string.Empty;
        return element.TryGetProperty(property, out var item) &&
            item.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(value = item.GetString() ?? string.Empty);
    }

    private CancellationTokenSource CreateTimeout(CancellationToken cancellationToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.RequestTimeout);
        return timeout;
    }

    private static DeviceOperationStatus ToStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => DeviceOperationStatus.Unauthorized,
        HttpStatusCode.NotFound => DeviceOperationStatus.ResourceNotFound,
        _ => DeviceOperationStatus.BridgeUnavailable,
    };
}

public readonly record struct PhilipsAirPlusMqttPacket(byte Header, byte[] Body)
{
    public int PacketType => Header >> 4;
}

public static class PhilipsAirPlusMqtt311
{
    public static byte[] CreateConnect(string clientId)
    {
        var variableHeader = Concat(
            EncodeString("MQTT"),
            [4, 2, 0, 30],
            EncodeString(clientId));
        return CreatePacket(0x10, variableHeader);
    }

    public static byte[] CreateSubscribe(ushort packetId, IReadOnlyList<string> topics)
    {
        if (topics.Count is < 1 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(topics));
        }

        var body = new List<byte> { (byte)(packetId >> 8), (byte)packetId };
        foreach (var topic in topics)
        {
            body.AddRange(EncodeString(topic));
            body.Add(0);
        }

        return CreatePacket(0x82, body.ToArray());
    }

    public static byte[] CreatePublish(string topic, ReadOnlySpan<byte> payload) =>
        CreatePacket(0x30, Concat(EncodeString(topic), payload.ToArray()));

    public static bool IsAcceptedConnection(PhilipsAirPlusMqttPacket packet) =>
        packet.Header == 0x20 && packet.Body.Length == 2 && packet.Body[0] == 0 && packet.Body[1] == 0;

    public static bool IsAcceptedSubscription(PhilipsAirPlusMqttPacket packet, ushort packetId) =>
        packet.Header == 0x90 && packet.Body.Length >= 3 &&
        packet.Body[0] == (byte)(packetId >> 8) && packet.Body[1] == (byte)packetId &&
        packet.Body[2..].All(code => code == 0);

    public static bool TryParsePublish(
        PhilipsAirPlusMqttPacket packet,
        out string topic,
        out byte[] payload)
    {
        topic = string.Empty;
        payload = Array.Empty<byte>();
        if (packet.PacketType != 3 || packet.Body.Length < 2)
        {
            return false;
        }

        var length = (packet.Body[0] << 8) | packet.Body[1];
        if (length == 0 || packet.Body.Length < length + 2)
        {
            return false;
        }

        try
        {
            topic = new UTF8Encoding(false, true).GetString(packet.Body, 2, length);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        var offset = length + 2;
        var qos = (packet.Header >> 1) & 3;
        if (qos > 0)
        {
            if (packet.Body.Length < offset + 2)
            {
                return false;
            }

            offset += 2;
        }

        payload = packet.Body[offset..];
        return true;
    }

    public static bool TryParsePacket(ReadOnlySpan<byte> data, out PhilipsAirPlusMqttPacket packet)
    {
        packet = default;
        if (data.Length < 2)
        {
            return false;
        }

        var multiplier = 1;
        var remaining = 0;
        var offset = 1;
        for (var count = 0; count < 4; count++)
        {
            if (offset >= data.Length)
            {
                return false;
            }

            var digit = data[offset++];
            remaining += (digit & 0x7f) * multiplier;
            if ((digit & 0x80) == 0)
            {
                if (remaining != data.Length - offset)
                {
                    return false;
                }

                packet = new PhilipsAirPlusMqttPacket(data[0], data[offset..].ToArray());
                return true;
            }

            multiplier *= 128;
        }

        return false;
    }

    private static byte[] CreatePacket(byte header, byte[] body) =>
        Concat([header], EncodeRemainingLength(body.Length), body);

    private static byte[] EncodeString(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("An MQTT string is required.", nameof(value));
        }

        var data = new UTF8Encoding(false, true).GetBytes(value);
        if (data.Length > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        return Concat([(byte)(data.Length >> 8), (byte)data.Length], data);
    }

    private static byte[] EncodeRemainingLength(int length)
    {
        if (length is < 0 or > 268_435_455)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        var output = new List<byte>(4);
        do
        {
            var digit = length % 128;
            length /= 128;
            if (length > 0)
            {
                digit |= 0x80;
            }

            output.Add((byte)digit);
        }
        while (length > 0);
        return output.ToArray();
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var length = parts.Sum(part => part.Length);
        var output = new byte[length];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(output, offset);
            offset += part.Length;
        }

        return output;
    }
}

public static class PhilipsAirPlusShadowMapper
{
    private static readonly (string Attribute, string[] Fields)[] AttributeFields =
    [
        ("pm25UgM3", ["pm25"]),
        ("allergenIndex", ["iaql", "iaql_allergen", "allergens"]),
        ("airQualityIndex", ["aqi", "aqil"]),
        ("airQualityIndexTarget", ["aqit"]),
        ("gasLevel", ["gas", "gaslvl"]),
        ("temperatureC", ["temp", "temperature"]),
        ("humidityPercent", ["rh"]),
        ("humiditySetpointPercent", ["rhset"]),
        ("filterStatus0", ["fltsts0"]),
        ("filterStatus1", ["fltsts1"]),
        ("filterStatus2", ["fltsts2"]),
        ("productState", ["productState"]),
        ("productError", ["productError", "err"]),
        ("fanSpeed", ["om"]),
    ];

    private static readonly (string Attribute, string Port, string Field)[] NcpAttributeFields =
    [
        ("pm25UgM3", "Status", "D03221"),
        ("allergenIndex", "Status", "D03120"),
        ("displayBrightnessPercent", "Status", "D03105"),
        ("fanLevel", "Status", "D0310D"),
        ("mainFilterRemainingHours", "filtRd", "D0540E"),
        ("preFilterRemainingHours", "filtRd", "D0520D"),
    ];

    public static DeviceState Map(JsonElement shadow)
    {
        if (shadow.ValueKind != JsonValueKind.Object)
        {
            return new DeviceState(null, null);
        }

        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (attribute, fields) in AttributeFields)
        {
            foreach (var field in fields)
            {
                if (TryGetStateValue(shadow, field, out var value) && TryGetScalar(value, out var scalar))
                {
                    attributes.Add(attribute, scalar);
                    break;
                }
            }
        }

        foreach (var (attribute, port, field) in NcpAttributeFields)
        {
            if (!attributes.ContainsKey(attribute) &&
                TryGetNcpValue(shadow, port, field, out var value) &&
                TryGetScalar(value, out var scalar))
            {
                attributes.Add(attribute, scalar);
            }
        }

        bool? isOn = null;
        if (TryGetStateValue(shadow, "powerOn", out var powerOn) &&
            TryGetBoolean(powerOn, out var parsedPower))
        {
            isOn = parsedPower;
        }
        else if (TryGetStateValue(shadow, "pwr", out var pwr) && TryGetBoolean(pwr, out parsedPower))
        {
            isOn = parsedPower;
        }

        return new DeviceState(isOn, null, Attributes: attributes.Count == 0 ? null : attributes);
    }

    private static bool TryGetStateValue(
        JsonElement shadow,
        string field,
        out JsonElement value)
    {
        value = default;
        if (shadow.ValueKind == JsonValueKind.Object &&
            shadow.TryGetProperty("state", out var state) &&
            state.ValueKind == JsonValueKind.Object)
        {
            if (state.TryGetProperty("reported", out var reported) &&
                reported.ValueKind == JsonValueKind.Object &&
                reported.TryGetProperty(field, out value))
            {
                return true;
            }

            if (state.TryGetProperty("desired", out var desired) &&
                desired.ValueKind == JsonValueKind.Object)
            {
                return desired.TryGetProperty(field, out value);
            }
        }

        return shadow.ValueKind == JsonValueKind.Object &&
            shadow.TryGetProperty(field, out value);
    }

    private static bool TryGetNcpValue(
        JsonElement shadow,
        string portName,
        string field,
        out JsonElement value)
    {
        value = default;
        return shadow.ValueKind == JsonValueKind.Object &&
            shadow.TryGetProperty("ncp", out var ncp) &&
            ncp.ValueKind == JsonValueKind.Object &&
            ncp.TryGetProperty(portName, out var port) &&
            port.ValueKind == JsonValueKind.Object &&
            port.TryGetProperty("properties", out var properties) &&
            properties.ValueKind == JsonValueKind.Object &&
            properties.TryGetProperty(field, out value);
    }

    private static bool TryGetScalar(JsonElement value, out string scalar)
    {
        scalar = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => string.Empty,
        };
        return scalar.Length is > 0 and <= 200;
    }

    private static bool TryGetBoolean(JsonElement value, out bool parsed)
    {
        parsed = false;
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            parsed = value.GetBoolean();
            return true;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var numeric) &&
            (numeric == 0 || numeric == 1))
        {
            parsed = numeric == 1;
            return true;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var text = value.GetString();
        if (bool.TryParse(text, out parsed))
        {
            return true;
        }

        if (text is "1" or "0")
        {
            parsed = text == "1";
            return true;
        }

        return false;
    }
}
