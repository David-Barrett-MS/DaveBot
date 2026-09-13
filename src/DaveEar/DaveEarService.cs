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
    Action<object>? statusChanged = null)
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
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                }
            }
        }
        finally
        {
            _segments.Writer.TryComplete();
            await uploadTask;
        }
    }

    public async Task<HeartbeatResult> HeartbeatAsync(CancellationToken cancellationToken)
    {
        var heartbeat = await uploader.HeartbeatAsync(configuration.EarId, cancellationToken);
        await tokenProvider.PersistRotatedCredentialsAsync(credentialStore, cancellationToken);
        Enabled = heartbeat.IsEnabled;
        LastStatus = heartbeat.Code;
        PublishStatus();
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
        var segmenter = new AudioSegmenter(configuration, detector);
        await foreach (var frame in capture.CaptureAsync(cancellationToken))
        {
            var segment = segmenter.Push(frame);
            if (segment is not null)
            {
                CapturedSegments++;
                if (!_segments.Writer.TryWrite(segment))
                {
                    DroppedSegments++;
                    status("Segment queue is full; discarded newest segment.");
                    PublishStatus();
                }
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
                await HandleUploadAsync(due.Item.SegmentId, due.Wav, due.Item.Attempts, status, cancellationToken);
            }

            if (_segments.Reader.TryRead(out var segment))
            {
                var wav = encoder.Encode(segment);
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
        var result = await uploader.UploadAsync(configuration.EarId, segmentId, wav, cancellationToken);
        await tokenProvider.PersistRotatedCredentialsAsync(credentialStore, cancellationToken);
        LastStatus = result.Code;
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
}
