using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DaveHome;

namespace DaveBrain;

public sealed record PhilipsAirPlusAuthorizationResult(
    DeviceOperationStatus Status,
    Uri? AuthorizationUri = null,
    string? State = null,
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

public interface IPhilipsAirPlusAuthorizationStateStore
{
    PhilipsAirPlusAuthorizationState Create(string authenticatedUserId);

    bool TryConsume(
        string authenticatedUserId,
        string state,
        out PhilipsAirPlusAuthorizationState authorizationState);
}

public sealed record PhilipsAirPlusAuthorizationState(
    string State,
    string CodeVerifier,
    DateTimeOffset ExpiresAtUtc);

public sealed class InMemoryPhilipsAirPlusAuthorizationStateStore :
    IPhilipsAirPlusAuthorizationStateStore
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public PhilipsAirPlusAuthorizationState Create(string authenticatedUserId)
    {
        if (string.IsNullOrWhiteSpace(authenticatedUserId))
        {
            throw new ArgumentException("An authenticated user identifier is required.", nameof(authenticatedUserId));
        }

        RemoveExpired();
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        var created = new PhilipsAirPlusAuthorizationState(
            state, verifier, DateTimeOffset.UtcNow.Add(Lifetime));
        if (!_entries.TryAdd(state, new Entry(authenticatedUserId, created)))
        {
            throw new InvalidOperationException("Could not create a unique Philips Air+ authorization state.");
        }

        return created;
    }

    public bool TryConsume(
        string authenticatedUserId,
        string state,
        out PhilipsAirPlusAuthorizationState authorizationState)
    {
        authorizationState = null!;
        if (string.IsNullOrWhiteSpace(authenticatedUserId) || string.IsNullOrWhiteSpace(state) ||
            !_entries.TryGetValue(state, out var entry) ||
            entry.Authorization.ExpiresAtUtc < DateTimeOffset.UtcNow ||
            !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(entry.UserId),
                Encoding.UTF8.GetBytes(authenticatedUserId)))
        {
            return false;
        }

        if (!_entries.TryRemove(state, out _))
        {
            return false;
        }

        authorizationState = entry.Authorization;
        return true;
    }

    private void RemoveExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (key, entry) in _entries)
        {
            if (entry.Authorization.ExpiresAtUtc < now)
            {
                _entries.TryRemove(key, out _);
            }
        }
    }

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record Entry(string UserId, PhilipsAirPlusAuthorizationState Authorization);
}

public sealed class PhilipsAirPlusHubService(
    IHomeStateStore homeStateStore,
    ISecretStore secretStore,
    IPhilipsAirPlusClient airPlusClient,
    IPhilipsAirPlusAuthorizationStateStore authorizationStates,
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

    public Task<PhilipsAirPlusAuthorizationResult> BeginAuthorizationAsync(
        string authenticatedUserId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options.Validate();
        var authorization = authorizationStates.Create(authenticatedUserId);
        var challenge = Convert.ToBase64String(
                SHA256.HashData(Encoding.ASCII.GetBytes(authorization.CodeVerifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var query = new Dictionary<string, string>
        {
            ["client_id"] = options.ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = options.RedirectUri,
            ["scope"] = options.Scope,
            ["state"] = authorization.State,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
        };
        var builder = new UriBuilder(options.AuthorizationEndpoint)
        {
            Query = string.Join("&", query.Select(pair =>
                $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}")),
        };
        return Task.FromResult(new PhilipsAirPlusAuthorizationResult(
            DeviceOperationStatus.Succeeded, builder.Uri, authorization.State));
    }

    public async Task<PhilipsAirPlusHubOperationResult> CompleteAuthorizationAsync(
        string authenticatedUserId,
        string redirectUrlOrCode,
        string state,
        string hubName,
        CancellationToken cancellationToken = default)
    {
        options.Validate();
        if (string.IsNullOrWhiteSpace(hubName) || hubName.Trim().Length > 200 ||
            !TryExtractAuthorizationCode(redirectUrlOrCode, out var code, out var callbackState) ||
            !string.IsNullOrWhiteSpace(callbackState) &&
            !FixedTimeEquals(callbackState, state) ||
            !authorizationStates.TryConsume(authenticatedUserId, state, out var authorization))
        {
            return new PhilipsAirPlusHubOperationResult(DeviceOperationStatus.InvalidState);
        }

        var token = await airPlusClient.ExchangeAuthorizationCodeAsync(
            code, authorization.CodeVerifier, cancellationToken);
        if (!token.Succeeded || token.Value is null)
        {
            return new PhilipsAirPlusHubOperationResult(token.Status, Detail: token.Detail);
        }

        var account = await airPlusClient.GetAccountIdAsync(token.Value.AccessToken, cancellationToken);
        if (!account.Succeeded || string.IsNullOrWhiteSpace(account.Value))
        {
            return new PhilipsAirPlusHubOperationResult(account.Status, Detail: account.Detail);
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
            var shadow = await airPlusClient.GetShadowAsync(token.Value.AccessToken, remote, cancellationToken);
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

        var shadow = await airPlusClient.GetShadowAsync(token.Value.AccessToken, remote.Value, cancellationToken);
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

    private static bool TryExtractAuthorizationCode(
        string value, out string code, out string? state)
    {
        code = string.Empty;
        state = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var redirect))
        {
            code = value.Trim();
            return code.Length <= 4096 && !code.Contains(' ');
        }

        foreach (var pair in redirect.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var key = Uri.UnescapeDataString(separator < 0 ? pair : pair[..separator]);
            var decoded = Uri.UnescapeDataString(separator < 0 ? string.Empty : pair[(separator + 1)..]);
            if (key == "code")
            {
                code = decoded;
            }
            else if (key == "state")
            {
                state = decoded;
            }
        }

        return code.Length is > 0 and <= 4096;
    }

    private static bool FixedTimeEquals(string first, string second) =>
        !string.IsNullOrWhiteSpace(first) && !string.IsNullOrWhiteSpace(second) &&
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(first), Encoding.UTF8.GetBytes(second));
}
