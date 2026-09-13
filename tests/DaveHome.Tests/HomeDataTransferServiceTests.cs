using DaveHome;

namespace DaveHome.Tests;

public sealed class HomeDataTransferServiceTests
{
    [Fact]
    public async Task ImportOverwritesMatchingIdsAndRetainsLocalObjects()
    {
        var persistence = new InMemoryHomePersistence();
        var store = new HomeStateStore(persistence);
        await store.InitializeAsync();
        var sharedLocationId = Guid.NewGuid();
        var retainedLocationId = Guid.NewGuid();
        var sharedDeviceId = Guid.NewGuid();
        await store.UpdateAsync(state => state.Document with
        {
            Locations =
            [
                new Location(sharedLocationId, "Old name", null),
                new Location(retainedLocationId, "Retained", null),
            ],
            Devices = [new Device(sharedDeviceId, "Old device", "test.device", sharedLocationId)],
        }, StoreRevision.Initial);
        var importedLocationId = Guid.NewGuid();
        var transfer = new HomeDataTransferService(store);

        await transfer.ImportAsync(new HomeDataExportDocument(
            HomeDataExportDocument.CurrentSchemaVersion,
            [
                new Location(sharedLocationId, "Updated name", null),
                new Location(importedLocationId, "Imported", null),
            ],
            [new Device(sharedDeviceId, "Updated device", "test.device", importedLocationId)]));

        Assert.Equal("Updated name", store.Current.LocationsById[sharedLocationId].Name);
        Assert.Contains(retainedLocationId, store.Current.LocationsById.Keys);
        Assert.Contains(importedLocationId, store.Current.LocationsById.Keys);
        Assert.Equal(importedLocationId, store.Current.DevicesById[sharedDeviceId].LocationId);
        Assert.Equal("Updated device", store.Current.DevicesById[sharedDeviceId].Name);
    }

    [Fact]
    public async Task ImportRejectsInvalidReferencesWithoutChangingState()
    {
        var store = new HomeStateStore(new InMemoryHomePersistence());
        await store.InitializeAsync();
        var transfer = new HomeDataTransferService(store);

        await Assert.ThrowsAsync<InvalidOperationException>(() => transfer.ImportAsync(
            new HomeDataExportDocument(
                HomeDataExportDocument.CurrentSchemaVersion,
                [],
                [new Device(Guid.NewGuid(), "Invalid", "test.device", Guid.NewGuid())])));

        Assert.Equal(StoreRevision.Initial, store.Current.Revision);
        Assert.Empty(store.Current.Document.Devices);
    }

    [Fact]
    public async Task ExportAndImportPreserveLocationAliases()
    {
        var sourceStore = new HomeStateStore(new InMemoryHomePersistence());
        await sourceStore.InitializeAsync();
        var location = new Location(Guid.NewGuid(), "Kitchen", null);
        var alias = new CommandAlias(Guid.NewGuid(), "cooking room", location.Id);
        await sourceStore.UpdateAsync(state => state.Document with
        {
            Locations = [location],
            CommandAliases = [alias],
        }, StoreRevision.Initial);

        var exported = new HomeDataTransferService(sourceStore).Export();
        var destinationStore = new HomeStateStore(new InMemoryHomePersistence());
        await destinationStore.InitializeAsync();
        await new HomeDataTransferService(destinationStore).ImportAsync(exported);

        Assert.Equal(alias, Assert.Single(destinationStore.Current.Document.CommandAliases));
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
}
