using DaveHome;

namespace DaveHome.Tests;

public sealed class CommandProcessorTests
{
    [Fact]
    public void ParserNormalizesGrammarPercentagesAndColours()
    {
        var brightness = TextCommandParser.Parse("  SET, Kitchen lights to 50 %!! ");
        var colour = TextCommandParser.Parse("set kitchen lights to BLUE");
        var defaultTurn = TextCommandParser.Parse("turn lights off");

        Assert.Equal("set kitchen lights to 50 %", brightness.Command.Transcript);
        Assert.Equal(TextCommandIntent.SetBrightness, brightness.Intent);
        Assert.Equal(50, brightness.Brightness);
        Assert.Equal(TextCommandIntent.SetColour, colour.Intent);
        Assert.Equal(new DeviceColour(0.168, 0.041), colour.Colour);
        Assert.Equal(TextCommandIntent.LightsOff, defaultTurn.Intent);
        Assert.Null(defaultTurn.LocationName);
    }

    [Fact]
    public async Task AliasAndExplicitLocationOverrideEarLocation()
    {
        var kitchen = new Location(Guid.NewGuid(), "Kitchen", null);
        var bedroom = new Location(Guid.NewGuid(), "Bedroom", null);
        var kitchenLight = CreateLight("Kitchen light", kitchen.Id);
        var bedroomLight = CreateLight("Bedroom light", bedroom.Id);
        var store = await CreateStoreAsync(
            [kitchen, bedroom],
            [kitchenLight, bedroomLight],
            [new CommandAlias(Guid.NewGuid(), "cooking room", kitchen.Id)]);
        var provider = new RecordingProvider();
        var processor = new CommandProcessor(store, [provider], new InMemoryCommandAuditStore());

        var result = await processor.ProcessAsync(
            "Turn cooking room lights off",
            new CommandSource("ear:test", bedroom.Id));

        Assert.Equal(CommandOutcome.Completed, result.Outcome);
        Assert.Equal([kitchenLight.Id], provider.SetDevices);
        Assert.Equal(false, provider.Changes.Single().IsOn);
    }

    [Fact]
    public async Task DefaultLocationIncludesDescendants()
    {
        var wholeHome = new Location(Guid.NewGuid(), "Home", null);
        var kitchen = new Location(Guid.NewGuid(), "Kitchen", wholeHome.Id);
        var store = await CreateStoreAsync(
            [wholeHome, kitchen],
            [CreateLight("Hall", wholeHome.Id), CreateLight("Kitchen", kitchen.Id)]);
        var provider = new RecordingProvider();
        var processor = new CommandProcessor(store, [provider], new InMemoryCommandAuditStore());

        var result = await processor.ProcessAsync("lights on", new CommandSource("ear:test", wholeHome.Id));

        Assert.Equal(CommandOutcome.Completed, result.Outcome);
        Assert.Equal(2, provider.SetDevices.Count);
        Assert.Contains("source location", result.TargetSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AmbiguousAndUnsupportedCommandsMakeNoDeviceAction()
    {
        var kitchen = new Location(Guid.NewGuid(), "Kitchen", null);
        var dining = new Location(Guid.NewGuid(), "Dining", null);
        var plainLight = CreateLight("Plain", kitchen.Id, DeviceCapabilities.OnOff);
        var store = await CreateStoreAsync(
            [kitchen, dining],
            [plainLight],
            [new CommandAlias(Guid.NewGuid(), "kitchen", dining.Id)]);
        var provider = new RecordingProvider();
        var processor = new CommandProcessor(store, [provider], new InMemoryCommandAuditStore());

        var ambiguous = await processor.ProcessAsync("turn kitchen lights on", new CommandSource("ear:test"));
        var unsupported = await processor.ProcessAsync("set dining lights to blue", new CommandSource("ear:test"));

        Assert.Equal(CommandOutcome.Rejected, ambiguous.Outcome);
        Assert.Equal("ambiguous-location", ambiguous.Message.Code);
        Assert.Equal(CommandOutcome.Rejected, unsupported.Outcome);
        Assert.Equal("unsupported", unsupported.Message.Code);
        Assert.Empty(provider.SetDevices);
    }

    [Fact]
    public async Task UnsupportedCapabilityAndMalformedInputAreAuditedWithoutActions()
    {
        var kitchen = new Location(Guid.NewGuid(), "Kitchen", null);
        var store = await CreateStoreAsync(
            [kitchen],
            [CreateLight("Plain", kitchen.Id, DeviceCapabilities.OnOff)]);
        var provider = new RecordingProvider();
        var audit = new InMemoryCommandAuditStore();
        var processor = new CommandProcessor(store, [provider], audit);

        var unsupported = await processor.ProcessAsync("set kitchen lights to blue", new CommandSource("ear:test"));
        var malformed = await processor.ProcessAsync("make lights dance", new CommandSource("ear:test", kitchen.Id));

        Assert.Equal("unsupported", unsupported.Message.Code);
        Assert.Equal("malformed", malformed.Message.Code);
        Assert.Empty(provider.SetDevices);
        Assert.Equal(2, audit.ReadRecent(10).Count);
    }

    [Fact]
    public async Task StatusReadsTheResolvedLights()
    {
        var kitchen = new Location(Guid.NewGuid(), "Kitchen", null);
        var light = CreateLight("Kitchen light", kitchen.Id);
        var store = await CreateStoreAsync([kitchen], [light]);
        var provider = new RecordingProvider { State = new DeviceState(true, 40) };
        var processor = new CommandProcessor(store, [provider], new InMemoryCommandAuditStore());

        var result = await processor.ProcessAsync("are kitchen lights on?", new CommandSource("admin"));

        Assert.Equal(CommandOutcome.Completed, result.Outcome);
        Assert.Equal("status", result.Message.Code);
        Assert.Equal([light.Id], provider.ReadDevices);
    }

    [Fact]
    public async Task DeviceFailuresProducePartialResultWithoutRetryingTargets()
    {
        var kitchen = new Location(Guid.NewGuid(), "Kitchen", null);
        var first = CreateLight("First", kitchen.Id);
        var second = CreateLight("Second", kitchen.Id);
        var store = await CreateStoreAsync([kitchen], [first, second]);
        var provider = new RecordingProvider();
        provider.FailingSetDevices.Add(second.Id);
        var processor = new CommandProcessor(store, [provider], new InMemoryCommandAuditStore());

        var result = await processor.ProcessAsync("lights on", new CommandSource("ear:test", kitchen.Id));

        Assert.Equal(CommandOutcome.PartiallyCompleted, result.Outcome);
        Assert.Equal("partial-failure", result.Message.Code);
        Assert.Equal([first.Id, second.Id], provider.SetDevices);
    }

    [Fact]
    public async Task AllDeviceFailuresReturnAFailureWithoutRetryingTargets()
    {
        var kitchen = new Location(Guid.NewGuid(), "Kitchen", null);
        var light = CreateLight("Kitchen", kitchen.Id);
        var store = await CreateStoreAsync([kitchen], [light]);
        var provider = new RecordingProvider();
        provider.FailingSetDevices.Add(light.Id);
        var processor = new CommandProcessor(store, [provider], new InMemoryCommandAuditStore());

        var result = await processor.ProcessAsync("lights on", new CommandSource("ear:test", kitchen.Id));

        Assert.Equal(CommandOutcome.Failed, result.Outcome);
        Assert.Equal("failed", result.Message.Code);
        Assert.Equal([light.Id], provider.SetDevices);
    }

    [Fact]
    public async Task DefaultCommandWithoutEarLocationIsRejected()
    {
        var kitchen = new Location(Guid.NewGuid(), "Kitchen", null);
        var store = await CreateStoreAsync([kitchen], [CreateLight("Kitchen", kitchen.Id)]);
        var provider = new RecordingProvider();
        var processor = new CommandProcessor(store, [provider], new InMemoryCommandAuditStore());

        var result = await processor.ProcessAsync("lights on", new CommandSource("admin"));

        Assert.Equal(CommandOutcome.Rejected, result.Outcome);
        Assert.Equal("missing-source-location", result.Message.Code);
        Assert.Empty(provider.SetDevices);
    }

    [Fact]
    public async Task AuditTranscriptAndResultFieldsAreBounded()
    {
        var kitchen = new Location(Guid.NewGuid(), new string('K', 1_000), null);
        var store = await CreateStoreAsync([kitchen], [CreateLight("Kitchen", kitchen.Id)]);
        var audit = new InMemoryCommandAuditStore();
        var processor = new CommandProcessor(store, [new RecordingProvider()], audit);

        var result = await processor.ProcessAsync(
            "lights on",
            new CommandSource(new string('S', 1_000), kitchen.Id));

        var entry = Assert.Single(audit.ReadRecent(1));
        Assert.Equal(CommandOutcome.Completed, result.Outcome);
        Assert.True(result.TargetSummary.Length <= 500);
        Assert.True(entry.Source.Length <= 500);
        Assert.True(entry.Summary.Length <= 500);
        Assert.True(entry.Transcript!.Length <= TextCommandNormalizer.MaximumTranscriptLength);
    }

    private static Device CreateLight(
        string name,
        Guid locationId,
        DeviceCapabilities capabilities = DeviceCapabilities.OnOff | DeviceCapabilities.Brightness | DeviceCapabilities.Colour) =>
        new(Guid.NewGuid(), name, "test.light", locationId, Capabilities: capabilities);

    private static async Task<HomeStateStore> CreateStoreAsync(
        IReadOnlyList<Location> locations,
        IReadOnlyList<Device> devices,
        IReadOnlyList<CommandAlias>? aliases = null)
    {
        var store = new HomeStateStore(new InMemoryPersistence());
        await store.InitializeAsync();
        await store.UpdateAsync(_ => new HomeStateDocument(
            HomeStateDocument.CurrentSchemaVersion,
            StoreRevision.Initial,
            locations,
            devices)
        {
            CommandAliases = aliases ?? Array.Empty<CommandAlias>(),
        }, StoreRevision.Initial);
        return store;
    }

    private sealed class InMemoryPersistence : IHomeStatePersistence
    {
        private HomeStateDocument? _document;

        public Task<HomeStateDocument?> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_document);

        public Task SaveAsync(
            HomeStateDocument document,
            StoreRevision expectedRevision,
            CancellationToken cancellationToken = default)
        {
            _document = document;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingProvider : IDeviceProvider
    {
        public string DeviceType => "test.light";

        public List<Guid> SetDevices { get; } = [];

        public List<Guid> ReadDevices { get; } = [];

        public List<DeviceStateChange> Changes { get; } = [];

        public HashSet<Guid> FailingSetDevices { get; } = [];

        public DeviceState State { get; set; } = new(false, 0);

        public Task<DeviceOperationResult> GetStateAsync(
            Device device,
            CancellationToken cancellationToken = default)
        {
            ReadDevices.Add(device.Id);
            return Task.FromResult(new DeviceOperationResult(DeviceOperationStatus.Succeeded, State));
        }

        public Task<DeviceOperationResult> SetStateAsync(
            Device device,
            DeviceStateChange change,
            CancellationToken cancellationToken = default)
        {
            SetDevices.Add(device.Id);
            Changes.Add(change);
            return Task.FromResult(FailingSetDevices.Contains(device.Id)
                ? new DeviceOperationResult(DeviceOperationStatus.BridgeUnavailable)
                : new DeviceOperationResult(DeviceOperationStatus.Succeeded, State));
        }
    }
}
