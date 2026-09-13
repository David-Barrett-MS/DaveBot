using System.Runtime.CompilerServices;
using DaveHome;

namespace DaveBrain;

public interface IHueSwitchEventSource : IDeviceStateSource
{
    IReadOnlyList<Guid> GetMappedHubIds();

    Task<DeviceStateSourceResult> ReconcileHubAsync(
        Guid hubId,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<DeviceStateChanged> ReadHubEventStreamAsync(
        Guid hubId,
        CancellationToken cancellationToken = default);

    Task<DeviceStateSourceResult> ReadHubStateChangesAsync(
        Guid hubId,
        CancellationToken cancellationToken = default);
}

public sealed record HueListenerDebugEvent(
    long Sequence,
    DateTimeOffset ReceivedAt,
    Guid HubId,
    Guid DeviceId,
    string DeviceName,
    string EventId,
    IReadOnlyDictionary<string, string>? Attributes);

public sealed record HueListenerDebugSnapshot(
    long LatestSequence,
    IReadOnlyList<HueListenerDebugEvent> Events);

public sealed class HueListenerDebugLog
{
    private const int DefaultMaximumEvents = 100;
    private readonly object _gate = new();
    private readonly Queue<HueListenerDebugEvent> _events = new();
    private readonly int _maximumEvents;
    private long _latestSequence;

    public HueListenerDebugLog(int maximumEvents = DefaultMaximumEvents)
    {
        if (maximumEvents < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEvents));
        }

        _maximumEvents = maximumEvents;
    }

    public void Record(Guid hubId, HueMappedSwitchState state, string deviceName)
    {
        lock (_gate)
        {
            var attributes = state.State.Attributes?.ToDictionary(
                attribute => attribute.Key,
                attribute => attribute.Value,
                StringComparer.Ordinal);
            _events.Enqueue(new HueListenerDebugEvent(
                ++_latestSequence,
                DateTimeOffset.UtcNow,
                hubId,
                state.DeviceId,
                deviceName,
                state.EventId ?? string.Empty,
                attributes));
            while (_events.Count > _maximumEvents)
            {
                _events.Dequeue();
            }
        }
    }

    public HueListenerDebugSnapshot ReadAfter(long sequence)
    {
        lock (_gate)
        {
            return new HueListenerDebugSnapshot(
                _latestSequence,
                _events.Where(@event => @event.Sequence > sequence).ToArray());
        }
    }
}

public sealed class HueSwitchStateSource(
    HueHubService hueHubs,
    HueListenerDebugLog? listenerDebugLog = null) : IHueSwitchEventSource
{
    private const int MaximumTrackedEventIds = HomeStateDocument.MaximumHueAutomationMappings;
    private readonly object _gate = new();
    private readonly HueListenerDebugLog _listenerDebugLog = listenerDebugLog ?? new HueListenerDebugLog();
    private readonly Dictionary<Guid, string> _lastEventIds = new();
    private readonly Dictionary<Guid, DeviceState> _lastStates = new();
    private readonly HashSet<Guid> _reconciliationRequiredHubs = new();
    private bool _reconciliationRequired = true;

    public IReadOnlyList<Guid> GetMappedHubIds() => hueHubs.GetMappedSwitchHubIds();

    public async Task<DeviceStateSourceResult> ReconcileAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await hueHubs.ReadMappedSwitchStatesAsync(cancellationToken);
        if (!result.Succeeded)
        {
            lock (_gate)
            {
                _reconciliationRequired = true;
            }

            return new DeviceStateSourceResult(result.Status, Array.Empty<DeviceStateChanged>(), result.Detail);
        }

        return SeedReconciledEvents(result.Switches, hubId: null, result.Detail);
    }

    public async Task<DeviceStateSourceResult> ReconcileHubAsync(
        Guid hubId,
        CancellationToken cancellationToken = default)
    {
        var result = await hueHubs.ReadMappedSwitchStatesAsync(hubId, cancellationToken);
        if (!result.Succeeded)
        {
            lock (_gate)
            {
                _reconciliationRequiredHubs.Add(hubId);
            }

            return new DeviceStateSourceResult(result.Status, Array.Empty<DeviceStateChanged>(), result.Detail);
        }

        return SeedReconciledEvents(result.Switches, hubId, result.Detail);
    }

    public async IAsyncEnumerable<DeviceStateChanged> ReadHubEventStreamAsync(
        Guid hubId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var switchState in hueHubs.ReadMappedSwitchEventsAsync(hubId, cancellationToken))
        {
            var deviceName = hueHubs.GetMappedHueDevices()
                .SingleOrDefault(device => device.Id == switchState.DeviceId)
                ?.Name ?? switchState.DeviceId.ToString("D");
            _listenerDebugLog.Record(hubId, switchState, deviceName);
            var change = AcceptEvent(switchState);
            await hueHubs.PersistMappedSwitchStateAsync(switchState, cancellationToken);
            if (change is not null)
            {
                yield return change;
            }
        }
    }

    public async Task<DeviceStateSourceResult> ReadHubStateChangesAsync(
        Guid hubId,
        CancellationToken cancellationToken = default)
    {
        var result = await hueHubs.ReadMappedSwitchStatesAsync(hubId, cancellationToken);
        if (!result.Succeeded)
        {
            lock (_gate)
            {
                _reconciliationRequiredHubs.Add(hubId);
            }

            return new DeviceStateSourceResult(result.Status, Array.Empty<DeviceStateChanged>(), result.Detail);
        }

        lock (_gate)
        {
            if (_reconciliationRequiredHubs.Contains(hubId))
            {
                return SeedReconciledEventsLocked(result.Switches, hubId, result.Detail);
            }
        }

        return new DeviceStateSourceResult(
            DeviceOperationStatus.Succeeded,
            AcceptEvents(result.Switches),
            result.Detail);
    }

    public async Task<DeviceStateSourceResult> ReadStateChangesAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await hueHubs.ReadMappedSwitchStatesAsync(cancellationToken);
        if (!result.Succeeded)
        {
            lock (_gate)
            {
                _reconciliationRequired = true;
            }

            return new DeviceStateSourceResult(result.Status, Array.Empty<DeviceStateChanged>(), result.Detail);
        }

        lock (_gate)
        {
            if (_reconciliationRequired)
            {
                return SeedReconciledEventsLocked(result.Switches, hubId: null, result.Detail);
            }
        }

        return new DeviceStateSourceResult(
            DeviceOperationStatus.Succeeded,
            AcceptEvents(result.Switches),
            result.Detail);
    }

    private DeviceStateSourceResult SeedReconciledEvents(
        IReadOnlyList<HueMappedSwitchState> switches,
        Guid? hubId,
        string? detail)
    {
        lock (_gate)
        {
            return SeedReconciledEventsLocked(switches, hubId, detail);
        }
    }

    private DeviceStateSourceResult SeedReconciledEventsLocked(
        IReadOnlyList<HueMappedSwitchState> switches,
        Guid? hubId,
        string? detail)
    {
        PruneTrackedEventIdsLocked();
        foreach (var switchState in switches)
        {
            if (!string.IsNullOrWhiteSpace(switchState.EventId))
            {
                _lastEventIds[switchState.DeviceId] = switchState.EventId;
                _lastStates[switchState.DeviceId] = switchState.State;
            }
        }

        if (hubId is { } reconciledHubId)
        {
            _reconciliationRequiredHubs.Remove(reconciledHubId);
        }
        else
        {
            _reconciliationRequired = false;
            _reconciliationRequiredHubs.Clear();
        }

        return new DeviceStateSourceResult(
            DeviceOperationStatus.Succeeded,
            switches
                .Where(switchState => !string.IsNullOrWhiteSpace(switchState.EventId))
                .Select(switchState => new DeviceStateChanged(
                    switchState.DeviceId,
                    switchState.State,
                    switchState.EventId!,
                    DateTimeOffset.UtcNow,
                    IsReconciliation: true))
                .ToArray(),
            detail);
    }

    private IReadOnlyList<DeviceStateChanged> AcceptEvents(
        IReadOnlyList<HueMappedSwitchState> switches)
    {
        lock (_gate)
        {
            var changes = new List<DeviceStateChanged>();
            foreach (var switchState in switches)
            {
                var change = AcceptEventLocked(switchState);
                if (change is not null)
                {
                    changes.Add(change);
                }
            }

            return changes;
        }
    }

    private DeviceStateChanged? AcceptEvent(HueMappedSwitchState switchState)
    {
        lock (_gate)
        {
            return AcceptEventLocked(switchState);
        }
    }

    private DeviceStateChanged? AcceptEventLocked(HueMappedSwitchState switchState)
    {
        if (string.IsNullOrWhiteSpace(switchState.EventId))
        {
            return null;
        }

        if (!_lastEventIds.TryGetValue(switchState.DeviceId, out var lastEventId))
        {
            var mappedDevice = hueHubs.GetMappedHueDevices().SingleOrDefault(
                device => device.Id == switchState.DeviceId);
            var persistedEventId = mappedDevice?.ExternalResourceId is { } resourceId &&
                mappedDevice.State?.Attributes?.TryGetValue("eventTime", out var eventTime) is true
                ? $"{resourceId}:{eventTime}"
                : null;
            if (_lastEventIds.Count >= MaximumTrackedEventIds)
            {
                PruneTrackedEventIdsLocked();
            }

            if (_lastEventIds.Count < MaximumTrackedEventIds)
            {
                _lastEventIds[switchState.DeviceId] = switchState.EventId;
                _lastStates[switchState.DeviceId] = switchState.State;
            }

            return string.Equals(persistedEventId, switchState.EventId, StringComparison.Ordinal) ||
                   (mappedDevice?.DeviceType == "hue.motion-sensor" &&
                    StateEquals(mappedDevice.State, switchState.State))
                ? null
                : new DeviceStateChanged(
                    switchState.DeviceId,
                    switchState.State,
                    switchState.EventId,
                    DateTimeOffset.UtcNow);
        }

        if (lastEventId == switchState.EventId)
        {
            return null;
        }

        var currentMappedDevice = hueHubs.GetMappedHueDevices().SingleOrDefault(
            device => device.Id == switchState.DeviceId);
        if (currentMappedDevice?.DeviceType == "hue.motion-sensor" &&
            _lastStates.TryGetValue(switchState.DeviceId, out var lastState) &&
            StateEquals(lastState, switchState.State))
        {
            _lastEventIds[switchState.DeviceId] = switchState.EventId;
            return null;
        }

        _lastEventIds[switchState.DeviceId] = switchState.EventId;
        _lastStates[switchState.DeviceId] = switchState.State;
        return new DeviceStateChanged(
            switchState.DeviceId,
            switchState.State,
            switchState.EventId,
            DateTimeOffset.UtcNow);
    }

    private void PruneTrackedEventIdsLocked()
    {
        var mappedDeviceIds = hueHubs.GetMappedHueDevices()
            .Where(device => device.Enabled &&
                device.DeviceType is "hue.switch" or "hue.motion-sensor")
            .Select(device => device.Id)
            .ToHashSet();
        foreach (var deviceId in _lastEventIds.Keys
            .Where(deviceId => !mappedDeviceIds.Contains(deviceId))
            .ToArray())
        {
            _lastEventIds.Remove(deviceId);
            _lastStates.Remove(deviceId);
        }
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
}

public sealed class HueAutomationHealth
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, DeviceOperationStatus>
        _hubStatuses = new();

    public DeviceOperationStatus GetStatus(IReadOnlyCollection<Guid> requiredHubIds)
    {
        foreach (var hubId in requiredHubIds)
        {
            if (!_hubStatuses.TryGetValue(hubId, out var status))
            {
                return DeviceOperationStatus.BridgeUnavailable;
            }

            if (status != DeviceOperationStatus.Succeeded)
            {
                return status;
            }
        }

        return DeviceOperationStatus.Succeeded;
    }

    public void Report(Guid hubId, DeviceOperationStatus status) =>
        _hubStatuses[hubId] = status;

    public void Remove(Guid hubId) => _hubStatuses.TryRemove(hubId, out _);
}

public sealed record HueAutomationIngestionOptions(
    TimeSpan HubDiscoveryInterval,
    TimeSpan FallbackPollInterval,
    TimeSpan MaximumRetryDelay,
    int StreamFailuresBeforeFallback)
{
    public static HueAutomationIngestionOptions Default { get; } = new(
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(30),
        StreamFailuresBeforeFallback: 3);
}

public sealed class HueAutomationIngestionService(
    IHueSwitchEventSource source,
    IAutomationEngine automation,
    HueAutomationHealth health,
    ILogger<HueAutomationIngestionService> logger,
    HueAutomationIngestionOptions? options = null) : BackgroundService
{
    private readonly HueAutomationIngestionOptions _options = options ?? HueAutomationIngestionOptions.Default;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workers = new Dictionary<Guid, HubWorker>();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var hubIds = source.GetMappedHubIds().ToHashSet();
                foreach (var worker in workers
                    .Where(entry => !hubIds.Contains(entry.Key))
                    .ToArray())
                {
                    worker.Value.Cancellation.Cancel();
                    health.Remove(worker.Key);
                }

                foreach (var completed in workers
                    .Where(entry => entry.Value.Task.IsCompleted)
                    .Select(entry => entry.Key)
                    .ToArray())
                {
                    workers[completed].Cancellation.Dispose();
                    workers.Remove(completed);
                }

                foreach (var hubId in hubIds)
                {
                    if (workers.ContainsKey(hubId))
                    {
                        continue;
                    }

                    var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    workers.Add(hubId, new HubWorker(
                        cancellation,
                        RunHubAsync(hubId, cancellation.Token)));
                }

                await Task.Delay(_options.HubDiscoveryInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            foreach (var worker in workers.Values)
            {
                worker.Cancellation.Cancel();
            }

            try
            {
                await Task.WhenAll(workers.Values.Select(worker => worker.Task));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
            foreach (var worker in workers.Values)
            {
                worker.Cancellation.Dispose();
            }
        }
    }

    private async Task RunHubAsync(Guid hubId, CancellationToken stoppingToken)
    {
        var streamFailureCount = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var reconciliation = await ReconcileHubAsync(hubId, stoppingToken);
            if (!reconciliation)
            {
                await Task.Delay(GetRetryDelay(++streamFailureCount), stoppingToken);
                continue;
            }

            try
            {
                logger.LogInformation("Connecting to the Hue event stream for hub {HubId}.", hubId);
                await foreach (var change in source.ReadHubEventStreamAsync(hubId, stoppingToken))
                {
                    streamFailureCount = 0;
                    health.Report(hubId, DeviceOperationStatus.Succeeded);
                    await automation.HandleStateChangedAsync(change, stoppingToken);
                }

                throw new HueEventStreamException(
                    DeviceOperationStatus.BridgeUnavailable,
                    "The Hue event stream ended.");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (HueEventStreamException exception)
            {
                streamFailureCount++;
                health.Report(hubId, exception.Status);
                logger.LogWarning(
                    "Hue event stream for hub {HubId} is unavailable with status {Status}.",
                    hubId,
                    exception.Status);
            }
            catch (Exception exception)
            {
                streamFailureCount++;
                health.Report(hubId, DeviceOperationStatus.BridgeUnavailable);
                logger.LogWarning(
                    "Hue event stream for hub {HubId} failed with {ExceptionType}.",
                    hubId,
                    exception.GetType().Name);
            }

            var retryDelay = GetRetryDelay(streamFailureCount);
            if (streamFailureCount >= _options.StreamFailuresBeforeFallback)
            {
                var fallbackBaseline = await ReconcileHubAsync(hubId, stoppingToken);
                if (fallbackBaseline)
                {
                    logger.LogWarning(
                        "Hue event stream for hub {HubId} has failed {Failures} times; using the bounded one-second polling fallback until the next stream retry.",
                        hubId,
                        streamFailureCount);
                    try
                    {
                        await ReadFallbackUntilRetryAsync(hubId, retryDelay, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception exception)
                    {
                        health.Report(hubId, DeviceOperationStatus.BridgeUnavailable);
                        logger.LogWarning(
                            "Hue polling fallback for hub {HubId} failed with {ExceptionType}.",
                            hubId,
                            exception.GetType().Name);
                    }
                    continue;
                }
            }

            await Task.Delay(retryDelay, stoppingToken);
        }
    }

    private async Task<bool> ReconcileHubAsync(Guid hubId, CancellationToken cancellationToken)
    {
        try
        {
            var result = await source.ReconcileHubAsync(hubId, cancellationToken);
            if (result.Succeeded)
            {
                health.Report(hubId, DeviceOperationStatus.Succeeded);
                logger.LogInformation(
                    "Hue event-source state for hub {HubId} was reconciled before accepting new events.",
                    hubId);
                return true;
            }

            health.Report(hubId, result.Status);
            logger.LogWarning(
                "Hue event-source reconciliation for hub {HubId} is unavailable with status {Status}.",
                hubId,
                result.Status);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            health.Report(hubId, DeviceOperationStatus.BridgeUnavailable);
            logger.LogWarning(
                "Hue event-source reconciliation for hub {HubId} failed with {ExceptionType}.",
                hubId,
                exception.GetType().Name);
        }

        return false;
    }

    private async Task ReadFallbackUntilRetryAsync(
        Guid hubId,
        TimeSpan retryDelay,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + retryDelay;
        while (DateTimeOffset.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(
                TimeSpan.FromMilliseconds(Math.Min(
                    _options.FallbackPollInterval.TotalMilliseconds,
                    Math.Max(1, (deadline - DateTimeOffset.UtcNow).TotalMilliseconds))),
                cancellationToken);
            var result = await source.ReadHubStateChangesAsync(hubId, cancellationToken);
            if (!result.Succeeded)
            {
                health.Report(hubId, result.Status);
                logger.LogWarning(
                    "Hue polling fallback for hub {HubId} is unavailable with status {Status}.",
                    hubId,
                    result.Status);
                continue;
            }

            health.Report(hubId, DeviceOperationStatus.Succeeded);
            foreach (var change in result.Changes)
            {
                if (!change.IsReconciliation)
                {
                    await automation.HandleStateChangedAsync(change, cancellationToken);
                }
            }
        }
    }

    private TimeSpan GetRetryDelay(int failureCount) =>
        TimeSpan.FromSeconds(Math.Min(
            _options.MaximumRetryDelay.TotalSeconds,
            Math.Pow(2, Math.Min(Math.Max(failureCount - 1, 0), 5))));

    private sealed record HubWorker(CancellationTokenSource Cancellation, Task Task);
}
