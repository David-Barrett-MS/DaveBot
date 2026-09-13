using System.Runtime.CompilerServices;
using DaveBrain;
using DaveHome;
using Microsoft.Extensions.Logging.Abstractions;

namespace DaveBrain.Tests;

public sealed class HueAutomationIngestionServiceTests
{
    [Fact]
    public void ListenerDebugLogKeepsOnlyTheMostRecentEvents()
    {
        var log = new HueListenerDebugLog(maximumEvents: 2);
        var hubId = Guid.NewGuid();
        log.Record(hubId, CreateDebugState("first"), "Kitchen dimmer - Button 1");
        log.Record(hubId, CreateDebugState("second"), "Kitchen dimmer - Button 2");
        log.Record(hubId, CreateDebugState("third"), "Kitchen dimmer - Button 3");

        var snapshot = log.ReadAfter(0);

        Assert.Equal(3, snapshot.LatestSequence);
        Assert.Collection(
            snapshot.Events,
            first => Assert.Equal("second", first.EventId),
            second =>
            {
                Assert.Equal("third", second.EventId);
                Assert.Equal("Kitchen dimmer - Button 3", second.DeviceName);
            });
        Assert.Equal("third", log.ReadAfter(2).Events.Single().EventId);
    }

    [Fact]
    public void HealthRequiresEveryMappedHubToBeAvailable()
    {
        var firstHub = Guid.NewGuid();
        var secondHub = Guid.NewGuid();
        var health = new HueAutomationHealth();

        health.Report(firstHub, DeviceOperationStatus.Succeeded);
        health.Report(secondHub, DeviceOperationStatus.BridgeUnavailable);

        Assert.Equal(
            DeviceOperationStatus.BridgeUnavailable,
            health.GetStatus([firstHub, secondHub]));

        health.Report(secondHub, DeviceOperationStatus.Succeeded);

        Assert.Equal(DeviceOperationStatus.Succeeded, health.GetStatus([firstHub, secondHub]));
    }

    [Fact]
    public async Task IndependentHubStreamsDeliverEventsWhileFailedHubUsesBoundedFallback()
    {
        var healthyHub = Guid.NewGuid();
        var unavailableHub = Guid.NewGuid();
        var source = new TestEventSource(healthyHub, unavailableHub);
        var automation = new RecordingAutomation();
        var service = new HueAutomationIngestionService(
            source,
            automation,
            new HueAutomationHealth(),
            NullLogger<HueAutomationIngestionService>.Instance,
            new HueAutomationIngestionOptions(
                TimeSpan.FromMilliseconds(5),
                TimeSpan.FromMilliseconds(5),
                TimeSpan.FromMilliseconds(10),
                StreamFailuresBeforeFallback: 3));

        await service.StartAsync(CancellationToken.None);
        try
        {
            await source.FallbackUsed.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await automation.TwoEventsReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        Assert.True(source.StreamAttempts[healthyHub] >= 1);
        Assert.True(source.StreamAttempts[unavailableHub] >= 3);
        Assert.True(source.Reconciliations[unavailableHub] >= 4);
        Assert.Contains(automation.Events, change => change.DeviceId == healthyHub);
        Assert.Contains(automation.Events, change => change.DeviceId == unavailableHub);
        Assert.DoesNotContain(automation.Events, change => change.IsReconciliation);
    }

    private sealed class TestEventSource(Guid healthyHub, Guid unavailableHub) : IHueSwitchEventSource
    {
        public Dictionary<Guid, int> StreamAttempts { get; } = new()
        {
            [healthyHub] = 0,
            [unavailableHub] = 0,
        };

        public Dictionary<Guid, int> Reconciliations { get; } = new()
        {
            [healthyHub] = 0,
            [unavailableHub] = 0,
        };

        public TaskCompletionSource FallbackUsed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<Guid> GetMappedHubIds() => [healthyHub, unavailableHub];

        public Task<DeviceStateSourceResult> ReconcileAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new DeviceStateSourceResult(
                DeviceOperationStatus.Succeeded,
                Array.Empty<DeviceStateChanged>()));

        public Task<DeviceStateSourceResult> ReconcileHubAsync(
            Guid hubId,
            CancellationToken cancellationToken = default)
        {
            Reconciliations[hubId]++;
            return Task.FromResult(new DeviceStateSourceResult(
                DeviceOperationStatus.Succeeded,
                [
                    new DeviceStateChanged(
                        hubId,
                        new DeviceState(null, null),
                        "historic-last-event",
                        DateTimeOffset.UtcNow,
                        IsReconciliation: true),
                ]));
        }

        public async IAsyncEnumerable<DeviceStateChanged> ReadHubEventStreamAsync(
            Guid hubId,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            StreamAttempts[hubId]++;
            if (hubId == unavailableHub)
            {
                throw new HueEventStreamException(DeviceOperationStatus.BridgeUnavailable);
            }

            yield return new DeviceStateChanged(
                hubId,
                new DeviceState(null, null),
                "stream-event",
                DateTimeOffset.UtcNow);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public Task<DeviceStateSourceResult> ReadHubStateChangesAsync(
            Guid hubId,
            CancellationToken cancellationToken = default)
        {
            FallbackUsed.TrySetResult();
            return Task.FromResult(new DeviceStateSourceResult(
                DeviceOperationStatus.Succeeded,
                [
                    new DeviceStateChanged(
                        hubId,
                        new DeviceState(null, null),
                        "fallback-event",
                        DateTimeOffset.UtcNow),
                ]));
        }

        public Task<DeviceStateSourceResult> ReadStateChangesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new DeviceStateSourceResult(
                DeviceOperationStatus.Succeeded,
                Array.Empty<DeviceStateChanged>()));
    }

    private static HueMappedSwitchState CreateDebugState(string eventId) =>
        new(
            Guid.NewGuid(),
            new DeviceState(
                null,
                null,
                Attributes: new Dictionary<string, string>
                {
                    ["event"] = "initial_press",
                    ["button"] = "1",
                }),
            eventId);

    private sealed class RecordingAutomation : IAutomationEngine
    {
        private readonly List<DeviceStateChanged> _events = [];

        public TaskCompletionSource TwoEventsReceived { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<DeviceStateChanged> Events
        {
            get
            {
                lock (_events)
                {
                    return _events.ToArray();
                }
            }
        }

        public Task<AutomationExecutionResult> HandleStateChangedAsync(
            DeviceStateChanged change,
            CancellationToken cancellationToken = default)
        {
            lock (_events)
            {
                _events.Add(change);
                if (_events.Count >= 2)
                {
                    TwoEventsReceived.TrySetResult();
                }
            }

            return Task.FromResult(new AutomationExecutionResult(
                Guid.NewGuid(),
                IsDuplicate: false,
                MatchedTriggerCount: 0,
                SuccessfulActionCount: 0,
                FailedActionCount: 0));
        }
    }
}
