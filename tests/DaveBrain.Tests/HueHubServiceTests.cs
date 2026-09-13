using System.Text.Json;
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

    private sealed class FakeHueBridgeClient : IHueBridgeClient
    {
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
