using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DaveHome;

namespace DaveBrain;

public sealed record PhilipsAirPlusEmailVerificationResult(
    DeviceOperationStatus Status,
    string? VerificationId = null,
    string? Detail = null);

public sealed record PhilipsAirPlusHubOperationResult(
    DeviceOperationStatus Status,
    Hub? Hub = null,
    string? Detail = null);

public sealed record PhilipsAirPlusDeviceCandidate(
    string DeviceId,
    string Name,
    string? ProductType,
    DeviceState? State);

public sealed record PhilipsAirPlusDeviceQueryResult(
    DeviceOperationStatus Status,
    IReadOnlyList<PhilipsAirPlusDeviceCandidate> Devices,
    string? Detail = null)
{
    public bool Succeeded => Status == DeviceOperationStatus.Succeeded;
}

public interface IPhilipsAirPlusEmailVerificationStore
{
    string Create(
        string authenticatedUserId,
        PhilipsAirPlusEmailVerificationTicket verificationTicket);

    bool TryGet(
        string authenticatedUserId,
        string verificationId,
        out PhilipsAirPlusEmailVerificationTicket verificationTicket);

    bool TryConsume(string authenticatedUserId, string verificationId);
}

public sealed class InMemoryPhilipsAirPlusEmailVerificationStore :
    IPhilipsAirPlusEmailVerificationStore
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public string Create(
        string authenticatedUserId,
        PhilipsAirPlusEmailVerificationTicket verificationTicket)
    {
        if (string.IsNullOrWhiteSpace(authenticatedUserId) ||
            verificationTicket is null)
        {
            throw new ArgumentException("A valid email verification request is required.");
        }

        RemoveExpired();
        var verificationId = Base64Url(RandomNumberGenerator.GetBytes(32));
        if (!_entries.TryAdd(verificationId, new Entry(
                authenticatedUserId, verificationTicket, DateTimeOffset.UtcNow.Add(Lifetime))))
        {
            throw new InvalidOperationException("Could not create a unique Philips Air+ email verification request.");
        }

        return verificationId;
    }

    public bool TryGet(
        string authenticatedUserId,
        string verificationId,
        out PhilipsAirPlusEmailVerificationTicket verificationTicket)
    {
        verificationTicket = null!;
        if (!TryGetEntry(authenticatedUserId, verificationId, out var entry))
        {
            return false;
        }

        verificationTicket = entry.VerificationTicket;
        return true;
    }

    public bool TryConsume(string authenticatedUserId, string verificationId)
    {
        if (!TryGetEntry(authenticatedUserId, verificationId, out _))
        {
            return false;
        }

        return _entries.TryRemove(verificationId, out _);
    }

    private bool TryGetEntry(
        string authenticatedUserId,
        string verificationId,
        out Entry entry)
    {
        entry = null!;
        if (string.IsNullOrWhiteSpace(authenticatedUserId) ||
            string.IsNullOrWhiteSpace(verificationId) ||
            !_entries.TryGetValue(verificationId, out var stored))
        {
            return false;
        }

        if (stored.ExpiresAtUtc < DateTimeOffset.UtcNow)
        {
            _entries.TryRemove(verificationId, out _);
            return false;
        }

        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(stored.UserId),
                Encoding.UTF8.GetBytes(authenticatedUserId)))
        {
            return false;
        }

        entry = stored;
        return true;
    }

    private void RemoveExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (key, entry) in _entries)
        {
            if (entry.ExpiresAtUtc < now)
            {
                _entries.TryRemove(key, out _);
            }
        }
    }

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record Entry(
        string UserId,
        PhilipsAirPlusEmailVerificationTicket VerificationTicket,
        DateTimeOffset ExpiresAtUtc);
}

public sealed class PhilipsAirPlusHubService(
    IHomeStateStore homeStateStore,
    ISecretStore secretStore,
    IPhilipsAirPlusClient airPlusClient,
    IPhilipsAirPlusEmailVerificationStore emailVerifications,
    PhilipsAirPlusOptions options) : IDeviceProvider
{
    private const string ProviderType = "philips.airplus";
    private static readonly TimeSpan RefreshSafetyMargin = TimeSpan.FromMinutes(1);
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _tokenLocks = new();

    public string DeviceType => ProviderType;

    public IReadOnlyList<Hub> GetHubs() => homeStateStore.Current.Document.Hubs
        .Where(hub => hub.ProviderType == ProviderType)
        .OrderBy(hub => hub.Name, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public IReadOnlyList<Device> GetMappedDevices() => homeStateStore.Current.Document.Devices
        .Where(device => device.DeviceType == ProviderType)
        .OrderBy(device => device.Name, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public async Task<PhilipsAirPlusEmailVerificationResult> RequestEmailVerificationAsync(
        string authenticatedUserId,
        string email,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options.Validate();
        if (string.IsNullOrWhiteSpace(authenticatedUserId) || !IsValidEmail(email))
        {
            return new PhilipsAirPlusEmailVerificationResult(DeviceOperationStatus.InvalidState);
        }

        var requested = await airPlusClient.RequestEmailVerificationAsync(
            email.Trim(), cancellationToken);
        if (!requested.Succeeded || requested.Value is null)
        {
            return new PhilipsAirPlusEmailVerificationResult(
                requested.Status, Detail: requested.Detail);
        }

        var verificationId = emailVerifications.Create(
            authenticatedUserId, requested.Value);
        return new PhilipsAirPlusEmailVerificationResult(
            DeviceOperationStatus.Succeeded, verificationId);
    }

    public async Task<PhilipsAirPlusHubOperationResult> CompleteEmailVerificationAsync(
        string authenticatedUserId,
        string verificationId,
        string code,
        string hubName,
        CancellationToken cancellationToken = default)
    {
        options.Validate();
        if (string.IsNullOrWhiteSpace(authenticatedUserId) ||
            !IsValidVerificationId(verificationId) ||
            !IsValidVerificationCode(code) ||
            string.IsNullOrWhiteSpace(hubName) || hubName.Trim().Length > 200 ||
            !emailVerifications.TryGet(
                authenticatedUserId, verificationId,
                out var verificationTicket))
        {
            return new PhilipsAirPlusHubOperationResult(DeviceOperationStatus.InvalidState);
        }

        var token = await airPlusClient.CompleteEmailVerificationAsync(
            code.Trim(), verificationTicket, cancellationToken);
        if (!token.Succeeded || token.Value is null)
        {
            return new PhilipsAirPlusHubOperationResult(token.Status, Detail: token.Detail);
        }

        if (!emailVerifications.TryConsume(authenticatedUserId, verificationId))
        {
            return new PhilipsAirPlusHubOperationResult(DeviceOperationStatus.InvalidState);
        }

        var secretReference = await secretStore.StoreAsync(
            JsonSerializer.Serialize(token.Value), cancellationToken);
        var hub = new Hub(
            Guid.NewGuid(),
            hubName.Trim(),
            ProviderType,
            options.ApiBaseUri.AbsoluteUri,
            secretReference,
            Enabled: true);
        try
        {
            await homeStateStore.UpdateAsync(current =>
            {
                if (current.Document.Hubs.Any(existing => existing.ProviderType == ProviderType))
                {
                    throw new InvalidOperationException("A Philips Air+ account is already linked.");
                }

                return current.Document with { Hubs = [.. current.Document.Hubs, hub] };
            }, homeStateStore.Current.Revision, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            await secretStore.DeleteAsync(secretReference, cancellationToken);
            throw;
        }
        catch (IOException)
        {
            await secretStore.DeleteAsync(secretReference, cancellationToken);
            throw;
        }

        return new PhilipsAirPlusHubOperationResult(DeviceOperationStatus.Succeeded, hub);
    }

    public async Task<PhilipsAirPlusDeviceQueryResult> GetImportCandidatesAsync(
        Guid hubId,
        CancellationToken cancellationToken = default)
    {
        if (GetHub(hubId) is not { } hub)
        {
            return new PhilipsAirPlusDeviceQueryResult(
                DeviceOperationStatus.ResourceNotFound, Array.Empty<PhilipsAirPlusDeviceCandidate>());
        }

        var token = await GetUsableTokenAsync(hub, cancellationToken);
        if (!token.Succeeded || token.Value is null)
        {
            return new PhilipsAirPlusDeviceQueryResult(token.Status, Array.Empty<PhilipsAirPlusDeviceCandidate>(), token.Detail);
        }

        var remoteDevices = await airPlusClient.GetDevicesAsync(token.Value.AccessToken, cancellationToken);
        if (!remoteDevices.Succeeded || remoteDevices.Value is null)
        {
            return new PhilipsAirPlusDeviceQueryResult(
                remoteDevices.Status, Array.Empty<PhilipsAirPlusDeviceCandidate>(), remoteDevices.Detail);
        }

        var candidates = new List<PhilipsAirPlusDeviceCandidate>();
        foreach (var remote in remoteDevices.Value)
        {
            var shadow = await airPlusClient.GetDeviceDataAsync(
                token.Value.AccessToken,
                remote,
                cancellationToken);
            candidates.Add(new PhilipsAirPlusDeviceCandidate(
                remote.DeviceId, remote.FriendlyName, remote.ProductType,
                shadow.Succeeded && shadow.Value.ValueKind != JsonValueKind.Undefined
                    ? PhilipsAirPlusShadowMapper.Map(shadow.Value)
                    : null));
        }

        return new PhilipsAirPlusDeviceQueryResult(DeviceOperationStatus.Succeeded, candidates);
    }

    public async Task<PhilipsAirPlusDeviceQueryResult> GetUnmappedImportCandidatesAsync(
        Guid hubId,
        CancellationToken cancellationToken = default)
    {
        var result = await GetImportCandidatesAsync(hubId, cancellationToken);
        if (!result.Succeeded)
        {
            return result;
        }

        var mapped = homeStateStore.Current.Document.Devices
            .Where(device => device.HubId == hubId && device.DeviceType == ProviderType)
            .Select(device => device.ExternalResourceId)
            .ToHashSet(StringComparer.Ordinal);
        return result with { Devices = result.Devices.Where(candidate => !mapped.Contains(candidate.DeviceId)).ToArray() };
    }

    public async Task<DeviceOperationResult> ImportDeviceAsync(
        Guid hubId,
        string deviceId,
        string displayName,
        Guid? locationId,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidInput(deviceId, displayName) || locationId == Guid.Empty)
        {
            return new DeviceOperationResult(DeviceOperationStatus.InvalidState);
        }

        var candidates = await GetImportCandidatesAsync(hubId, cancellationToken);
        var candidate = candidates.Devices.SingleOrDefault(item =>
            string.Equals(item.DeviceId, deviceId, StringComparison.Ordinal));
        if (!candidates.Succeeded || candidate is null)
        {
            return new DeviceOperationResult(
                candidates.Succeeded ? DeviceOperationStatus.ResourceNotFound : candidates.Status,
                Detail: candidates.Detail);
        }

        await homeStateStore.UpdateAsync(current =>
        {
            if (!current.HubsById.TryGetValue(hubId, out var hub) || hub.ProviderType != ProviderType ||
                locationId is { } assignedLocation && !current.LocationsById.ContainsKey(assignedLocation) ||
                current.Document.Devices.Any(device => device.HubId == hubId &&
                    device.DeviceType == ProviderType && device.ExternalResourceId == candidate.DeviceId))
            {
                throw new InvalidOperationException("The Philips Air+ device mapping is invalid.");
            }

            return current.Document with
            {
                Devices =
                [
                    .. current.Document.Devices,
                    new Device(
                        Guid.NewGuid(),
                        CreateUniqueName(displayName, current.Document.Devices),
                        ProviderType,
                        locationId,
                        HubId: hubId,
                        ExternalResourceId: candidate.DeviceId,
                        Capabilities: DeviceCapabilities.FanSpeed,
                        State: candidate.State),
                ],
            };
        }, homeStateStore.Current.Revision, cancellationToken);
        return new DeviceOperationResult(DeviceOperationStatus.Succeeded, candidate.State);
    }

    public async Task<DeviceOperationResult> ReadDeviceStateAsync(
        Device device,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetDevice(device, out var hub, out var deviceId))
        {
            return new DeviceOperationResult(DeviceOperationStatus.InvalidState);
        }

        var token = await GetUsableTokenAsync(hub, cancellationToken);
        if (!token.Succeeded || token.Value is null)
        {
            return new DeviceOperationResult(token.Status, Detail: token.Detail);
        }

        var remote = await FindRemoteDeviceAsync(token.Value.AccessToken, deviceId, cancellationToken);
        if (!remote.Succeeded || remote.Value is null)
        {
            return new DeviceOperationResult(remote.Status, Detail: remote.Detail);
        }

        var shadow = await airPlusClient.GetDeviceDataAsync(
            token.Value.AccessToken,
            remote.Value,
            cancellationToken);
        if (!shadow.Succeeded)
        {
            return new DeviceOperationResult(shadow.Status, Detail: shadow.Detail);
        }

        return await PersistStateAsync(
            device.Id, PhilipsAirPlusShadowMapper.Map(shadow.Value), cancellationToken);
    }

    public async Task<DeviceOperationResult> SetFanSpeedAsync(
        Device device,
        int fanSpeed,
        CancellationToken cancellationToken = default)
    {
        if (fanSpeed is not (>= 1 and <= 10) and not 81 and not 82 ||
            !device.Capabilities.HasFlag(DeviceCapabilities.FanSpeed) ||
            !TryGetDevice(device, out var hub, out var deviceId))
        {
            return new DeviceOperationResult(DeviceOperationStatus.InvalidState);
        }

        var token = await GetUsableTokenAsync(hub, cancellationToken);
        if (!token.Succeeded || token.Value is null)
        {
            return new DeviceOperationResult(token.Status, Detail: token.Detail);
        }

        var remote = await FindRemoteDeviceAsync(token.Value.AccessToken, deviceId, cancellationToken);
        if (!remote.Succeeded || remote.Value is null)
        {
            return new DeviceOperationResult(remote.Status, Detail: remote.Detail);
        }

        var update = await airPlusClient.SetFanSpeedAsync(token.Value.AccessToken, remote.Value, fanSpeed, cancellationToken);
        if (!update.Succeeded)
        {
            return new DeviceOperationResult(update.Status, Detail: update.Detail);
        }

        var mapped = PhilipsAirPlusShadowMapper.Map(update.Value);
        var attributes = mapped.Attributes is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(mapped.Attributes, StringComparer.Ordinal);
        attributes["fanSpeed"] = fanSpeed.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return await PersistStateAsync(
            device.Id, mapped with { Attributes = attributes }, cancellationToken);
    }

    Task<DeviceOperationResult> IDeviceProvider.GetStateAsync(
        Device device, CancellationToken cancellationToken) =>
        ReadDeviceStateAsync(device, cancellationToken);

    Task<DeviceOperationResult> IDeviceProvider.SetStateAsync(
        Device device, DeviceStateChange change, CancellationToken cancellationToken) =>
        change is { IsOn: null, Brightness: null, Colour: null, FanSpeed: { } fanSpeed }
            ? SetFanSpeedAsync(device, fanSpeed, cancellationToken)
            : Task.FromResult(new DeviceOperationResult(DeviceOperationStatus.InvalidState));

    public async Task SetDeviceEnabledAsync(
        Guid deviceId, bool enabled, CancellationToken cancellationToken = default) =>
        await UpdateDeviceAsync(deviceId, device => device with { Enabled = enabled }, cancellationToken);

    public async Task SetDeviceLocationAsync(
        Guid deviceId, Guid? locationId, CancellationToken cancellationToken = default)
    {
        await homeStateStore.UpdateAsync(current =>
        {
            if (locationId is { } assignedLocation && !current.LocationsById.ContainsKey(assignedLocation))
            {
                throw new InvalidOperationException("The assigned location does not exist.");
            }

            return UpdateDevice(current, deviceId, device => device with { LocationId = locationId });
        }, homeStateStore.Current.Revision, cancellationToken);
    }

    public async Task RemoveDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default) =>
        await homeStateStore.UpdateAsync(current =>
        {
            if (!current.DevicesById.TryGetValue(deviceId, out var device) || device.DeviceType != ProviderType)
            {
                throw new InvalidOperationException("The Philips Air+ device does not exist.");
            }

            return current.Document with
            {
                Devices = current.Document.Devices.Where(device => device.Id != deviceId).ToArray(),
            };
        }, homeStateStore.Current.Revision, cancellationToken);

    public async Task SetHubEnabledAsync(Guid hubId, bool enabled, CancellationToken cancellationToken = default) =>
        await homeStateStore.UpdateAsync(current =>
        {
            if (!current.HubsById.TryGetValue(hubId, out var hub) || hub.ProviderType != ProviderType)
            {
                throw new InvalidOperationException("The Philips Air+ hub does not exist.");
            }

            return current.Document with
            {
                Hubs = current.Document.Hubs.Select(hub => hub.Id == hubId
                    ? hub with { Enabled = enabled }
                    : hub).ToArray(),
            };
        }, homeStateStore.Current.Revision, cancellationToken);

    public async Task RemoveHubAsync(Guid hubId, CancellationToken cancellationToken = default)
    {
        if (!homeStateStore.Current.HubsById.TryGetValue(hubId, out var hub) ||
            hub.ProviderType != ProviderType)
        {
            throw new InvalidOperationException("The Philips Air+ hub does not exist.");
        }

        await homeStateStore.UpdateAsync(current => current.Document with
        {
            Hubs = current.Document.Hubs.Where(candidate => candidate.Id != hubId).ToArray(),
            Devices = current.Document.Devices.Where(device => device.HubId != hubId).ToArray(),
        }, homeStateStore.Current.Revision, cancellationToken);
        await secretStore.DeleteAsync(hub.SecretReference, cancellationToken);
    }

    private async Task<PhilipsAirPlusRequestResult<PhilipsAirPlusAccessToken>> GetUsableTokenAsync(
        Hub hub,
        CancellationToken cancellationToken)
    {
        var tokenLock = _tokenLocks.GetOrAdd(hub.Id, _ => new SemaphoreSlim(1, 1));
        await tokenLock.WaitAsync(cancellationToken);
        try
        {
            if (!homeStateStore.Current.HubsById.TryGetValue(hub.Id, out var currentHub) ||
                currentHub.ProviderType != ProviderType || !currentHub.Enabled)
            {
                return new PhilipsAirPlusRequestResult<PhilipsAirPlusAccessToken>(
                    DeviceOperationStatus.ResourceNotFound,
                    Detail: "The Philips Air+ account is unavailable.");
            }

            PhilipsAirPlusAccessToken token;
            try
            {
                token = JsonSerializer.Deserialize<PhilipsAirPlusAccessToken>(
                    await secretStore.GetAsync(currentHub.SecretReference, cancellationToken)) ??
                    throw new JsonException();
            }
            catch (JsonException)
            {
                return new PhilipsAirPlusRequestResult<PhilipsAirPlusAccessToken>(
                    DeviceOperationStatus.InvalidState, Detail: "The Philips Air+ credential is invalid.");
            }
            catch (IOException)
            {
                return new PhilipsAirPlusRequestResult<PhilipsAirPlusAccessToken>(
                    DeviceOperationStatus.BridgeUnavailable,
                    Detail: "The Philips Air+ credential could not be read.");
            }

            if (token.ExpiresAtUtc > DateTimeOffset.UtcNow.Add(RefreshSafetyMargin))
            {
                return new PhilipsAirPlusRequestResult<PhilipsAirPlusAccessToken>(
                    DeviceOperationStatus.Succeeded, token);
            }

            var refreshed = await airPlusClient.RefreshTokenAsync(token, cancellationToken);
            if (!refreshed.Succeeded || refreshed.Value is null)
            {
                return refreshed;
            }

            var reference = await secretStore.StoreAsync(
                JsonSerializer.Serialize(refreshed.Value),
                cancellationToken);
            try
            {
                await homeStateStore.UpdateAsync(current =>
                {
                    if (!current.HubsById.TryGetValue(hub.Id, out var latestHub) ||
                        latestHub.SecretReference != currentHub.SecretReference)
                    {
                        throw new InvalidOperationException("The Philips Air+ credential was changed.");
                    }

                    return current.Document with
                    {
                        Hubs = current.Document.Hubs.Select(item => item.Id == hub.Id
                            ? item with { SecretReference = reference }
                            : item).ToArray(),
                    };
                }, homeStateStore.Current.Revision, cancellationToken);
            }
            catch (InvalidOperationException)
            {
                await secretStore.DeleteAsync(reference, cancellationToken);
                throw;
            }
            catch (IOException)
            {
                await secretStore.DeleteAsync(reference, cancellationToken);
                throw;
            }

            await secretStore.DeleteAsync(currentHub.SecretReference, cancellationToken);
            return refreshed;
        }
        finally
        {
            tokenLock.Release();
        }
    }

    private async Task<PhilipsAirPlusRequestResult<PhilipsAirPlusRemoteDevice>> FindRemoteDeviceAsync(
        string accessToken, string deviceId, CancellationToken cancellationToken)
    {
        var devices = await airPlusClient.GetDevicesAsync(accessToken, cancellationToken);
        if (!devices.Succeeded || devices.Value is null)
        {
            return new PhilipsAirPlusRequestResult<PhilipsAirPlusRemoteDevice>(
                devices.Status, Detail: devices.Detail);
        }

        var device = devices.Value.SingleOrDefault(item => item.DeviceId == deviceId);
        return device is null
            ? new PhilipsAirPlusRequestResult<PhilipsAirPlusRemoteDevice>(DeviceOperationStatus.ResourceNotFound)
            : new PhilipsAirPlusRequestResult<PhilipsAirPlusRemoteDevice>(
                DeviceOperationStatus.Succeeded, device);
    }

    private async Task<DeviceOperationResult> PersistStateAsync(
        Guid deviceId, DeviceState state, CancellationToken cancellationToken)
    {
        await homeStateStore.UpdateAsync(current => UpdateDevice(
            current, deviceId, device => device with { State = state }),
            homeStateStore.Current.Revision, cancellationToken);
        return new DeviceOperationResult(DeviceOperationStatus.Succeeded, state);
    }

    private async Task UpdateDeviceAsync(
        Guid deviceId, Func<Device, Device> change, CancellationToken cancellationToken) =>
        await homeStateStore.UpdateAsync(
            current => UpdateDevice(current, deviceId, change),
            homeStateStore.Current.Revision,
            cancellationToken);

    private static HomeStateDocument UpdateDevice(
        HomeState state, Guid deviceId, Func<Device, Device> change)
    {
        if (!state.DevicesById.TryGetValue(deviceId, out var device) || device.DeviceType != ProviderType)
        {
            throw new InvalidOperationException("The Philips Air+ device does not exist.");
        }

        return state.Document with
        {
            Devices = state.Document.Devices.Select(candidate => candidate.Id == deviceId
                ? change(candidate)
                : candidate).ToArray(),
        };
    }

    private Hub? GetHub(Guid hubId) =>
        homeStateStore.Current.HubsById.TryGetValue(hubId, out var hub) &&
        hub.ProviderType == ProviderType && hub.Enabled
            ? hub
            : null;

    private bool TryGetDevice(Device device, out Hub hub, out string externalId)
    {
        hub = null!;
        externalId = string.Empty;
        if (!device.Enabled || device.DeviceType != ProviderType ||
            device.HubId is not { } hubId ||
            string.IsNullOrWhiteSpace(device.ExternalResourceId) ||
            GetHub(hubId) is not { } resolvedHub)
        {
            return false;
        }

        hub = resolvedHub;
        externalId = device.ExternalResourceId;
        return true;
    }

    private static bool IsValidInput(string deviceId, string displayName) =>
        !string.IsNullOrWhiteSpace(deviceId) && deviceId == deviceId.Trim() &&
        deviceId.Length <= 200 && !string.IsNullOrWhiteSpace(displayName) &&
        displayName.Trim().Length <= 200;

    private static string CreateUniqueName(string requested, IReadOnlyList<Device> devices)
    {
        var name = requested.Trim();
        var names = devices.Select(device => device.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (names.Add(name))
        {
            return name;
        }

        for (var suffix = 2; suffix < 10_000; suffix++)
        {
            var candidate = $"{name} ({suffix})";
            if (candidate.Length <= 200 && names.Add(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Could not create a unique Philips Air+ device name.");
    }

    private static bool IsValidVerificationId(string value) =>
        value.Length is >= 40 and <= 128 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static bool IsValidVerificationCode(string value) =>
        value.Length is > 0 and <= 32 &&
        value.All(char.IsAsciiLetterOrDigit);

    private static bool IsValidEmail(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.Length > 254 ||
            value.Any(char.IsWhiteSpace) || value.Count(character => character == '@') != 1)
        {
            return false;
        }

        var at = value.IndexOf('@');
        var local = value[..at];
        var domain = value[(at + 1)..];
        if (local.Length is < 1 or > 64 || local[0] == '.' || local[^1] == '.' ||
            !domain.Contains('.', StringComparison.Ordinal) || domain.Length > 253)
        {
            return false;
        }

        return local.All(character =>
                   char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or '+') &&
               domain.Split('.').All(label => label.Length is > 0 and <= 63 &&
                   label[0] != '-' && label[^1] != '-' &&
                   label.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'));
    }
}
