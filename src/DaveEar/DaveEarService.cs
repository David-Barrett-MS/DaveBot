using System.Threading.Channels;

namespace DaveEar;

public sealed class DaveEarService(
    DaveEarConfiguration configuration,
    IAudioCapture capture,
    IVoiceActivityDetector detector,
    IAudioSegmentEncoder encoder,
    ICommandUploader uploader,
    RetrySpool spool,
    MtlsAccessTokenProvider tokenProvider,
    ICredentialStore credentialStore,
    Action<object>? statusChanged = null,
    Action<string>? verbose = null)
{
    private readonly Channel<AudioSegment> _segments = Channel.CreateBounded<AudioSegment>(
        new BoundedChannelOptions(configuration.QueueCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

    public long CapturedSegments { get; private set; }
    public long DroppedSegments { get; private set; }
    public long AcceptedSegments { get; private set; }
    public long RejectedSegments { get; private set; }
    public long HeldSegments { get; private set; }
    public bool Enabled { get; private set; } = true;
    public string LastStatus { get; private set; } = "starting";

    public async Task RunAsync(Action<string> status, CancellationToken cancellationToken)
    {
        Trace("Monitoring started.");
        var uploadTask = UploadLoopAsync(status, cancellationToken);
        try
        {
            while (!cancellationToken.IsCancellationRequested && Enabled)
            {
                try
                {
                    await CaptureOnceAsync(status, cancellationToken);
                    LastStatus = "microphone-stream-ended";
                    PublishStatus();
                    Trace("Microphone capture stream ended; reconnecting in 5 seconds.");
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    LastStatus = $"microphone-recovering:{exception.GetType().Name}";
                    PublishStatus();
                    status("Microphone unavailable; retrying without retaining audio.");
                    Trace($"Microphone capture failed with {exception.GetType().Name}: {exception.Message}");
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                }
            }
        }
        finally
        {
            _segments.Writer.TryComplete();
            await uploadTask;
            Trace("Monitoring stopped.");
        }
    }

    public async Task<HeartbeatResult> HeartbeatAsync(CancellationToken cancellationToken)
    {
        Trace("Heartbeat due.");
        var heartbeat = await uploader.HeartbeatAsync(configuration.EarId, cancellationToken);
        await tokenProvider.PersistRotatedCredentialsAsync(credentialStore, cancellationToken);
        Enabled = heartbeat.IsEnabled;
        LastStatus = heartbeat.Code;
        PublishStatus();
        Trace($"Heartbeat completed: enabled={heartbeat.IsEnabled}, code={heartbeat.Code}.");
        return heartbeat;
    }

    public object GetSafeStatus() => new
    {
        enabled = Enabled,
        status = LastStatus,
        capturedSegments = CapturedSegments,
        droppedSegments = DroppedSegments,
        acceptedSegments = AcceptedSegments,
        rejectedSegments = RejectedSegments,
        heldSegments = HeldSegments,
    };

    private async Task CaptureOnceAsync(Action<string> status, CancellationToken cancellationToken)
    {
        Trace("Microphone capture stream started.");
        var segmenter = new AudioSegmenter(configuration, detector);
        var framesSinceReport = 0;
        var millisecondsSinceReport = 0L;
        var peakSinceReport = 0f;
        var nextReport = DateTimeOffset.UtcNow.AddSeconds(5);
        await foreach (var frame in capture.CaptureAsync(cancellationToken))
        {
            framesSinceReport++;
            millisecondsSinceReport += frame.DurationMilliseconds;
            peakSinceReport = Math.Max(peakSinceReport, frame.Samples.Length == 0
                ? 0
                : frame.Samples.Max(sample => Math.Abs(sample)));
            var segment = segmenter.Push(frame);
            if (segment is not null)
            {
                CapturedSegments++;
                Trace(
                    $"Speech segment captured: id={segment.Id:D}, duration={segment.Samples.Length * 1000d / segment.SampleRate:F0} ms, " +
                    $"forced={segment.WasForced}.");
                if (!_segments.Writer.TryWrite(segment))
                {
                    DroppedSegments++;
                    status("Segment queue is full; discarded newest segment.");
                    PublishStatus();
                }
            }
            if (DateTimeOffset.UtcNow >= nextReport)
            {
                Trace(
                    $"Microphone monitoring: captured {framesSinceReport} frame(s), {millisecondsSinceReport} ms audio, " +
                    $"peak={peakSinceReport:F3}, completedSegments={CapturedSegments}.");
                framesSinceReport = 0;
                millisecondsSinceReport = 0;
                peakSinceReport = 0;
                nextReport = DateTimeOffset.UtcNow.AddSeconds(5);
            }
        }
    }

    private async Task UploadLoopAsync(Action<string> status, CancellationToken cancellationToken)
    {
        var nextHeartbeat = DateTimeOffset.UtcNow;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (DateTimeOffset.UtcNow >= nextHeartbeat)
            {
                var heartbeat = await HeartbeatAsync(cancellationToken);
                if (!heartbeat.IsEnabled)
                {
                    status($"Uploads stopped: {heartbeat.Code}.");
                    return;
                }
                nextHeartbeat = DateTimeOffset.UtcNow.AddMinutes(1);
            }

            foreach (var due in await spool.GetDueAsync(cancellationToken))
            {
                Trace($"Retrying queued segment {due.Item.SegmentId:D}, attempt {due.Item.Attempts + 1}.");
                await HandleUploadAsync(due.Item.SegmentId, due.Wav, due.Item.Attempts, status, cancellationToken);
            }

            if (_segments.Reader.TryRead(out var segment))
            {
                var wav = encoder.Encode(segment);
                Trace($"Encoding complete for segment {segment.Id:D}: {wav.Length} bytes.");
                await HandleUploadAsync(segment.Id, wav, 0, status, cancellationToken);
            }
            else
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
            }
        }
    }

    private async Task HandleUploadAsync(
        Guid segmentId,
        byte[] wav,
        int priorAttempts,
        Action<string> status,
        CancellationToken cancellationToken)
    {
        Trace($"Sending segment {segmentId:D}; attempt {priorAttempts + 1}.");
        var result = await uploader.UploadAsync(configuration.EarId, segmentId, wav, cancellationToken);
        await tokenProvider.PersistRotatedCredentialsAsync(credentialStore, cancellationToken);
        LastStatus = result.Code;
        Trace(
            $"Segment {segmentId:D} completed: disposition={result.Disposition}, code={result.Code}, " +
            $"correlation={result.ServerCorrelationId?.ToString("D") ?? "none"}.");
        switch (result.Disposition)
        {
            case UploadDisposition.Accepted:
                AcceptedSegments++;
                await spool.RemoveAsync(segmentId, cancellationToken);
                break;
            case UploadDisposition.Retry:
                if (!await spool.SaveForRetryAsync(segmentId, wav, priorAttempts + 1, result.RetryAfter, cancellationToken))
                {
                    DroppedSegments++;
                    status("Retry spool quota reached; discarded segment.");
                }
                break;
            case UploadDisposition.Rejected:
                RejectedSegments++;
                await spool.RemoveAsync(segmentId, cancellationToken);
                break;
            case UploadDisposition.AuthenticationFailure:
                Enabled = false;
                status("Uploads stopped; re-enrollment is required.");
                break;
            case UploadDisposition.Ambiguous:
                if (await spool.SaveHeldAsync(segmentId, wav, cancellationToken))
                {
                    HeldSegments++;
                    status("Upload outcome is unknown; segment held and will not be resent automatically.");
                }
                else
                {
                    DroppedSegments++;
                    status("Retry spool quota reached; discarded segment with unknown upload outcome.");
                }
                break;
        }
        PublishStatus();
    }

    private void PublishStatus()
    {
        try
        {
            statusChanged?.Invoke(GetSafeStatus());
        }
        catch
        {
            // A diagnostic write must never interrupt capture or upload.
        }
    }

    private void Trace(string message) => verbose?.Invoke(message);
}
