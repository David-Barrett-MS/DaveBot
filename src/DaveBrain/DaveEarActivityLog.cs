using DaveIdentity;
using Microsoft.Extensions.Hosting;

namespace DaveBrain;

public sealed record DaveEarActivityEntry(
    long Sequence,
    DateTimeOffset OccurredUtc,
    string Event,
    Guid EarId,
    string EarName,
    string Details);

public sealed class DaveEarActivityLog
{
    private const int MaximumEntries = 250;
    private static readonly TimeSpan DisconnectAfter = TimeSpan.FromMinutes(2);
    private readonly object gate = new();
    private readonly Dictionary<Guid, ConnectedEar> connectedEars = [];
    private readonly Queue<DaveEarActivityEntry> entries = new();
    private long sequence;

    public void RecordRegistered(EarRecord ear) =>
        Write("Registered", ear, $"Location: {ear.LocationId:D}");

    public void RecordConnected(EarRecord ear)
    {
        lock (gate)
        {
            var isNewConnection = !connectedEars.ContainsKey(ear.Id);
            connectedEars[ear.Id] = new ConnectedEar(ear, DateTimeOffset.UtcNow);
            if (isNewConnection)
            {
                WriteUnsafe("Connected", ear, "Authenticated activity received.");
            }
        }
    }

    public void RecordCommandReceived(EarRecord ear, string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        Write("Command received", ear, command);
    }

    public void RecordDeleted(EarRecord ear)
    {
        lock (gate)
        {
            connectedEars.Remove(ear.Id);
            WriteUnsafe("Deleted", ear, "Registration permanently removed by an administrator.");
        }
    }

    public void RecordDisconnectedEars(DateTimeOffset now)
    {
        lock (gate)
        {
            foreach (var connectedEar in connectedEars.Values
                         .Where(ear => now - ear.LastActivityUtc >= DisconnectAfter)
                         .ToArray())
            {
                connectedEars.Remove(connectedEar.Ear.Id);
                WriteUnsafe(
                    "Disconnected",
                    connectedEar.Ear,
                    $"No authenticated activity for {DisconnectAfter.TotalMinutes:0} minutes.");
            }
        }
    }

    public IReadOnlyList<DaveEarActivityEntry> Read()
    {
        lock (gate)
        {
            return entries.ToArray();
        }
    }

    private void Write(string @event, EarRecord ear, string details)
    {
        lock (gate)
        {
            WriteUnsafe(@event, ear, details);
        }
    }

    private void WriteUnsafe(string @event, EarRecord ear, string details)
    {
        entries.Enqueue(new DaveEarActivityEntry(
            ++sequence,
            DateTimeOffset.UtcNow,
            @event,
            ear.Id,
            ear.Name,
            details.Length <= 4_096 ? details : details[..4_096] + "\n[truncated]"));
        while (entries.Count > MaximumEntries)
        {
            entries.Dequeue();
        }
    }

    private sealed record ConnectedEar(EarRecord Ear, DateTimeOffset LastActivityUtc);
}

public sealed class DaveEarActivityMonitor(DaveEarActivityLog activityLog) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                activityLog.RecordDisconnectedEars(DateTimeOffset.UtcNow);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
