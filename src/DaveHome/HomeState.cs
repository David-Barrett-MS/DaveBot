namespace DaveHome;

public sealed record StoreRevision(long Value)
{
    public static readonly StoreRevision Initial = new(0);
}

public sealed record Location(Guid Id, string Name, Guid? ParentLocationId);

public sealed record DeviceGroup(Guid Id, string Name);

[Flags]
public enum DeviceCapabilities
{
    None = 0,
    OnOff = 1,
    Brightness = 2,
    Colour = 4,
    FanSpeed = 8,
}

public sealed record DeviceColour(double X, double Y);

public sealed record DeviceState(
    bool? IsOn,
    int? Brightness,
    DeviceColour? Colour = null,
    IReadOnlyDictionary<string, string>? Attributes = null);

public sealed record Hub(
    Guid Id,
    string Name,
    string ProviderType,
    string Address,
    string SecretReference,
    bool Enabled,
    DateTimeOffset? LastSynchronizedUtc = null,
    string? BridgeId = null,
    string? TlsCertificateThumbprint = null);

public sealed record Device(
    Guid Id,
    string Name,
    string DeviceType,
    Guid? LocationId,
    bool Enabled = true,
    Guid? HubId = null,
    string? ExternalResourceId = null,
    DeviceCapabilities Capabilities = DeviceCapabilities.None,
    DeviceState? State = null,
    IReadOnlyList<Guid>? GroupIds = null);

public static class HueMotionSensorResourceReference
{
    private const string MotionPrefix = "motion:";
    private const string LightLevelPrefix = ";lightLevel:";

    public static string Create(string motionResourceId, string? lightLevelResourceId)
    {
        if (!IsResourceId(motionResourceId) ||
            (lightLevelResourceId is not null && !IsResourceId(lightLevelResourceId)))
        {
            throw new ArgumentException("Hue motion sensor resource IDs are invalid.");
        }

        return lightLevelResourceId is null
            ? $"{MotionPrefix}{motionResourceId}"
            : $"{MotionPrefix}{motionResourceId}{LightLevelPrefix}{lightLevelResourceId}";
    }

    public static bool TryParse(
        string? value,
        out string motionResourceId,
        out string? lightLevelResourceId)
    {
        motionResourceId = string.Empty;
        lightLevelResourceId = null;
        if (string.IsNullOrWhiteSpace(value) ||
            !value.StartsWith(MotionPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var encodedResources = value[MotionPrefix.Length..];
        var separator = encodedResources.IndexOf(LightLevelPrefix, StringComparison.Ordinal);
        if (separator < 0)
        {
            if (!IsResourceId(encodedResources))
            {
                return false;
            }

            motionResourceId = encodedResources;
            return true;
        }

        var motionId = encodedResources[..separator];
        var lightLevelId = encodedResources[(separator + LightLevelPrefix.Length)..];
        if (!IsResourceId(motionId) || !IsResourceId(lightLevelId) ||
            lightLevelId.Contains(LightLevelPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        motionResourceId = motionId;
        lightLevelResourceId = lightLevelId;
        return true;
    }

    private static bool IsResourceId(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 200 &&
        value == value.Trim() &&
        !value.Contains(';', StringComparison.Ordinal) &&
        !value.Contains(':', StringComparison.Ordinal);
}

public sealed record CommandAlias(Guid Id, string Alias, Guid LocationId);

public sealed record HomeStateDocument(
    int SchemaVersion,
    StoreRevision Revision,
    IReadOnlyList<Location> Locations,
    IReadOnlyList<Device> Devices)
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumHueAutomationMappings = 500;
    public const int MaximumHueSwitchMappings = MaximumHueAutomationMappings;

    public IReadOnlyList<Hub> Hubs { get; init; } = Array.Empty<Hub>();

    public IReadOnlyList<CommandAlias> CommandAliases { get; init; } = Array.Empty<CommandAlias>();

    // Optional to keep existing state.json documents compatible.
    public IReadOnlyList<DeviceGroup> Groups { get; init; } = Array.Empty<DeviceGroup>();

    // Optional properties keep existing state.json documents compatible.
    public IReadOnlyList<AutomationAction> AutomationActions { get; init; } =
        Array.Empty<AutomationAction>();

    public IReadOnlyList<AutomationActionSet> AutomationActionSets { get; init; } =
        Array.Empty<AutomationActionSet>();

    public IReadOnlyList<AutomationTrigger> AutomationTriggers { get; init; } =
        Array.Empty<AutomationTrigger>();

    public static HomeStateDocument Empty { get; } = new(
        CurrentSchemaVersion,
        StoreRevision.Initial,
        Array.Empty<Location>(),
        Array.Empty<Device>());
}

public sealed class HomeState
{
    private HomeState(HomeStateDocument document)
    {
        Document = document;
        LocationsById = document.Locations.ToDictionary(location => location.Id);
        GroupsById = document.Groups.ToDictionary(group => group.Id);
        DevicesById = document.Devices.ToDictionary(device => device.Id);
        HubsById = document.Hubs.ToDictionary(hub => hub.Id);
        CommandAliasesById = document.CommandAliases.ToDictionary(alias => alias.Id);
        AutomationActionsById = document.AutomationActions.ToDictionary(action => action.Id);
        AutomationActionSetsById = document.AutomationActionSets.ToDictionary(
            actionSet => actionSet.Id);
        AutomationTriggersById = document.AutomationTriggers.ToDictionary(
            trigger => trigger.Id);
    }

    public HomeStateDocument Document { get; }

    public StoreRevision Revision => Document.Revision;

    public IReadOnlyDictionary<Guid, Location> LocationsById { get; }

    public IReadOnlyDictionary<Guid, DeviceGroup> GroupsById { get; }

    public IReadOnlyDictionary<Guid, Device> DevicesById { get; }

    public IReadOnlyDictionary<Guid, Hub> HubsById { get; }

    public IReadOnlyDictionary<Guid, CommandAlias> CommandAliasesById { get; }

    public IReadOnlyDictionary<Guid, AutomationAction> AutomationActionsById { get; }

    public IReadOnlyDictionary<Guid, AutomationActionSet> AutomationActionSetsById { get; }

    public IReadOnlyDictionary<Guid, AutomationTrigger> AutomationTriggersById { get; }

    public static HomeState FromDocument(HomeStateDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        Validate(document);
        return new HomeState(document);
    }

    public static void Validate(HomeStateDocument document)
    {
        if (document.SchemaVersion != HomeStateDocument.CurrentSchemaVersion)
        {
            throw new InvalidOperationException(
                $"Unsupported home state schema version {document.SchemaVersion}.");
        }

        if (document.Revision.Value < 0)
        {
            throw new InvalidOperationException("Store revision cannot be negative.");
        }

        var locationIds = new HashSet<Guid>();
        var locationNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var location in document.Locations)
        {
            if (location.Id == Guid.Empty || string.IsNullOrWhiteSpace(location.Name) ||
                !locationIds.Add(location.Id) || !locationNames.Add(location.Name.Trim()))
            {
                throw new InvalidOperationException("Locations must have unique IDs and names.");
            }
        }

        var locations = document.Locations.ToDictionary(location => location.Id);
        if (document.Groups is null || document.Groups.Count > 500)
        {
            throw new InvalidOperationException("Device groups exceed the supported limit.");
        }

        var groupIds = new HashSet<Guid>();
        var groupNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in document.Groups)
        {
            var normalizedName = TextCommandNormalizer.Normalize(group.Name);
            if (group.Id == Guid.Empty || string.IsNullOrWhiteSpace(normalizedName) ||
                normalizedName.Length > 200 || !groupIds.Add(group.Id) || !groupNames.Add(normalizedName))
            {
                throw new InvalidOperationException("Device groups must have unique IDs and names.");
            }
        }

        var aliasIds = new HashSet<Guid>();
        var aliases = new HashSet<string>(StringComparer.Ordinal);
        foreach (var alias in document.CommandAliases)
        {
            var normalizedAlias = TextCommandNormalizer.Normalize(alias.Alias);
            if (alias.Id == Guid.Empty || string.IsNullOrWhiteSpace(normalizedAlias) ||
                normalizedAlias.Length > 200 || !aliasIds.Add(alias.Id) ||
                !aliases.Add(normalizedAlias) || !locations.ContainsKey(alias.LocationId))
            {
                throw new InvalidOperationException(
                    "Command aliases must have unique names and existing locations.");
            }
        }

        foreach (var location in document.Locations)
        {
            if (location.ParentLocationId is { } parentId && !locations.ContainsKey(parentId))
            {
                throw new InvalidOperationException("A location parent must exist.");
            }

            var visited = new HashSet<Guid> { location.Id };
            var cursor = location;
            while (cursor.ParentLocationId is { } ancestorId)
            {
                if (!visited.Add(ancestorId))
                {
                    throw new InvalidOperationException("Location containment cannot contain a cycle.");
                }

                cursor = locations[ancestorId];
            }
        }

        var hubIds = new HashSet<Guid>();
        foreach (var hub in document.Hubs)
        {
            if (hub.Id == Guid.Empty || string.IsNullOrWhiteSpace(hub.Name) ||
                string.IsNullOrWhiteSpace(hub.ProviderType) ||
                string.IsNullOrWhiteSpace(hub.SecretReference) ||
                !hubIds.Add(hub.Id) ||
                !Uri.TryCreate(hub.Address, UriKind.Absolute, out var address) ||
                address.Scheme != Uri.UriSchemeHttps)
            {
                throw new InvalidOperationException(
                    "Hubs must have unique IDs, HTTPS addresses, and opaque secret references.");
            }
        }

        var deviceIds = new HashSet<Guid>();
        var externalResourceIds = new HashSet<(Guid HubId, string ResourceId)>();
        foreach (var device in document.Devices)
        {
            if (device.Id == Guid.Empty || string.IsNullOrWhiteSpace(device.Name) ||
                string.IsNullOrWhiteSpace(device.DeviceType) || !deviceIds.Add(device.Id) ||
                (device.LocationId is { } locationId && !locations.ContainsKey(locationId)) ||
                (device.GroupIds is { Count: > 100 } ||
                 device.GroupIds is { } deviceGroupIds &&
                 (deviceGroupIds.Distinct().Count() != deviceGroupIds.Count ||
                  deviceGroupIds.Any(groupId => !groupIds.Contains(groupId)))))
            {
                throw new InvalidOperationException(
                    "Devices must have unique IDs, names, types, locations, and groups.");
            }

            if (device.DeviceType is "hue.light" or "hue.switch" or "hue.motion-sensor" &&
                (device.HubId is not { } hubId || !hubIds.Contains(hubId) ||
                 !TryGetHueResourceIds(device, out var resourceIds) ||
                 resourceIds.Any(resourceId => !externalResourceIds.Add((hubId, resourceId)))))
            {
                throw new InvalidOperationException(
                    "Hue devices must map uniquely to an existing hub resource.");
            }

            if (device.DeviceType == "philips.airplus" &&
                (device.HubId is not { } airPlusHubId ||
                 !HubsByProvider(document, airPlusHubId, "philips.airplus") ||
                 device.ExternalResourceId is not { } airPlusDeviceId ||
                 !IsExternalResourceId(airPlusDeviceId) ||
                 !externalResourceIds.Add((airPlusHubId, airPlusDeviceId))))
            {
                throw new InvalidOperationException(
                    "Philips Air+ devices must map uniquely to a Philips Air+ hub API device ID.");
            }

            if (device.DeviceType == "blink.camera" &&
                (device.HubId is not { } blinkHubId ||
                 !HubsByProvider(document, blinkHubId, "blink") ||
                 device.ExternalResourceId is not { } blinkCameraId ||
                 !IsExternalResourceId(blinkCameraId) ||
                 !externalResourceIds.Add((blinkHubId, blinkCameraId))))
            {
                throw new InvalidOperationException(
                    "Blink cameras must map uniquely to an existing Blink account camera.");
            }

            if (device.State?.Brightness is < 0 or > 100)
            {
                throw new InvalidOperationException("Device brightness must be between 0 and 100.");
            }

            if (device.State?.Colour is { } colour &&
                (colour.X is < 0 or > 1 || colour.Y is < 0 or > 1))
            {
                throw new InvalidOperationException("Device colour coordinates must be between zero and one.");
            }

            if (device.State?.Attributes is { } attributes &&
                (attributes.Count > 20 ||
                 attributes.Any(attribute =>
                     string.IsNullOrWhiteSpace(attribute.Key) ||
                     attribute.Key.Length > 50 ||
                     attribute.Value is null ||
                     attribute.Value.Length > 200)))
            {
                throw new InvalidOperationException("Device state attributes are invalid.");
            }

            if (device.DeviceType == "hue.motion-sensor" &&
                !IsValidHueMotionSensorState(device.State))
            {
                throw new InvalidOperationException("Hue motion sensor state attributes are invalid.");
            }
        }

        if (document.Devices.Count(device =>
                device.DeviceType is "hue.switch" or "hue.motion-sensor") >
            HomeStateDocument.MaximumHueAutomationMappings)
        {
            throw new InvalidOperationException(
                $"At most {HomeStateDocument.MaximumHueAutomationMappings} Hue event-source mappings are supported.");
        }

        ValidateAutomation(document);
    }

    private static void ValidateAutomation(HomeStateDocument document)
    {
        const int maximumItems = 500;
        if (document.AutomationActions is null || document.AutomationActionSets is null ||
            document.AutomationTriggers is null ||
            document.AutomationActions.Count > maximumItems ||
            document.AutomationActionSets.Count > maximumItems ||
            document.AutomationTriggers.Count > maximumItems)
        {
            throw new InvalidOperationException("Automation configuration exceeds its supported limit.");
        }

        var devices = document.Devices.ToDictionary(device => device.Id);
        ValidateAutomationNames(
            document.AutomationActions,
            action => action.Id,
            action => action.Name,
            "Automation actions");
        var actions = document.AutomationActions.ToDictionary(action => action.Id);
        foreach (var action in document.AutomationActions)
        {
            if (action.ActionType != AutomationActionTypes.DeviceSetState ||
                action.TargetDeviceId == Guid.Empty ||
                !devices.TryGetValue(action.TargetDeviceId, out var target) ||
                action.StateChange is null ||
                !IsValidStateChange(action.StateChange, target.Capabilities))
            {
                throw new InvalidOperationException(
                    "Automation actions must be valid allow-listed device state changes.");
            }
        }

        ValidateAutomationNames(
            document.AutomationActionSets,
            actionSet => actionSet.Id,
            actionSet => actionSet.Name,
            "Automation action sets");
        var actionSets = document.AutomationActionSets.ToDictionary(actionSet => actionSet.Id);
        foreach (var actionSet in document.AutomationActionSets)
        {
            if (actionSet.ActionIds is null || actionSet.ActionIds.Count is < 1 or > 100 ||
                actionSet.ActionIds.Distinct().Count() != actionSet.ActionIds.Count ||
                actionSet.ActionIds.Any(actionId => !actions.ContainsKey(actionId)))
            {
                throw new InvalidOperationException(
                    "Automation action sets must contain unique existing actions.");
            }
        }

        ValidateAutomationNames(
            document.AutomationTriggers,
            trigger => trigger.Id,
            trigger => trigger.Name,
            "Automation triggers");
        foreach (var trigger in document.AutomationTriggers)
        {
            if (trigger.SourceDeviceId == Guid.Empty ||
                !devices.TryGetValue(trigger.SourceDeviceId, out var source) ||
                source.DeviceType is not ("hue.switch" or "hue.motion-sensor") ||
                trigger.ActionSetIds is null ||
                trigger.ActionSetIds.Count is < 1 or > 100 ||
                trigger.ActionSetIds.Distinct().Count() != trigger.ActionSetIds.Count ||
                trigger.ActionSetIds.Any(actionSetId => !actionSets.ContainsKey(actionSetId)) ||
                trigger.CooldownSeconds is < 0 or > 86_400 ||
                !AutomationStateCriteriaParser.Parse(trigger.StateCriteriaJson).Succeeded)
            {
                throw new InvalidOperationException(
                    "Automation triggers must use Hue event sources, valid criteria, and existing action sets.");
            }
        }
    }

    private static void ValidateAutomationNames<T>(
        IReadOnlyList<T> entries,
        Func<T, Guid> getId,
        Func<T, string> getName,
        string type)
    {
        var ids = new HashSet<Guid>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (entries.Any(entry => getId(entry) == Guid.Empty ||
            string.IsNullOrWhiteSpace(getName(entry)) ||
            getName(entry).Trim().Length > 200 ||
            !ids.Add(getId(entry)) ||
            !names.Add(getName(entry).Trim())))
        {
            throw new InvalidOperationException($"{type} must have unique IDs and names.");
        }
    }

    private static bool IsValidStateChange(
        DeviceStateChange change,
        DeviceCapabilities capabilities) =>
        change is not { IsOn: null, Brightness: null, Colour: null, FanSpeed: null } &&
        (change.IsOn is null || capabilities.HasFlag(DeviceCapabilities.OnOff)) &&
        (change.Brightness is null ||
         (change.Brightness is >= 0 and <= 100 &&
          capabilities.HasFlag(DeviceCapabilities.Brightness))) &&
        (change.Colour is null ||
         (change.Colour.X is >= 0 and <= 1 &&
          change.Colour.Y is >= 0 and <= 1 &&
          capabilities.HasFlag(DeviceCapabilities.Colour)) &&
        (change.FanSpeed is null ||
         (change.FanSpeed is >= 1 and <= 10 or 81 or 82) &&
         capabilities.HasFlag(DeviceCapabilities.FanSpeed)));

    private static bool HubsByProvider(HomeStateDocument document, Guid hubId, string providerType) =>
        document.Hubs.Any(hub => hub.Id == hubId &&
            string.Equals(hub.ProviderType, providerType, StringComparison.Ordinal));

    private static bool IsExternalResourceId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 200 &&
        value == value.Trim();

    private static bool TryGetHueResourceIds(Device device, out IReadOnlyList<string> resourceIds)
    {
        resourceIds = Array.Empty<string>();
        if (string.IsNullOrWhiteSpace(device.ExternalResourceId))
        {
            return false;
        }

        if (device.DeviceType != "hue.motion-sensor")
        {
            resourceIds = [device.ExternalResourceId];
            return true;
        }

        if (!HueMotionSensorResourceReference.TryParse(
                device.ExternalResourceId,
                out var motionResourceId,
                out var lightLevelResourceId))
        {
            return false;
        }

        resourceIds = lightLevelResourceId is null
            ? [motionResourceId]
            : [motionResourceId, lightLevelResourceId];
        return true;
    }

    private static bool IsValidHueMotionSensorState(DeviceState? state)
    {
        if (state is null || state.IsOn is not null || state.Brightness is not null ||
            state.Colour is not null || state.Attributes is null)
        {
            return state is null;
        }

        foreach (var (key, value) in state.Attributes)
        {
            if (key is "motion" or "motionValid" or "lightLevelAvailable" or "lightLevelValid")
            {
                if (value is not ("true" or "false"))
                {
                    return false;
                }

                continue;
            }

            if (key == "lightLevel" &&
                (!int.TryParse(
                    value,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var lightLevel) ||
                 lightLevel is < 0 or > 65_535))
            {
                return false;
            }

            if (key != "lightLevel")
            {
                return false;
            }
        }

        return true;
    }
}
