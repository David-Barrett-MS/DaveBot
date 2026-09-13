using System.Text.Json;
using System.Runtime.CompilerServices;
using DaveBrain;
using DaveHome;

namespace DaveBrain.Tests;

public sealed class HueHubServiceTests
{
    [Fact]
    public async Task LinkSynchronizesLightsWithOpaqueCredentialReference()
    {
        var homeState = new HomeStateStore(new InMemoryHomePersistence());
        await homeState.InitializeAsync();
        var locationId = Guid.NewGuid();
        await homeState.UpdateAsync(state => state.Document with
        {
            Locations = [new Location(locationId, "Kitchen", null)],
        }, StoreRevision.Initial);
        var secrets = new FakeSecretStore();
        var service = new HueHubService(homeState, secrets, new FakeHueBridgeClient());

        var linked = await service.LinkAsync(
            "Kitchen bridge",
            new Uri("https://192.168.1.20/"),
            "bridge-id");

        Assert.True(linked.Hub is not null);
        Assert.Equal(DeviceOperationStatus.Succeeded, linked.Status);
        Assert.Equal("bridge-id", linked.Hub.BridgeId);
        Assert.NotEqual("hue-application-key", linked.Hub.SecretReference);
        Assert.DoesNotContain(
            "hue-application-key",
            JsonSerializer.Serialize(homeState.Current.Document),
            StringComparison.Ordinal);
        Assert.Equal(1, linked.ImportedLightCount);
        var device = Assert.Single(homeState.Current.Document.Devices);
        Assert.Equal("hue.light", device.DeviceType);
        Assert.Equal(linked.Hub.Id, device.HubId);
        Assert.Equal("light-1", device.ExternalResourceId);
        Assert.Equal(DeviceCapabilities.OnOff | DeviceCapabilities.Brightness, device.Capabilities);
        Assert.Equal(new DeviceState(true, 40), device.State);
        Assert.Null(device.LocationId);
        Assert.Single(homeState.Current.Document.Locations);
        Assert.Equal("Kitchen", homeState.Current.Document.Locations[0].Name);
        var unmappedCandidates = await service.GetUnmappedLightImportCandidatesAsync(linked.Hub.Id);
        Assert.True(unmappedCandidates.Succeeded);
        Assert.Empty(unmappedCandidates.Lights);

        var legacyLocation = new Location(Guid.NewGuid(), "Unassigned Hue devices", null);
        await homeState.UpdateAsync(state => state.Document with
        {
            Locations = [.. state.Document.Locations, legacyLocation],
            Devices = state.Document.Devices
                .Select(existing => existing.Id == device.Id
                    ? existing with { LocationId = legacyLocation.Id }
                    : existing)
                .ToArray(),
        }, homeState.Current.Revision);

        var synchronized = await service.SynchronizeHubAsync(linked.Hub.Id);
        Assert.Equal(DeviceOperationStatus.Succeeded, synchronized.Status);
        Assert.Equal(0, synchronized.ImportedLightCount);
        Assert.Single(homeState.Current.Document.Devices);
        Assert.Null(homeState.Current.DevicesById[device.Id].LocationId);
        Assert.DoesNotContain(
            homeState.Current.Document.Locations,
            location => location.Id == legacyLocation.Id);

        await service.SetDeviceEnabledAsync(device.Id, enabled: false);
        var disabledResult = await service.ReadLightStateAsync(
            homeState.Current.DevicesById[device.Id]);
        Assert.Equal(DeviceOperationStatus.InvalidState, disabledResult.Status);
    }

    [Fact]
    public async Task LinkSynchronizesClipButtonResourcesAsHueSwitches()
    {
        var homeState = new HomeStateStore(new InMemoryHomePersistence());
        await homeState.InitializeAsync();
        var service = new HueHubService(
            homeState,
            new FakeSecretStore(),
            new FakeHueBridgeClient(
                [
                    new HueSwitchResource(
                        "button-1",
                        "Kitchen button",
                        new DeviceState(
                            null,
                            null,
                            Attributes: new Dictionary<string, string>
                            {
                                ["event"] = "initial_press",
                                ["button"] = "1",
                            }),
                        "button-1:2026-01-01T00:00:00Z"),
                ]));

        var linked = await service.LinkAsync(
            "Kitchen bridge",
            new Uri("https://192.168.1.20/"),
            "bridge-id");

        Assert.Equal(DeviceOperationStatus.Succeeded, linked.Status);
        Assert.Equal(1, linked.ImportedSwitchCount);
        var switchDevice = Assert.Single(
            homeState.Current.Document.Devices,
            device => device.DeviceType == "hue.switch");
        Assert.Equal("button-1", switchDevice.ExternalResourceId);
        Assert.Equal("initial_press", switchDevice.State!.Attributes!["event"]);
    }

    [Fact]
    public async Task StreamUpdateMapsToTheImportedSwitchAndPreservesItsButtonControl()
    {
        var baseline = new HueSwitchResource(
            "button-1",
            "Kitchen button",
            new DeviceState(
                null,
                null,
                Attributes: new Dictionary<string, string>
                {
                    ["event"] = "initial_press",
                    ["button"] = "1",
                }),
            "button-1:2026-01-01T00:00:00Z");
        var streamUpdate = new HueSwitchResource(
            "button-1",
            "Kitchen button",
            new DeviceState(
                null,
                null,
                Attributes: new Dictionary<string, string>
                {
                    ["event"] = "repeat",
                    ["eventTime"] = "2026-01-01T00:00:01Z",
                }),
            "button-1:2026-01-01T00:00:01Z");
        var homeState = new HomeStateStore(new InMemoryHomePersistence());
        await homeState.InitializeAsync();
        var service = new HueHubService(
            homeState,
            new FakeSecretStore(),
            new FakeHueBridgeClient([baseline], [streamUpdate]));

        var linked = await service.LinkAsync(
            "Kitchen bridge",
            new Uri("https://192.168.1.20/"),
            "bridge-id");
        var mapped = await CollectAsync(service.ReadMappedSwitchEventsAsync(linked.Hub!.Id));

        var update = Assert.Single(mapped);
        var device = Assert.Single(
            homeState.Current.Document.Devices,
            device => device.DeviceType == "hue.switch");
        Assert.Equal(device.Id, update.DeviceId);
        Assert.Equal("button-1:2026-01-01T00:00:01Z", update.EventId);
        Assert.Equal("repeat", update.State.Attributes!["event"]);
        Assert.Equal("1", update.State.Attributes["button"]);
    }

    [Fact]
    public async Task SynchronizesMotionSensorAndMergesLightLevelSseUpdates()
    {
        var baseline = new HueMotionSensorResource(
            "motion-1",
            "level-1",
            "Kitchen motion sensor",
            new DeviceState(
                null,
                null,
                Attributes: new Dictionary<string, string>
                {
                    ["motion"] = "false",
                    ["motionValid"] = "true",
                    ["lightLevel"] = "2000",
                    ["lightLevelValid"] = "true",
                    ["lightLevelAvailable"] = "true",
                }),
            "sensor:baseline");
        var streamUpdate = new HueEventResource(
            "motion-1",
            "motion",
            new DeviceState(
                null,
                null,
                Attributes: new Dictionary<string, string>
                {
                    ["motion"] = "true",
                    ["motionValid"] = "true",
                }),
            "sensor-event:motion-1");
        var homeState = new HomeStateStore(new InMemoryHomePersistence());
        await homeState.InitializeAsync();
        var service = new HueHubService(
            homeState,
            new FakeSecretStore(),
            new FakeHueBridgeClient(
                motionSensors: [baseline],
                streamResourceUpdates: [streamUpdate]));

        var linked = await service.LinkAsync(
            "Kitchen bridge",
            new Uri("https://192.168.1.20/"),
            "bridge-id");
        var sensor = Assert.Single(
            homeState.Current.Document.Devices,
            device => device.DeviceType == "hue.motion-sensor");

        Assert.Equal(1, linked.ImportedMotionSensorCount);
        Assert.Equal("Kitchen motion sensor", sensor.Name);
        Assert.Equal("motion:motion-1;lightLevel:level-1", sensor.ExternalResourceId);
        var update = Assert.Single(await CollectAsync(service.ReadMappedSwitchEventsAsync(linked.Hub!.Id)));
        Assert.Equal(sensor.Id, update.DeviceId);
        Assert.Equal("true", update.State.Attributes!["motion"]);
        Assert.Equal("2000", update.State.Attributes["lightLevel"]);
    }

    private static async Task<IReadOnlyList<HueMappedSwitchState>> CollectAsync(
        IAsyncEnumerable<HueMappedSwitchState> events)
    {
        var updates = new List<HueMappedSwitchState>();
        await foreach (var update in events)
        {
            updates.Add(update);
        }

        return updates;
    }

    private sealed class InMemoryHomePersistence : IHomeStatePersistence
    {
        private HomeStateDocument? _document;

        public Task<HomeStateDocument?> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_document);

        public Task SaveAsync(
            HomeStateDocument document,
            StoreRevision expectedRevision,
            CancellationToken cancellationToken = default)
        {
            _document = document;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> _secrets = new();

        public Task<string> StoreAsync(string secret, CancellationToken cancellationToken = default)
        {
            var reference = $"hue-{Guid.NewGuid():N}";
            _secrets.Add(reference, secret);
            return Task.FromResult(reference);
        }

        public Task<string> GetAsync(string reference, CancellationToken cancellationToken = default) =>
            Task.FromResult(_secrets[reference]);

        public Task DeleteAsync(string reference, CancellationToken cancellationToken = default)
        {
            _secrets.Remove(reference);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeHueBridgeClient(
        IReadOnlyList<HueSwitchResource>? switches = null,
        IReadOnlyList<HueSwitchResource>? streamUpdates = null,
        IReadOnlyList<HueMotionSensorResource>? motionSensors = null,
        IReadOnlyList<HueEventResource>? streamResourceUpdates = null) : IHueBridgeClient
    {
        private readonly IReadOnlyList<HueSwitchResource> _switches =
            switches ?? Array.Empty<HueSwitchResource>();
        private readonly IReadOnlyList<HueSwitchResource> _streamUpdates =
            streamUpdates ?? Array.Empty<HueSwitchResource>();
        private readonly IReadOnlyList<HueMotionSensorResource> _motionSensors =
            motionSensors ?? Array.Empty<HueMotionSensorResource>();
        private readonly IReadOnlyList<HueEventResource> _streamResourceUpdates =
            streamResourceUpdates ?? Array.Empty<HueEventResource>();

        public Task<IReadOnlyList<HueBridgeDiscovery>> DiscoverAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<HueBridgeDiscovery>>(Array.Empty<HueBridgeDiscovery>());

        public Task<HueLinkResult> LinkAsync(
            Uri bridgeAddress,
            string? bridgeId = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new HueLinkResult(
                DeviceOperationStatus.Succeeded,
                "hue-application-key",
                TlsCertificateThumbprint: "pinned-certificate-thumbprint"));

        public Task<HueLightQueryResult> GetLightsAsync(
            Uri bridgeAddress,
            string applicationKey,
            string? bridgeId = null,
            string? tlsCertificateThumbprint = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new HueLightQueryResult(
                DeviceOperationStatus.Succeeded,
                [new HueLightResource(
                    "light-1",
                    "Bridge light",
                    DeviceCapabilities.OnOff | DeviceCapabilities.Brightness,
                    new DeviceState(true, 40))]));

        public Task<HueSwitchQueryResult> GetSwitchesAsync(
            Uri bridgeAddress,
            string applicationKey,
            string? bridgeId = null,
            string? tlsCertificateThumbprint = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new HueSwitchQueryResult(
                DeviceOperationStatus.Succeeded,
                _switches));

        public Task<HueMotionSensorQueryResult> GetMotionSensorsAsync(
            Uri bridgeAddress,
            string applicationKey,
            string? bridgeId = null,
            string? tlsCertificateThumbprint = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new HueMotionSensorQueryResult(
                DeviceOperationStatus.Succeeded,
                _motionSensors));

        public async IAsyncEnumerable<HueSwitchResource> ReadEventStreamAsync(
            Uri bridgeAddress,
            string applicationKey,
            string? bridgeId = null,
            string? tlsCertificateThumbprint = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var update in _streamUpdates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return update;
            }

            await Task.CompletedTask;
        }

        public async IAsyncEnumerable<HueEventResource> ReadEventStreamResourcesAsync(
            Uri bridgeAddress,
            string applicationKey,
            string? bridgeId = null,
            string? tlsCertificateThumbprint = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var update in _streamResourceUpdates.Count > 0
                         ? _streamResourceUpdates
                         : _streamUpdates.Select(update => new HueEventResource(
                             update.ResourceId,
                             "button",
                             update.State,
                             update.EventId)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return update;
            }

            await Task.CompletedTask;
        }

        public Task<DeviceOperationResult> GetLightStateAsync(
            Uri bridgeAddress,
            string applicationKey,
            string resourceId,
            string? bridgeId = null,
            string? tlsCertificateThumbprint = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new DeviceOperationResult(
                DeviceOperationStatus.Succeeded,
                new DeviceState(true, 40)));

        public Task<DeviceOperationResult> SetLightStateAsync(
            Uri bridgeAddress,
            string applicationKey,
            string resourceId,
            DeviceStateChange change,
            string? bridgeId = null,
            string? tlsCertificateThumbprint = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new DeviceOperationResult(
                DeviceOperationStatus.Succeeded,
                new DeviceState(change.IsOn ?? true, change.Brightness ?? 40)));
    }
}
