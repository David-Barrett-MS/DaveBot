using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DaveHome;

public sealed record PhilipsAirPlusOptions
{
    public string ClientId { get; init; } = string.Empty;
    public string ClientSecret { get; init; } = string.Empty;
    public string RedirectUri { get; init; } = string.Empty;
    public Uri AuthorizationEndpoint { get; init; } =
        new("https://cdc.accounts.home.id/oidc/op/v1.0/4_JGZWlP8eQHpEqkvQElolbA/authorize");
    public Uri TokenEndpoint { get; init; } =
        new("https://cdc.accounts.home.id/oidc/op/v1.0/4_JGZWlP8eQHpEqkvQElolbA/oauth/token");
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
        if (string.IsNullOrWhiteSpace(ClientId) || string.IsNullOrWhiteSpace(ClientSecret))
        {
            throw new InvalidOperationException(
                "PhilipsAirPlus:ClientId and PhilipsAirPlus:ClientSecret must be configured.");
        }

        if (!Uri.TryCreate(RedirectUri, UriKind.Absolute, out var redirect) ||
            (redirect.Scheme != Uri.UriSchemeHttps && redirect.Scheme != "com.philips.air"))
        {
            throw new InvalidOperationException(
                "PhilipsAirPlus:RedirectUri must be an absolute HTTPS callback URI or the registered com.philips.air redirect URI.");
        }

        if (AuthorizationEndpoint.Scheme != Uri.UriSchemeHttps ||
            TokenEndpoint.Scheme != Uri.UriSchemeHttps ||
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

public sealed record PhilipsAirPlusAccessToken(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAtUtc);

public sealed record PhilipsAirPlusRemoteDevice(
    string DeviceId,
    string FriendlyName,
    string? ProductType,
    string ThingName);

public sealed record PhilipsAirPlusRequestResult<T>(
    DeviceOperationStatus Status,
    T? Value = default,
    string? Detail = null)
{
    public bool Succeeded => Status == DeviceOperationStatus.Succeeded;
}

public interface IPhilipsAirPlusClient
{
    Task<PhilipsAirPlusRequestResult<PhilipsAirPlusAccessToken>> ExchangeAuthorizationCodeAsync(
        string code,
        string codeVerifier,
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

    Task<PhilipsAirPlusRequestResult<JsonElement>> SetFanSpeedAsync(
        string accessToken,
        PhilipsAirPlusRemoteDevice device,
        int fanSpeed,
        CancellationToken cancellationToken = default);
}

public sealed class PhilipsAirPlusClient(
    HttpClient httpClient,
    PhilipsAirPlusOptions options) : IPhilipsAirPlusClient
{
    private const int MaximumWebSocketMessageBytes = 65_536;

    public async Task<PhilipsAirPlusRequestResult<PhilipsAirPlusAccessToken>>
        ExchangeAuthorizationCodeAsync(
            string code,
            string codeVerifier,
            CancellationToken cancellationToken = default) =>
        await RequestTokenAsync(
            new Dictionary<string, string>
            {
                ["client_id"] = options.ClientId,
                ["client_secret"] = options.ClientSecret,
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = options.RedirectUri,
                ["code_verifier"] = codeVerifier,
            },
            cancellationToken);

    public async Task<PhilipsAirPlusRequestResult<PhilipsAirPlusAccessToken>> RefreshTokenAsync(
        PhilipsAirPlusAccessToken token,
        CancellationToken cancellationToken = default) =>
        await RequestTokenAsync(
            new Dictionary<string, string>
            {
                ["client_id"] = options.ClientId,
                ["client_secret"] = options.ClientSecret,
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

    private async Task<PhilipsAirPlusRequestResult<PhilipsAirPlusAccessToken>> RequestTokenAsync(
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
            using var response = await httpClient.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                return new PhilipsAirPlusRequestResult<PhilipsAirPlusAccessToken>(
                    ToStatus(response.StatusCode), Detail: "Philips Air+ token exchange was rejected.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
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

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return new PhilipsAirPlusRequestResult<JsonDocument>(
                DeviceOperationStatus.Succeeded,
                await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken));
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
                return new PhilipsAirPlusRequestResult<JsonElement>(
                    DeviceOperationStatus.Unauthorized, Detail: "Philips Air+ MQTT connection was rejected.");
            }

            var acceptedTopic = $"{prefix}/{operation}/accepted";
            var rejectedTopic = $"{prefix}/{operation}/rejected";
            await SendAsync(socket, PhilipsAirPlusMqtt311.CreateSubscribe(1, [acceptedTopic, rejectedTopic]), timeout.Token);
            if (!PhilipsAirPlusMqtt311.IsAcceptedSubscription(
                    await ReceivePacketAsync(socket, timeout.Token), 1))
            {
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

                if (topic == rejectedTopic)
                {
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
            return new PhilipsAirPlusRequestResult<JsonElement>(
                DeviceOperationStatus.TimedOut, Detail: "Philips Air+ MQTT request timed out.");
        }
        catch (WebSocketException)
        {
            return new PhilipsAirPlusRequestResult<JsonElement>(
                DeviceOperationStatus.BridgeUnavailable, Detail: "Philips Air+ MQTT could not be reached.");
        }
        catch (JsonException)
        {
            return new PhilipsAirPlusRequestResult<JsonElement>(
                DeviceOperationStatus.InvalidState, Detail: "The Philips Air+ shadow response was invalid.");
        }
    }

    private static async Task SendAsync(
        ClientWebSocket socket,
        byte[] packet,
        CancellationToken cancellationToken) =>
        await socket.SendAsync(packet, WebSocketMessageType.Binary, true, cancellationToken);

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
            if (result.MessageType != WebSocketMessageType.Binary ||
                data.Length + result.Count > MaximumWebSocketMessageBytes)
            {
                throw new WebSocketException(WebSocketError.InvalidMessageType);
            }

            await data.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken);
        }
        while (!result.EndOfMessage);

        if (!PhilipsAirPlusMqtt311.TryParsePacket(data.GetBuffer().AsSpan(0, checked((int)data.Length)), out var packet))
        {
            throw new WebSocketException(WebSocketError.Faulted);
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
        packet.Header == 0x90 && packet.Body.Length == 4 &&
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

    public static DeviceState Map(JsonElement shadow)
    {
        var source = GetState(shadow);
        if (source.ValueKind != JsonValueKind.Object)
        {
            return new DeviceState(null, null);
        }

        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (attribute, fields) in AttributeFields)
        {
            foreach (var field in fields)
            {
                if (source.TryGetProperty(field, out var value) &&
                    TryGetScalar(value, out var scalar))
                {
                    attributes.Add(attribute, scalar);
                    break;
                }
            }
        }

        bool? isOn = null;
        if (source.TryGetProperty("powerOn", out var powerOn) && TryGetBoolean(powerOn, out var parsedPower))
        {
            isOn = parsedPower;
        }
        else if (source.TryGetProperty("pwr", out var pwr) && TryGetBoolean(pwr, out parsedPower))
        {
            isOn = parsedPower;
        }

        return new DeviceState(isOn, null, Attributes: attributes.Count == 0 ? null : attributes);
    }

    private static JsonElement GetState(JsonElement shadow)
    {
        if (shadow.ValueKind == JsonValueKind.Object &&
            shadow.TryGetProperty("state", out var state) &&
            state.ValueKind == JsonValueKind.Object)
        {
            if (state.TryGetProperty("reported", out var reported) &&
                reported.ValueKind == JsonValueKind.Object)
            {
                return reported;
            }

            if (state.TryGetProperty("desired", out var desired) &&
                desired.ValueKind == JsonValueKind.Object)
            {
                return desired;
            }
        }

        return shadow.ValueKind == JsonValueKind.Object ? shadow : default;
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
