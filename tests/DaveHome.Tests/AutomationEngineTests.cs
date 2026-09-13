using DaveHome;

namespace DaveHome.Tests;

public sealed class AutomationEngineTests
{
    [Fact]
    public void StateCriteriaMatchesOnlySupportedTypedFields()
    {
        var parsed = AutomationStateCriteriaParser.Parse(
            """{"all":[{"field":"event","equals":"initial_press"},{"field":"button","equals":"1"},{"field":"brightness","equals":42}]}""");

        Assert.True(parsed.Succeeded);
        Assert.True(parsed.Criteria!.IsMatch(new DeviceState(
            null,
            42,
            Attributes: new Dictionary<string, string>
            {
                ["event"] = "initial_press",
                ["button"] = "1",
            })));
        Assert.False(parsed.Criteria.IsMatch(new DeviceState(
            null,
            42,
            Attributes: new Dictionary<string, string> { ["event"] = "repeat" })));
        Assert.False(AutomationStateCriteriaParser.Parse(
            """{"all":[{"field":"expression","equals":"anything"}]}""").Succeeded);
    }

    [Fact]
    public void MotionAndLightLevelCriteriaUseStrictHueTypesAndBounds()
    {
        var parsed = AutomationStateCriteriaParser.Parse(
            """{"all":[{"field":"motion","equals":true},{"field":"lightLevel","equals":5000}]}""");

        Assert.True(parsed.Succeeded);
        Assert.True(parsed.Criteria!.IsMatch(new DeviceState(
            null,
            null,
            Attributes: new Dictionary<string, string>
            {
                ["motion"] = "true",
                ["lightLevel"] = "5000",
                ["lightLevelValid"] = "true",
            })));
        Assert.False(parsed.Criteria.IsMatch(new DeviceState(
            null,
            null,
            Attributes: new Dictionary<string, string>
            {
                ["motion"] = "true",
                ["lightLevel"] = "invalid",
            })));
        Assert.False(AutomationStateCriteriaParser.Parse(
            """{"all":[{"field":"motion","equals":"true"}]}""").Succeeded);
        Assert.False(AutomationStateCriteriaParser.Parse(
            """{"all":[{"field":"lightLevel","equals":65536}]}""").Succeeded);
    }

    [Fact]
    public async Task MotionSensorTriggerRoutesMatchingSseStateToAllowlistedAction()
    {
        var ids = new AutomationIds();
        var action = Action(ids.FirstAction, "Turn on", ids.Light, new DeviceStateChange(IsOn: true));
        var actionSet = new AutomationActionSet(ids.FirstSet, "Set", [action.Id]);
        var trigger = new AutomationTrigger(
            ids.Trigger,
            "Kitchen motion",
            ids.Switch,
            """{"all":[{"field":"motion","equals":true},{"field":"lightLevel","equals":5000}]}""",
            [actionSet.Id]);
        var store = await CreateStoreAsync(
            ids,
            [action],
            [actionSet],
            [trigger],
            motionSensorSource: true);
        var provider = new RecordingProvider(Guid.Empty, [action]);
        var engine = new AutomationEngine(
            store,
            [provider],
            new InMemoryAutomationAuditStore(),
            new TestTimeProvider());

        var result = await engine.HandleStateChangedAsync(new DeviceStateChanged(
            ids.Switch,
            new DeviceState(
                null,
                null,
                Attributes: new Dictionary<string, string>
                {
                    ["motion"] = "true",
                    ["lightLevel"] = "5000",
                }),
            "sensor-event:1",
            DateTimeOffset.UtcNow));

        Assert.Equal(1, result.MatchedTriggerCount);
        Assert.Equal([new DeviceStateChange(IsOn: true)], provider.Changes);
    }

    [Fact]
    public async Task MatchingTriggerRunsActionSetsInDeclaredOrderAndContinuesAfterFailure()
    {
        var ids = new AutomationIds();
        var actions = new[]
        {
            Action(ids.FirstAction, "First", ids.Light, new DeviceStateChange(IsOn: true)),
            Action(ids.FailedAction, "Failed", ids.Light, new DeviceStateChange(Brightness: 45)),
            Action(ids.LastAction, "Last", ids.Light, new DeviceStateChange(IsOn: false)),
        };
        var actionSets = new[]
        {
            new AutomationActionSet(ids.FirstSet, "First set", [ids.FirstAction, ids.FailedAction]),
            new AutomationActionSet(ids.LastSet, "Last set", [ids.LastAction]),
        };
        var trigger = Trigger(ids.Trigger, ids.Switch, [ids.FirstSet, ids.LastSet]);
        var store = await CreateStoreAsync(ids, actions, actionSets, [trigger]);
        var provider = new RecordingProvider(ids.FailedAction, actions);
        var audit = new InMemoryAutomationAuditStore();
        var engine = new AutomationEngine(store, [provider], audit, new TestTimeProvider());

        var result = await engine.HandleStateChangedAsync(Change(ids.Switch, "event-1"));

        Assert.Equal(1, result.MatchedTriggerCount);
        Assert.Equal(2, result.SuccessfulActionCount);
        Assert.Equal(1, result.FailedActionCount);
        Assert.Equal(
            [new DeviceStateChange(IsOn: true), new DeviceStateChange(Brightness: 45), new DeviceStateChange(IsOn: false)],
            provider.Changes);
        Assert.Equal(
            ["triggered", "action-succeeded", "action-failed", "action-succeeded"],
            audit.ReadRecent(10).Reverse().Select(entry => entry.Outcome));
    }

    [Fact]
    public async Task DuplicateEventsAndCooldownDoNotRepeatCompletedActions()
    {
        var ids = new AutomationIds();
        var action = Action(ids.FirstAction, "Action", ids.Light, new DeviceStateChange(IsOn: true));
        var actionSet = new AutomationActionSet(ids.FirstSet, "Set", [action.Id]);
        var trigger = Trigger(ids.Trigger, ids.Switch, [actionSet.Id]) with { CooldownSeconds = 10 };
        var store = await CreateStoreAsync(ids, [action], [actionSet], [trigger]);
        var provider = new RecordingProvider(Guid.Empty, [action]);
        var clock = new TestTimeProvider();
        var audit = new InMemoryAutomationAuditStore();
        var engine = new AutomationEngine(store, [provider], audit, clock);

        await engine.HandleStateChangedAsync(Change(ids.Switch, "event-1"));
        var duplicate = await engine.HandleStateChangedAsync(Change(ids.Switch, "event-1"));
        clock.Advance(TimeSpan.FromSeconds(2));
        await engine.HandleStateChangedAsync(Change(ids.Switch, "event-2"));
        clock.Advance(TimeSpan.FromSeconds(10));
        await engine.HandleStateChangedAsync(Change(ids.Switch, "event-3"));

        Assert.True(duplicate.IsDuplicate);
        Assert.Equal(2, provider.Changes.Count);
        Assert.Contains(audit.ReadRecent(10), entry => entry.Outcome == "duplicate-event");
        Assert.Contains(audit.ReadRecent(10), entry => entry.Outcome == "cooldown");
    }

    [Fact]
    public async Task TriggerSelectionAndDisabledReferencesDoNotExecuteActions()
    {
        var ids = new AutomationIds();
        var action = Action(ids.FirstAction, "Action", ids.Light, new DeviceStateChange(IsOn: true)) with
        {
            Enabled = false,
        };
        var actionSet = new AutomationActionSet(ids.FirstSet, "Set", [action.Id]);
        var matched = Trigger(ids.Trigger, ids.Switch, [actionSet.Id]);
        var unmatched = Trigger(Guid.NewGuid(), ids.Switch, [actionSet.Id]) with
        {
            Name = "Repeat only",
            StateCriteriaJson = """{"all":[{"field":"event","equals":"repeat"}]}""",
        };
        var disabled = Trigger(Guid.NewGuid(), ids.Switch, [actionSet.Id]) with
        {
            Name = "Disabled",
            Enabled = false,
        };
        var store = await CreateStoreAsync(ids, [action], [actionSet], [matched, unmatched, disabled]);
        var provider = new RecordingProvider(Guid.Empty, [action]);
        var audit = new InMemoryAutomationAuditStore();
        var engine = new AutomationEngine(store, [provider], audit, new TestTimeProvider());

        var result = await engine.HandleStateChangedAsync(Change(ids.Switch, "event-1"));

        Assert.Equal(1, result.MatchedTriggerCount);
        Assert.Empty(provider.Changes);
        var outcomes = audit.ReadRecent(10).Select(entry => entry.Outcome);
        Assert.Contains("action-disabled", outcomes);
        Assert.Contains("criteria-not-matched", outcomes);
        Assert.Contains("trigger-disabled", outcomes);
    }

    [Fact]
    public async Task InvalidMissingReferencesAndNonAllowlistedActionsAreRejected()
    {
        var ids = new AutomationIds();
        var shellAction = Action(ids.FirstAction, "Shell", ids.Light, new DeviceStateChange(IsOn: true)) with
        {
            ActionType = "shell.execute",
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateStoreAsync(
                ids,
                [shellAction],
                [new AutomationActionSet(ids.FirstSet, "Set", [shellAction.Id])],
                [Trigger(ids.Trigger, ids.Switch, [ids.FirstSet])]));

        var validAction = Action(ids.FirstAction, "Valid", ids.Light, new DeviceStateChange(IsOn: true));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateStoreAsync(
                ids,
                [validAction],
                [new AutomationActionSet(ids.FirstSet, "Set", [Guid.NewGuid()])],
                [Trigger(ids.Trigger, ids.Switch, [ids.FirstSet])]));

        var unsupportedCapability = Action(
            ids.FirstAction,
            "Unsupported",
            ids.Switch,
            new DeviceStateChange(IsOn: true));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateStoreAsync(
                ids,
                [unsupportedCapability],
                [new AutomationActionSet(ids.FirstSet, "Set", [unsupportedCapability.Id])],
                [Trigger(ids.Trigger, ids.Switch, [ids.FirstSet])]));
    }

    private static AutomationAction Action(
        Guid id,
        string name,
        Guid targetDeviceId,
        DeviceStateChange stateChange) =>
        new(id, name, AutomationActionTypes.DeviceSetState, targetDeviceId, stateChange);

    private static AutomationTrigger Trigger(
        Guid id,
        Guid sourceDeviceId,
        IReadOnlyList<Guid> actionSetIds) =>
        new(
            id,
            "Initial press",
            sourceDeviceId,
            """{"all":[{"field":"event","equals":"initial_press"},{"field":"button","equals":"1"}]}""",
            actionSetIds);

    private static DeviceStateChanged Change(Guid deviceId, string eventId) =>
        new(
            deviceId,
            new DeviceState(
                null,
                null,
                Attributes: new Dictionary<string, string>
                {
                    ["event"] = "initial_press",
                    ["button"] = "1",
                }),
            eventId,
            DateTimeOffset.UtcNow);

    private static async Task<IHomeStateStore> CreateStoreAsync(
        AutomationIds ids,
        IReadOnlyList<AutomationAction> actions,
        IReadOnlyList<AutomationActionSet> actionSets,
        IReadOnlyList<AutomationTrigger> triggers,
        bool motionSensorSource = false)
    {
        var store = new HomeStateStore(new InMemoryPersistence());
        await store.InitializeAsync();
        await store.UpdateAsync(state => state.Document with
        {
            Hubs =
            [
                new Hub(
                    ids.Hub,
                    "Hue",
                    "hue",
                    "https://192.168.1.20/",
                    "opaque-reference",
                    true),
            ],
            Devices =
            [
                new Device(
                    ids.Switch,
                    "Switch",
                    motionSensorSource ? "hue.motion-sensor" : "hue.switch",
                    null,
                    true,
                    ids.Hub,
                    motionSensorSource ? "motion:motion-1;lightLevel:level-1" : "button-1",
                    DeviceCapabilities.None,
                    motionSensorSource
                        ? new DeviceState(
                            null,
                            null,
                            Attributes: new Dictionary<string, string>
                            {
                                ["motion"] = "false",
                                ["lightLevelAvailable"] = "true",
                            })
                        : new DeviceState(null, null)),
                new Device(
                    ids.Light,
                    "Light",
                    "hue.light",
                    null,
                    true,
                    ids.Hub,
                    "light-1",
                    DeviceCapabilities.OnOff | DeviceCapabilities.Brightness,
                    new DeviceState(false, 0)),
            ],
            AutomationActions = actions,
            AutomationActionSets = actionSets,
            AutomationTriggers = triggers,
        }, StoreRevision.Initial);
        return store;
    }

    private sealed class InMemoryPersistence : IHomeStatePersistence
    {
        public Task<HomeStateDocument?> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<HomeStateDocument?>(null);

        public Task SaveAsync(
            HomeStateDocument document,
            StoreRevision expectedRevision,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingProvider(
        Guid failingActionId,
        IReadOnlyList<AutomationAction> actions) : IDeviceProvider
    {
        private readonly Dictionary<DeviceStateChange, Guid> _actionIds = actions.ToDictionary(
            action => action.StateChange,
            action => action.Id);

        public string DeviceType => "hue.light";

        public List<DeviceStateChange> Changes { get; } = [];

        public Task<DeviceOperationResult> GetStateAsync(
            Device device,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new DeviceOperationResult(DeviceOperationStatus.Succeeded, device.State));

        public Task<DeviceOperationResult> SetStateAsync(
            Device device,
            DeviceStateChange change,
            CancellationToken cancellationToken = default)
        {
            Changes.Add(change);
            var status = _actionIds.TryGetValue(change, out var actionId) &&
                actionId == failingActionId
                ? DeviceOperationStatus.BridgeUnavailable
                : DeviceOperationStatus.Succeeded;
            return Task.FromResult(new DeviceOperationResult(status));
        }
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan amount) => _now += amount;
    }

    private sealed class AutomationIds
    {
        public Guid Hub { get; } = Guid.NewGuid();
        public Guid Switch { get; } = Guid.NewGuid();
        public Guid Light { get; } = Guid.NewGuid();
        public Guid FirstAction { get; } = Guid.NewGuid();
        public Guid FailedAction { get; } = Guid.NewGuid();
        public Guid LastAction { get; } = Guid.NewGuid();
        public Guid FirstSet { get; } = Guid.NewGuid();
        public Guid LastSet { get; } = Guid.NewGuid();
        public Guid Trigger { get; } = Guid.NewGuid();
    }
}
