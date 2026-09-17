using System.Buffers.Binary;
using System.Diagnostics;
using System.Threading.Channels;

namespace DaveBrain;

public sealed record SpeechTranscriptionOptions(
    string Model,
    TimeSpan Timeout,
    int MaximumRequestBytes,
    TimeSpan MaximumDuration,
    int QueueCapacity,
    TimeSpan QueueWaitTimeout)
{
    public const string NemotronSpeechStreamingEnglishModel = "nemotron-speech-streaming-en-0.6b";
    public const int BytesPerSecond = 32_000;

    public static SpeechTranscriptionOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("DaveBot:SpeechTranscription");
        return new SpeechTranscriptionOptions(
            section["Model"] ?? NemotronSpeechStreamingEnglishModel,
            TimeSpan.FromSeconds(section.GetValue("TimeoutSeconds", 60)),
            section.GetValue("MaximumRequestBytes", 1_000_000),
            TimeSpan.FromSeconds(section.GetValue("MaximumDurationSeconds", 30)),
            section.GetValue("QueueCapacity", 2),
            TimeSpan.FromSeconds(section.GetValue("QueueWaitTimeoutSeconds", 65)));
    }

    public void Validate()
    {
        if (!string.Equals(Model, NemotronSpeechStreamingEnglishModel, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"DaveBot only permits the {NemotronSpeechStreamingEnglishModel} transcription model.");
        }

        if (Timeout <= TimeSpan.Zero || QueueWaitTimeout <= TimeSpan.Zero ||
            MaximumRequestBytes is < 44 or > 10_000_000 ||
            MaximumDuration <= TimeSpan.Zero || MaximumDuration > TimeSpan.FromMinutes(5) ||
            QueueCapacity is < 1 or > 32)
        {
            throw new InvalidOperationException("Speech transcription limits are outside their safe bounds.");
        }
    }

}

public enum WavValidationFailure
{
    None,
    Empty,
    TooLarge,
    Malformed,
    Truncated,
    UnsupportedFormat,
    TooLong,
}

public sealed record ValidatedWav(int DataOffset, int DataLength, TimeSpan Duration);

public sealed record WavValidationResult(WavValidationFailure Failure, ValidatedWav? Audio = null)
{
    public bool Succeeded => Failure == WavValidationFailure.None;

    public static WavValidationResult Valid(int dataOffset, int dataLength, TimeSpan duration) =>
        new(WavValidationFailure.None, new ValidatedWav(dataOffset, dataLength, duration));
}

public sealed class WavAudioValidator(SpeechTranscriptionOptions options)
{
    public WavValidationResult Validate(ReadOnlySpan<byte> audio)
    {
        if (audio.IsEmpty)
        {
            return new WavValidationResult(WavValidationFailure.Empty);
        }

        if (audio.Length > options.MaximumRequestBytes)
        {
            return new WavValidationResult(WavValidationFailure.TooLarge);
        }

        if (audio.Length < 12)
        {
            return new WavValidationResult(WavValidationFailure.Truncated);
        }

        if (!audio[..4].SequenceEqual("RIFF"u8) || !audio.Slice(8, 4).SequenceEqual("WAVE"u8))
        {
            return new WavValidationResult(WavValidationFailure.Malformed);
        }

        var riffLength = BinaryPrimitives.ReadUInt32LittleEndian(audio.Slice(4, 4));
        if (riffLength > int.MaxValue || riffLength + 8 != (uint)audio.Length)
        {
            return new WavValidationResult(WavValidationFailure.Truncated);
        }

        var foundFormat = false;
        var foundData = false;
        var offset = 12;
        var dataOffset = 0;
        var dataLength = 0;
        ushort channels = 0;
        uint sampleRate = 0;
        uint byteRate = 0;
        ushort bitsPerSample = 0;
        ushort blockAlign = 0;

        while (offset < audio.Length)
        {
            if (audio.Length - offset < 8)
            {
                return new WavValidationResult(WavValidationFailure.Truncated);
            }

            var chunkLength = BinaryPrimitives.ReadUInt32LittleEndian(audio.Slice(offset + 4, 4));
            var chunkDataOffset = offset + 8;
            if (chunkLength > int.MaxValue || chunkLength > audio.Length - chunkDataOffset)
            {
                return new WavValidationResult(WavValidationFailure.Truncated);
            }

            var paddedLength = (long)chunkLength + (chunkLength & 1);
            if (paddedLength > audio.Length - chunkDataOffset)
            {
                return new WavValidationResult(WavValidationFailure.Truncated);
            }

            var id = audio.Slice(offset, 4);
            if (id.SequenceEqual("fmt "u8))
            {
                if (foundFormat || chunkLength < 16)
                {
                    return new WavValidationResult(WavValidationFailure.Malformed);
                }

                var format = BinaryPrimitives.ReadUInt16LittleEndian(audio.Slice(chunkDataOffset, 2));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(audio.Slice(chunkDataOffset + 2, 2));
                sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(audio.Slice(chunkDataOffset + 4, 4));
                byteRate = BinaryPrimitives.ReadUInt32LittleEndian(audio.Slice(chunkDataOffset + 8, 4));
                blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(audio.Slice(chunkDataOffset + 12, 2));
                bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(audio.Slice(chunkDataOffset + 14, 2));
                if (format != 1 || channels != 1 || sampleRate != 16_000 ||
                    byteRate != SpeechTranscriptionOptions.BytesPerSecond ||
                    bitsPerSample != 16 || blockAlign != 2)
                {
                    return new WavValidationResult(WavValidationFailure.UnsupportedFormat);
                }

                foundFormat = true;
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (foundData)
                {
                    return new WavValidationResult(WavValidationFailure.Malformed);
                }

                foundData = true;
                dataOffset = chunkDataOffset;
                dataLength = (int)chunkLength;
            }

            offset = checked((int)(chunkDataOffset + paddedLength));
        }

        if (offset != audio.Length || !foundFormat || !foundData || dataLength == 0)
        {
            return new WavValidationResult(WavValidationFailure.Malformed);
        }

        if (dataLength % blockAlign != 0)
        {
            return new WavValidationResult(WavValidationFailure.Malformed);
        }

        var duration = TimeSpan.FromSeconds((double)dataLength / (sampleRate * blockAlign));
        return duration > options.MaximumDuration
            ? new WavValidationResult(WavValidationFailure.TooLong)
            : WavValidationResult.Valid(dataOffset, dataLength, duration);
    }
}

public enum TranscriptionStatus
{
    Succeeded,
    NoSpeech,
    TimedOut,
    Unavailable,
    Failed,
    Canceled,
}

public sealed record TranscriptionResult(TranscriptionStatus Status, string? Transcript = null)
{
    public static TranscriptionResult Success(string transcript) =>
        new(TranscriptionStatus.Succeeded, transcript);
}

public sealed record TranscriptionDependencyStatus(
    bool IsAvailable,
    string Code,
    string? ModelId = null);

public interface ITranscriptionService
{
    Task<TranscriptionResult> TranscribeAsync(
        ReadOnlyMemory<byte> wavAudio,
        CancellationToken cancellationToken = default);

    Task<TranscriptionDependencyStatus> GetDependencyStatusAsync(
        CancellationToken cancellationToken = default);
}

public enum SpeechQueueStatus
{
    Completed,
    Overloaded,
    Canceled,
}

public sealed record SpeechQueueResult(
    SpeechQueueStatus Status,
    Guid CorrelationId,
    TranscriptionResult? Transcription = null);

public sealed class SpeechTranscriptionQueue(
    ITranscriptionService transcriptionService,
    SpeechTranscriptionOptions options,
    ILogger<SpeechTranscriptionQueue> logger) : BackgroundService
{
    private readonly Channel<QueuedSpeech> _queue = Channel.CreateBounded<QueuedSpeech>(
        new BoundedChannelOptions(options.QueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

    public async Task<SpeechQueueResult> EnqueueAsync(
        byte[] audio,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audio);
        var correlationId = Guid.NewGuid();
        var queued = new QueuedSpeech(correlationId, audio, cancellationToken);
        if (!_queue.Writer.TryWrite(queued))
        {
            return new SpeechQueueResult(SpeechQueueStatus.Overloaded, correlationId);
        }

        logger.LogInformation(
            "Speech transcription {CorrelationId} queued with {AudioBytes} audio bytes.",
            correlationId,
            audio.Length);
        try
        {
            return new SpeechQueueResult(
                SpeechQueueStatus.Completed,
                correlationId,
                await queued.Completion.Task.WaitAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new SpeechQueueResult(
                SpeechQueueStatus.Canceled,
                correlationId,
                new TranscriptionResult(TranscriptionStatus.Canceled));
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (await _queue.Reader.WaitToReadAsync(stoppingToken))
            {
                while (_queue.Reader.TryRead(out var queued))
                {
                    await ProcessAsync(queued, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            while (_queue.Reader.TryRead(out var queued))
            {
                queued.Completion.TrySetResult(new TranscriptionResult(TranscriptionStatus.Canceled));
            }
        }
    }

    private async Task ProcessAsync(QueuedSpeech queued, CancellationToken stoppingToken)
    {
        if (queued.RequestCancellation.IsCancellationRequested)
        {
            logger.LogInformation(
                "Speech transcription {CorrelationId} was canceled before processing.",
                queued.CorrelationId);
            queued.Completion.TrySetResult(new TranscriptionResult(TranscriptionStatus.Canceled));
            return;
        }

        var elapsed = Stopwatch.StartNew();
        logger.LogInformation("Speech transcription {CorrelationId} started.", queued.CorrelationId);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            stoppingToken,
            queued.RequestCancellation);
        timeout.CancelAfter(options.QueueWaitTimeout);
        TranscriptionResult result;
        try
        {
            result = await transcriptionService.TranscribeAsync(queued.Audio, timeout.Token)
                .WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (queued.RequestCancellation.IsCancellationRequested ||
                                                stoppingToken.IsCancellationRequested)
        {
            result = new TranscriptionResult(TranscriptionStatus.Canceled);
        }
        catch (OperationCanceledException)
        {
            result = new TranscriptionResult(TranscriptionStatus.TimedOut);
        }
        catch (HttpRequestException)
        {
            result = new TranscriptionResult(TranscriptionStatus.Unavailable);
        }
        catch (InvalidOperationException)
        {
            result = new TranscriptionResult(TranscriptionStatus.Failed);
        }

        queued.Completion.TrySetResult(result);
        logger.LogInformation(
            "Speech transcription {CorrelationId} completed with {Status} after {ElapsedMilliseconds} ms.",
            queued.CorrelationId,
            result.Status,
            elapsed.ElapsedMilliseconds);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _queue.Writer.TryComplete();
        return base.StopAsync(cancellationToken);
    }

    private sealed class QueuedSpeech(Guid correlationId, byte[] audio, CancellationToken requestCancellation)
    {
        public Guid CorrelationId { get; } = correlationId;
        public byte[] Audio { get; } = audio;
        public CancellationToken RequestCancellation { get; } = requestCancellation;
        public TaskCompletionSource<TranscriptionResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

public enum SpeechCommandStatus
{
    Completed,
    NoSpeech,
    TranscriptionFailed,
    DependencyUnavailable,
    TimedOut,
    Canceled,
    Overloaded,
    InvalidAudio,
}

public sealed record SpeechCommandResponse(
    Guid CorrelationId,
    SpeechCommandStatus Status,
    string? Transcript = null,
    DaveHome.CommandExecutionResult? Command = null,
    string? ErrorCode = null,
    TranscriptionStatus? TranscriptionOutcome = null);

public sealed record SpeechCommandAuditEntry(
    Guid CorrelationId,
    DateTimeOffset Timestamp,
    string Source,
    SpeechCommandStatus Status,
    Guid? CommandCorrelationId = null);

public interface ISpeechCommandAuditStore
{
    Task WriteAsync(SpeechCommandAuditEntry entry, CancellationToken cancellationToken = default);
}

public sealed class InMemorySpeechCommandAuditStore : ISpeechCommandAuditStore
{
    private const int MaximumEntries = 200;
    private readonly object _sync = new();
    private readonly Queue<SpeechCommandAuditEntry> _entries = new();

    public IReadOnlyList<SpeechCommandAuditEntry> Recent
    {
        get
        {
            lock (_sync)
            {
                return _entries.Reverse().ToArray();
            }
        }
    }

    public Task WriteAsync(SpeechCommandAuditEntry entry, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > MaximumEntries)
            {
                _entries.Dequeue();
            }
        }

        return Task.CompletedTask;
    }
}
