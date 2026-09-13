using DaveHome;

namespace DaveHome.Tests;

public sealed class HomeStateStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"DaveBot-{Guid.NewGuid():N}");

    [Fact]
    public async Task UpdatePersistsRevisionAndLocations()
    {
        var path = Path.Combine(_directory, "state.json");
        var store = new HomeStateStore(new JsonSnapshotHomeStatePersistence(path));
        await store.InitializeAsync();

        var expectedRevision = store.Current.Revision;
        var updated = await store.UpdateAsync(state => state.Document with
        {
            Locations = [new Location(Guid.NewGuid(), "Kitchen", null)],
        }, expectedRevision);

        Assert.Equal(1, updated.Revision.Value);
        var restored = new HomeStateStore(new JsonSnapshotHomeStatePersistence(path));
        await restored.InitializeAsync();
        Assert.Single(restored.Current.Document.Locations);
        Assert.Equal("Kitchen", restored.Current.Document.Locations[0].Name);
    }

    [Fact]
    public async Task UpdateRejectsStaleRevision()
    {
        var store = new HomeStateStore(
            new JsonSnapshotHomeStatePersistence(Path.Combine(_directory, "state.json")));
        await store.InitializeAsync();

        await store.UpdateAsync(state => state.Document, StoreRevision.Initial);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.UpdateAsync(state => state.Document, StoreRevision.Initial));
    }

    [Fact]
    public async Task UpdateAllowsDeviceWithoutLocation()
    {
        var store = new HomeStateStore(
            new JsonSnapshotHomeStatePersistence(Path.Combine(_directory, "state.json")));
        await store.InitializeAsync();

        var device = new Device(Guid.NewGuid(), "Unassigned device", "test.device", null);
        await store.UpdateAsync(
            state => state.Document with { Devices = [device] },
            StoreRevision.Initial);

        Assert.Null(store.Current.DevicesById[device.Id].LocationId);
    }

    [Fact]
    public void MotionSensorCompositeResourcesRequireAnExistingHubAndUniqueParts()
    {
        var hubId = Guid.NewGuid();
        var hub = new Hub(
            hubId,
            "Hue",
            "hue",
            "https://192.168.1.20/",
            "opaque",
            true);
        var sensor = new Device(
            Guid.NewGuid(),
            "Kitchen motion sensor",
            "hue.motion-sensor",
            null,
            true,
            hubId,
            "motion:motion-1;lightLevel:level-1",
            State: new DeviceState(
                null,
                null,
                Attributes: new Dictionary<string, string>
                {
                    ["motion"] = "true",
                    ["motionValid"] = "true",
                    ["lightLevel"] = "5000",
                    ["lightLevelAvailable"] = "true",
                }));

        var valid = HomeStateDocument.Empty with { Hubs = [hub], Devices = [sensor] };
        HomeState.FromDocument(valid);

        var conflictingLight = new Device(
            Guid.NewGuid(),
            "Conflict",
            "hue.light",
            null,
            true,
            hubId,
            "level-1");
        Assert.Throws<InvalidOperationException>(() =>
            HomeState.FromDocument(valid with { Devices = [sensor, conflictingLight] }));
        Assert.Throws<InvalidOperationException>(() =>
            HomeState.FromDocument(valid with
            {
                Devices =
                [
                    sensor with { ExternalResourceId = "motion:motion-1;lightLevel:" },
                ],
            }));
    }

    [Fact]
    public async Task UpdateRejectsAliasWithoutAnExistingLocation()
    {
        var store = new HomeStateStore(
            new JsonSnapshotHomeStatePersistence(Path.Combine(_directory, "state.json")));
        await store.InitializeAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpdateAsync(
            state => state.Document with
            {
                CommandAliases = [new CommandAlias(Guid.NewGuid(), "kitchen", Guid.NewGuid())],
            },
            StoreRevision.Initial));

        Assert.Empty(store.Current.Document.CommandAliases);
    }

    [Fact]
    public async Task LoadRecoversFromBackupWhenPrimaryJsonIsCorrupt()
    {
        var path = Path.Combine(_directory, "state.json");
        var store = new HomeStateStore(new JsonSnapshotHomeStatePersistence(path));
        await store.InitializeAsync();
        await store.UpdateAsync(state => state.Document with
        {
            Locations = [new Location(Guid.NewGuid(), "Kitchen", null)],
        }, StoreRevision.Initial);
        await store.UpdateAsync(state => state.Document with
        {
            Locations = [.. state.Document.Locations, new Location(Guid.NewGuid(), "Office", null)],
        }, store.Current.Revision);
        await File.WriteAllTextAsync(path, "{not valid json");

        var restored = new HomeStateStore(new JsonSnapshotHomeStatePersistence(path));
        await restored.InitializeAsync();

        Assert.Single(restored.Current.Document.Locations);
        Assert.Equal("Kitchen", restored.Current.Document.Locations[0].Name);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
