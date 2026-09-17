using DaveIdentity;
using Microsoft.Extensions.Hosting;

namespace DaveBrain;

public sealed record DaveEarActivityEntry(
    long Sequence,
    DateTimeOffset OccurredUtc,
    string Event,
    Guid EarId,
    string EarName,
    string Details,
    string? Transcript = null);

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
        Write("Command received", ear, "Speech command transcribed.", command);
    }

    public void RecordSpeechUploadReceived(EarRecord ear, Guid? segmentId) =>
        Write(
            "Speech upload received",
            ear,
            segmentId is null ? "Segment ID was not supplied." : $"Segment: {segmentId:D}");

    public void RecordSpeechTranscriptionQueued(EarRecord ear, Guid? segmentId) =>
        Write(
            "Speech transcription queued",
            ear,
            segmentId is null
                ? "Validated audio is awaiting local transcription."
                : $"Segment: {segmentId:D}; awaiting local transcription.");

    public void RecordSpeechTranscriptionOutcome(
        EarRecord ear,
        TranscriptionStatus status,
        string errorCode) =>
        Write("Speech transcription completed", ear, $"Status: {status}; code: {errorCode}.");

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

    private void Write(string @event, EarRecord ear, string details, string? transcript = null)
    {
        lock (gate)
        {
            WriteUnsafe(@event, ear, details, transcript);
        }
    }

    private void WriteUnsafe(string @event, EarRecord ear, string details, string? transcript = null)
    {
        entries.Enqueue(new DaveEarActivityEntry(
            ++sequence,
            DateTimeOffset.UtcNow,
            @event,
            ear.Id,
            ear.Name,
            Truncate(details),
            transcript is null ? null : Truncate(transcript)));
        while (entries.Count > MaximumEntries)
        {
            entries.Dequeue();
        }
    }

    private static string Truncate(string value) =>
        value.Length <= 4_096 ? value : value[..4_096] + "\n[truncated]";

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
