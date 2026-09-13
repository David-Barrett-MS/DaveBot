namespace DaveHome;

public sealed record StoreRevision(long Value)
{
    public static readonly StoreRevision Initial = new(0);
}

public sealed record Location(Guid Id, string Name, Guid? ParentLocationId);

[Flags]
public enum DeviceCapabilities
{
    None = 0,
    OnOff = 1,
    Brightness = 2,
    Colour = 4,
}

public sealed record DeviceColour(double X, double Y);

public sealed record DeviceState(bool? IsOn, int? Brightness, DeviceColour? Colour = null);

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
    DeviceState? State = null);

public sealed record CommandAlias(Guid Id, string Alias, Guid LocationId);

public sealed record HomeStateDocument(
    int SchemaVersion,
    StoreRevision Revision,
    IReadOnlyList<Location> Locations,
    IReadOnlyList<Device> Devices)
{
    public const int CurrentSchemaVersion = 1;

    public IReadOnlyList<Hub> Hubs { get; init; } = Array.Empty<Hub>();

    public IReadOnlyList<CommandAlias> CommandAliases { get; init; } = Array.Empty<CommandAlias>();

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
        DevicesById = document.Devices.ToDictionary(device => device.Id);
        HubsById = document.Hubs.ToDictionary(hub => hub.Id);
        CommandAliasesById = document.CommandAliases.ToDictionary(alias => alias.Id);
    }

    public HomeStateDocument Document { get; }

    public StoreRevision Revision => Document.Revision;

    public IReadOnlyDictionary<Guid, Location> LocationsById { get; }

    public IReadOnlyDictionary<Guid, Device> DevicesById { get; }

    public IReadOnlyDictionary<Guid, Hub> HubsById { get; }

    public IReadOnlyDictionary<Guid, CommandAlias> CommandAliasesById { get; }

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
                (device.LocationId is { } locationId && !locations.ContainsKey(locationId)))
            {
                throw new InvalidOperationException(
                    "Devices must have unique IDs, names, types, and existing locations.");
            }

            if (device.DeviceType == "hue.light" &&
                (device.HubId is not { } hubId || !hubIds.Contains(hubId) ||
                 string.IsNullOrWhiteSpace(device.ExternalResourceId) ||
                 !externalResourceIds.Add((hubId, device.ExternalResourceId))))
            {
                throw new InvalidOperationException(
                    "Hue lights must map uniquely to an existing hub resource.");
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
        }
    }
}
