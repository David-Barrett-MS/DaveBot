using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace DaveEar;

public sealed record OidcDiscovery(Uri Issuer, Uri DeviceAuthorizationEndpoint, Uri TokenEndpoint);

public sealed record DeviceAuthorization(string DeviceCode, string UserCode, Uri VerificationUri, Uri? VerificationUriComplete, int IntervalSeconds, DateTimeOffset ExpiresUtc);

public sealed record OAuthToken(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresUtc);

public sealed class DaveEarEnrollmentClient(HttpClient httpClient)
{
    public async Task<OidcDiscovery> DiscoverAsync(Uri issuer, CancellationToken cancellationToken)
    {
        RequireHttps(issuer);
        using var response = await httpClient.GetAsync(new Uri(issuer, "/.well-known/openid-configuration"), cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        var root = document.RootElement;
        var advertisedIssuer = RequiredUri(root, "issuer");
        if (!Uri.Compare(advertisedIssuer, issuer, UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase).Equals(0))
        {
            throw new InvalidOperationException("OIDC discovery issuer does not match the configured issuer.");
        }
        return new OidcDiscovery(advertisedIssuer, RequiredUri(root, "device_authorization_endpoint"), RequiredUri(root, "token_endpoint"));
    }

    public async Task<DeviceAuthorization> StartDeviceAuthorizationAsync(
        OidcDiscovery discovery,
        string clientId,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync(discovery.DeviceAuthorizationEndpoint,
            Form(("client_id", clientId), ("scope", "davebrain.ear.enroll davebrain.ear.command")), cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        var root = document.RootElement;
        var expires = root.GetProperty("expires_in").GetInt32();
        return new DeviceAuthorization(
            RequiredString(root, "device_code"),
            RequiredString(root, "user_code"),
            RequiredUri(root, "verification_uri"),
            root.TryGetProperty("verification_uri_complete", out var complete) && complete.ValueKind == JsonValueKind.String
                ? new Uri(complete.GetString()!, UriKind.Absolute) : null,
            root.TryGetProperty("interval", out var interval) ? Math.Clamp(interval.GetInt32(), 1, 30) : 5,
            DateTimeOffset.UtcNow.AddSeconds(expires));
    }

    public async Task<OAuthToken> AwaitDeviceAuthorizationAsync(
        OidcDiscovery discovery,
        string clientId,
        DeviceAuthorization authorization,
        Action<string> progress,
        CancellationToken cancellationToken)
    {
        var interval = authorization.IntervalSeconds;
        while (DateTimeOffset.UtcNow < authorization.ExpiresUtc)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), cancellationToken);
            using var response = await httpClient.PostAsync(discovery.TokenEndpoint,
                Form(
                    ("client_id", clientId),
                    ("grant_type", "urn:ietf:params:oauth:grant-type:device_code"),
                    ("device_code", authorization.DeviceCode)),
                cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return await ReadTokenAsync(response, cancellationToken);
            }

            var error = await ReadOAuthErrorAsync(response, cancellationToken);
            if (error == "authorization_pending")
            {
                progress("Waiting for administrator consent.");
                continue;
            }
            if (error == "slow_down")
            {
                interval = Math.Min(interval + 5, 30);
                continue;
            }
            if (error is "access_denied" or "expired_token")
            {
                throw new InvalidOperationException($"Device authorization {error}; register again.");
            }
            throw new InvalidOperationException($"Device authorization failed ({error ?? ((int)response.StatusCode).ToString()}).");
        }

        throw new InvalidOperationException("Device authorization expired; register again.");
    }

    public async Task<(Guid EarId, string CertificatePem)> EnrollAsync(
        Uri issuer,
        OAuthToken token,
        string name,
        Guid locationId,
        string csrPem,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(issuer, "/api/v1/ears/enrollments"))
        {
            Content = JsonContent.Create(new
            {
                name,
                locationId,
                certificateSigningRequestPem = csrPem,
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        var root = document.RootElement;
        return (Guid.Parse(RequiredString(root, "earId")), RequiredString(root, "certificate"));
    }

    private static FormUrlEncodedContent Form(params (string Name, string Value)[] fields) =>
        new(fields.Select(pair => new KeyValuePair<string, string>(pair.Name, pair.Value)));

    internal static async Task<OAuthToken> ReadTokenAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        var root = document.RootElement;
        return new OAuthToken(
            RequiredString(root, "access_token"),
            root.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() : null,
            DateTimeOffset.UtcNow.AddSeconds(root.TryGetProperty("expires_in", out var expires) ? expires.GetInt32() : 600));
    }

    private static async Task<string?> ReadOAuthErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
            return document.RootElement.TryGetProperty("error", out var error) ? error.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string RequiredString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new InvalidOperationException($"OIDC response is missing {name}.");

    private static Uri RequiredUri(JsonElement root, string name)
    {
        var uri = new Uri(RequiredString(root, name), UriKind.Absolute);
        RequireHttps(uri);
        return uri;
    }

    private static void RequireHttps(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("DaveEar only permits HTTPS OIDC endpoints.");
        }
    }
}

public sealed class MtlsAccessTokenProvider
{
    private readonly DaveEarConfiguration _configuration;
    private readonly OidcDiscovery _discovery;
    private readonly Func<X509Certificate2, HttpClient> _createClient;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private OAuthToken? _current;
    private EarCredentials _credentials;
    private bool _credentialsChanged;

    public MtlsAccessTokenProvider(
        DaveEarConfiguration configuration,
        EarCredentials credentials,
        OidcDiscovery discovery) : this(
            configuration,
            credentials,
            discovery,
            certificate =>
            {
                var handler = new HttpClientHandler();
                handler.ClientCertificates.Add(certificate);
                return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            })
    {
    }

    public MtlsAccessTokenProvider(
        DaveEarConfiguration configuration,
        EarCredentials credentials,
        OidcDiscovery discovery,
        Func<X509Certificate2, HttpClient> createClient)
    {
        _configuration = configuration;
        _credentials = credentials;
        _discovery = discovery;
        _createClient = createClient;
    }

    public bool RequiresReenrollment { get; private set; }
    public EarCredentials Credentials => _credentials;

    public async Task PersistRotatedCredentialsAsync(ICredentialStore store, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_credentialsChanged)
            {
                await store.SaveAsync(_credentials, cancellationToken);
                _credentialsChanged = false;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> GetAsync(CancellationToken cancellationToken)
    {
        if (RequiresReenrollment)
        {
            throw new InvalidOperationException("DaveEar credentials require re-enrollment.");
        }
        if (_current is { ExpiresUtc: var expiry, AccessToken: var accessToken } && expiry > DateTimeOffset.UtcNow.AddMinutes(2))
        {
            return accessToken;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_current is { ExpiresUtc: var currentExpiry, AccessToken: var token } && currentExpiry > DateTimeOffset.UtcNow.AddMinutes(2))
            {
                return token;
            }

            using var certificate = X509Certificate2.CreateFromPem(_credentials.CertificatePem, _credentials.PrivateKeyPem);
            using var client = _createClient(certificate);
            using var response = await client.PostAsync(_discovery.TokenEndpoint,
                new FormUrlEncodedContent(
                [
                    new("client_id", _configuration.ClientId),
                    new("grant_type", "refresh_token"),
                    new("refresh_token", _credentials.RefreshToken),
                    new("scope", "davebrain.ear.command"),
                ]),
                cancellationToken);
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                RequiresReenrollment = true;
                throw new InvalidOperationException("DaveEar refresh was rejected; re-enrollment is required.");
            }
            response.EnsureSuccessStatusCode();
            _current = await DaveEarEnrollmentClient.ReadTokenAsync(response, cancellationToken);
            if (!string.IsNullOrWhiteSpace(_current.RefreshToken))
            {
                _credentials = _credentials with { RefreshToken = _current.RefreshToken };
                _credentialsChanged = true;
            }
            return _current.AccessToken;
        }
        finally
        {
            _gate.Release();
        }
    }
}
