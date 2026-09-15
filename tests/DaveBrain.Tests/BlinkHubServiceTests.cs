using System.Text.Json;
using DaveBrain;
using DaveHome;
using SIPSorcery.Net;

namespace DaveBrain.Tests;

public sealed class BlinkHubServiceTests
{
    [Fact]
    public async Task ConnectAndImportCameraStoresOnlyProtectedCredentials()
    {
        var stateStore = new HomeStateStore(new InMemoryPersistence());
        await stateStore.InitializeAsync();
        var secrets = new FakeSecretStore();
        var client = new FakeBlinkClient();
        var service = new BlinkHubService(
            stateStore,
            secrets,
            client,
            new InMemoryBlinkConnectionStore());

        var connected = await service.ConnectAsync("admin", "admin@example.test", "password", "Blink");

        Assert.Equal(DeviceOperationStatus.Succeeded, connected.Status);
        var hub = Assert.Single(service.GetHubs());
        Assert.Equal("blink", hub.ProviderType);
        Assert.StartsWith("secret-", hub.SecretReference);
        Assert.DoesNotContain("access-token", JsonSerializer.Serialize(stateStore.Current.Document));

        var candidates = await service.GetUnmappedCameraCandidatesAsync(hub.Id);
        var camera = Assert.Single(candidates.Cameras);
        var imported = await service.ImportCameraAsync(hub.Id, camera.CameraId, camera.Name, null);

        Assert.True(imported.Succeeded);
        var mapped = Assert.Single(service.GetMappedDevices());
        Assert.Equal("blink.camera", mapped.DeviceType);
        Assert.Equal(camera.CameraId, mapped.ExternalResourceId);
        Assert.Equal(hub.Id, mapped.HubId);

        var snapshot = await service.GetSnapshotAsync(mapped);
        Assert.True(snapshot.Succeeded);
        Assert.Equal("image/jpeg", snapshot.Snapshot!.ContentType);
        Assert.Equal([1, 2, 3], snapshot.Snapshot.Content);
    }

    [Fact]
    public async Task LiveViewSessionCanOnlyBeStoppedByItsCreatingUser()
    {
        var stateStore = new HomeStateStore(new InMemoryPersistence());
        await stateStore.InitializeAsync();
        var secrets = new FakeSecretStore();
        var client = new FakeBlinkClient();
        var hubs = new BlinkHubService(
            stateStore,
            secrets,
            client,
            new InMemoryBlinkConnectionStore());
        var connected = await hubs.ConnectAsync("admin", "admin@example.test", "password", "Blink");
        var hub = Assert.Single(hubs.GetHubs());
        var camera = Assert.Single((await hubs.GetUnmappedCameraCandidatesAsync(hub.Id)).Cameras);
        await hubs.ImportCameraAsync(hub.Id, camera.CameraId, camera.Name, null);
        var device = Assert.Single(hubs.GetMappedDevices());
        await using var manager = new BlinkLiveViewManager(hubs);
        using var browser = CreateBrowserOffer();

        var started = await manager.StartAsync(
            "admin",
            new BlinkLiveViewOfferRequest(device.Id, browser.localDescription.sdp.ToString()));

        Assert.True(started.Succeeded);
        Assert.NotNull(started.SessionId);
        var rejectedStop = await manager.StopAsync("another-user", started.SessionId);
        Assert.False(rejectedStop.Found);
        Assert.Equal(0, client.LiveView!.StopCount);

        var acceptedStop = await manager.StopAsync("admin", started.SessionId);
        Assert.True(acceptedStop.Found);
        Assert.Equal(RTCPeerConnectionState.@new.ToString(), acceptedStop.PeerConnectionState);
        Assert.Equal(0, acceptedStop.H264AccessUnits);
        Assert.Equal(1, client.LiveView.StopCount);
    }

    private static RTCPeerConnection CreateBrowserOffer()
    {
        var browser = new RTCPeerConnection(new RTCConfiguration());
        browser.addTrack(new MediaStreamTrack(
            SDPMediaTypesEnum.video,
            false,
            [new SDPAudioVideoMediaFormat(
                SDPMediaTypesEnum.video,
                102,
                "H264",
                90_000,
                1,
                "packetization-mode=1;profile-level-id=42e01f")],
            MediaStreamStatusEnum.RecvOnly));
        var offer = browser.createOffer(null);
        browser.setLocalDescription(offer).GetAwaiter().GetResult();
        return browser;
    }

    private sealed class FakeBlinkClient : IBlinkClient
    {
        private static readonly BlinkAccessToken Token = new(
            "access-token",
            "refresh-token",
            DateTimeOffset.UtcNow.AddHours(1),
            "hardware-id",
            "account-id",
            "https://rest-e002.immedia-semi.com/");

        public FakeLiveViewStream? LiveView { get; private set; }

        public Task<BlinkConnectionResult> ConnectAsync(
            string email,
            string password,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new BlinkConnectionResult(DeviceOperationStatus.Succeeded, Token));

        public Task<BlinkConnectionResult> CompleteVerificationAsync(
            string code,
            BlinkConnectionTicket verificationTicket,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new BlinkConnectionResult(DeviceOperationStatus.InvalidState));

        public Task<BlinkConnectionResult> RefreshAsync(
            BlinkAccessToken token,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new BlinkConnectionResult(DeviceOperationStatus.Succeeded, Token));

        public Task<BlinkApiResult<IReadOnlyList<BlinkRemoteCamera>>> GetCamerasAsync(
            BlinkAccessToken token,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new BlinkApiResult<IReadOnlyList<BlinkRemoteCamera>>(
                DeviceOperationStatus.Succeeded,
                [new BlinkRemoteCamera("camera-id", "Front door", "mini")]));

        public Task<BlinkSnapshotResult> GetSnapshotAsync(
            BlinkAccessToken token,
            string cameraId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new BlinkSnapshotResult(
                DeviceOperationStatus.Succeeded,
                new DeviceSnapshot("image/jpeg", [1, 2, 3])));

        public Task<BlinkLiveViewStartResult> StartLiveViewAsync(
            BlinkAccessToken token,
            string cameraId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new BlinkLiveViewStartResult(
                DeviceOperationStatus.Succeeded,
                LiveView = new FakeLiveViewStream()));
    }

    private sealed class FakeLiveViewStream : IBlinkLiveViewStream
    {
        public int StopCount { get; private set; }

        public TimeSpan Duration => TimeSpan.FromMinutes(5);

        public async Task RunAsync(
            Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> onH264AccessUnit,
            CancellationToken cancellationToken = default) =>
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

        public Task StopAsync()
        {
            StopCount++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeSecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> values = [];

        public Task<string> StoreAsync(string secret, CancellationToken cancellationToken = default)
        {
            var reference = $"secret-{Guid.NewGuid():N}";
            values.Add(reference, secret);
            return Task.FromResult(reference);
        }

        public Task<string> GetAsync(string reference, CancellationToken cancellationToken = default) =>
            Task.FromResult(values[reference]);

        public Task DeleteAsync(string reference, CancellationToken cancellationToken = default)
        {
            values.Remove(reference);
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryPersistence : IHomeStatePersistence
    {
        private HomeStateDocument? document;

        public Task<HomeStateDocument?> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(document);

        public Task SaveAsync(
            HomeStateDocument value,
            StoreRevision expectedRevision,
            CancellationToken cancellationToken = default)
        {
            document = value;
            return Task.CompletedTask;
        }
    }
}
