using DaveBrain.Pages.Commands;
using DaveHome;

namespace DaveBrain.Tests;

public sealed class CommandPageTests
{
    [Fact]
    public async Task RunCommandIgnoresUnrelatedAliasValidationErrors()
    {
        var store = new HomeStateStore(new InMemoryPersistence());
        await store.InitializeAsync();
        var processor = new RecordingCommandProcessor();
        var model = new IndexModel(processor, store, new InMemoryCommandAuditStore())
        {
            Transcript = "lights on",
        };
        model.ModelState.AddModelError("NewAlias", "The alias is required.");

        await model.OnPostRunAsync(CancellationToken.None);

        Assert.Equal("lights on", processor.Transcript);
    }

    private sealed class RecordingCommandProcessor : ICommandProcessor
    {
        public string? Transcript { get; private set; }

        public Task<CommandExecutionResult> ProcessAsync(
            string? transcript,
            CommandSource source,
            CancellationToken cancellationToken = default)
        {
            Transcript = transcript;
            return Task.FromResult(new CommandExecutionResult(
                Guid.NewGuid(),
                transcript ?? string.Empty,
                TextCommandIntent.Unknown,
                "No target",
                CommandOutcome.Rejected,
                new SafeUserMessage("malformed", "I did not understand that command.")));
        }
    }

    private sealed class InMemoryPersistence : IHomeStatePersistence
    {
        public Task<HomeStateDocument?> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<HomeStateDocument?>(null);

        public Task SaveAsync(
            HomeStateDocument document,
            StoreRevision expectedRevision,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
