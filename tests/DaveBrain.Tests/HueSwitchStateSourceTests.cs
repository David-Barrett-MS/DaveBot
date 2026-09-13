using DaveBrain;
using DaveHome;
using System.Runtime.CompilerServices;

namespace DaveBrain.Tests;

public sealed class HueSwitchStateSourceTests
{
    [Fact]
    public async Task ReconciliationAndRecoverySuppressPreviouslyObservedButtonEvents()
    {
        var hubId = Guid.NewGuid();
        var switchId = Guid.NewGuid();
        var responses = new Queue<HueSwitchQueryResult>(
        [
            Success("event-1"),
            Success("event-1"),
            Success("event-2"),
            new HueSwitchQueryResult(
                DeviceOperationStatus.BridgeUnavailable,
                Array.Empty<HueSwitchResource>()),
            Success("event-3"),
            Success("event-4"),
        ]);
        var store = new HomeStateStore(new InMemoryPersistence());
        await store.InitializeAsync();
        await store.UpdateAsync(state => state.Document with
        {
            Hubs =
            [
                new Hub(
                    hubId,
                    "Hue",
                    "hue",
                    "https://192.168.1.20/",
                    "opaque",
                    true),
            ],
            Devices =
            [
                new Device(
                    switchId,
                    "Button",
                    "hue.switch",
                    null,
                    true,
                    hubId,
                    "button-1",
                    DeviceCapabilities.None),
            ],
        }, StoreRevision.Initial);
        var hue = new HueHubService(store, new FakeSecretStore(), new QueueHueBridgeClient(responses));
        var source = new HueSwitchStateSource(hue);

        var initial = await source.ReconcileAsync();
        var sameEvent = await source.ReadStateChangesAsync();
        var newEvent = await source.ReadStateChangesAsync();
        var failed = await source.ReadStateChangesAsync();
        var recovered = await source.ReadStateChangesAsync();
        var afterRecovery = await source.ReadStateChangesAsync();

        var initialEvent = Assert.Single(initial.Changes);
        Assert.True(initialEvent.IsReconciliation);
        Assert.Empty(sameEvent.Changes);
        var change = Assert.Single(newEvent.Changes);
        Assert.Equal("button-1:event-2", change.EventId);
        Assert.False(change.IsReconciliation);
        Assert.False(failed.Succeeded);
        var recoveredEvent = Assert.Single(recovered.Changes);
        Assert.True(recoveredEvent.IsReconciliation);
        var postRecoveryEvent = Assert.Single(afterRecovery.Changes);
        Assert.Equal("button-1:event-4", postRecoveryEvent.EventId);
    }

    [Fact]
    public async Task NewlyMappedSwitchUsesItsCurrentEventAsABaseline()
    {
        var hubId = Guid.NewGuid();
        var firstSwitchId = Guid.NewGuid();
        var secondSwitchId = Guid.NewGuid();
        var responses = new Queue<HueSwitchQueryResult>(
        [
            Result(Switch("button-1", "event-1")),
            Result(Switch("button-1", "event-1"), Switch("button-2", "event-9")),
            Result(Switch("button-1", "event-1"), Switch("button-2", "event-10")),
        ]);
        var store = new HomeStateStore(new InMemoryPersistence());
        await store.InitializeAsync();
        await store.UpdateAsync(state => state.Document with
        {
            Hubs =
            [
                new Hub(hubId, "Hue", "hue", "https://192.168.1.20/", "opaque", true),
            ],
            Devices =
            [
                new Device(
                    firstSwitchId,
                    "First button",
                    "hue.switch",
                    null,
                    true,
                    hubId,
                    "button-1"),
            ],
        }, StoreRevision.Initial);
        var source = new HueSwitchStateSource(
            new HueHubService(store, new FakeSecretStore(), new QueueHueBridgeClient(responses)));

        await source.ReconcileAsync();
        await store.UpdateAsync(state => state.Document with
        {
            Devices =
            [
                .. state.Document.Devices,
                new Device(
                    secondSwitchId,
                    "Second button",
                    "hue.switch",
                    null,
                    true,
                    hubId,
                    "button-2"),
            ],
        }, store.Current.Revision);

        var changes = await source.ReadStateChangesAsync();
        var press = await source.ReadStateChangesAsync();

        Assert.Empty(changes.Changes);
        var change = Assert.Single(press.Changes);
        Assert.Equal(secondSwitchId, change.DeviceId);
        Assert.Equal("button-2:event-10", change.EventId);
    }

    [Fact]
    public async Task StreamMapsButtonEventsAndSuppressesTheReconciledLastEvent()
    {
        var hubId = Guid.NewGuid();
        var switchId = Guid.NewGuid();
        var store = new HomeStateStore(new InMemoryPersistence());
        await store.InitializeAsync();
        await store.UpdateAsync(state => state.Document with
        {
            Hubs =
            [
                new Hub(hubId, "Hue", "hue", "https://192.168.1.20/", "opaque", true),
            ],
            Devices =
            [
                new Device(
                    switchId,
                    "Button",
                    "hue.switch",
                    null,
                    true,
                    hubId,
                    "button-1"),
            ],
        }, StoreRevision.Initial);
        var source = new HueSwitchStateSource(new HueHubService(
            store,
            new FakeSecretStore(),
            new QueueHueBridgeClient(
                new Queue<HueSwitchQueryResult>([Success("event-1")]),
                [Switch("button-1", "event-1"), Switch("button-1", "event-2")])));

        var reconciliation = await source.ReconcileHubAsync(hubId);
        var changes = await CollectAsync(source.ReadHubEventStreamAsync(hubId));

        Assert.Single(reconciliation.Changes);
        var change = Assert.Single(changes);
        Assert.Equal(switchId, change.DeviceId);
        Assert.Equal("button-1:event-2", change.EventId);
        Assert.False(change.IsReconciliation);
        Assert.Equal("initial_press", change.State.Attributes!["event"]);
    }

    [Fact]
    public async Task FirstStreamEventAfterMappingIsDeliveredWhenItDiffersFromTheMappedBaseline()
    {
        var hubId = Guid.NewGuid();
        var switchId = Guid.NewGuid();
        var baseline = Switch("button-1", "event-1");
        var store = new HomeStateStore(new InMemoryPersistence());
        await store.InitializeAsync();
        await store.UpdateAsync(state => state.Document with
        {
            Hubs =
            [
                new Hub(hubId, "Hue", "hue", "https://192.168.1.20/", "opaque", true),
            ],
            Devices =
            [
                new Device(
                    switchId,
                    "Button",
                    "hue.switch",
                    null,
                    true,
                    hubId,
                    "button-1",
                    State: baseline.State),
            ],
        }, StoreRevision.Initial);
        var source = new HueSwitchStateSource(new HueHubService(
            store,
            new FakeSecretStore(),
            new QueueHueBridgeClient(
                new Queue<HueSwitchQueryResult>(),
                [Switch("button-1", "event-2")])));

        var changes = await CollectAsync(source.ReadHubEventStreamAsync(hubId));

        var change = Assert.Single(changes);
        Assert.Equal(switchId, change.DeviceId);
        Assert.Equal("button-1:event-2", change.EventId);
    }

    [Fact]
    public async Task MotionSensorStreamMergesPartialUpdatesAndSuppressesTheReconciledSnapshot()
    {
        var hubId = Guid.NewGuid();
        var sensorId = Guid.NewGuid();
        var baseline = Sensor("false", "2000", "sensor:baseline");
        var store = new HomeStateStore(new InMemoryPersistence());
        await store.InitializeAsync();
        await store.UpdateAsync(state => state.Document with
        {
            Hubs =
            [
                new Hub(hubId, "Hue", "hue", "https://192.168.1.20/", "opaque", true),
            ],
            Devices =
            [
                new Device(
                    sensorId,
                    "Kitchen motion sensor",
                    "hue.motion-sensor",
                    null,
                    true,
                    hubId,
                    "motion:motion-1;lightLevel:level-1",
                    DeviceCapabilities.None,
                    baseline.State),
            ],
        }, StoreRevision.Initial);
        var source = new HueSwitchStateSource(new HueHubService(
            store,
            new FakeSecretStore(),
            new QueueHueBridgeClient(
                new Queue<HueSwitchQueryResult>(),
                motionResponses: new Queue<HueMotionSensorQueryResult>(
                    [new HueMotionSensorQueryResult(DeviceOperationStatus.Succeeded, [baseline])]),
                streamResourceUpdates:
                [
                    new HueEventResource(
                        "motion-1",
                        "motion",
                        baseline.State,
                        "sensor-event:initial-snapshot"),
                    new HueEventResource(
                        "level-1",
                        "light_level",
                        new DeviceState(
                            null,
                            null,
                            Attributes: new Dictionary<string, string>
                            {
                                ["lightLevelAvailable"] = "true",
                                ["lightLevelValid"] = "true",
                                ["lightLevel"] = "5000",
                            }),
                        "sensor-event:level-change"),
                ])));

        var reconciliation = await source.ReconcileHubAsync(hubId);
        var changes = await CollectAsync(source.ReadHubEventStreamAsync(hubId));

        Assert.Single(reconciliation.Changes);
        var change = Assert.Single(changes);
        Assert.Equal(sensorId, change.DeviceId);
        Assert.Equal("sensor-event:level-change", change.EventId);
        Assert.Equal("false", change.State.Attributes!["motion"]);
        Assert.Equal("5000", change.State.Attributes["lightLevel"]);
    }

    [Fact]
    public async Task MotionSensorFallbackReconcilesWithoutReplayingItsBaseline()
    {
        var hubId = Guid.NewGuid();
        var sensorId = Guid.NewGuid();
        var baseline = Sensor("false", "2000", "sensor:baseline");
        var recovered = Sensor("true", "2000", "sensor:recovered");
        var changed = Sensor("true", "5000", "sensor:changed");
        var store = new HomeStateStore(new InMemoryPersistence());
        await store.InitializeAsync();
        await store.UpdateAsync(state => state.Document with
        {
            Hubs =
            [
                new Hub(hubId, "Hue", "hue", "https://192.168.1.20/", "opaque", true),
            ],
            Devices =
            [
                new Device(
                    sensorId,
                    "Kitchen motion sensor",
                    "hue.motion-sensor",
                    null,
                    true,
                    hubId,
                    "motion:motion-1;lightLevel:level-1",
                    State: baseline.State),
            ],
        }, StoreRevision.Initial);
        var source = new HueSwitchStateSource(new HueHubService(
            store,
            new FakeSecretStore(),
            new QueueHueBridgeClient(
                new Queue<HueSwitchQueryResult>(),
                motionResponses: new Queue<HueMotionSensorQueryResult>(
                [
                    new HueMotionSensorQueryResult(DeviceOperationStatus.Succeeded, [baseline]),
                    new HueMotionSensorQueryResult(
                        DeviceOperationStatus.BridgeUnavailable,
                        Array.Empty<HueMotionSensorResource>()),
                    new HueMotionSensorQueryResult(DeviceOperationStatus.Succeeded, [recovered]),
                    new HueMotionSensorQueryResult(DeviceOperationStatus.Succeeded, [recovered]),
                    new HueMotionSensorQueryResult(DeviceOperationStatus.Succeeded, [changed]),
                ]))));

        await source.ReconcileHubAsync(hubId);
        var failed = await source.ReadHubStateChangesAsync(hubId);
        var recoveryBaseline = await source.ReadHubStateChangesAsync(hubId);
        var stale = await source.ReadHubStateChangesAsync(hubId);
        var fresh = await source.ReadHubStateChangesAsync(hubId);

        Assert.False(failed.Succeeded);
        Assert.True(Assert.Single(recoveryBaseline.Changes).IsReconciliation);
        Assert.Empty(stale.Changes);
        var change = Assert.Single(fresh.Changes);
        Assert.False(change.IsReconciliation);
        Assert.Equal(sensorId, change.DeviceId);
        Assert.Equal("5000", change.State.Attributes!["lightLevel"]);
    }

    private static HueSwitchQueryResult Success(string eventSuffix) =>
        Result(Switch("button-1", eventSuffix));

    private static HueSwitchQueryResult Result(params HueSwitchResource[] switches) =>
        new(DeviceOperationStatus.Succeeded, switches);

    private static HueSwitchResource Switch(string resourceId, string eventSuffix) =>
        new(
            resourceId,
            resourceId,
            new DeviceState(
                null,
                null,
                Attributes: new Dictionary<string, string>
                {
                    ["event"] = "initial_press",
                    ["button"] = "1",
                    ["eventTime"] = eventSuffix,
                }),
            $"{resourceId}:{eventSuffix}");

    private static HueMotionSensorResource Sensor(
        string motion,
        string lightLevel,
        string eventId) =>
        new(
            "motion-1",
            "level-1",
            "Kitchen motion sensor",
            new DeviceState(
                null,
                null,
                Attributes: new Dictionary<string, string>
                {
                    ["motion"] = motion,
                    ["motionValid"] = "true",
                    ["lightLevel"] = lightLevel,
                    ["lightLevelValid"] = "true",
                    ["lightLevelAvailable"] = "true",
                }),
            eventId);

    private static async Task<IReadOnlyList<DeviceStateChanged>> CollectAsync(
        IAsyncEnumerable<DeviceStateChanged> events)
    {
        var changes = new List<DeviceStateChanged>();
        await foreach (var change in events)
        {
            changes.Add(change);
        }

        return changes;
    }

    private sealed class InMemoryPersistence : IHomeStatePersistence
    {
        public Task<HomeStateDocument?> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<HomeStateDocument?>(null);

        public Task SaveAsync(
            HomeStateDocument document,
            StoreRevision expectedRevision,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeSecretStore : ISecretStore
    {
        public Task<string> StoreAsync(string secret, CancellationToken cancellationToken = default) =>
            Task.FromResult("opaque");

        public Task<string> GetAsync(string reference, CancellationToken cancellationToken = default) =>
            Task.FromResult("application-key");

        public Task DeleteAsync(string reference, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class QueueHueBridgeClient(
        Queue<HueSwitchQueryResult> responses,
        IReadOnlyList<HueSwitchResource>? streamUpdates = null,
        Queue<HueMotionSensorQueryResult>? motionResponses = null,
        IReadOnlyList<HueEventResource>? streamResourceUpdates = null) : IHueBridgeClient
    {
        private readonly IReadOnlyList<HueSwitchResource> _streamUpdates =
            streamUpdates ?? Array.Empty<HueSwitchResource>();
        private readonly Queue<HueMotionSensorQueryResult>? _motionResponses = motionResponses;
        private readonly IReadOnlyList<HueEventResource> _streamResourceUpdates =
            streamResourceUpdates ?? Array.Empty<HueEventResource>();

        public Task<IReadOnlyList<HueBridgeDiscovery>> DiscoverAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<HueBridgeDiscovery>>(Array.Empty<HueBridgeDiscovery>());

        public Task<HueLinkResult> LinkAsync(
            Uri bridgeAddress,
            string? bridgeId = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<HueLightQueryResult> GetLightsAsync(
            Uri bridgeAddress,
            string applicationKey,
            string? bridgeId = null,
            string? tlsCertificateThumbprint = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<HueSwitchQueryResult> GetSwitchesAsync(
            Uri bridgeAddress,
            string applicationKey,
            string? bridgeId = null,
            string? tlsCertificateThumbprint = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(responses.Dequeue());

        public Task<HueMotionSensorQueryResult> GetMotionSensorsAsync(
            Uri bridgeAddress,
            string applicationKey,
            string? bridgeId = null,
            string? tlsCertificateThumbprint = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_motionResponses is { Count: > 0 }
                ? _motionResponses.Dequeue()
                : new HueMotionSensorQueryResult(
                    DeviceOperationStatus.Succeeded,
                    Array.Empty<HueMotionSensorResource>()));

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
            throw new NotSupportedException();

        public Task<DeviceOperationResult> SetLightStateAsync(
            Uri bridgeAddress,
            string applicationKey,
            string resourceId,
            DeviceStateChange change,
            string? bridgeId = null,
            string? tlsCertificateThumbprint = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
