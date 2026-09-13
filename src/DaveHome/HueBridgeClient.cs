using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace DaveHome;

public sealed record HueBridgeDiscovery(string BridgeId, Uri Address);

public sealed record HueLinkResult(
    DeviceOperationStatus Status,
    string? ApplicationKey = null,
    string? Detail = null,
    string? TlsCertificateThumbprint = null);

public sealed record HueLightResource(
    string ResourceId,
    string Name,
    DeviceCapabilities Capabilities,
    DeviceState State);

public sealed record HueLightQueryResult(
    DeviceOperationStatus Status,
    IReadOnlyList<HueLightResource> Lights,
    string? Detail = null)
{
    public bool Succeeded => Status == DeviceOperationStatus.Succeeded;
}

public interface IHueBridgeClient
{
    Task<IReadOnlyList<HueBridgeDiscovery>> DiscoverAsync(
        CancellationToken cancellationToken = default);

    Task<HueLinkResult> LinkAsync(
        Uri bridgeAddress,
        string? bridgeId = null,
        CancellationToken cancellationToken = default);

    Task<HueLightQueryResult> GetLightsAsync(
        Uri bridgeAddress,
        string applicationKey,
        string? bridgeId = null,
        string? tlsCertificateThumbprint = null,
        CancellationToken cancellationToken = default);

    Task<DeviceOperationResult> GetLightStateAsync(
        Uri bridgeAddress,
        string applicationKey,
        string resourceId,
        string? bridgeId = null,
        string? tlsCertificateThumbprint = null,
        CancellationToken cancellationToken = default);

    Task<DeviceOperationResult> SetLightStateAsync(
        Uri bridgeAddress,
        string applicationKey,
        string resourceId,
        DeviceStateChange change,
        string? bridgeId = null,
        string? tlsCertificateThumbprint = null,
        CancellationToken cancellationToken = default);
}

public sealed class HueBridgeClient(HttpClient httpClient) : IHueBridgeClient
{
    private static readonly Uri DiscoveryEndpoint = new("https://discovery.meethue.com/");

    public async Task<IReadOnlyList<HueBridgeDiscovery>> DiscoverAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.GetAsync(DiscoveryEndpoint, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return Array.Empty<HueBridgeDiscovery>();
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<HueBridgeDiscovery>();
            }

            return document.RootElement.EnumerateArray()
                .Select(resource =>
                {
                    var bridgeId = GetString(resource, "id");
                    var address = GetString(resource, "internalipaddress");
                    var port = resource.TryGetProperty("port", out var portValue) &&
                        portValue.TryGetInt32(out var discoveredPort)
                        ? discoveredPort
                        : 443;
                    return Uri.TryCreate($"https://{address}:{port}/", UriKind.Absolute, out var uri) &&
                        !string.IsNullOrWhiteSpace(bridgeId)
                        ? new HueBridgeDiscovery(bridgeId, uri)
                        : null;
                })
                .Where(discovery => discovery is not null)
                .Cast<HueBridgeDiscovery>()
                .ToArray();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Array.Empty<HueBridgeDiscovery>();
        }
        catch (HttpRequestException)
        {
            return Array.Empty<HueBridgeDiscovery>();
        }
        catch (JsonException)
        {
            return Array.Empty<HueBridgeDiscovery>();
        }
    }

    public async Task<HueLinkResult> LinkAsync(
        Uri bridgeAddress,
        string? bridgeId = null,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            CreateUri(bridgeAddress, bridgeId, "api"))
        {
            Content = new StringContent(
                """{"devicetype":"davebot#davebrain","generateclientkey":true}""",
                Encoding.UTF8,
                "application/json"),
        };
        try
        {
            using var bridgeConnection = CreateBridgeHttpClient(
                bridgeAddress,
                bridgeId,
                expectedCertificateThumbprint: null,
                allowTrustOnFirstUse: true);
            using var response = await (bridgeConnection?.Client ?? httpClient)
                .SendAsync(request, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new HueLinkResult(
                    DeviceOperationStatus.Unauthorized,
                    Detail: await GetHueErrorDetailAsync(response, cancellationToken) ??
                        "Press the bridge link button and retry.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new HueLinkResult(
                    MapStatus(response.StatusCode),
                    Detail: await GetHueErrorDetailAsync(response, cancellationToken));
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var errorDetail = GetHueErrorDetail(document.RootElement);
            if (errorDetail is not null)
            {
                return new HueLinkResult(
                    GetHueErrorType(document.RootElement) == 101
                        ? DeviceOperationStatus.Unauthorized
                        : DeviceOperationStatus.InvalidState,
                    Detail: errorDetail);
            }

            var applicationKey = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray()
                    .Select(entry => entry.TryGetProperty("success", out var success)
                        ? GetString(success, "username")
                        : null)
                    .FirstOrDefault(key => !string.IsNullOrWhiteSpace(key))
                : null;
            return string.IsNullOrWhiteSpace(applicationKey)
                ? new HueLinkResult(DeviceOperationStatus.InvalidState, Detail: "The bridge did not return an application key.")
                : new HueLinkResult(
                    DeviceOperationStatus.Succeeded,
                    applicationKey,
                    TlsCertificateThumbprint: bridgeConnection?.ObservedCertificateThumbprint);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new HueLinkResult(
                DeviceOperationStatus.TimedOut,
                Detail: "The Hue bridge did not respond within the configured timeout.");
        }
        catch (HttpRequestException exception)
        {
            return new HueLinkResult(
                DeviceOperationStatus.BridgeUnavailable,
                Detail: $"Unable to connect to the Hue bridge: {exception.GetBaseException().Message}");
        }
        catch (JsonException)
        {
            return new HueLinkResult(
                DeviceOperationStatus.InvalidState,
                Detail: "The bridge returned an invalid link response.");
        }
    }

    public async Task<HueLightQueryResult> GetLightsAsync(
        Uri bridgeAddress,
        string applicationKey,
        string? bridgeId = null,
        string? tlsCertificateThumbprint = null,
        CancellationToken cancellationToken = default)
    {
        using var result = await SendAsync(
            HttpMethod.Get,
            bridgeAddress,
            bridgeId,
            tlsCertificateThumbprint,
            "clip/v2/resource/light",
            applicationKey,
            content: null,
            cancellationToken);
        if (result.Status != DeviceOperationStatus.Succeeded || result.Document is null)
        {
            return new HueLightQueryResult(result.Status, Array.Empty<HueLightResource>(), result.Detail);
        }

        try
        {
            return new HueLightQueryResult(
                DeviceOperationStatus.Succeeded,
                ParseLights(result.Document.RootElement));
        }
        catch (JsonException)
        {
            return new HueLightQueryResult(
                DeviceOperationStatus.InvalidState,
                Array.Empty<HueLightResource>(),
                "The bridge returned an invalid light response.");
        }
    }

    public async Task<DeviceOperationResult> GetLightStateAsync(
        Uri bridgeAddress,
        string applicationKey,
        string resourceId,
        string? bridgeId = null,
        string? tlsCertificateThumbprint = null,
        CancellationToken cancellationToken = default)
    {
        using var result = await SendAsync(
            HttpMethod.Get,
            bridgeAddress,
            bridgeId,
            tlsCertificateThumbprint,
            $"clip/v2/resource/light/{Uri.EscapeDataString(resourceId)}",
            applicationKey,
            content: null,
            cancellationToken);
        if (result.Status != DeviceOperationStatus.Succeeded || result.Document is null)
        {
            return new DeviceOperationResult(result.Status, Detail: result.Detail);
        }

        try
        {
            var light = ParseLights(result.Document.RootElement).SingleOrDefault();
            return light is null
                ? new DeviceOperationResult(DeviceOperationStatus.ResourceNotFound)
                : new DeviceOperationResult(DeviceOperationStatus.Succeeded, light.State);
        }
        catch (JsonException)
        {
            return new DeviceOperationResult(
                DeviceOperationStatus.InvalidState,
                Detail: "The bridge returned an invalid light response.");
        }
    }

    public async Task<DeviceOperationResult> SetLightStateAsync(
        Uri bridgeAddress,
        string applicationKey,
        string resourceId,
        DeviceStateChange change,
        string? bridgeId = null,
        string? tlsCertificateThumbprint = null,
        CancellationToken cancellationToken = default)
    {
        if (change is { IsOn: null, Brightness: null, Colour: null } ||
            change.Brightness is < 0 or > 100 ||
            change.Colour is { } colour &&
            (colour.X is < 0 or > 1 || colour.Y is < 0 or > 1))
        {
            return new DeviceOperationResult(DeviceOperationStatus.InvalidState);
        }

        var payload = new Dictionary<string, object>();
        if (change.IsOn is { } isOn)
        {
            payload["on"] = new { on = isOn };
        }

        if (change.Brightness is { } brightness)
        {
            payload["dimming"] = new { brightness = (double)brightness };
        }

        if (change.Colour is { } payloadColour)
        {
            payload["color"] = new { xy = new { x = payloadColour.X, y = payloadColour.Y } };
        }

        using var result = await SendAsync(
            HttpMethod.Put,
            bridgeAddress,
            bridgeId,
            tlsCertificateThumbprint,
            $"clip/v2/resource/light/{Uri.EscapeDataString(resourceId)}",
            applicationKey,
            JsonSerializer.Serialize(payload),
            cancellationToken);
        if (result.Status != DeviceOperationStatus.Succeeded)
        {
            return new DeviceOperationResult(result.Status, Detail: result.Detail);
        }

        return await GetLightStateAsync(
            bridgeAddress,
            applicationKey,
            resourceId,
            bridgeId,
            tlsCertificateThumbprint,
            cancellationToken);
    }

    private async Task<HueResponse> SendAsync(
        HttpMethod method,
        Uri bridgeAddress,
        string? bridgeId,
        string? tlsCertificateThumbprint,
        string relativePath,
        string applicationKey,
        string? content,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            method,
            CreateUri(bridgeAddress, bridgeId, relativePath));
        request.Headers.Add("hue-application-key", applicationKey);
        if (content is not null)
        {
            request.Content = new StringContent(content, Encoding.UTF8, "application/json");
        }

        try
        {
            using var bridgeConnection = CreateBridgeHttpClient(
                bridgeAddress,
                bridgeId,
                tlsCertificateThumbprint,
                allowTrustOnFirstUse: false);
            using var response = await (bridgeConnection?.Client ?? httpClient)
                .SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new HueResponse(MapStatus(response.StatusCode));
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return new HueResponse(
                DeviceOperationStatus.Succeeded,
                await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new HueResponse(DeviceOperationStatus.TimedOut);
        }
        catch (HttpRequestException)
        {
            return new HueResponse(DeviceOperationStatus.BridgeUnavailable);
        }
        catch (JsonException)
        {
            return new HueResponse(
                DeviceOperationStatus.InvalidState,
                Detail: "The bridge returned an invalid response.");
        }
    }

    private static IReadOnlyList<HueLightResource> ParseLights(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Hue response is missing its data array.");
        }

        return data.EnumerateArray().Select(light =>
        {
            var resourceId = GetString(light, "id")
                ?? throw new JsonException("Hue light is missing its ID.");
            if (!light.TryGetProperty("metadata", out var metadata))
            {
                throw new JsonException("Hue light is missing metadata.");
            }
            var capabilities = DeviceCapabilities.None;
            bool? isOn = null;
            int? brightness = null;
            DeviceColour? colour = null;
            if (light.TryGetProperty("on", out var on))
            {
                capabilities |= DeviceCapabilities.OnOff;
                isOn = on.TryGetProperty("on", out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? value.GetBoolean()
                    : null;
            }

            if (light.TryGetProperty("dimming", out var dimming))
            {
                capabilities |= DeviceCapabilities.Brightness;
                brightness = dimming.TryGetProperty("brightness", out var value) &&
                    value.TryGetDouble(out var rawBrightness)
                    ? (int)Math.Round(Math.Clamp(rawBrightness, 0, 100))
                    : null;
            }

            if (light.TryGetProperty("color", out var color) &&
                color.ValueKind == JsonValueKind.Object)
            {
                capabilities |= DeviceCapabilities.Colour;
                if (color.TryGetProperty("xy", out var xy) &&
                    xy.ValueKind == JsonValueKind.Object &&
                    xy.TryGetProperty("x", out var x) && x.TryGetDouble(out var rawX) &&
                    xy.TryGetProperty("y", out var y) && y.TryGetDouble(out var rawY) &&
                    rawX is >= 0 and <= 1 && rawY is >= 0 and <= 1)
                {
                    colour = new DeviceColour(rawX, rawY);
                }
            }

            return new HueLightResource(
                resourceId,
                GetString(metadata, "name") ?? resourceId,
                capabilities,
                new DeviceState(isOn, brightness, colour));
        }).ToArray();
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static async Task<string?> GetHueErrorDetailAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return GetHueErrorDetail(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetHueErrorDetail(JsonElement root)
    {
        foreach (var entry in GetEntries(root))
        {
            if (entry.TryGetProperty("error", out var error) &&
                GetString(error, "description") is { } description)
            {
                return description;
            }
        }

        return null;
    }

    private static int? GetHueErrorType(JsonElement root)
    {
        foreach (var entry in GetEntries(root))
        {
            if (entry.TryGetProperty("error", out var error) &&
                error.TryGetProperty("type", out var type) &&
                type.TryGetInt32(out var errorType))
            {
                return errorType;
            }
        }

        return null;
    }

    private static IEnumerable<JsonElement> GetEntries(JsonElement root) =>
        root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray().ToArray()
            : [root];

    private HueBridgeHttpClient? CreateBridgeHttpClient(
        Uri bridgeAddress,
        string? bridgeId,
        string? expectedCertificateThumbprint,
        bool allowTrustOnFirstUse)
    {
        if (string.IsNullOrWhiteSpace(bridgeId))
        {
            return null;
        }

        if (!IPAddress.TryParse(bridgeAddress.Host, out var bridgeIpAddress))
        {
            throw new InvalidOperationException(
                "A discovered Hue bridge must have an IP address for secure TLS host mapping.");
        }

        var certificateValidator = new HueBridgeCertificateValidator(
            bridgeId,
            expectedCertificateThumbprint,
            allowTrustOnFirstUse);
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (context, cancellationToken) =>
            {
                var socket = new Socket(
                    bridgeIpAddress.AddressFamily,
                    SocketType.Stream,
                    ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(
                        new IPEndPoint(bridgeIpAddress, context.DnsEndPoint.Port),
                        cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (SocketException)
                {
                    socket.Dispose();
                    throw;
                }
                catch (OperationCanceledException)
                {
                    socket.Dispose();
                    throw;
                }
            },
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = certificateValidator.Validate,
            },
        };
        return new HueBridgeHttpClient(
            new HttpClient(handler, disposeHandler: true)
            {
                Timeout = httpClient.Timeout,
            },
            certificateValidator);
    }

    private static Uri CreateUri(Uri bridgeAddress, string? bridgeId, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(bridgeId))
        {
            return new Uri(
                new Uri(
                    bridgeAddress.AbsoluteUri.EndsWith('/')
                        ? bridgeAddress.AbsoluteUri
                        : $"{bridgeAddress.AbsoluteUri}/"),
                relativePath);
        }

        return new UriBuilder(
            Uri.UriSchemeHttps,
            bridgeId,
            bridgeAddress.Port,
            relativePath).Uri;
    }

    private static DeviceOperationStatus MapStatus(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => DeviceOperationStatus.Unauthorized,
        HttpStatusCode.NotFound => DeviceOperationStatus.ResourceNotFound,
        HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => DeviceOperationStatus.TimedOut,
        HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity => DeviceOperationStatus.InvalidState,
        _ => DeviceOperationStatus.BridgeUnavailable,
    };

    private sealed class HueBridgeHttpClient(
        HttpClient client,
        HueBridgeCertificateValidator certificateValidator) : IDisposable
    {
        public HttpClient Client => client;

        public string? ObservedCertificateThumbprint =>
            certificateValidator.ObservedCertificateThumbprint;

        public void Dispose() => client.Dispose();
    }

    private sealed class HueBridgeCertificateValidator(
        string bridgeId,
        string? expectedCertificateThumbprint,
        bool allowTrustOnFirstUse)
    {
        public string? ObservedCertificateThumbprint { get; private set; }

        public bool Validate(
            object? sender,
            X509Certificate? certificate,
            X509Chain? chain,
            SslPolicyErrors errors)
        {
            if (certificate is null)
            {
                return false;
            }

            using var certificate2 = new X509Certificate2(certificate);
            if (!string.Equals(
                    certificate2.GetNameInfo(X509NameType.SimpleName, forIssuer: false),
                    bridgeId,
                    StringComparison.OrdinalIgnoreCase) ||
                certificate2.NotBefore > DateTime.Now ||
                certificate2.NotAfter < DateTime.Now ||
                !HasAcceptableChainStatus(chain, errors))
            {
                return false;
            }

            var thumbprint = Convert.ToHexString(SHA256.HashData(certificate2.RawData));
            if (expectedCertificateThumbprint is not null)
            {
                if (!string.Equals(
                        expectedCertificateThumbprint,
                        thumbprint,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            else if (!allowTrustOnFirstUse)
            {
                return false;
            }

            ObservedCertificateThumbprint = thumbprint;
            return true;
        }

        private static bool HasAcceptableChainStatus(X509Chain? chain, SslPolicyErrors errors)
        {
            if (errors == SslPolicyErrors.None)
            {
                return true;
            }

            if (errors != SslPolicyErrors.RemoteCertificateChainErrors || chain is null)
            {
                return false;
            }

            return chain.ChainStatus.Length > 0 &&
                chain.ChainStatus.All(status => status.Status == X509ChainStatusFlags.PartialChain);
        }
    }

    private sealed record HueResponse(
        DeviceOperationStatus Status,
        JsonDocument? Document = null,
        string? Detail = null) : IDisposable
    {
        public void Dispose() => Document?.Dispose();
    }
}
