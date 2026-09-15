using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DaveHome;

namespace DaveBrain;

public sealed record BlinkConnectionStartResult(
    DeviceOperationStatus Status,
    string? VerificationId = null,
    string? Detail = null)
{
    public bool RequiresVerification => Status == DeviceOperationStatus.VerificationRequired;
}

public sealed record BlinkHubOperationResult(
    DeviceOperationStatus Status,
    Hub? Hub = null,
    string? Detail = null);

public sealed record BlinkCameraCandidate(string CameraId, string Name, string? Model);

public sealed record BlinkCameraQueryResult(
    DeviceOperationStatus Status,
    IReadOnlyList<BlinkCameraCandidate> Cameras,
    string? Detail = null)
{
    public bool Succeeded => Status == DeviceOperationStatus.Succeeded;
}

public interface IBlinkConnectionStore
{
    string Create(string authenticatedUserId, BlinkConnectionTicket ticket, string hubName);
    bool TryGet(string authenticatedUserId, string verificationId, out BlinkConnectionTicket ticket, out string hubName);
    bool TryConsume(string authenticatedUserId, string verificationId);
}

public sealed class InMemoryBlinkConnectionStore : IBlinkConnectionStore
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);

    public string Create(string authenticatedUserId, BlinkConnectionTicket ticket, string hubName)
    {
        RemoveExpired();
        var id = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        if (!entries.TryAdd(id, new Entry(authenticatedUserId, ticket, hubName, DateTimeOffset.UtcNow.Add(Lifetime))))
        {
            throw new InvalidOperationException("Could not create a Blink verification request.");
        }

        return id;
    }

    public bool TryGet(string authenticatedUserId, string verificationId, out BlinkConnectionTicket ticket, out string hubName)
    {
        ticket = null!;
        hubName = string.Empty;
        if (string.IsNullOrWhiteSpace(authenticatedUserId) ||
            string.IsNullOrWhiteSpace(verificationId) ||
            !entries.TryGetValue(verificationId, out var entry) ||
            entry.ExpiresAtUtc < DateTimeOffset.UtcNow ||
            !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(entry.UserId),
                Encoding.UTF8.GetBytes(authenticatedUserId)))
        {
            return false;
        }

        ticket = entry.Ticket;
        hubName = entry.HubName;
        return true;
    }

    public bool TryConsume(string authenticatedUserId, string verificationId) =>
        TryGet(authenticatedUserId, verificationId, out _, out _) &&
        entries.TryRemove(verificationId, out _);

    private void RemoveExpired()
    {
        foreach (var (id, entry) in entries)
        {
            if (entry.ExpiresAtUtc < DateTimeOffset.UtcNow)
            {
                entries.TryRemove(id, out _);
            }
        }
    }

    private sealed record Entry(
        string UserId,
        BlinkConnectionTicket Ticket,
        string HubName,
        DateTimeOffset ExpiresAtUtc);
}

public sealed class BlinkHubService(
    IHomeStateStore homeStateStore,
    ISecretStore secretStore,
    IBlinkClient blinkClient,
    IBlinkConnectionStore connections) : IDeviceProvider, IDeviceSnapshotProvider
{
    private const string ProviderType = "blink";
    private static readonly TimeSpan RefreshSafetyMargin = TimeSpan.FromMinutes(1);
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> tokenLocks = new();

    public string DeviceType => "blink.camera";

    public IReadOnlyList<Hub> GetHubs() => homeStateStore.Current.Document.Hubs
        .Where(hub => hub.ProviderType == ProviderType)
        .OrderBy(hub => hub.Name, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public IReadOnlyList<Device> GetMappedDevices() => homeStateStore.Current.Document.Devices
        .Where(device => device.DeviceType == DeviceType)
        .OrderBy(device => device.Name, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public async Task<BlinkConnectionStartResult> ConnectAsync(
        string authenticatedUserId,
        string email,
        string password,
        string hubName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(authenticatedUserId) ||
            string.IsNullOrWhiteSpace(hubName) || hubName.Trim().Length > 200)
        {
            return new BlinkConnectionStartResult(DeviceOperationStatus.InvalidState);
        }

        var connection = await blinkClient.ConnectAsync(email, password, cancellationToken);
        if (connection is
            {
                Status: DeviceOperationStatus.VerificationRequired,
                VerificationTicket: { } verificationTicket,
            })
        {
            return new BlinkConnectionStartResult(
                connection.Status,
                connections.Create(authenticatedUserId, verificationTicket, hubName.Trim()),
                connection.Detail);
        }

        if (!connection.Succeeded || connection.Token is null)
        {
            return new BlinkConnectionStartResult(connection.Status, Detail: connection.Detail);
        }

        var linked = await LinkAsync(hubName.Trim(), connection.Token, cancellationToken);
        return new BlinkConnectionStartResult(linked.Status, Detail: linked.Detail);
    }

    public async Task<BlinkHubOperationResult> CompleteVerificationAsync(
        string authenticatedUserId,
        string verificationId,
        string code,
        CancellationToken cancellationToken = default)
    {
        if (!connections.TryGet(authenticatedUserId, verificationId, out var ticket, out var hubName))
        {
            return new BlinkHubOperationResult(DeviceOperationStatus.InvalidState,
                Detail: "The Blink verification request is invalid or expired.");
        }

        var connection = await blinkClient.CompleteVerificationAsync(code, ticket, cancellationToken);
        if (!connection.Succeeded || connection.Token is null)
        {
            return new BlinkHubOperationResult(connection.Status, Detail: connection.Detail);
        }

        if (!connections.TryConsume(authenticatedUserId, verificationId))
        {
            return new BlinkHubOperationResult(DeviceOperationStatus.InvalidState);
        }

        return await LinkAsync(hubName, connection.Token, cancellationToken);
    }

    public async Task<BlinkCameraQueryResult> GetUnmappedCameraCandidatesAsync(
        Guid hubId,
        CancellationToken cancellationToken = default)
    {
        var hub = GetHub(hubId);
        if (hub is null)
        {
            return new BlinkCameraQueryResult(DeviceOperationStatus.ResourceNotFound, Array.Empty<BlinkCameraCandidate>());
        }

        var token = await GetUsableTokenAsync(hub, cancellationToken);
        if (!token.Succeeded || token.Token is null)
        {
            return new BlinkCameraQueryResult(token.Status, Array.Empty<BlinkCameraCandidate>(), token.Detail);
        }

        var cameras = await blinkClient.GetCamerasAsync(token.Token, cancellationToken);
        if (!cameras.Succeeded || cameras.Value is null)
        {
            return new BlinkCameraQueryResult(cameras.Status, Array.Empty<BlinkCameraCandidate>(), cameras.Detail);
        }

        var mapped = homeStateStore.Current.Document.Devices
            .Where(device => device.HubId == hubId && device.DeviceType == DeviceType)
            .Select(device => device.ExternalResourceId)
            .ToHashSet(StringComparer.Ordinal);
        return new BlinkCameraQueryResult(
            DeviceOperationStatus.Succeeded,
            cameras.Value
                .Where(camera => !mapped.Contains(camera.CameraId))
                .Select(camera => new BlinkCameraCandidate(camera.CameraId, camera.Name, camera.Model))
                .ToArray());
    }

    public async Task<DeviceOperationResult> ImportCameraAsync(
        Guid hubId,
        string cameraId,
        string displayName,
        Guid? locationId,
        CancellationToken cancellationToken = default)
    {
        if (GetHub(hubId) is null || string.IsNullOrWhiteSpace(cameraId) ||
            string.IsNullOrWhiteSpace(displayName) || displayName.Trim().Length > 200)
        {
            return new DeviceOperationResult(DeviceOperationStatus.InvalidState);
        }

        var candidates = await GetUnmappedCameraCandidatesAsync(hubId, cancellationToken);
        if (!candidates.Succeeded)
        {
            return new DeviceOperationResult(candidates.Status, Detail: candidates.Detail);
        }

        if (!candidates.Cameras.Any(camera => camera.CameraId == cameraId))
        {
            return new DeviceOperationResult(DeviceOperationStatus.ResourceNotFound);
        }

        await homeStateStore.UpdateAsync(state =>
        {
            if (locationId is { } id && !state.LocationsById.ContainsKey(id))
            {
                throw new InvalidOperationException("The selected location does not exist.");
            }

            if (state.Document.Devices.Any(device => device.HubId == hubId &&
                device.DeviceType == DeviceType &&
                string.Equals(device.ExternalResourceId, cameraId, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("The Blink camera is already imported.");
            }

            return state.Document with
            {
                Devices = state.Document.Devices.Append(new Device(
                    Guid.NewGuid(),
                    CreateUniqueName(displayName.Trim(), state.Document.Devices),
                    DeviceType,
                    locationId,
                    HubId: hubId,
                    ExternalResourceId: cameraId)).ToArray(),
            };
        }, homeStateStore.Current.Revision, cancellationToken);
        return new DeviceOperationResult(DeviceOperationStatus.Succeeded);
    }

    public async Task SetDeviceEnabledAsync(
        Guid deviceId,
        bool enabled,
        CancellationToken cancellationToken = default) =>
        await UpdateDeviceAsync(deviceId, device => device with { Enabled = enabled }, cancellationToken);

    public async Task<DeviceSnapshotResult> GetSnapshotAsync(
        Device device,
        CancellationToken cancellationToken = default)
    {
        if (device.DeviceType != DeviceType || !device.Enabled ||
            device.HubId is not { } hubId || string.IsNullOrWhiteSpace(device.ExternalResourceId))
        {
            return new DeviceSnapshotResult(DeviceOperationStatus.InvalidState);
        }

        var hub = GetHub(hubId);
        if (hub is null)
        {
            return new DeviceSnapshotResult(DeviceOperationStatus.ResourceNotFound);
        }

        if (!hub.Enabled)
        {
            return new DeviceSnapshotResult(DeviceOperationStatus.InvalidState,
                Detail: "The Blink account is disabled.");
        }

        var token = await GetUsableTokenAsync(hub, cancellationToken);
        if (!token.Succeeded || token.Token is null)
        {
            return new DeviceSnapshotResult(token.Status, Detail: token.Detail);
        }

        var snapshot = await blinkClient.GetSnapshotAsync(
            token.Token,
            device.ExternalResourceId,
            cancellationToken);
        return new DeviceSnapshotResult(snapshot.Status, snapshot.Snapshot, snapshot.Detail);
    }

    public async Task<BlinkLiveViewStartResult> StartLiveViewAsync(
        Guid deviceId,
        CancellationToken cancellationToken = default)
    {
        if (!homeStateStore.Current.DevicesById.TryGetValue(deviceId, out var device) ||
            device.DeviceType != DeviceType ||
            !device.Enabled ||
            device.HubId is not { } hubId ||
            string.IsNullOrWhiteSpace(device.ExternalResourceId))
        {
            return new BlinkLiveViewStartResult(DeviceOperationStatus.InvalidState);
        }

        var hub = GetHub(hubId);
        if (hub is null)
        {
            return new BlinkLiveViewStartResult(DeviceOperationStatus.ResourceNotFound);
        }

        if (!hub.Enabled)
        {
            return new BlinkLiveViewStartResult(
                DeviceOperationStatus.InvalidState,
                Detail: "The Blink account is disabled.");
        }

        var token = await GetUsableTokenAsync(hub, cancellationToken);
        if (!token.Succeeded || token.Token is null)
        {
            return new BlinkLiveViewStartResult(token.Status, Detail: token.Detail);
        }

        return await blinkClient.StartLiveViewAsync(
            token.Token,
            device.ExternalResourceId,
            cancellationToken);
    }

    public async Task RemoveDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default) =>
        await homeStateStore.UpdateAsync(state =>
        {
            if (!state.DevicesById.TryGetValue(deviceId, out var device) || device.DeviceType != DeviceType)
            {
                throw new InvalidOperationException("The Blink camera does not exist.");
            }

            return state.Document with
            {
                Devices = state.Document.Devices.Where(device => device.Id != deviceId).ToArray(),
            };
        }, homeStateStore.Current.Revision, cancellationToken);

    public async Task RemoveHubAsync(Guid hubId, CancellationToken cancellationToken = default)
    {
        var hub = GetHub(hubId);
        if (hub is null)
        {
            throw new InvalidOperationException("The Blink account does not exist.");
        }

        await homeStateStore.UpdateAsync(state => state.Document with
        {
            Hubs = state.Document.Hubs.Where(item => item.Id != hubId).ToArray(),
            Devices = state.Document.Devices.Where(device => device.HubId != hubId).ToArray(),
        }, homeStateStore.Current.Revision, cancellationToken);
        await secretStore.DeleteAsync(hub.SecretReference, cancellationToken);
    }

    Task<DeviceOperationResult> IDeviceProvider.GetStateAsync(Device device, CancellationToken cancellationToken) =>
        Task.FromResult(new DeviceOperationResult(DeviceOperationStatus.InvalidState,
            Detail: "Blink camera state reading is not enabled."));

    Task<DeviceOperationResult> IDeviceProvider.SetStateAsync(
        Device device,
        DeviceStateChange change,
        CancellationToken cancellationToken) =>
        Task.FromResult(new DeviceOperationResult(DeviceOperationStatus.InvalidState,
            Detail: "Blink camera control is not enabled."));

    private async Task<BlinkHubOperationResult> LinkAsync(
        string hubName,
        BlinkAccessToken token,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(token.ApiBaseAddress, UriKind.Absolute, out var address) ||
            address.Scheme != Uri.UriSchemeHttps)
        {
            return new BlinkHubOperationResult(DeviceOperationStatus.InvalidState);
        }

        var secretReference = await secretStore.StoreAsync(JsonSerializer.Serialize(token), cancellationToken);
        var hub = new Hub(Guid.NewGuid(), hubName, ProviderType, address.AbsoluteUri, secretReference, true);
        try
        {
            await homeStateStore.UpdateAsync(state =>
            {
                if (state.Document.Hubs.Any(existing => existing.ProviderType == ProviderType))
                {
                    throw new InvalidOperationException("A Blink account is already linked.");
                }

                return state.Document with { Hubs = state.Document.Hubs.Append(hub).ToArray() };
            }, homeStateStore.Current.Revision, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            await secretStore.DeleteAsync(secretReference, cancellationToken);
            return new BlinkHubOperationResult(DeviceOperationStatus.InvalidState,
                Detail: "A Blink account is already linked.");
        }
        catch (IOException)
        {
            await secretStore.DeleteAsync(secretReference, cancellationToken);
            throw;
        }

        return new BlinkHubOperationResult(DeviceOperationStatus.Succeeded, hub);
    }

    private async Task<BlinkConnectionResult> GetUsableTokenAsync(Hub hub, CancellationToken cancellationToken)
    {
        BlinkAccessToken token;
        try
        {
            token = JsonSerializer.Deserialize<BlinkAccessToken>(
                await secretStore.GetAsync(hub.SecretReference, cancellationToken)) ??
                throw new InvalidOperationException();
        }
        catch (JsonException)
        {
            return new BlinkConnectionResult(DeviceOperationStatus.InvalidState,
                Detail: "The stored Blink credentials are invalid.");
        }

        if (token.ExpiresAtUtc > DateTimeOffset.UtcNow.Add(RefreshSafetyMargin))
        {
            return new BlinkConnectionResult(DeviceOperationStatus.Succeeded, token);
        }

        var tokenLock = tokenLocks.GetOrAdd(hub.Id, _ => new SemaphoreSlim(1, 1));
        await tokenLock.WaitAsync(cancellationToken);
        try
        {
            var currentHub = GetHub(hub.Id);
            if (currentHub is null)
            {
                return new BlinkConnectionResult(DeviceOperationStatus.ResourceNotFound);
            }

            token = JsonSerializer.Deserialize<BlinkAccessToken>(
                await secretStore.GetAsync(currentHub.SecretReference, cancellationToken)) ??
                throw new InvalidOperationException();
            if (token.ExpiresAtUtc > DateTimeOffset.UtcNow.Add(RefreshSafetyMargin))
            {
                return new BlinkConnectionResult(DeviceOperationStatus.Succeeded, token);
            }

            var refreshed = await blinkClient.RefreshAsync(token, cancellationToken);
            if (!refreshed.Succeeded || refreshed.Token is null)
            {
                return refreshed;
            }

            var reference = await secretStore.StoreAsync(
                JsonSerializer.Serialize(refreshed.Token),
                cancellationToken);
            await homeStateStore.UpdateAsync(state => state.Document with
            {
                Hubs = state.Document.Hubs.Select(item => item.Id == currentHub.Id
                    ? item with { SecretReference = reference }
                    : item).ToArray(),
            }, homeStateStore.Current.Revision, cancellationToken);
            await secretStore.DeleteAsync(currentHub.SecretReference, cancellationToken);
            return refreshed;
        }
        catch (JsonException)
        {
            return new BlinkConnectionResult(DeviceOperationStatus.InvalidState,
                Detail: "The stored Blink credentials are invalid.");
        }
        finally
        {
            tokenLock.Release();
        }
    }

    private Hub? GetHub(Guid id) =>
        homeStateStore.Current.HubsById.TryGetValue(id, out var hub) && hub.ProviderType == ProviderType
            ? hub
            : null;

    private async Task UpdateDeviceAsync(
        Guid deviceId,
        Func<Device, Device> update,
        CancellationToken cancellationToken) =>
        await homeStateStore.UpdateAsync(state =>
        {
            if (!state.DevicesById.TryGetValue(deviceId, out var device) || device.DeviceType != DeviceType)
            {
                throw new InvalidOperationException("The Blink camera does not exist.");
            }

            return state.Document with
            {
                Devices = state.Document.Devices.Select(item => item.Id == deviceId
                    ? update(item)
                    : item).ToArray(),
            };
        }, homeStateStore.Current.Revision, cancellationToken);

    private static string CreateUniqueName(string proposed, IReadOnlyList<Device> devices)
    {
        var names = devices.Select(device => device.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(proposed))
        {
            return proposed;
        }

        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{proposed} ({suffix})";
            if (!names.Contains(candidate))
            {
                return candidate;
            }
        }
    }
}
