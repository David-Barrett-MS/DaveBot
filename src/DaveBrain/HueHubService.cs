using DaveHome;
using System.Runtime.CompilerServices;

namespace DaveBrain;

public sealed record HueHubOperationResult(
    DeviceOperationStatus Status,
    Hub? Hub = null,
    string? Detail = null,
    int ImportedLightCount = 0,
    int ImportedSwitchCount = 0,
    int ImportedMotionSensorCount = 0);

public sealed record HueMappedSwitchState(
    Guid DeviceId,
    DeviceState State,
    string? EventId);

public sealed record HueMappedSwitchStateResult(
    DeviceOperationStatus Status,
    IReadOnlyList<HueMappedSwitchState> Switches,
    string? Detail = null)
{
    public bool Succeeded => Status == DeviceOperationStatus.Succeeded;
}

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

    public IReadOnlyList<Device> GetMappedSwitches() => homeStateStore.Current.Document.Devices
        .Where(device => device.DeviceType == "hue.switch")
        .OrderBy(device => device.Name, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public IReadOnlyList<Device> GetMappedMotionSensors() => homeStateStore.Current.Document.Devices
        .Where(device => device.DeviceType == "hue.motion-sensor")
        .OrderBy(device => device.Name, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public IReadOnlyList<Guid> GetMappedSwitchHubIds() => GetEnabledMappedEventSources()
        .Select(device => device.HubId!.Value)
        .Distinct()
        .ToArray();

    public IReadOnlyList<Device> GetMappedHueDevices() => homeStateStore.Current.Document.Devices
        .Where(device => device.DeviceType is "hue.light" or "hue.switch" or "hue.motion-sensor")
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
                ImportedLightCount: synchronization.ImportedLightCount,
                ImportedSwitchCount: synchronization.ImportedSwitchCount,
                ImportedMotionSensorCount: synchronization.ImportedMotionSensorCount)
            : new HueHubOperationResult(
                synchronization.Status,
                hub,
                synchronization.Detail,
                synchronization.ImportedLightCount,
                synchronization.ImportedSwitchCount,
                synchronization.ImportedMotionSensorCount);
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
        var switches = await GetSwitchImportCandidatesAsync(hubId, cancellationToken);
        if (!switches.Succeeded)
        {
            return new HueHubOperationResult(switches.Status, hub, switches.Detail);
        }
        var motionSensors = await GetMotionSensorImportCandidatesAsync(hubId, cancellationToken);
        if (!motionSensors.Succeeded)
        {
            return new HueHubOperationResult(
                motionSensors.Status,
                hub,
                motionSensors.Detail);
        }

        var importedLightCount = 0;
        var importedSwitchCount = 0;
        var importedMotionSensorCount = 0;
        await homeStateStore.UpdateAsync(state =>
        {
            var document = MigrateLegacyUnassignedLocations(state.Document);
            var lightsByResourceId = candidates.Lights.ToDictionary(light => light.ResourceId);
            var switchesByResourceId = switches.Switches.ToDictionary(switchDevice => switchDevice.ResourceId);
            var sensorsByMotionResourceId = motionSensors.MotionSensors.ToDictionary(
                sensor => sensor.MotionResourceId,
                StringComparer.Ordinal);
            var devices = document.Devices
                .Select(device => device.HubId == hubId &&
                    device.ExternalResourceId is { } resourceId &&
                    lightsByResourceId.TryGetValue(resourceId, out var light)
                    ? device with { Capabilities = light.Capabilities, State = light.State }
                    : device.HubId == hubId &&
                      device.ExternalResourceId is { } switchResourceId &&
                      switchesByResourceId.TryGetValue(switchResourceId, out var switchDevice)
                        ? device with
                        {
                            Capabilities = DeviceCapabilities.None,
                            State = switchDevice.State,
                        }
                    : device.HubId == hubId &&
                      device.DeviceType == "hue.motion-sensor" &&
                      device.ExternalResourceId is { } sensorReference &&
                      HueMotionSensorResourceReference.TryParse(
                          sensorReference,
                          out var motionResourceId,
                          out _) &&
                      sensorsByMotionResourceId.TryGetValue(
                          motionResourceId,
                          out var motionSensor)
                        ? device with
                        {
                            ExternalResourceId = motionSensor.LightLevelResourceId is null
                                ? device.ExternalResourceId
                                : motionSensor.ResourceId,
                            Capabilities = DeviceCapabilities.None,
                            State = MergeMotionSensorState(device.State, motionSensor.State),
                        }
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

            foreach (var switchDevice in switches.Switches)
            {
                if (devices.Any(device => device.HubId == hubId &&
                    string.Equals(device.ExternalResourceId, switchDevice.ResourceId, StringComparison.Ordinal)))
                {
                    continue;
                }

                devices.Add(new Device(
                    Guid.NewGuid(),
                    CreateUniqueDeviceName(
                        switchDevice.Name,
                        hub.Name,
                        switchDevice.ResourceId,
                        deviceNames),
                    "hue.switch",
                    LocationId: null,
                    HubId: hubId,
                    ExternalResourceId: switchDevice.ResourceId,
                    Capabilities: DeviceCapabilities.None,
                    State: switchDevice.State));
                importedSwitchCount++;
            }

            foreach (var motionSensor in motionSensors.MotionSensors)
            {
                if (devices.Any(device => device.HubId == hubId &&
                    ((device.DeviceType == "hue.motion-sensor" &&
                      HueMotionSensorResourceReference.TryParse(
                          device.ExternalResourceId,
                          out var mappedMotionResourceId,
                          out _) &&
                      string.Equals(
                          mappedMotionResourceId,
                          motionSensor.MotionResourceId,
                          StringComparison.Ordinal)) ||
                     string.Equals(
                         device.ExternalResourceId,
                         motionSensor.ResourceId,
                         StringComparison.Ordinal))))
                {
                    continue;
                }

                devices.Add(new Device(
                    Guid.NewGuid(),
                    CreateUniqueDeviceName(
                        motionSensor.Name,
                        hub.Name,
                        motionSensor.ResourceId,
                        deviceNames),
                    "hue.motion-sensor",
                    LocationId: null,
                    HubId: hubId,
                    ExternalResourceId: motionSensor.ResourceId,
                    Capabilities: DeviceCapabilities.None,
                    State: motionSensor.State));
                importedMotionSensorCount++;
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
            ImportedLightCount: importedLightCount,
            ImportedSwitchCount: importedSwitchCount,
            ImportedMotionSensorCount: importedMotionSensorCount);
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

    public async Task<HueSwitchQueryResult> GetSwitchImportCandidatesAsync(
        Guid hubId,
        CancellationToken cancellationToken = default)
    {
        var hub = GetHub(hubId);
        if (hub is null)
        {
            return new HueSwitchQueryResult(
                DeviceOperationStatus.ResourceNotFound,
                Array.Empty<HueSwitchResource>());
        }

        var applicationKey = await secretStore.GetAsync(hub.SecretReference, cancellationToken);
        return await hueBridgeClient.GetSwitchesAsync(
            new Uri(hub.Address),
            applicationKey,
            hub.BridgeId,
            hub.TlsCertificateThumbprint,
            cancellationToken);
    }

    public async Task<HueMotionSensorQueryResult> GetMotionSensorImportCandidatesAsync(
        Guid hubId,
        CancellationToken cancellationToken = default)
    {
        var hub = GetHub(hubId);
        if (hub is null)
        {
            return new HueMotionSensorQueryResult(
                DeviceOperationStatus.ResourceNotFound,
                Array.Empty<HueMotionSensorResource>());
        }

        var applicationKey = await secretStore.GetAsync(hub.SecretReference, cancellationToken);
        return await hueBridgeClient.GetMotionSensorsAsync(
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

        var mappedResourceIds = GetMappedHueResourceIds(hubId);
        return result with
        {
            Lights = result.Lights
                .Where(light => !mappedResourceIds.Contains(light.ResourceId))
                .ToArray(),
        };
    }

    public async Task<HueSwitchQueryResult> GetUnmappedSwitchImportCandidatesAsync(
        Guid hubId,
        CancellationToken cancellationToken = default)
    {
        var result = await GetSwitchImportCandidatesAsync(hubId, cancellationToken);
        if (!result.Succeeded)
        {
            return result;
        }

        var mappedResourceIds = GetMappedHueResourceIds(hubId);
        return result with
        {
            Switches = result.Switches
                .Where(switchDevice => !mappedResourceIds.Contains(switchDevice.ResourceId))
                .ToArray(),
        };
    }

    public async Task<HueMotionSensorQueryResult> GetUnmappedMotionSensorImportCandidatesAsync(
        Guid hubId,
        CancellationToken cancellationToken = default)
    {
        var result = await GetMotionSensorImportCandidatesAsync(hubId, cancellationToken);
        if (!result.Succeeded)
        {
            return result;
        }

        var mappedResourceIds = GetMappedHueResourceIds(hubId);
        return result with
        {
            MotionSensors = result.MotionSensors
                .Where(sensor =>
                    !mappedResourceIds.Contains(sensor.MotionResourceId) &&
                    (sensor.LightLevelResourceId is null ||
                     !mappedResourceIds.Contains(sensor.LightLevelResourceId)))
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

            if (GetMappedHueResourceIds(state.Document, hubId).Contains(resourceId))
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

    public async Task<DeviceOperationResult> ImportSwitchAsync(
        Guid hubId,
        string resourceId,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(resourceId) || string.IsNullOrWhiteSpace(displayName) ||
            displayName.Length > 200)
        {
            return new DeviceOperationResult(DeviceOperationStatus.InvalidState);
        }
        if (GetMappedEventSources().Count >= HomeStateDocument.MaximumHueAutomationMappings)
        {
            return new DeviceOperationResult(
                DeviceOperationStatus.InvalidState,
                Detail: $"At most {HomeStateDocument.MaximumHueAutomationMappings} Hue event-source mappings are supported.");
        }

        var candidates = await GetSwitchImportCandidatesAsync(hubId, cancellationToken);
        var switchDevice = candidates.Switches.SingleOrDefault(candidate =>
            string.Equals(candidate.ResourceId, resourceId, StringComparison.Ordinal));
        if (candidates.Status != DeviceOperationStatus.Succeeded || switchDevice is null)
        {
            return new DeviceOperationResult(
                candidates.Status == DeviceOperationStatus.Succeeded
                    ? DeviceOperationStatus.ResourceNotFound
                    : candidates.Status,
                Detail: candidates.Detail);
        }

        await homeStateStore.UpdateAsync(state =>
        {
            if (GetMappedHueResourceIds(state.Document, hubId).Contains(resourceId))
            {
                throw new InvalidOperationException("This Hue switch is already imported.");
            }

            var names = new HashSet<string>(
                state.Document.Devices.Select(device => device.Name),
                StringComparer.OrdinalIgnoreCase);
            var hub = state.HubsById[hubId];
            return state.Document with
            {
                Devices = state.Document.Devices.Append(new Device(
                    Guid.NewGuid(),
                    CreateUniqueDeviceName(displayName, hub.Name, resourceId, names),
                    "hue.switch",
                    LocationId: null,
                    HubId: hubId,
                    ExternalResourceId: switchDevice.ResourceId,
                    Capabilities: DeviceCapabilities.None,
                    State: switchDevice.State)).ToArray(),
            };
        }, homeStateStore.Current.Revision, cancellationToken);
        return new DeviceOperationResult(DeviceOperationStatus.Succeeded, switchDevice.State);
    }

    public async Task<DeviceOperationResult> ImportMotionSensorAsync(
        Guid hubId,
        string resourceId,
        string displayName,
        Guid? locationId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(resourceId) || string.IsNullOrWhiteSpace(displayName) ||
            displayName.Length > 200 || locationId == Guid.Empty ||
            !HueMotionSensorResourceReference.TryParse(
                resourceId,
                out var motionResourceId,
                out var lightLevelResourceId))
        {
            return new DeviceOperationResult(DeviceOperationStatus.InvalidState);
        }

        if (GetMappedEventSources().Count >= HomeStateDocument.MaximumHueAutomationMappings)
        {
            return new DeviceOperationResult(
                DeviceOperationStatus.InvalidState,
                Detail: $"At most {HomeStateDocument.MaximumHueAutomationMappings} Hue event-source mappings are supported.");
        }

        var candidates = await GetMotionSensorImportCandidatesAsync(hubId, cancellationToken);
        var motionSensor = candidates.MotionSensors.SingleOrDefault(candidate =>
            string.Equals(candidate.MotionResourceId, motionResourceId, StringComparison.Ordinal) &&
            string.Equals(
                candidate.LightLevelResourceId,
                lightLevelResourceId,
                StringComparison.Ordinal));
        if (candidates.Status != DeviceOperationStatus.Succeeded || motionSensor is null)
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

            var mappedResourceIds = GetMappedHueResourceIds(state.Document, hubId);
            if (mappedResourceIds.Contains(motionSensor.MotionResourceId) ||
                (motionSensor.LightLevelResourceId is { } levelResourceId &&
                 mappedResourceIds.Contains(levelResourceId)))
            {
                throw new InvalidOperationException("This Hue motion sensor is already imported.");
            }

            var names = new HashSet<string>(
                state.Document.Devices.Select(device => device.Name),
                StringComparer.OrdinalIgnoreCase);
            var hub = state.HubsById[hubId];
            return state.Document with
            {
                Devices = state.Document.Devices.Append(new Device(
                    Guid.NewGuid(),
                    CreateUniqueDeviceName(
                        displayName,
                        hub.Name,
                        motionSensor.ResourceId,
                        names),
                    "hue.motion-sensor",
                    locationId,
                    HubId: hubId,
                    ExternalResourceId: motionSensor.ResourceId,
                    Capabilities: DeviceCapabilities.None,
                    State: motionSensor.State)).ToArray(),
            };
        }, homeStateStore.Current.Revision, cancellationToken);
        return new DeviceOperationResult(DeviceOperationStatus.Succeeded, motionSensor.State);
    }

    public async Task<HueMappedSwitchStateResult> ReadMappedSwitchStatesAsync(
        CancellationToken cancellationToken = default)
    {
        return await ReadMappedSwitchStatesAsync(
            GetEnabledMappedEventSources(),
            cancellationToken);
    }

    public async Task<HueMappedSwitchStateResult> ReadMappedSwitchStatesAsync(
        Guid hubId,
        CancellationToken cancellationToken = default)
    {
        if (GetHub(hubId) is null)
        {
            return new HueMappedSwitchStateResult(
                DeviceOperationStatus.ResourceNotFound,
                Array.Empty<HueMappedSwitchState>());
        }

        return await ReadMappedSwitchStatesAsync(
            GetEnabledMappedEventSources(hubId),
            cancellationToken);
    }

    public async IAsyncEnumerable<HueMappedSwitchState> ReadMappedSwitchEventsAsync(
        Guid hubId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var hub = GetHub(hubId);
        if (hub is null || !hub.Enabled)
        {
            yield break;
        }

        var applicationKey = await secretStore.GetAsync(hub.SecretReference, cancellationToken);
        await foreach (var update in hueBridgeClient.ReadEventStreamResourcesAsync(
            new Uri(hub.Address),
            applicationKey,
            hub.BridgeId,
            hub.TlsCertificateThumbprint,
            cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(update.EventId))
            {
                continue;
            }

            var mappedDevices = GetEnabledMappedEventSources(hubId)
                .SelectMany(device => GetHueEventResourceIds(device)
                    .Select(resourceId => new KeyValuePair<string, Device>(resourceId, device)))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            if (!mappedDevices.TryGetValue(update.ResourceId, out var device))
            {
                continue;
            }

            var observation = new HueMappedSwitchState(
                device.Id,
                MergeEventState(device, update),
                update.EventId);
            yield return observation;
        }
    }

    private async Task<HueMappedSwitchStateResult> ReadMappedSwitchStatesAsync(
        IReadOnlyList<Device> mappedDevices,
        CancellationToken cancellationToken)
    {
        if (mappedDevices.Count == 0)
        {
            return new HueMappedSwitchStateResult(
                DeviceOperationStatus.Succeeded,
                Array.Empty<HueMappedSwitchState>());
        }

        var observations = new List<HueMappedSwitchState>();
        foreach (var group in mappedDevices.GroupBy(device => device.HubId!.Value))
        {
            var mappedGroup = group.ToArray();
            var switchResult = mappedGroup.Any(device => device.DeviceType == "hue.switch")
                ? await GetSwitchImportCandidatesAsync(group.Key, cancellationToken)
                : new HueSwitchQueryResult(
                    DeviceOperationStatus.Succeeded,
                    Array.Empty<HueSwitchResource>());
            if (!switchResult.Succeeded)
            {
                return new HueMappedSwitchStateResult(
                    switchResult.Status,
                    observations,
                    switchResult.Detail);
            }

            var motionResult = mappedGroup.Any(device => device.DeviceType == "hue.motion-sensor")
                ? await GetMotionSensorImportCandidatesAsync(group.Key, cancellationToken)
                : new HueMotionSensorQueryResult(
                    DeviceOperationStatus.Succeeded,
                    Array.Empty<HueMotionSensorResource>());
            if (!motionResult.Succeeded)
            {
                return new HueMappedSwitchStateResult(
                    motionResult.Status,
                    observations,
                    motionResult.Detail);
            }

            var switchesByResourceId = switchResult.Switches.ToDictionary(
                switchDevice => switchDevice.ResourceId,
                StringComparer.Ordinal);
            var sensorsByMotionResourceId = motionResult.MotionSensors.ToDictionary(
                sensor => sensor.MotionResourceId,
                StringComparer.Ordinal);
            foreach (var device in mappedGroup)
            {
                if (device.DeviceType == "hue.switch" &&
                    device.ExternalResourceId is { } resourceId &&
                    switchesByResourceId.TryGetValue(resourceId, out var switchDevice))
                {
                    observations.Add(new HueMappedSwitchState(
                        device.Id,
                        switchDevice.State,
                        switchDevice.EventId));
                }
                else if (device.DeviceType == "hue.motion-sensor" &&
                         device.ExternalResourceId is { } sensorReference &&
                         HueMotionSensorResourceReference.TryParse(
                             sensorReference,
                             out var motionResourceId,
                             out _) &&
                         sensorsByMotionResourceId.TryGetValue(
                             motionResourceId,
                             out var motionSensor))
                {
                    observations.Add(new HueMappedSwitchState(
                        device.Id,
                        MergeMotionSensorState(device.State, motionSensor.State),
                        motionSensor.EventId));
                }
            }
        }

        var cacheDetail = await PersistMappedSwitchStatesAsync(observations, cancellationToken);
        return new HueMappedSwitchStateResult(
            DeviceOperationStatus.Succeeded,
            observations,
            cacheDetail);
    }

    private async Task<string?> PersistMappedSwitchStatesAsync(
        IReadOnlyList<HueMappedSwitchState> observations,
        CancellationToken cancellationToken)
    {
        var current = homeStateStore.Current;
        if (!observations.Any(observation =>
                current.DevicesById.TryGetValue(observation.DeviceId, out var device) &&
                !StateEquals(device.State, observation.State)))
        {
            return null;
        }

        var observedStates = observations.ToDictionary(
            observation => observation.DeviceId,
            observation => observation.State);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                await homeStateStore.UpdateAsync(state => state.Document with
                {
                    Devices = state.Document.Devices.Select(device =>
                        observedStates.TryGetValue(device.Id, out var observedState)
                            ? device with { State = observedState }
                            : device).ToArray(),
                }, homeStateStore.Current.Revision, cancellationToken);
                return null;
            }
            catch (InvalidOperationException) when (attempt == 0)
            {
                // Retry only the non-authoritative cache update against the
                // latest configuration; the observed press remains deliverable.
            }
            catch (InvalidOperationException)
            {
                return "The switch-state cache will be refreshed after the next configuration update.";
            }
        }

        return null;
    }

    public Task<string?> PersistMappedSwitchStateAsync(
        HueMappedSwitchState observation,
        CancellationToken cancellationToken = default) =>
        PersistMappedSwitchStatesAsync([observation], cancellationToken);

    private IReadOnlyList<Device> GetMappedEventSources() =>
        homeStateStore.Current.Document.Devices
            .Where(device => device.DeviceType is "hue.switch" or "hue.motion-sensor")
            .OrderBy(device => device.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private IReadOnlyList<Device> GetEnabledMappedEventSources(Guid? hubId = null) =>
        GetMappedEventSources()
            .Where(device => device.Enabled &&
                device.HubId is { } mappedHubId &&
                (!hubId.HasValue || mappedHubId == hubId.Value) &&
                homeStateStore.Current.HubsById.TryGetValue(mappedHubId, out var hub) &&
                hub.Enabled)
            .ToArray();

    private static IReadOnlyList<string> GetHueEventResourceIds(Device device)
    {
        if (device.DeviceType != "hue.motion-sensor")
        {
            return string.IsNullOrWhiteSpace(device.ExternalResourceId)
                ? Array.Empty<string>()
                : [device.ExternalResourceId];
        }

        return HueMotionSensorResourceReference.TryParse(
            device.ExternalResourceId,
            out var motionResourceId,
            out var lightLevelResourceId)
            ? lightLevelResourceId is null
                ? [motionResourceId]
                : [motionResourceId, lightLevelResourceId]
            : Array.Empty<string>();
    }

    private HashSet<string> GetMappedHueResourceIds(Guid hubId) =>
        GetMappedHueResourceIds(homeStateStore.Current.Document, hubId);

    private static HashSet<string> GetMappedHueResourceIds(
        HomeStateDocument document,
        Guid hubId) =>
        document.Devices
            .Where(device => device.HubId == hubId &&
                device.DeviceType is "hue.light" or "hue.switch" or "hue.motion-sensor")
            .SelectMany(GetHueEventResourceIds)
            .ToHashSet(StringComparer.Ordinal);

    private static DeviceState MergeEventState(Device device, HueEventResource update) =>
        device.DeviceType == "hue.motion-sensor"
            ? MergeMotionSensorState(device.State, update.State)
            : MergeSwitchState(device.State, update.State);

    private static DeviceState MergeSwitchState(DeviceState? previous, DeviceState update)
    {
        var attributes = previous?.Attributes is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(previous.Attributes, StringComparer.Ordinal);
        if (update.Attributes is not null)
        {
            foreach (var attribute in update.Attributes)
            {
                attributes[attribute.Key] = attribute.Value;
            }
        }

        return update with { Attributes = attributes };
    }

    private static DeviceState MergeMotionSensorState(
        DeviceState? previous,
        DeviceState update)
    {
        var attributes = previous?.Attributes is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(previous.Attributes, StringComparer.Ordinal);
        if (update.Attributes is not null)
        {
            foreach (var attribute in update.Attributes)
            {
                attributes[attribute.Key] = attribute.Value;
            }

            if (update.Attributes.ContainsKey("motionValid") &&
                !update.Attributes.ContainsKey("motion"))
            {
                attributes.Remove("motion");
            }

            if (update.Attributes.ContainsKey("lightLevelValid") &&
                !update.Attributes.ContainsKey("lightLevel"))
            {
                attributes.Remove("lightLevel");
            }

            if (update.Attributes.TryGetValue("lightLevelAvailable", out var available) &&
                available == "false")
            {
                attributes.Remove("lightLevel");
                attributes.Remove("lightLevelValid");
            }
        }

        return new DeviceState(null, null, Attributes: attributes);
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
                device.DeviceType is not ("hue.light" or "hue.switch" or "hue.motion-sensor"))
            {
                throw new InvalidOperationException("The Hue device does not exist.");
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
                device.DeviceType is not ("hue.light" or "hue.switch" or "hue.motion-sensor"))
            {
                throw new InvalidOperationException("The Hue device does not exist.");
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

    private static bool StateEquals(DeviceState? left, DeviceState? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return left.IsOn == right.IsOn &&
            left.Brightness == right.Brightness &&
            left.Colour == right.Colour &&
            ((left.Attributes is null && right.Attributes is null) ||
             (left.Attributes is not null && right.Attributes is not null &&
              left.Attributes.Count == right.Attributes.Count &&
              left.Attributes.All(attribute =>
                  right.Attributes.TryGetValue(attribute.Key, out var value) &&
                  value == attribute.Value)));
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
