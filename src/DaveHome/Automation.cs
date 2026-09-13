using System.Text.Json;

namespace DaveHome;

public static class AutomationActionTypes
{
    public const string DeviceSetState = "device.setState";
}

public sealed record AutomationAction(
    Guid Id,
    string Name,
    string ActionType,
    Guid TargetDeviceId,
    DeviceStateChange StateChange,
    bool Enabled = true);

public sealed record AutomationActionSet(
    Guid Id,
    string Name,
    IReadOnlyList<Guid> ActionIds,
    bool Enabled = true);

public sealed record AutomationTrigger(
    Guid Id,
    string Name,
    Guid SourceDeviceId,
    string StateCriteriaJson,
    IReadOnlyList<Guid> ActionSetIds,
    int CooldownSeconds = 0,
    bool Enabled = true);

public sealed record DeviceStateChanged(
    Guid DeviceId,
    DeviceState State,
    string EventId,
    DateTimeOffset OccurredUtc,
    bool IsReconciliation = false);

public sealed record DeviceStateSourceResult(
    DeviceOperationStatus Status,
    IReadOnlyList<DeviceStateChanged> Changes,
    string? Detail = null)
{
    public bool Succeeded => Status == DeviceOperationStatus.Succeeded;
}

public interface IDeviceStateSource
{
    Task<DeviceStateSourceResult> ReconcileAsync(CancellationToken cancellationToken = default);

    Task<DeviceStateSourceResult> ReadStateChangesAsync(
        CancellationToken cancellationToken = default);
}

public sealed record AutomationStateCriterion(string Field, JsonElement Value);

public sealed record AutomationStateCriteria(IReadOnlyList<AutomationStateCriterion> All)
{
    public bool IsMatch(DeviceState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return All.All(criterion => Matches(criterion, state));
    }

    private static bool Matches(AutomationStateCriterion criterion, DeviceState state)
    {
        return criterion.Field switch
        {
            "isOn" => criterion.Value.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                state.IsOn == criterion.Value.GetBoolean(),
            "brightness" => criterion.Value.ValueKind == JsonValueKind.Number &&
                criterion.Value.TryGetInt32(out var brightness) &&
                state.Brightness == brightness,
            "fanSpeed" => criterion.Value.ValueKind == JsonValueKind.Number &&
                criterion.Value.TryGetInt32(out var fanSpeed) &&
                state.Attributes is not null &&
                state.Attributes.TryGetValue("fanSpeed", out var actualFanSpeed) &&
                int.TryParse(
                    actualFanSpeed,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var actualFanSpeedValue) &&
                actualFanSpeedValue == fanSpeed,
            "colour.x" => criterion.Value.ValueKind == JsonValueKind.Number &&
                criterion.Value.TryGetDouble(out var x) &&
                state.Colour?.X == x,
            "colour.y" => criterion.Value.ValueKind == JsonValueKind.Number &&
                criterion.Value.TryGetDouble(out var y) &&
                state.Colour?.Y == y,
            "event" => MatchesAttribute(state, "event", criterion.Value),
            "button" => MatchesAttribute(state, "button", criterion.Value),
            "motion" => MatchesBooleanAttribute(state, "motion", criterion.Value),
            "lightLevel" => MatchesIntegerAttribute(state, "lightLevel", criterion.Value),
            _ => false,
        };
    }

    private static bool MatchesAttribute(DeviceState state, string name, JsonElement expected) =>
        expected.ValueKind == JsonValueKind.String &&
        state.Attributes is not null &&
        state.Attributes.TryGetValue(name, out var actual) &&
        string.Equals(actual, expected.GetString(), StringComparison.Ordinal);

    private static bool MatchesBooleanAttribute(
        DeviceState state,
        string name,
        JsonElement expected) =>
        expected.ValueKind is JsonValueKind.True or JsonValueKind.False &&
        state.Attributes is not null &&
        state.Attributes.TryGetValue(name, out var actual) &&
        bool.TryParse(actual, out var actualValue) &&
        actualValue == expected.GetBoolean();

    private static bool MatchesIntegerAttribute(
        DeviceState state,
        string name,
        JsonElement expected) =>
        expected.ValueKind == JsonValueKind.Number &&
        expected.TryGetInt32(out var expectedValue) &&
        state.Attributes is not null &&
        state.Attributes.TryGetValue(name, out var actual) &&
        int.TryParse(
            actual,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var actualValue) &&
        actualValue == expectedValue;
}

public sealed record StateCriteriaParseResult(
    AutomationStateCriteria? Criteria,
    string? Error = null)
{
    public bool Succeeded => Criteria is not null && Error is null;
}

public static class AutomationStateCriteriaParser
{
    public const int MaximumJsonLength = 4096;
    public const int MaximumCriterionCount = 8;

    private static readonly HashSet<string> SupportedFields = new(StringComparer.Ordinal)
    {
        "isOn",
        "brightness",
        "fanSpeed",
        "colour.x",
        "colour.y",
        "event",
        "button",
        "motion",
        "lightLevel",
    };

    public static StateCriteriaParseResult Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaximumJsonLength)
        {
            return new StateCriteriaParseResult(null, "State criteria must be valid JSON within 4096 characters.");
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                MaxDepth = 8,
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                root.EnumerateObject().Any(property => property.Name != "all") ||
                !root.TryGetProperty("all", out var all) ||
                all.ValueKind != JsonValueKind.Array)
            {
                return new StateCriteriaParseResult(
                    null,
                    "State criteria must be an object with an all array.");
            }

            var entries = all.EnumerateArray().ToArray();
            if (entries.Length is < 1 or > MaximumCriterionCount)
            {
                return new StateCriteriaParseResult(
                    null,
                    $"State criteria must contain between 1 and {MaximumCriterionCount} conditions.");
            }

            var criteria = new List<AutomationStateCriterion>(entries.Length);
            foreach (var entry in entries)
            {
                if (entry.ValueKind != JsonValueKind.Object ||
                    entry.EnumerateObject().Any(property =>
                        property.Name is not ("field" or "equals")) ||
                    !entry.TryGetProperty("field", out var field) ||
                    field.ValueKind != JsonValueKind.String ||
                    !SupportedFields.Contains(field.GetString() ?? string.Empty) ||
                    !entry.TryGetProperty("equals", out var equals) ||
                    !IsExpectedValueValid(field.GetString()!, equals))
                {
                    return new StateCriteriaParseResult(
                        null,
                        "Each state criterion must use a supported field and a scalar equals value.");
                }

                criteria.Add(new AutomationStateCriterion(field.GetString()!, equals.Clone()));
            }

            if (criteria.Select(criterion => criterion.Field).Distinct(StringComparer.Ordinal).Count() !=
                criteria.Count)
            {
                return new StateCriteriaParseResult(null, "State criteria cannot repeat a field.");
            }

            return new StateCriteriaParseResult(new AutomationStateCriteria(criteria));
        }
        catch (JsonException)
        {
            return new StateCriteriaParseResult(null, "State criteria must be valid JSON.");
        }
    }

    private static bool IsExpectedValueValid(string field, JsonElement value) =>
        field switch
        {
            "isOn" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "brightness" => value.ValueKind == JsonValueKind.Number &&
                value.TryGetInt32(out var brightness) && brightness is >= 0 and <= 100,
            "fanSpeed" => value.ValueKind == JsonValueKind.Number &&
                value.TryGetInt32(out var fanSpeed) &&
                (fanSpeed is >= 1 and <= 10 or 81 or 82),
            "colour.x" or "colour.y" => value.ValueKind == JsonValueKind.Number &&
                value.TryGetDouble(out var coordinate) && coordinate is >= 0 and <= 1,
            "event" or "button" => value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(value.GetString()) && value.GetString()!.Length <= 200,
            "motion" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "lightLevel" => value.ValueKind == JsonValueKind.Number &&
                value.TryGetInt32(out var lightLevel) && lightLevel is >= 0 and <= 65_535,
            _ => false,
        };
}

public sealed record AutomationAuditEntry(
    Guid CorrelationId,
    DateTimeOffset OccurredUtc,
    Guid SourceDeviceId,
    Guid? TriggerId,
    Guid? ActionId,
    string Outcome,
    DeviceOperationStatus? Status = null);

public interface IAutomationAuditStore
{
    Task WriteAsync(AutomationAuditEntry entry, CancellationToken cancellationToken = default);

    IReadOnlyList<AutomationAuditEntry> ReadRecent(int maximumCount);
}

public sealed class InMemoryAutomationAuditStore(int capacity = 500) : IAutomationAuditStore
{
    private readonly object _sync = new();
    private readonly Queue<AutomationAuditEntry> _entries = new();
    private readonly int _capacity = capacity > 0
        ? capacity
        : throw new ArgumentOutOfRangeException(nameof(capacity));

    public Task WriteAsync(AutomationAuditEntry entry, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > _capacity)
            {
                _entries.Dequeue();
            }
        }

        return Task.CompletedTask;
    }

    public IReadOnlyList<AutomationAuditEntry> ReadRecent(int maximumCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCount);
        lock (_sync)
        {
            return _entries.Reverse().Take(maximumCount).ToArray();
        }
    }
}

public sealed record AutomationExecutionResult(
    Guid CorrelationId,
    bool IsDuplicate,
    int MatchedTriggerCount,
    int SuccessfulActionCount,
    int FailedActionCount);

public interface IAutomationEngine
{
    Task<AutomationExecutionResult> HandleStateChangedAsync(
        DeviceStateChanged change,
        CancellationToken cancellationToken = default);
}

public sealed class AutomationEngine(
    IHomeStateStore homeStateStore,
    IEnumerable<IDeviceProvider> deviceProviders,
    IAutomationAuditStore auditStore,
    TimeProvider? timeProvider = null) : IAutomationEngine
{
    private const int MaximumDeduplicationEntries = 2_048;
    private const int MaximumCooldownEntries = 1_024;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly IReadOnlyDictionary<string, IDeviceProvider> _providers = deviceProviders
        .GroupBy(provider => provider.DeviceType, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
    private readonly HashSet<string> _deduplicationKeys = new(StringComparer.Ordinal);
    private readonly Queue<string> _deduplicationOrder = new();
    private readonly Dictionary<Guid, DateTimeOffset> _lastTriggerExecution = new();
    private readonly Queue<Guid> _cooldownOrder = new();

    public async Task<AutomationExecutionResult> HandleStateChangedAsync(
        DeviceStateChanged change,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        var correlationId = Guid.NewGuid();
        if (change.DeviceId == Guid.Empty || change.State is null ||
            string.IsNullOrWhiteSpace(change.EventId) || change.EventId.Length > 200)
        {
            await WriteAuditAsync(
                correlationId,
                change.DeviceId,
                null,
                null,
                "invalid-event",
                null,
                cancellationToken);
            return new AutomationExecutionResult(correlationId, false, 0, 0, 0);
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (change.IsReconciliation)
            {
                await WriteAuditAsync(
                    correlationId,
                    change.DeviceId,
                    null,
                    null,
                    "reconciled",
                    null,
                    cancellationToken);
                return new AutomationExecutionResult(correlationId, false, 0, 0, 0);
            }

            var deduplicationKey = $"{change.DeviceId:N}:{change.EventId}";
            if (!_deduplicationKeys.Add(deduplicationKey))
            {
                await WriteAuditAsync(
                    correlationId,
                    change.DeviceId,
                    null,
                    null,
                    "duplicate-event",
                    null,
                    cancellationToken);
                return new AutomationExecutionResult(correlationId, true, 0, 0, 0);
            }

            _deduplicationOrder.Enqueue(deduplicationKey);
            while (_deduplicationOrder.Count > MaximumDeduplicationEntries)
            {
                _deduplicationKeys.Remove(_deduplicationOrder.Dequeue());
            }

            var state = homeStateStore.Current;
            var now = _timeProvider.GetUtcNow();
            var matchedTriggers = 0;
            var successfulActions = 0;
            var failedActions = 0;
            foreach (var trigger in state.Document.AutomationTriggers
                         .Where(trigger => trigger.SourceDeviceId == change.DeviceId)
                         .OrderBy(trigger => trigger.Id))
            {
                if (!trigger.Enabled)
                {
                    await WriteAuditAsync(
                        correlationId,
                        change.DeviceId,
                        trigger.Id,
                        null,
                        "trigger-disabled",
                        null,
                        cancellationToken);
                    continue;
                }

                var criteria = AutomationStateCriteriaParser.Parse(trigger.StateCriteriaJson).Criteria;
                if (criteria is null || !criteria.IsMatch(change.State))
                {
                    await WriteAuditAsync(
                        correlationId,
                        change.DeviceId,
                        trigger.Id,
                        null,
                        "criteria-not-matched",
                        null,
                        cancellationToken);
                    continue;
                }

                if (_lastTriggerExecution.TryGetValue(trigger.Id, out var lastExecution) &&
                    now - lastExecution < TimeSpan.FromSeconds(trigger.CooldownSeconds))
                {
                    await WriteAuditAsync(
                        correlationId,
                        change.DeviceId,
                        trigger.Id,
                        null,
                        "cooldown",
                        null,
                        cancellationToken);
                    continue;
                }

                SetCooldown(trigger.Id, now);
                matchedTriggers++;
                await WriteAuditAsync(
                    correlationId,
                    change.DeviceId,
                    trigger.Id,
                    null,
                    "triggered",
                    null,
                    cancellationToken);

                foreach (var actionSetId in trigger.ActionSetIds)
                {
                    if (state.Document.AutomationActionSets.FirstOrDefault(
                            actionSet => actionSet.Id == actionSetId) is not { } actionSet)
                    {
                        await WriteAuditAsync(
                            correlationId,
                            change.DeviceId,
                            trigger.Id,
                            null,
                            "action-set-missing",
                            null,
                            cancellationToken);
                        continue;
                    }

                    if (!actionSet.Enabled)
                    {
                        await WriteAuditAsync(
                            correlationId,
                            change.DeviceId,
                            trigger.Id,
                            null,
                            "action-set-disabled",
                            null,
                            cancellationToken);
                        continue;
                    }

                    foreach (var actionId in actionSet.ActionIds)
                    {
                        var action = state.Document.AutomationActions.FirstOrDefault(
                            candidate => candidate.Id == actionId);
                        if (action is null)
                        {
                            failedActions++;
                            await WriteAuditAsync(
                                correlationId,
                                change.DeviceId,
                                trigger.Id,
                                actionId,
                                "action-missing",
                                DeviceOperationStatus.ResourceNotFound,
                                cancellationToken);
                            continue;
                        }

                        if (!action.Enabled)
                        {
                            await WriteAuditAsync(
                                correlationId,
                                change.DeviceId,
                                trigger.Id,
                                action.Id,
                                "action-disabled",
                                null,
                                cancellationToken);
                            continue;
                        }

                        var status = await ExecuteActionAsync(action, state, cancellationToken);
                        if (status == DeviceOperationStatus.Succeeded)
                        {
                            successfulActions++;
                        }
                        else
                        {
                            failedActions++;
                        }

                        await WriteAuditAsync(
                            correlationId,
                            change.DeviceId,
                            trigger.Id,
                            action.Id,
                            status == DeviceOperationStatus.Succeeded
                                ? "action-succeeded"
                                : "action-failed",
                            status,
                            cancellationToken);
                    }
                }
            }

            return new AutomationExecutionResult(
                correlationId,
                false,
                matchedTriggers,
                successfulActions,
                failedActions);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<DeviceOperationStatus> ExecuteActionAsync(
        AutomationAction action,
        HomeState state,
        CancellationToken cancellationToken)
    {
        if (action.ActionType != AutomationActionTypes.DeviceSetState ||
            !state.DevicesById.TryGetValue(action.TargetDeviceId, out var device) ||
            !device.Enabled ||
            !_providers.TryGetValue(device.DeviceType, out var provider))
        {
            return DeviceOperationStatus.InvalidState;
        }

        try
        {
            return (await provider.SetStateAsync(device, action.StateChange, cancellationToken)).Status;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return DeviceOperationStatus.TimedOut;
        }
        catch (HttpRequestException)
        {
            return DeviceOperationStatus.BridgeUnavailable;
        }
        catch (IOException)
        {
            return DeviceOperationStatus.BridgeUnavailable;
        }
        catch (InvalidOperationException)
        {
            return DeviceOperationStatus.InvalidState;
        }
    }

    private void SetCooldown(Guid triggerId, DateTimeOffset now)
    {
        if (!_lastTriggerExecution.ContainsKey(triggerId))
        {
            _cooldownOrder.Enqueue(triggerId);
        }

        _lastTriggerExecution[triggerId] = now;
        while (_cooldownOrder.Count > MaximumCooldownEntries)
        {
            _lastTriggerExecution.Remove(_cooldownOrder.Dequeue());
        }
    }

    private Task WriteAuditAsync(
        Guid correlationId,
        Guid sourceDeviceId,
        Guid? triggerId,
        Guid? actionId,
        string outcome,
        DeviceOperationStatus? status,
        CancellationToken cancellationToken) =>
        auditStore.WriteAsync(
            new AutomationAuditEntry(
                correlationId,
                _timeProvider.GetUtcNow(),
                sourceDeviceId,
                triggerId,
                actionId,
                outcome,
                status),
            cancellationToken);
}

public sealed record AutomationActionPreview(
    Guid ActionId,
    string Name,
    bool Enabled,
    string Target,
    string StateChange);

public sealed record AutomationTriggerPreview(
    Guid TriggerId,
    string Name,
    bool Enabled,
    string Source,
    IReadOnlyList<string> StateFields,
    IReadOnlyList<string> References,
    IReadOnlyList<AutomationActionPreview> EffectiveTargets,
    string? Error = null);

public sealed record AutomationValidationPreview(
    IReadOnlyList<AutomationTriggerPreview> Triggers,
    IReadOnlyList<string> Errors);

public sealed class AutomationConfigurationService(IHomeStateStore homeStateStore)
{
    public AutomationValidationPreview GetPreview()
    {
        var state = homeStateStore.Current;
        var actions = state.Document.AutomationActions.ToDictionary(action => action.Id);
        var actionSets = state.Document.AutomationActionSets.ToDictionary(actionSet => actionSet.Id);
        var devices = state.DevicesById;
        var previews = new List<AutomationTriggerPreview>();
        var errors = new List<string>();

        foreach (var trigger in state.Document.AutomationTriggers)
        {
            var criteriaResult = AutomationStateCriteriaParser.Parse(trigger.StateCriteriaJson);
            var references = new List<string>();
            var targets = new List<AutomationActionPreview>();
            foreach (var actionSetId in trigger.ActionSetIds)
            {
                if (!actionSets.TryGetValue(actionSetId, out var actionSet))
                {
                    references.Add($"Missing action set {actionSetId}.");
                    continue;
                }

                references.Add($"{actionSet.Name} ({(actionSet.Enabled ? "enabled" : "disabled")})");
                foreach (var actionId in actionSet.ActionIds)
                {
                    if (!actions.TryGetValue(actionId, out var action))
                    {
                        references.Add($"Missing action {actionId}.");
                        continue;
                    }

                    var target = devices.TryGetValue(action.TargetDeviceId, out var device)
                        ? $"{device.Name} ({(device.Enabled ? "enabled" : "disabled")})"
                        : $"Missing device {action.TargetDeviceId}";
                    targets.Add(new AutomationActionPreview(
                        action.Id,
                        action.Name,
                        action.Enabled,
                        target,
                        DescribeStateChange(action.StateChange)));
                }
            }

            var source = devices.TryGetValue(trigger.SourceDeviceId, out var sourceDevice)
                ? $"{sourceDevice.Name} ({(sourceDevice.Enabled ? "enabled" : "disabled")})"
                : $"Missing device {trigger.SourceDeviceId}";
            var preview = new AutomationTriggerPreview(
                trigger.Id,
                trigger.Name,
                trigger.Enabled,
                source,
                criteriaResult.Criteria?.All.Select(criterion => criterion.Field).ToArray() ??
                    Array.Empty<string>(),
                references,
                targets,
                criteriaResult.Error);
            previews.Add(preview);
            if (preview.Error is not null)
            {
                errors.Add($"{trigger.Name}: {preview.Error}");
            }
        }

        return new AutomationValidationPreview(previews, errors);
    }

    public Task SaveActionAsync(
        AutomationAction action,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(document => document with
        {
            AutomationActions = ReplaceById(
                document.AutomationActions,
                action,
                candidate => candidate.Id),
        }, cancellationToken);

    public Task SaveActionSetAsync(
        AutomationActionSet actionSet,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(document => document with
        {
            AutomationActionSets = ReplaceById(
                document.AutomationActionSets,
                actionSet,
                candidate => candidate.Id),
        }, cancellationToken);

    public Task SaveTriggerAsync(
        AutomationTrigger trigger,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(document => document with
        {
            AutomationTriggers = ReplaceById(
                document.AutomationTriggers,
                trigger,
                candidate => candidate.Id),
        }, cancellationToken);

    public Task DeleteActionAsync(Guid id, CancellationToken cancellationToken = default) =>
        UpdateAsync(document => document with
        {
            AutomationActions = document.AutomationActions
                .Where(action => action.Id != id)
                .ToArray(),
        }, cancellationToken);

    public Task DeleteActionSetAsync(Guid id, CancellationToken cancellationToken = default) =>
        UpdateAsync(document => document with
        {
            AutomationActionSets = document.AutomationActionSets
                .Where(actionSet => actionSet.Id != id)
                .ToArray(),
        }, cancellationToken);

    public Task DeleteTriggerAsync(Guid id, CancellationToken cancellationToken = default) =>
        UpdateAsync(document => document with
        {
            AutomationTriggers = document.AutomationTriggers
                .Where(trigger => trigger.Id != id)
                .ToArray(),
        }, cancellationToken);

    private async Task UpdateAsync(
        Func<HomeStateDocument, HomeStateDocument> update,
        CancellationToken cancellationToken)
    {
        await homeStateStore.UpdateAsync(
            state => update(state.Document),
            homeStateStore.Current.Revision,
            cancellationToken);
    }

    private static IReadOnlyList<T> ReplaceById<T>(
        IReadOnlyList<T> source,
        T replacement,
        Func<T, Guid> getId)
    {
        var replacementId = getId(replacement);
        return source.Any(candidate => getId(candidate) == replacementId)
            ? source.Select(candidate => getId(candidate) == replacementId
                ? replacement
                : candidate).ToArray()
            : source.Append(replacement).ToArray();
    }

    private static string DescribeStateChange(DeviceStateChange stateChange)
    {
        var entries = new List<string>();
        if (stateChange.IsOn is { } isOn)
        {
            entries.Add(isOn ? "on" : "off");
        }

        if (stateChange.Brightness is { } brightness)
        {
            entries.Add($"brightness {brightness}%");
        }

        if (stateChange.FanSpeed is { } fanSpeed)
        {
            entries.Add($"fan speed {fanSpeed}");
        }

        if (stateChange.Colour is { } colour)
        {
            entries.Add($"colour {colour.X:0.###},{colour.Y:0.###}");
        }

        return string.Join(", ", entries);
    }
}
