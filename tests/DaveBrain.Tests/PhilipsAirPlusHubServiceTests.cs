using System.Text.Json;
using System.Net;
using System.Net.Http.Headers;
using DaveBrain;
using DaveHome;

namespace DaveBrain.Tests;

public sealed class PhilipsAirPlusHubServiceTests
{
    [Fact]
    public void MqttFramingAndShadowMappingAreBoundedAndDeterministic()
    {
        var connect = PhilipsAirPlusMqtt311.CreateConnect("client");
        Assert.True(PhilipsAirPlusMqtt311.TryParsePacket(connect, out var packet));
        Assert.Equal(1, packet.PacketType);
        Assert.False(PhilipsAirPlusMqtt311.TryParsePacket([0x20, 0x82, 0x01, 0], out _));

        using var shadow = JsonDocument.Parse("""
            {"state":{"reported":{"pm25":12,"temp":21.5,"rh":"44","om":"3","pwr":"1"},
            "desired":{"pm25":99,"gas":7}}}
            """);
        var state = PhilipsAirPlusShadowMapper.Map(shadow.RootElement);
        Assert.True(state.IsOn);
        Assert.Equal("12", state.Attributes!["pm25UgM3"]);
        Assert.Equal("21.5", state.Attributes["temperatureC"]);
        Assert.Equal("44", state.Attributes["humidityPercent"]);
        Assert.Equal("3", state.Attributes["fanSpeed"]);
        Assert.DoesNotContain("gasLevel", state.Attributes.Keys);
    }

    [Fact]
    public async Task ClientParsesDevicesAndUsesBearerAuthorization()
    {
        var handler = new DeviceResponseHandler();
        var client = new PhilipsAirPlusClient(new HttpClient(handler), ValidOptions());

        var result = await client.GetDevicesAsync("token-value");

        var device = Assert.Single(result.Value!);
        Assert.Equal(DeviceOperationStatus.Succeeded, result.Status);
        Assert.Equal("device-id", device.DeviceId);
        Assert.Equal("Purifier", device.FriendlyName);
        Assert.Equal("Bearer", handler.Authorization!.Scheme);
        Assert.Equal("token-value", handler.Authorization.Parameter);
    }

    [Fact]
    public async Task ImportReadAndFanSpeedUseApiIdAndPersistOnlyAcceptedState()
    {
        var stateStore = new HomeStateStore(new InMemoryPersistence());
        await stateStore.InitializeAsync();
        var secrets = new FakeSecretStore();
        var tokenReference = await secrets.StoreAsync(JsonSerializer.Serialize(new PhilipsAirPlusAccessToken(
            "access", "refresh", DateTimeOffset.UtcNow.AddHours(1))));
        var hub = new Hub(
            Guid.NewGuid(), "Air+", "philips.airplus", "https://prod.eu-da.iot.versuni.com/",
            tokenReference, true);
        await stateStore.UpdateAsync(document => document.Document with { Hubs = [hub] }, stateStore.Current.Revision);
        var client = new FakeClient();
        var service = new PhilipsAirPlusHubService(
            stateStore, secrets, client, new InMemoryPhilipsAirPlusAuthorizationStateStore(), ValidOptions());

        var candidates = await service.GetUnmappedImportCandidatesAsync(hub.Id);
        var candidate = Assert.Single(candidates.Devices);
        Assert.Equal("api-device-id", candidate.DeviceId);
        Assert.Equal("7", candidate.State!.Attributes!["fanSpeed"]);

        var imported = await service.ImportDeviceAsync(hub.Id, candidate.DeviceId, "Bedroom purifier", null);
        Assert.True(imported.Succeeded);
        var device = Assert.Single(stateStore.Current.Document.Devices);
        Assert.Equal("api-device-id", device.ExternalResourceId);
        Assert.DoesNotContain("thing-name", JsonSerializer.Serialize(stateStore.Current.Document));
        Assert.True(device.Capabilities.HasFlag(DeviceCapabilities.FanSpeed));

        client.ReportedFanSpeed = 4;
        var read = await service.ReadDeviceStateAsync(device);
        Assert.True(read.Succeeded);
        Assert.Equal("4", stateStore.Current.DevicesById[device.Id].State!.Attributes!["fanSpeed"]);

        var rejected = await service.SetFanSpeedAsync(device, 11);
        Assert.Equal(DeviceOperationStatus.InvalidState, rejected.Status);
        Assert.Equal(0, client.SetFanSpeedCalls);

        client.AcceptSet = false;
        var notAccepted = await service.SetFanSpeedAsync(device, 81);
        Assert.Equal(DeviceOperationStatus.InvalidState, notAccepted.Status);
        Assert.Equal("4", stateStore.Current.DevicesById[device.Id].State!.Attributes!["fanSpeed"]);

        client.AcceptSet = true;
        var accepted = await service.SetFanSpeedAsync(device, 81);
        Assert.True(accepted.Succeeded);
        Assert.Equal(2, client.SetFanSpeedCalls);
        Assert.Equal("81", stateStore.Current.DevicesById[device.Id].State!.Attributes!["fanSpeed"]);
    }

    [Fact]
    public async Task AuthorizationRequiresBoundStateAndStoresOnlyProtectedTokenReference()
    {
        var stateStore = new HomeStateStore(new InMemoryPersistence());
        await stateStore.InitializeAsync();
        var secrets = new FakeSecretStore();
        var client = new FakeClient();
        var service = new PhilipsAirPlusHubService(
            stateStore, secrets, client, new InMemoryPhilipsAirPlusAuthorizationStateStore(), ValidOptions());

        var begin = await service.BeginAuthorizationAsync("user-a");
        Assert.True(begin.Status == DeviceOperationStatus.Succeeded);
        Assert.Contains("code_challenge_method=S256", begin.AuthorizationUri!.Query);
        var invalid = await service.CompleteAuthorizationAsync(
            "user-b", "callback-code", begin.State!, "Air+");
        Assert.Equal(DeviceOperationStatus.InvalidState, invalid.Status);

        var completed = await service.CompleteAuthorizationAsync(
            "user-a", $"https://brain.example/callback?code=callback-code&state={begin.State}",
            begin.State!, "Air+");
        Assert.True(completed.Status == DeviceOperationStatus.Succeeded);
        Assert.NotNull(completed.Hub);
        Assert.DoesNotContain("access", JsonSerializer.Serialize(stateStore.Current.Document));
        Assert.StartsWith("secret-", completed.Hub!.SecretReference);
    }

    private static PhilipsAirPlusOptions ValidOptions() => new()
    {
        ClientId = "configured-client",
        ClientSecret = "configured-secret",
        RedirectUri = "https://brain.example/callback",
    };

    private sealed class InMemoryPersistence : IHomeStatePersistence
    {
        private HomeStateDocument? _document;

        public Task<HomeStateDocument?> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_document);

        public Task SaveAsync(HomeStateDocument document, StoreRevision expectedRevision,
            CancellationToken cancellationToken = default)
        {
            _document = document;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> _values = [];

        public Task<string> StoreAsync(string secret, CancellationToken cancellationToken = default)
        {
            var reference = $"secret-{Guid.NewGuid():N}";
            _values.Add(reference, secret);
            return Task.FromResult(reference);
        }

        public Task<string> GetAsync(string reference, CancellationToken cancellationToken = default) =>
            Task.FromResult(_values[reference]);

        public Task DeleteAsync(string reference, CancellationToken cancellationToken = default)
        {
            _values.Remove(reference);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeClient : IPhilipsAirPlusClient
    {
        public int ReportedFanSpeed { get; set; } = 7;
        public bool AcceptSet { get; set; } = true;
        public int SetFanSpeedCalls { get; private set; }

        public Task<PhilipsAirPlusRequestResult<PhilipsAirPlusAccessToken>> ExchangeAuthorizationCodeAsync(
            string code, string codeVerifier, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PhilipsAirPlusRequestResult<PhilipsAirPlusAccessToken>(
                DeviceOperationStatus.Succeeded,
                new PhilipsAirPlusAccessToken("access", "refresh", DateTimeOffset.UtcNow.AddHours(1))));

        public Task<PhilipsAirPlusRequestResult<PhilipsAirPlusAccessToken>> RefreshTokenAsync(
            PhilipsAirPlusAccessToken token, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PhilipsAirPlusRequestResult<PhilipsAirPlusAccessToken>(
                DeviceOperationStatus.Succeeded, token with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1) }));

        public Task<PhilipsAirPlusRequestResult<string>> GetAccountIdAsync(
            string accessToken, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PhilipsAirPlusRequestResult<string>(DeviceOperationStatus.Succeeded, "account-id"));

        public Task<PhilipsAirPlusRequestResult<IReadOnlyList<PhilipsAirPlusRemoteDevice>>> GetDevicesAsync(
            string accessToken, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PhilipsAirPlusRequestResult<IReadOnlyList<PhilipsAirPlusRemoteDevice>>(
                DeviceOperationStatus.Succeeded,
                [new PhilipsAirPlusRemoteDevice("api-device-id", "Purifier", "AC1234", "thing-name")]));

        public Task<PhilipsAirPlusRequestResult<JsonElement>> GetShadowAsync(
            string accessToken, PhilipsAirPlusRemoteDevice device, CancellationToken cancellationToken = default) =>
            Task.FromResult(ShadowResult(ReportedFanSpeed));

        public Task<PhilipsAirPlusRequestResult<JsonElement>> SetFanSpeedAsync(
            string accessToken, PhilipsAirPlusRemoteDevice device, int fanSpeed,
            CancellationToken cancellationToken = default)
        {
            SetFanSpeedCalls++;
            return Task.FromResult(AcceptSet
                ? ShadowResult(fanSpeed)
                : new PhilipsAirPlusRequestResult<JsonElement>(DeviceOperationStatus.InvalidState));
        }

        private static PhilipsAirPlusRequestResult<JsonElement> ShadowResult(int fanSpeed)
        {
            using var shadow = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                state = new
                {
                    reported = new Dictionary<string, object>
                    {
                        ["pm25"] = 9,
                        ["om"] = fanSpeed,
                        ["pwr"] = "1",
                    },
                },
            }));
            return new PhilipsAirPlusRequestResult<JsonElement>(
                DeviceOperationStatus.Succeeded, shadow.RootElement.Clone());
        }
    }

    private sealed class DeviceResponseHandler : HttpMessageHandler
    {
        public AuthenticationHeaderValue? Authorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """[{"id":"device-id","friendlyName":"Purifier","ctn":"AC1234","thingName":"private-thing"}]"""),
            });
        }
    }
}
