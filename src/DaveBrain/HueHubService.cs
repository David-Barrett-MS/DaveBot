using DaveHome;

namespace DaveBrain;

public sealed record HueHubOperationResult(
    DeviceOperationStatus Status,
    Hub? Hub = null,
    string? Detail = null,
    int ImportedLightCount = 0);

public sealed class HueHubService(
    IHomeStateStore homeStateStore,
    ISecretStore secretStore,
    IHueBridgeClient hueBridgeClient) : IDeviceProvider
{
    private const string UnassignedHueDevicesLocationName = "Unassigned Hue devices";

    public string DeviceType => "hue.light";

    public IReadOnlyList<Hub> GetHubs() => homeStateStore.Current.HubsById.Values
        .OrderBy(hub => hub.Name, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public IReadOnlyList<Device> GetMappedLights() => homeStateStore.Current.Document.Devices
        .Where(device => device.DeviceType == "hue.light")
        .OrderBy(device => device.Name, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public async Task MigrateLegacyUnassignedLightLocationsAsync(
        CancellationToken cancellationToken = default)
    {
        if (!ContainsLegacyUnassignedLocation(homeStateStore.Current.Document))
        {
            return;
        }

        await homeStateStore.UpdateAsync(
            state => MigrateLegacyUnassignedLocations(state.Document),
            homeStateStore.Current.Revision,
            cancellationToken);
    }

    public Task<IReadOnlyList<HueBridgeDiscovery>> DiscoverAsync(
        CancellationToken cancellationToken = default) =>
        hueBridgeClient.DiscoverAsync(cancellationToken);

    public async Task<HueHubOperationResult> LinkAsync(
        string name,
        Uri bridgeAddress,
        string? bridgeId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200 ||
            bridgeAddress.Scheme != Uri.UriSchemeHttps)
        {
            return new HueHubOperationResult(DeviceOperationStatus.InvalidState);
        }

        if (homeStateStore.Current.Document.Hubs.Any(existing =>
            string.Equals(existing.Address, bridgeAddress.AbsoluteUri, StringComparison.OrdinalIgnoreCase)))
        {
            return new HueHubOperationResult(
                DeviceOperationStatus.InvalidState,
                Detail: "The Hue bridge is already linked.");
        }

        var link = await hueBridgeClient.LinkAsync(bridgeAddress, bridgeId, cancellationToken);
        if (link.Status != DeviceOperationStatus.Succeeded || link.ApplicationKey is null)
        {
            return new HueHubOperationResult(link.Status, Detail: link.Detail);
        }
        if (!string.IsNullOrWhiteSpace(bridgeId) &&
            string.IsNullOrWhiteSpace(link.TlsCertificateThumbprint))
        {
            return new HueHubOperationResult(
                DeviceOperationStatus.InvalidState,
                Detail: "The Hue bridge certificate could not be verified and pinned.");
        }

        var secretReference = await secretStore.StoreAsync(link.ApplicationKey, cancellationToken);
        var hub = new Hub(
            Guid.NewGuid(),
            name.Trim(),
            "hue",
            bridgeAddress.AbsoluteUri,
            secretReference,
            Enabled: true,
            BridgeId: bridgeId,
            TlsCertificateThumbprint: link.TlsCertificateThumbprint);
        try
        {
            await homeStateStore.UpdateAsync(state =>
            {
                if (state.Document.Hubs.Any(existing =>
                    string.Equals(existing.Address, hub.Address, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException("The Hue bridge is already linked.");
                }

                return state.Document with { Hubs = state.Document.Hubs.Append(hub).ToArray() };
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

        var synchronization = await SynchronizeHubAsync(hub.Id, cancellationToken);
        return synchronization.Status == DeviceOperationStatus.Succeeded
            ? new HueHubOperationResult(
                DeviceOperationStatus.Succeeded,
                hub,
                ImportedLightCount: synchronization.ImportedLightCount)
            : new HueHubOperationResult(
                synchronization.Status,
                hub,
                synchronization.Detail,
                synchronization.ImportedLightCount);
    }

    public async Task<HueHubOperationResult> SynchronizeHubAsync(
        Guid hubId,
        CancellationToken cancellationToken = default)
    {
        var hub = GetHub(hubId);
        if (hub is null)
        {
            return new HueHubOperationResult(DeviceOperationStatus.ResourceNotFound);
        }

        var candidates = await GetImportCandidatesAsync(hubId, cancellationToken);
        if (!candidates.Succeeded)
        {
            return new HueHubOperationResult(candidates.Status, hub, candidates.Detail);
        }

        var importedLightCount = 0;
        await homeStateStore.UpdateAsync(state =>
        {
            var document = MigrateLegacyUnassignedLocations(state.Document);
            var lightsByResourceId = candidates.Lights.ToDictionary(light => light.ResourceId);
            var devices = document.Devices
                .Select(device => device.HubId == hubId &&
                    device.ExternalResourceId is { } resourceId &&
                    lightsByResourceId.TryGetValue(resourceId, out var light)
                    ? device with { Capabilities = light.Capabilities, State = light.State }
                    : device)
                .ToList();
            var deviceNames = new HashSet<string>(
                devices.Select(device => device.Name),
                StringComparer.OrdinalIgnoreCase);
            foreach (var light in candidates.Lights)
            {
                if (devices.Any(device => device.HubId == hubId &&
                    string.Equals(device.ExternalResourceId, light.ResourceId, StringComparison.Ordinal)))
                {
                    continue;
                }

                devices.Add(new Device(
                    Guid.NewGuid(),
                    CreateUniqueDeviceName(light.Name, hub.Name, light.ResourceId, deviceNames),
                    "hue.light",
                    LocationId: null,
                    HubId: hubId,
                    ExternalResourceId: light.ResourceId,
                    Capabilities: light.Capabilities,
                    State: light.State));
                importedLightCount++;
            }

            return state.Document with
            {
                Locations = document.Locations,
                Hubs = state.Document.Hubs.Select(existing => existing.Id == hubId
                    ? existing with { LastSynchronizedUtc = DateTimeOffset.UtcNow }
                    : existing).ToArray(),
                Devices = devices,
            };
        }, homeStateStore.Current.Revision, cancellationToken);

        return new HueHubOperationResult(
            DeviceOperationStatus.Succeeded,
            homeStateStore.Current.HubsById[hubId],
            ImportedLightCount: importedLightCount);
    }

    public async Task<HueLightQueryResult> GetImportCandidatesAsync(
        Guid hubId,
        CancellationToken cancellationToken = default)
    {
        var hub = GetHub(hubId);
        if (hub is null)
        {
            return new HueLightQueryResult(DeviceOperationStatus.ResourceNotFound, Array.Empty<HueLightResource>());
        }

        var applicationKey = await secretStore.GetAsync(hub.SecretReference, cancellationToken);
        return await hueBridgeClient.GetLightsAsync(
            new Uri(hub.Address),
            applicationKey,
            hub.BridgeId,
            hub.TlsCertificateThumbprint,
            cancellationToken);
    }

    public async Task<HueLightQueryResult> GetUnmappedLightImportCandidatesAsync(
        Guid hubId,
        CancellationToken cancellationToken = default)
    {
        var result = await GetImportCandidatesAsync(hubId, cancellationToken);
        if (!result.Succeeded)
        {
            return result;
        }

        var mappedResourceIds = homeStateStore.Current.Document.Devices
            .Where(device => device.HubId == hubId && device.ExternalResourceId is not null)
            .Select(device => device.ExternalResourceId!)
            .ToHashSet(StringComparer.Ordinal);
        return result with
        {
            Lights = result.Lights
                .Where(light => !mappedResourceIds.Contains(light.ResourceId))
                .ToArray(),
        };
    }

    public async Task<DeviceOperationResult> ImportLightAsync(
        Guid hubId,
        string resourceId,
        string displayName,
        Guid? locationId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(resourceId) || string.IsNullOrWhiteSpace(displayName) ||
            displayName.Length > 200 || locationId == Guid.Empty)
        {
            return new DeviceOperationResult(DeviceOperationStatus.InvalidState);
        }

        var candidates = await GetImportCandidatesAsync(hubId, cancellationToken);
        var light = candidates.Lights.SingleOrDefault(candidate =>
            string.Equals(candidate.ResourceId, resourceId, StringComparison.Ordinal));
        if (candidates.Status != DeviceOperationStatus.Succeeded || light is null)
        {
            return new DeviceOperationResult(
                candidates.Status == DeviceOperationStatus.Succeeded
                    ? DeviceOperationStatus.ResourceNotFound
                    : candidates.Status,
                Detail: candidates.Detail);
        }

        await homeStateStore.UpdateAsync(state =>
        {
            if (locationId is { } assignedLocationId &&
                !state.LocationsById.ContainsKey(assignedLocationId))
            {
                throw new InvalidOperationException("The assigned location does not exist.");
            }

            if (state.Document.Devices.Any(device => device.HubId == hubId &&
                string.Equals(device.ExternalResourceId, resourceId, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("This Hue light is already imported.");
            }

            return state.Document with
            {
                Devices = state.Document.Devices.Append(new Device(
                    Guid.NewGuid(),
                    displayName.Trim(),
                    "hue.light",
                    locationId,
                    HubId: hubId,
                    ExternalResourceId: light.ResourceId,
                    Capabilities: light.Capabilities,
                    State: light.State)).ToArray(),
            };
        }, homeStateStore.Current.Revision, cancellationToken);
        return new DeviceOperationResult(DeviceOperationStatus.Succeeded, light.State);
    }

    public async Task<DeviceOperationResult> ReadLightStateAsync(
        Device device,
        CancellationToken cancellationToken = default)
    {
        if (!device.Enabled || !TryGetHueDevice(device, out var hub, out var resourceId))
        {
            return new DeviceOperationResult(DeviceOperationStatus.InvalidState);
        }

        var applicationKey = await secretStore.GetAsync(hub.SecretReference, cancellationToken);
        var result = await hueBridgeClient.GetLightStateAsync(
            new Uri(hub.Address),
            applicationKey,
            resourceId,
            hub.BridgeId,
            hub.TlsCertificateThumbprint,
            cancellationToken);
        return await PersistStateAsync(device.Id, result, cancellationToken);
    }

    public async Task<DeviceOperationResult> SetLightStateAsync(
        Device device,
        DeviceStateChange change,
        CancellationToken cancellationToken = default)
    {
        if (!device.Enabled || !TryGetHueDevice(device, out var hub, out var resourceId) ||
            (change.IsOn is not null && !device.Capabilities.HasFlag(DeviceCapabilities.OnOff)) ||
            (change.Brightness is not null &&
             (!device.Capabilities.HasFlag(DeviceCapabilities.Brightness) ||
              change.Brightness is < 0 or > 100)) ||
            (change.Colour is not null &&
             (!device.Capabilities.HasFlag(DeviceCapabilities.Colour) ||
              change.Colour.X is < 0 or > 1 ||
              change.Colour.Y is < 0 or > 1)))
        {
            return new DeviceOperationResult(DeviceOperationStatus.InvalidState);
        }

        var applicationKey = await secretStore.GetAsync(hub.SecretReference, cancellationToken);
        var result = await hueBridgeClient.SetLightStateAsync(
            new Uri(hub.Address),
            applicationKey,
            resourceId,
            change,
            hub.BridgeId,
            hub.TlsCertificateThumbprint,
            cancellationToken);
        return await PersistStateAsync(device.Id, result, cancellationToken);
    }

    Task<DeviceOperationResult> IDeviceProvider.GetStateAsync(
        Device device,
        CancellationToken cancellationToken) =>
        ReadLightStateAsync(device, cancellationToken);

    Task<DeviceOperationResult> IDeviceProvider.SetStateAsync(
        Device device,
        DeviceStateChange change,
        CancellationToken cancellationToken) =>
        SetLightStateAsync(device, change, cancellationToken);

    public async Task SetDeviceEnabledAsync(
        Guid deviceId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        await homeStateStore.UpdateAsync(state =>
        {
            if (!state.DevicesById.TryGetValue(deviceId, out var device) ||
                device.DeviceType != "hue.light")
            {
                throw new InvalidOperationException("The Hue light does not exist.");
            }

            return state.Document with
            {
                Devices = state.Document.Devices.Select(candidate => candidate.Id == deviceId
                    ? candidate with { Enabled = enabled }
                    : candidate).ToArray(),
            };
        }, homeStateStore.Current.Revision, cancellationToken);
    }

    public async Task RemoveDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        await homeStateStore.UpdateAsync(state =>
        {
            if (!state.DevicesById.TryGetValue(deviceId, out var device) ||
                device.DeviceType != "hue.light")
            {
                throw new InvalidOperationException("The Hue light does not exist.");
            }

            return state.Document with
            {
                Devices = state.Document.Devices
                    .Where(candidate => candidate.Id != deviceId)
                    .ToArray(),
            };
        }, homeStateStore.Current.Revision, cancellationToken);
    }

    private Hub? GetHub(Guid hubId) =>
        homeStateStore.Current.HubsById.TryGetValue(hubId, out var hub) && hub.Enabled
            ? hub
            : null;

    private static bool ContainsLegacyUnassignedLocation(HomeStateDocument document) =>
        document.Locations.Any(location => string.Equals(
            location.Name,
            UnassignedHueDevicesLocationName,
            StringComparison.OrdinalIgnoreCase));

    private static HomeStateDocument MigrateLegacyUnassignedLocations(HomeStateDocument document)
    {
        var legacyLocationIds = document.Locations
            .Where(location => string.Equals(
                location.Name,
                UnassignedHueDevicesLocationName,
                StringComparison.OrdinalIgnoreCase))
            .Select(location => location.Id)
            .ToHashSet();
        if (legacyLocationIds.Count == 0)
        {
            return document;
        }

        var devices = document.Devices.Select(device =>
            device.DeviceType == "hue.light" &&
            device.LocationId is { } locationId &&
            legacyLocationIds.Contains(locationId)
                ? device with { LocationId = null }
                : device).ToArray();
        var assignedLocationIds = devices
            .Where(device => device.LocationId is not null)
            .Select(device => device.LocationId!.Value)
            .ToHashSet();

        return document with
        {
            Locations = document.Locations
                .Where(location => !legacyLocationIds.Contains(location.Id) ||
                    assignedLocationIds.Contains(location.Id))
                .ToArray(),
            Devices = devices,
        };
    }

    private bool TryGetHueDevice(Device device, out Hub hub, out string resourceId)
    {
        hub = null!;
        resourceId = string.Empty;
        if (device.DeviceType != "hue.light" || device.HubId is not { } hubId ||
            string.IsNullOrWhiteSpace(device.ExternalResourceId) ||
            GetHub(hubId) is not { } resolvedHub)
        {
            return false;
        }

        hub = resolvedHub;
        resourceId = device.ExternalResourceId;
        return true;
    }

    private async Task<DeviceOperationResult> PersistStateAsync(
        Guid deviceId,
        DeviceOperationResult result,
        CancellationToken cancellationToken)
    {
        if (!result.Succeeded || result.State is null)
        {
            return result;
        }

        await homeStateStore.UpdateAsync(state => state.Document with
        {
            Devices = state.Document.Devices.Select(device => device.Id == deviceId
                ? device with { State = result.State }
                : device).ToArray(),
        }, homeStateStore.Current.Revision, cancellationToken);
        return result;
    }

    private static string CreateUniqueDeviceName(
        string hueName,
        string hubName,
        string resourceId,
        ISet<string> existingNames)
    {
        var baseName = string.IsNullOrWhiteSpace(hueName) ? resourceId : hueName.Trim();
        if (existingNames.Add(baseName))
        {
            return baseName;
        }

        var withHub = $"{baseName} ({hubName})";
        if (existingNames.Add(withHub))
        {
            return withHub;
        }

        var withResource = $"{withHub} [{resourceId}]";
        existingNames.Add(withResource);
        return withResource;
    }
}
