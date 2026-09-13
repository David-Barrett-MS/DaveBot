namespace DaveEar;

public sealed record AudioFrame(float[] Samples, int SampleRate, DateTimeOffset CapturedUtc)
{
    public int DurationMilliseconds => checked((int)(Samples.Length * 1000L / SampleRate));
}

public sealed record AudioSegment(Guid Id, float[] Samples, int SampleRate, DateTimeOffset StartedUtc, bool WasForced);

public interface IAudioCapture : IAsyncDisposable
{
    IAsyncEnumerable<AudioFrame> CaptureAsync(CancellationToken cancellationToken);
    Task<AudioCaptureDiagnostic> DiagnoseAsync(CancellationToken cancellationToken);
}

public sealed record AudioCaptureDiagnostic(bool IsAvailable, string Code);

public interface IVoiceActivityDetector
{
    bool IsVoice(AudioFrame frame);
}

public interface IAudioSegmentEncoder
{
    byte[] Encode(AudioSegment segment);
}

public interface ICommandUploader
{
    Task<UploadResult> UploadAsync(Guid earId, Guid segmentId, ReadOnlyMemory<byte> wav, CancellationToken cancellationToken);
    Task<HeartbeatResult> HeartbeatAsync(Guid earId, CancellationToken cancellationToken);
}

public enum UploadDisposition
{
    Accepted,
    Retry,
    Rejected,
    AuthenticationFailure,
    Ambiguous,
}

public sealed record UploadResult(
    UploadDisposition Disposition,
    string Code,
    Guid? ServerCorrelationId = null,
    TimeSpan? RetryAfter = null);

public sealed record HeartbeatResult(bool IsEnabled, string Code);
