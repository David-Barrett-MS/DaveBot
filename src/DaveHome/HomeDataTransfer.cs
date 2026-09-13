namespace DaveHome;

public sealed record HomeDataExportDocument(
    int SchemaVersion,
    IReadOnlyList<Location> Locations,
    IReadOnlyList<Device> Devices)
{
    public const int CurrentSchemaVersion = 1;

    // Optional to keep existing version-1 import documents compatible.
    public IReadOnlyList<CommandAlias> CommandAliases { get; init; } = Array.Empty<CommandAlias>();

    // Optional to keep existing version-1 import documents compatible.
    public IReadOnlyList<AutomationAction> AutomationActions { get; init; } =
        Array.Empty<AutomationAction>();

    public IReadOnlyList<AutomationActionSet> AutomationActionSets { get; init; } =
        Array.Empty<AutomationActionSet>();

    public IReadOnlyList<AutomationTrigger> AutomationTriggers { get; init; } =
        Array.Empty<AutomationTrigger>();
}

public sealed record HomeDataImportResult(int ImportedLocations, int ImportedDevices);

public sealed class HomeDataTransferService(IHomeStateStore homeStateStore)
{
    public HomeDataExportDocument Export()
    {
        var state = homeStateStore.Current.Document;
        return new HomeDataExportDocument(
            HomeDataExportDocument.CurrentSchemaVersion,
            state.Locations.ToArray(),
            state.Devices.ToArray())
        {
            CommandAliases = state.CommandAliases.ToArray(),
            AutomationActions = state.AutomationActions.ToArray(),
            AutomationActionSets = state.AutomationActionSets.ToArray(),
            AutomationTriggers = state.AutomationTriggers.ToArray(),
        };
    }

    public async Task<HomeDataImportResult> ImportAsync(
        HomeDataExportDocument document,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.SchemaVersion != HomeDataExportDocument.CurrentSchemaVersion ||
            document.Locations is null || document.Devices is null || document.CommandAliases is null ||
            document.AutomationActions is null || document.AutomationActionSets is null ||
            document.AutomationTriggers is null)
        {
            throw new InvalidOperationException("The home data import format is not supported.");
        }

        if (document.Locations.GroupBy(location => location.Id).Any(group => group.Count() > 1) ||
            document.Devices.GroupBy(device => device.Id).Any(group => group.Count() > 1) ||
            document.CommandAliases.GroupBy(alias => alias.Id).Any(group => group.Count() > 1) ||
            document.AutomationActions.GroupBy(action => action.Id).Any(group => group.Count() > 1) ||
            document.AutomationActionSets.GroupBy(actionSet => actionSet.Id)
                .Any(group => group.Count() > 1) ||
            document.AutomationTriggers.GroupBy(trigger => trigger.Id).Any(group => group.Count() > 1))
        {
            throw new InvalidOperationException("The import file contains duplicate object IDs.");
        }

        await homeStateStore.UpdateAsync(state => state.Document with
        {
            Locations = MergeById(state.Document.Locations, document.Locations, location => location.Id),
            Devices = MergeById(state.Document.Devices, document.Devices, device => device.Id),
            CommandAliases = MergeById(
                state.Document.CommandAliases,
                document.CommandAliases,
                alias => alias.Id),
            AutomationActions = MergeById(
                state.Document.AutomationActions,
                document.AutomationActions,
                action => action.Id),
            AutomationActionSets = MergeById(
                state.Document.AutomationActionSets,
                document.AutomationActionSets,
                actionSet => actionSet.Id),
            AutomationTriggers = MergeById(
                state.Document.AutomationTriggers,
                document.AutomationTriggers,
                trigger => trigger.Id),
        }, homeStateStore.Current.Revision, cancellationToken);

        return new HomeDataImportResult(document.Locations.Count, document.Devices.Count);
    }

    private static IReadOnlyList<T> MergeById<T>(
        IReadOnlyList<T> existing,
        IReadOnlyList<T> imported,
        Func<T, Guid> getId)
    {
        var positions = new Dictionary<Guid, int>();
        var merged = existing.ToList();
        for (var index = 0; index < merged.Count; index++)
        {
            positions.Add(getId(merged[index]), index);
        }

        foreach (var item in imported)
        {
            var id = getId(item);
            if (positions.TryGetValue(id, out var index))
            {
                merged[index] = item;
            }
            else
            {
                positions.Add(id, merged.Count);
                merged.Add(item);
            }
        }

        return merged;
    }
}
