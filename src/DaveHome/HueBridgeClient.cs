using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Globalization;
using System.Runtime.CompilerServices;

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

public sealed record HueSwitchResource(
    string ResourceId,
    string Name,
    DeviceState State,
    string? EventId);

public sealed record HueSwitchQueryResult(
    DeviceOperationStatus Status,
    IReadOnlyList<HueSwitchResource> Switches,
    string? Detail = null)
{
    public bool Succeeded => Status == DeviceOperationStatus.Succeeded;
}

public sealed record HueMotionSensorResource(
    string MotionResourceId,
    string? LightLevelResourceId,
    string Name,
    DeviceState State,
    string? EventId)
{
    public string ResourceId => HueMotionSensorResourceReference.Create(
        MotionResourceId,
        LightLevelResourceId);
}

public sealed record HueMotionSensorQueryResult(
    DeviceOperationStatus Status,
    IReadOnlyList<HueMotionSensorResource> MotionSensors,
    string? Detail = null)
{
    public bool Succeeded => Status == DeviceOperationStatus.Succeeded;
}

public sealed record HueEventResource(
    string ResourceId,
    string ResourceType,
    DeviceState State,
    string? EventId);

public sealed class HueEventStreamException(
    DeviceOperationStatus status,
    string? detail = null,
    Exception? innerException = null) : Exception(detail, innerException)
{
    public DeviceOperationStatus Status { get; } = status;
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

    Task<HueSwitchQueryResult> GetSwitchesAsync(
        Uri bridgeAddress,
        string applicationKey,
        string? bridgeId = null,
        string? tlsCertificateThumbprint = null,
        CancellationToken cancellationToken = default);

    Task<HueMotionSensorQueryResult> GetMotionSensorsAsync(
        Uri bridgeAddress,
        string applicationKey,
        string? bridgeId = null,
        string? tlsCertificateThumbprint = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new HueMotionSensorQueryResult(
            DeviceOperationStatus.Succeeded,
            Array.Empty<HueMotionSensorResource>()));

    IAsyncEnumerable<HueSwitchResource> ReadEventStreamAsync(
        Uri bridgeAddress,
        string applicationKey,
        string? bridgeId = null,
        string? tlsCertificateThumbprint = null,
        CancellationToken cancellationToken = default);

    async IAsyncEnumerable<HueEventResource> ReadEventStreamResourcesAsync(
        Uri bridgeAddress,
        string applicationKey,
        string? bridgeId = null,
        string? tlsCertificateThumbprint = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in ReadEventStreamAsync(
            bridgeAddress,
            applicationKey,
            bridgeId,
            tlsCertificateThumbprint,
            cancellationToken))
        {
            yield return new HueEventResource(
                update.ResourceId,
                "button",
                update.State,
                update.EventId);
        }
    }

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

    public async Task<HueSwitchQueryResult> GetSwitchesAsync(
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
            "clip/v2/resource/button",
            applicationKey,
            content: null,
            cancellationToken);
        if (result.Status != DeviceOperationStatus.Succeeded || result.Document is null)
        {
            return new HueSwitchQueryResult(result.Status, Array.Empty<HueSwitchResource>(), result.Detail);
        }

        using var devices = await SendAsync(
            HttpMethod.Get,
            bridgeAddress,
            bridgeId,
            tlsCertificateThumbprint,
            "clip/v2/resource/device",
            applicationKey,
            content: null,
            cancellationToken);
        if (devices.Status != DeviceOperationStatus.Succeeded || devices.Document is null)
        {
            return new HueSwitchQueryResult(
                devices.Status,
                Array.Empty<HueSwitchResource>(),
                devices.Detail);
        }

        try
        {
            return new HueSwitchQueryResult(
                DeviceOperationStatus.Succeeded,
                ParseSwitches(result.Document.RootElement, devices.Document.RootElement));
        }
        catch (JsonException)
        {
            return new HueSwitchQueryResult(
                DeviceOperationStatus.InvalidState,
                Array.Empty<HueSwitchResource>(),
                "The bridge returned an invalid switch response.");
        }
    }

    public async Task<HueMotionSensorQueryResult> GetMotionSensorsAsync(
        Uri bridgeAddress,
        string applicationKey,
        string? bridgeId = null,
        string? tlsCertificateThumbprint = null,
        CancellationToken cancellationToken = default)
    {
        using var motions = await SendAsync(
            HttpMethod.Get,
            bridgeAddress,
            bridgeId,
            tlsCertificateThumbprint,
            "clip/v2/resource/motion",
            applicationKey,
            content: null,
            cancellationToken);
        if (motions.Status != DeviceOperationStatus.Succeeded || motions.Document is null)
        {
            return new HueMotionSensorQueryResult(
                motions.Status,
                Array.Empty<HueMotionSensorResource>(),
                motions.Detail);
        }

        using var lightLevels = await SendAsync(
            HttpMethod.Get,
            bridgeAddress,
            bridgeId,
            tlsCertificateThumbprint,
            "clip/v2/resource/light_level",
            applicationKey,
            content: null,
            cancellationToken);
        if (lightLevels.Status is not (
                DeviceOperationStatus.Succeeded or DeviceOperationStatus.ResourceNotFound))
        {
            return new HueMotionSensorQueryResult(
                lightLevels.Status,
                Array.Empty<HueMotionSensorResource>(),
                lightLevels.Detail);
        }

        using var devices = await SendAsync(
            HttpMethod.Get,
            bridgeAddress,
            bridgeId,
            tlsCertificateThumbprint,
            "clip/v2/resource/device",
            applicationKey,
            content: null,
            cancellationToken);
        if (devices.Status != DeviceOperationStatus.Succeeded || devices.Document is null)
        {
            return new HueMotionSensorQueryResult(
                devices.Status,
                Array.Empty<HueMotionSensorResource>(),
                devices.Detail);
        }

        try
        {
            return new HueMotionSensorQueryResult(
                DeviceOperationStatus.Succeeded,
                ParseMotionSensors(
                    motions.Document.RootElement,
                    lightLevels.Document?.RootElement,
                    devices.Document.RootElement));
        }
        catch (JsonException)
        {
            return new HueMotionSensorQueryResult(
                DeviceOperationStatus.InvalidState,
                Array.Empty<HueMotionSensorResource>(),
                "The bridge returned an invalid motion-sensor response.");
        }
    }

    public async IAsyncEnumerable<HueSwitchResource> ReadEventStreamAsync(
        Uri bridgeAddress,
        string applicationKey,
        string? bridgeId = null,
        string? tlsCertificateThumbprint = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var connection = await OpenEventStreamAsync(
            bridgeAddress,
            applicationKey,
            bridgeId,
            tlsCertificateThumbprint,
            cancellationToken);
        var reader = new BoundedSseReader(connection.Reader);
        var data = new StringBuilder();
        var discardEvent = false;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.IsOverlong)
            {
                discardEvent = true;
                data.Clear();
                continue;
            }

            if (line.Value.Length == 0)
            {
                if (!discardEvent)
                {
                    if (!discardEvent)
                    {
                        foreach (var update in ParseEventData(data))
                        {
                            yield return update;
                        }
                    }
                }

                data.Clear();
                discardEvent = false;
                continue;
            }

            if (discardEvent || !line.Value.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var value = line.Value.AsSpan(5).TrimStart();
            if (data.Length + value.Length + 1 > HueEventStreamParser.MaximumEventDataLength)
            {
                data.Clear();
                discardEvent = true;
                continue;
            }

            if (data.Length > 0)
            {
                data.Append('\n');
            }

            data.Append(value);
        }

        foreach (var update in ParseEventData(data))
        {
            yield return update;
        }

        throw new HueEventStreamException(
            DeviceOperationStatus.BridgeUnavailable,
            "The Hue event stream ended.");
    }

    public async IAsyncEnumerable<HueEventResource> ReadEventStreamResourcesAsync(
        Uri bridgeAddress,
        string applicationKey,
        string? bridgeId = null,
        string? tlsCertificateThumbprint = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var connection = await OpenEventStreamAsync(
            bridgeAddress,
            applicationKey,
            bridgeId,
            tlsCertificateThumbprint,
            cancellationToken);
        var reader = new BoundedSseReader(connection.Reader);
        var data = new StringBuilder();
        var discardEvent = false;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.IsOverlong)
            {
                discardEvent = true;
                data.Clear();
                continue;
            }

            if (line.Value.Length == 0)
            {
                if (!discardEvent)
                {
                    foreach (var update in ParseEventResources(data))
                    {
                        yield return update;
                    }
                }

                data.Clear();
                discardEvent = false;
                continue;
            }

            if (discardEvent || !line.Value.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var value = line.Value.AsSpan(5).TrimStart();
            if (data.Length + value.Length + 1 > HueEventStreamParser.MaximumEventDataLength)
            {
                data.Clear();
                discardEvent = true;
                continue;
            }

            if (data.Length > 0)
            {
                data.Append('\n');
            }

            data.Append(value);
        }

        foreach (var update in ParseEventResources(data))
        {
            yield return update;
        }

        throw new HueEventStreamException(
            DeviceOperationStatus.BridgeUnavailable,
            "The Hue event stream ended.");
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

    private async Task<HueEventStreamConnection> OpenEventStreamAsync(
        Uri bridgeAddress,
        string applicationKey,
        string? bridgeId,
        string? tlsCertificateThumbprint,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            CreateUri(bridgeAddress, bridgeId, "eventstream/clip/v2"));
        request.Headers.Add("hue-application-key", applicationKey);
        request.Headers.Accept.ParseAdd("text/event-stream");

        HueBridgeHttpClient? bridgeConnection = null;
        HttpResponseMessage? response = null;
        try
        {
            bridgeConnection = CreateBridgeHttpClient(
                bridgeAddress,
                bridgeId,
                tlsCertificateThumbprint,
                allowTrustOnFirstUse: false);
            response = await (bridgeConnection?.Client ?? httpClient).SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new HueEventStreamException(
                    MapStatus(response.StatusCode),
                    $"The Hue event stream returned HTTP {(int)response.StatusCode}.");
            }

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return new HueEventStreamConnection(
                request,
                response,
                bridgeConnection,
                new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            request.Dispose();
            response?.Dispose();
            bridgeConnection?.Dispose();
            throw;
        }
        catch (HueEventStreamException)
        {
            request.Dispose();
            response?.Dispose();
            bridgeConnection?.Dispose();
            throw;
        }
        catch (OperationCanceledException exception)
        {
            request.Dispose();
            response?.Dispose();
            bridgeConnection?.Dispose();
            throw new HueEventStreamException(
                DeviceOperationStatus.TimedOut,
                "The Hue event stream did not respond within the configured timeout.",
                exception);
        }
        catch (HttpRequestException exception)
        {
            request.Dispose();
            response?.Dispose();
            bridgeConnection?.Dispose();
            throw new HueEventStreamException(
                DeviceOperationStatus.BridgeUnavailable,
                "Unable to connect to the Hue event stream.",
                exception);
        }
        catch
        {
            request.Dispose();
            response?.Dispose();
            bridgeConnection?.Dispose();
            throw;
        }
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

    private static IReadOnlyList<HueSwitchResource> ParseEventData(StringBuilder data)
    {
        if (data.Length == 0)
        {
            return Array.Empty<HueSwitchResource>();
        }

        try
        {
            return HueEventStreamParser.Parse(data.ToString());
        }
        catch (JsonException)
        {
            return Array.Empty<HueSwitchResource>();
        }
    }

    private static IReadOnlyList<HueEventResource> ParseEventResources(StringBuilder data)
    {
        if (data.Length == 0)
        {
            return Array.Empty<HueEventResource>();
        }

        try
        {
            return HueEventStreamParser.ParseResources(data.ToString());
        }
        catch (JsonException)
        {
            return Array.Empty<HueEventResource>();
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

    private static IReadOnlyList<HueSwitchResource> ParseSwitches(
        JsonElement root,
        JsonElement devicesRoot)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Hue response is missing its data array.");
        }

        var devices = ParseDevices(devicesRoot);
        return data.EnumerateArray().Select(buttonResource =>
        {
            var resourceId = GetString(buttonResource, "id")
                ?? throw new JsonException("Hue switch button is missing its ID.");
            if (!buttonResource.TryGetProperty("button", out var button) ||
                button.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("Hue switch button is missing state.");
            }

            var eventName = GetString(button, "last_event");
            var eventTime = GetString(button, "last_event_date");
            var controlId = GetControlId(buttonResource);
            var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!string.IsNullOrWhiteSpace(controlId))
            {
                attributes["button"] = controlId;
            }

            if (!string.IsNullOrWhiteSpace(eventName))
            {
                attributes["event"] = eventName;
            }

            if (!string.IsNullOrWhiteSpace(eventTime))
            {
                attributes["eventTime"] = eventTime;
            }

            var ownerDeviceId = GetOwnerDeviceId(buttonResource);
            if (ownerDeviceId is null)
            {
                var owningDevices = devices
                    .Where(pair => pair.Value.ServiceResourceIds.Contains(resourceId))
                    .Select(pair => pair.Key)
                    .Take(2)
                    .ToArray();
                ownerDeviceId = owningDevices.Length == 1 ? owningDevices[0] : null;
            }

            devices.TryGetValue(ownerDeviceId ?? string.Empty, out var ownerDevice);
            var buttonName = GetMetadataName(buttonResource);
            var deviceName = ownerDevice?.Name;
            var name = !string.IsNullOrWhiteSpace(deviceName)
                ? $"{deviceName} - {GetButtonName(controlId, buttonName, resourceId)}"
                : buttonName ?? (string.IsNullOrWhiteSpace(controlId)
                    ? $"Hue switch {resourceId}"
                    : $"Hue switch button {controlId}");
            var eventId = !string.IsNullOrWhiteSpace(eventName) &&
                !string.IsNullOrWhiteSpace(eventTime)
                ? $"{resourceId}:{eventTime}"
                : null;
            return new HueSwitchResource(
                resourceId,
                name,
                new DeviceState(null, null, Attributes: attributes),
                eventId);
        }).ToArray();
    }

    private static IReadOnlyList<HueMotionSensorResource> ParseMotionSensors(
        JsonElement motionsRoot,
        JsonElement? lightLevelsRoot,
        JsonElement devicesRoot)
    {
        var devices = ParseDevices(devicesRoot);

        var lightLevels = lightLevelsRoot is { } root
            ? GetData(root)
                .Select(lightLevel => new MotionSensorLightLevel(
                    GetString(lightLevel, "id")
                        ?? throw new JsonException("Hue light-level resource is missing its ID."),
                    GetOwnerDeviceId(lightLevel),
                    lightLevel))
                .ToArray()
            : Array.Empty<MotionSensorLightLevel>();
        var sensors = new List<HueMotionSensorResource>();
        var motionResourceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var motion in GetData(motionsRoot))
        {
            var motionResourceId = GetString(motion, "id")
                ?? throw new JsonException("Hue motion resource is missing its ID.");
            if (!motionResourceIds.Add(motionResourceId))
            {
                throw new JsonException("Hue motion resource IDs must be unique.");
            }
            var ownerDeviceId = GetOwnerDeviceId(motion);
            if (ownerDeviceId is null)
            {
                var owningDevices = devices
                    .Where(pair => pair.Value.ServiceResourceIds.Contains(motionResourceId))
                    .Select(pair => pair.Key)
                    .Take(2)
                    .ToArray();
                ownerDeviceId = owningDevices.Length == 1 ? owningDevices[0] : null;
            }

            devices.TryGetValue(ownerDeviceId ?? string.Empty, out var ownerDevice);
            var lightLevel = lightLevels
                .Where(candidate =>
                    string.Equals(candidate.OwnerDeviceId, ownerDeviceId, StringComparison.Ordinal) ||
                    (ownerDevice is not null &&
                     ownerDevice.ServiceResourceIds.Contains(candidate.ResourceId)))
                .OrderBy(candidate => candidate.ResourceId, StringComparer.Ordinal)
                .FirstOrDefault();
            var state = CreateMotionSensorState(motion, lightLevel?.Resource);
            var name = ownerDevice?.Name ?? GetMetadataName(motion) ??
                $"Hue motion sensor {motionResourceId}";
            try
            {
                var resourceReference = HueMotionSensorResourceReference.Create(
                    motionResourceId,
                    lightLevel?.ResourceId);
                sensors.Add(new HueMotionSensorResource(
                    motionResourceId,
                    lightLevel?.ResourceId,
                    name,
                    state,
                    CreateSensorSnapshotEventId(resourceReference, state)));
            }
            catch (ArgumentException exception)
            {
                throw new JsonException("Hue motion sensor resource IDs are invalid.", exception);
            }
        }

        return sensors;
    }

    private static IEnumerable<JsonElement> GetData(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Hue response is missing its data array.");
        }

        return data.EnumerateArray().ToArray();
    }

    private static IReadOnlyDictionary<string, HueDevice> ParseDevices(JsonElement root)
    {
        var devices = new Dictionary<string, HueDevice>(StringComparer.Ordinal);
        foreach (var device in GetData(root))
        {
            var id = GetString(device, "id")
                ?? throw new JsonException("Hue device is missing its ID.");
            if (!devices.TryAdd(
                    id,
                    new HueDevice(
                        GetMetadataName(device),
                        GetServiceResourceIds(device))))
            {
                throw new JsonException("Hue device IDs must be unique.");
            }
        }

        return devices;
    }

    private static DeviceState CreateMotionSensorState(
        JsonElement motionResource,
        JsonElement? lightLevelResource)
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (motionResource.TryGetProperty("motion", out var motion) &&
            motion.ValueKind == JsonValueKind.Object)
        {
            AddBooleanAttribute(motion, "motion_valid", "motionValid", attributes);
            var motionValid = GetBoolean(motion, "motion_valid");
            if (GetBoolean(motion, "motion") is { } detected && motionValid is not false)
            {
                attributes["motion"] = detected ? "true" : "false";
            }
        }

        attributes["lightLevelAvailable"] = lightLevelResource is null ? "false" : "true";
        if (lightLevelResource is { } resource &&
            resource.TryGetProperty("light_level", out var lightLevel) &&
            lightLevel.ValueKind == JsonValueKind.Object)
        {
            AddBooleanAttribute(lightLevel, "light_level_valid", "lightLevelValid", attributes);
            var levelValid = GetBoolean(lightLevel, "light_level_valid");
            if (lightLevel.TryGetProperty("light_level", out var value) &&
                value.TryGetInt32(out var reportedLevel) &&
                reportedLevel is >= 0 and <= 65_535 &&
                levelValid is not false)
            {
                attributes["lightLevel"] = reportedLevel.ToString(CultureInfo.InvariantCulture);
            }
        }

        return new DeviceState(null, null, Attributes: attributes);
    }

    private static void AddBooleanAttribute(
        JsonElement resource,
        string sourceName,
        string attributeName,
        IDictionary<string, string> attributes)
    {
        if (GetBoolean(resource, sourceName) is { } value)
        {
            attributes[attributeName] = value ? "true" : "false";
        }
    }

    private static bool? GetBoolean(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static string? GetMetadataName(JsonElement resource) =>
        resource.TryGetProperty("metadata", out var metadata) &&
        metadata.ValueKind == JsonValueKind.Object
            ? GetString(metadata, "name")
            : null;

    private static string? GetOwnerDeviceId(JsonElement resource) =>
        resource.TryGetProperty("owner", out var owner) &&
        owner.ValueKind == JsonValueKind.Object &&
        string.Equals(GetString(owner, "rtype"), "device", StringComparison.Ordinal) &&
        GetString(owner, "rid") is { } ownerId
            ? ownerId
            : null;

    private static IReadOnlySet<string> GetServiceResourceIds(JsonElement device)
    {
        if (!device.TryGetProperty("services", out var services) ||
            services.ValueKind != JsonValueKind.Array)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        return services.EnumerateArray()
            .Select(service => GetString(service, "rid"))
            .Where(resourceId => !string.IsNullOrWhiteSpace(resourceId))
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string CreateSensorSnapshotEventId(string resourceReference, DeviceState state)
    {
        var canonicalState = state.Attributes is null
            ? string.Empty
            : string.Join(
                ';',
                state.Attributes
                    .OrderBy(attribute => attribute.Key, StringComparer.Ordinal)
                    .Select(attribute => $"{attribute.Key}={attribute.Value}"));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{resourceReference}|{canonicalState}"));
        return $"sensor:{Convert.ToHexString(hash)}";
    }

    private sealed record HueDevice(
        string? Name,
        IReadOnlySet<string> ServiceResourceIds);

    private sealed record MotionSensorLightLevel(
        string ResourceId,
        string? OwnerDeviceId,
        JsonElement Resource);

    private static string? GetControlId(JsonElement resource)
    {
        if (!resource.TryGetProperty("metadata", out var metadata) ||
            !metadata.TryGetProperty("control_id", out var controlId))
        {
            return null;
        }

        return controlId.ValueKind switch
        {
            JsonValueKind.String => controlId.GetString(),
            JsonValueKind.Number when controlId.TryGetInt32(out var number) =>
                number.ToString(CultureInfo.InvariantCulture),
            _ => null,
        };
    }

    private static string GetButtonName(string? controlId, string? buttonName, string resourceId) =>
        !string.IsNullOrWhiteSpace(controlId)
            ? $"Button {controlId}"
            : buttonName ?? $"Button {resourceId}";

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

    private sealed class HueEventStreamConnection(
        HttpRequestMessage request,
        HttpResponseMessage response,
        HueBridgeHttpClient? bridgeConnection,
        StreamReader reader) : IDisposable
    {
        public StreamReader Reader => reader;

        public void Dispose()
        {
            reader.Dispose();
            response.Dispose();
            bridgeConnection?.Dispose();
            request.Dispose();
        }
    }

    private sealed class BoundedSseReader(StreamReader reader)
    {
        private static readonly TimeSpan InactivityTimeout = TimeSpan.FromSeconds(90);
        private const int BufferLength = 4096;
        private const int MaximumPhysicalLineLength = HueEventStreamParser.MaximumEventDataLength;

        private readonly char[] _buffer = new char[BufferLength];
        private int _bufferOffset;
        private int _bufferLength;

        public async ValueTask<SseLine?> ReadLineAsync(CancellationToken cancellationToken)
        {
            var line = new StringBuilder();
            var isOverlong = false;
            while (true)
            {
                var character = await ReadCharacterAsync(cancellationToken);
                if (character is null)
                {
                    return line.Length == 0 && !isOverlong
                        ? null
                        : new SseLine(line.ToString(), isOverlong);
                }

                if (character == '\n')
                {
                    return new SseLine(line.ToString(), isOverlong);
                }

                if (character == '\r')
                {
                    continue;
                }

                if (line.Length == MaximumPhysicalLineLength)
                {
                    isOverlong = true;
                    continue;
                }

                line.Append(character.Value);
            }
        }

        private async ValueTask<char?> ReadCharacterAsync(CancellationToken cancellationToken)
        {
            if (_bufferOffset == _bufferLength)
            {
                using var inactivityCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
                inactivityCancellation.CancelAfter(InactivityTimeout);
                try
                {
                    _bufferLength = await reader.ReadAsync(
                        _buffer.AsMemory(),
                        inactivityCancellation.Token);
                    _bufferOffset = 0;
                }
                catch (OperationCanceledException exception)
                    when (!cancellationToken.IsCancellationRequested)
                {
                    throw new HueEventStreamException(
                        DeviceOperationStatus.TimedOut,
                        "The Hue event stream became inactive.",
                        exception);
                }
            }

            return _bufferLength == 0 ? null : _buffer[_bufferOffset++];
        }

        public sealed record SseLine(string Value, bool IsOverlong);
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

public static class HueEventStreamParser
{
    public const int MaximumEventDataLength = 65_536;
    private const int MaximumResourcesPerEvent = 500;

    public static IReadOnlyList<HueSwitchResource> Parse(string eventData)
    {
        return ParseResources(eventData)
            .Where(resource => resource.ResourceType == "button")
            .Select(resource => new HueSwitchResource(
                resource.ResourceId,
                $"Hue switch {resource.ResourceId}",
                resource.State,
                resource.EventId))
            .ToArray();
    }

    public static IReadOnlyList<HueEventResource> ParseResources(string eventData)
    {
        if (string.IsNullOrWhiteSpace(eventData) || eventData.Length > MaximumEventDataLength)
        {
            return Array.Empty<HueEventResource>();
        }

        using var document = JsonDocument.Parse(eventData, new JsonDocumentOptions { MaxDepth = 16 });
        var resources = new List<HueEventResource>();
        CollectResources(document.RootElement, inheritedEventTime: null, resources);
        return resources;
    }

    private static void CollectResources(
        JsonElement element,
        string? inheritedEventTime,
        ICollection<HueEventResource> resources)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectResources(item, inheritedEventTime, resources);
                }

                return;

            case JsonValueKind.Object:
                var eventTime = GetString(element, "creationtime") ?? inheritedEventTime;
                if (IsButtonResource(element))
                {
                    if (resources.Count < MaximumResourcesPerEvent)
                    {
                        resources.Add(ParseButtonResource(element));
                    }

                    return;
                }

                if (IsMotionResource(element))
                {
                    if (resources.Count < MaximumResourcesPerEvent)
                    {
                        resources.Add(ParseMotionResource(element, eventTime));
                    }

                    return;
                }

                if (IsLightLevelResource(element))
                {
                    if (resources.Count < MaximumResourcesPerEvent)
                    {
                        resources.Add(ParseLightLevelResource(element, eventTime));
                    }

                    return;
                }

                if (element.TryGetProperty("data", out var data) &&
                    data.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in data.EnumerateArray())
                    {
                        CollectResources(item, eventTime, resources);
                    }
                }

                return;

            default:
                return;
        }
    }

    private static bool IsButtonResource(JsonElement resource) =>
        resource.TryGetProperty("type", out var type) &&
        type.ValueKind == JsonValueKind.String &&
        string.Equals(type.GetString(), "button", StringComparison.Ordinal) &&
        resource.TryGetProperty("button", out var button) &&
        button.ValueKind == JsonValueKind.Object;

    private static bool IsMotionResource(JsonElement resource) =>
        resource.TryGetProperty("type", out var type) &&
        type.ValueKind == JsonValueKind.String &&
        string.Equals(type.GetString(), "motion", StringComparison.Ordinal) &&
        resource.TryGetProperty("motion", out var motion) &&
        motion.ValueKind == JsonValueKind.Object;

    private static bool IsLightLevelResource(JsonElement resource) =>
        resource.TryGetProperty("type", out var type) &&
        type.ValueKind == JsonValueKind.String &&
        string.Equals(type.GetString(), "light_level", StringComparison.Ordinal) &&
        resource.TryGetProperty("light_level", out var lightLevel) &&
        lightLevel.ValueKind == JsonValueKind.Object;

    private static HueEventResource ParseButtonResource(JsonElement buttonResource)
    {
        var resourceId = GetString(buttonResource, "id")
            ?? throw new JsonException("Hue switch button event is missing its ID.");
        var button = buttonResource.GetProperty("button");
        var eventName = GetString(button, "last_event");
        var eventTime = GetString(button, "last_event_date");
        var controlId = GetControlId(buttonResource);
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(controlId))
        {
            attributes["button"] = controlId;
        }

        if (!string.IsNullOrWhiteSpace(eventName))
        {
            attributes["event"] = eventName;
        }

        if (!string.IsNullOrWhiteSpace(eventTime))
        {
            attributes["eventTime"] = eventTime;
        }

        return new HueEventResource(
            resourceId,
            "button",
            new DeviceState(null, null, Attributes: attributes),
            !string.IsNullOrWhiteSpace(eventName) && !string.IsNullOrWhiteSpace(eventTime)
                ? $"{resourceId}:{eventTime}"
                : null);
    }

    private static HueEventResource ParseMotionResource(
        JsonElement motionResource,
        string? eventTime)
    {
        var resourceId = GetString(motionResource, "id")
            ?? throw new JsonException("Hue motion event is missing its ID.");
        var motion = motionResource.GetProperty("motion");
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        AddBooleanAttribute(motion, "motion_valid", "motionValid", attributes);
        var motionValid = GetBoolean(motion, "motion_valid");
        if (GetBoolean(motion, "motion") is { } detected && motionValid is not false)
        {
            attributes["motion"] = detected ? "true" : "false";
        }

        return new HueEventResource(
            resourceId,
            "motion",
            new DeviceState(null, null, Attributes: attributes),
            CreateSensorEventId(resourceId, eventTime, attributes));
    }

    private static HueEventResource ParseLightLevelResource(
        JsonElement lightLevelResource,
        string? eventTime)
    {
        var resourceId = GetString(lightLevelResource, "id")
            ?? throw new JsonException("Hue light-level event is missing its ID.");
        var lightLevel = lightLevelResource.GetProperty("light_level");
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["lightLevelAvailable"] = "true",
        };
        AddBooleanAttribute(lightLevel, "light_level_valid", "lightLevelValid", attributes);
        var levelValid = GetBoolean(lightLevel, "light_level_valid");
        if (lightLevel.TryGetProperty("light_level", out var value) &&
            value.TryGetInt32(out var reportedLevel) &&
            reportedLevel is >= 0 and <= 65_535 &&
            levelValid is not false)
        {
            attributes["lightLevel"] = reportedLevel.ToString(CultureInfo.InvariantCulture);
        }

        return new HueEventResource(
            resourceId,
            "light_level",
            new DeviceState(null, null, Attributes: attributes),
            CreateSensorEventId(resourceId, eventTime, attributes));
    }

    private static string CreateSensorEventId(
        string resourceId,
        string? eventTime,
        IReadOnlyDictionary<string, string> attributes)
    {
        var canonicalAttributes = string.Join(
            ';',
            attributes
                .OrderBy(attribute => attribute.Key, StringComparer.Ordinal)
                .Select(attribute => $"{attribute.Key}={attribute.Value}"));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{resourceId}|{eventTime ?? string.Empty}|{canonicalAttributes}"));
        return $"sensor-event:{Convert.ToHexString(hash)}";
    }

    private static void AddBooleanAttribute(
        JsonElement resource,
        string sourceName,
        string attributeName,
        IDictionary<string, string> attributes)
    {
        if (GetBoolean(resource, sourceName) is { } value)
        {
            attributes[attributeName] = value ? "true" : "false";
        }
    }

    private static bool? GetBoolean(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static string? GetControlId(JsonElement resource)
    {
        if (!resource.TryGetProperty("metadata", out var metadata) ||
            !metadata.TryGetProperty("control_id", out var controlId))
        {
            return null;
        }

        return controlId.ValueKind switch
        {
            JsonValueKind.String => controlId.GetString(),
            JsonValueKind.Number when controlId.TryGetInt32(out var number) =>
                number.ToString(CultureInfo.InvariantCulture),
            _ => null,
        };
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}
