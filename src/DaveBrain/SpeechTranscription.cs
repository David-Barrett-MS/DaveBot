using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Channels;

namespace DaveBrain;

public sealed record SpeechTranscriptionOptions(
    Uri Endpoint,
    string Model,
    TimeSpan Timeout,
    int MaximumRequestBytes,
    TimeSpan MaximumDuration,
    int QueueCapacity,
    TimeSpan QueueWaitTimeout)
{
    public const string WhisperTinyModel = "whisper-tiny";
    public const int BytesPerSecond = 32_000;

    public static SpeechTranscriptionOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("DaveBot:SpeechTranscription");
        var endpoint = section["Endpoint"] ?? "http://127.0.0.1:5273/v1/audio/transcriptions";
        return new SpeechTranscriptionOptions(
            new Uri(endpoint, UriKind.Absolute),
            section["Model"] ?? WhisperTinyModel,
            TimeSpan.FromSeconds(section.GetValue("TimeoutSeconds", 20)),
            section.GetValue("MaximumRequestBytes", 1_000_000),
            TimeSpan.FromSeconds(section.GetValue("MaximumDurationSeconds", 30)),
            section.GetValue("QueueCapacity", 2),
            TimeSpan.FromSeconds(section.GetValue("QueueWaitTimeoutSeconds", 25)));
    }

    public void Validate()
    {
        if (!Endpoint.IsAbsoluteUri ||
            (Endpoint.Scheme != Uri.UriSchemeHttp && Endpoint.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(Endpoint.UserInfo) ||
            !IsLoopbackHost(Endpoint.Host))
        {
            throw new InvalidOperationException(
                "DaveBot:SpeechTranscription:Endpoint must be an HTTP(S) localhost or loopback URI.");
        }

        if (!string.Equals(Model, WhisperTinyModel, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"DaveBot only permits the {WhisperTinyModel} transcription model.");
        }

        if (Timeout <= TimeSpan.Zero || QueueWaitTimeout <= TimeSpan.Zero ||
            MaximumRequestBytes is < 44 or > 10_000_000 ||
            MaximumDuration <= TimeSpan.Zero || MaximumDuration > TimeSpan.FromMinutes(5) ||
            QueueCapacity is < 1 or > 32)
        {
            throw new InvalidOperationException("Speech transcription limits are outside their safe bounds.");
        }
    }

    private static bool IsLoopbackHost(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));
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

public sealed record TranscriptionDependencyStatus(bool IsAvailable, string Code);

public interface ITranscriptionService
{
    Task<TranscriptionResult> TranscribeAsync(
        ReadOnlyMemory<byte> wavAudio,
        CancellationToken cancellationToken = default);

    Task<TranscriptionDependencyStatus> GetDependencyStatusAsync(
        CancellationToken cancellationToken = default);
}

public sealed class FoundryLocalTranscriptionService(
    HttpClient httpClient,
    SpeechTranscriptionOptions options) : ITranscriptionService
{
    private const int MaximumResponseBytes = 65_536;

    public async Task<TranscriptionResult> TranscribeAsync(
        ReadOnlyMemory<byte> wavAudio,
        CancellationToken cancellationToken = default)
    {
        if (wavAudio.Length > options.MaximumRequestBytes)
        {
            return new TranscriptionResult(TranscriptionStatus.Failed);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(SpeechTranscriptionOptions.WhisperTinyModel), "model");
        var audio = new ByteArrayContent(wavAudio.ToArray());
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(audio, "file", "command.wav");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, options.Endpoint) { Content = form };
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            if (response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout)
            {
                return new TranscriptionResult(TranscriptionStatus.TimedOut);
            }

            if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
                return new TranscriptionResult(TranscriptionStatus.Unavailable);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new TranscriptionResult(TranscriptionStatus.Failed);
            }

            if (response.Content.Headers.ContentLength > MaximumResponseBytes)
            {
                return new TranscriptionResult(TranscriptionStatus.Failed);
            }

            await using var stream = new LimitedReadStream(
                await response.Content.ReadAsStreamAsync(timeout.Token),
                MaximumResponseBytes);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
            if (!document.RootElement.TryGetProperty("text", out var text) ||
                text.ValueKind != JsonValueKind.String)
            {
                return new TranscriptionResult(TranscriptionStatus.Failed);
            }

            var transcript = text.GetString()?.Trim();
            return string.IsNullOrEmpty(transcript)
                ? new TranscriptionResult(TranscriptionStatus.NoSpeech)
                : TranscriptionResult.Success(transcript.Length <= 500 ? transcript : transcript[..500]);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new TranscriptionResult(TranscriptionStatus.Canceled);
        }
        catch (OperationCanceledException)
        {
            return new TranscriptionResult(TranscriptionStatus.TimedOut);
        }
        catch (HttpRequestException)
        {
            return new TranscriptionResult(TranscriptionStatus.Unavailable);
        }
        catch (JsonException)
        {
            return new TranscriptionResult(TranscriptionStatus.Failed);
        }
        catch (InvalidDataException)
        {
            return new TranscriptionResult(TranscriptionStatus.Failed);
        }
    }

    public async Task<TranscriptionDependencyStatus> GetDependencyStatusAsync(
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);
        var modelsEndpoint = new Uri(options.Endpoint, "../models");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, modelsEndpoint);
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                return new TranscriptionDependencyStatus(false, "endpoint-unavailable");
            }

            if (response.Content.Headers.ContentLength > MaximumResponseBytes)
            {
                return new TranscriptionDependencyStatus(false, "endpoint-invalid-response");
            }

            await using var stream = new LimitedReadStream(
                await response.Content.ReadAsStreamAsync(timeout.Token),
                MaximumResponseBytes);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
            var modelFound = document.RootElement.TryGetProperty("data", out var models) &&
                models.ValueKind == JsonValueKind.Array &&
                models.EnumerateArray().Any(model =>
                    model.TryGetProperty("id", out var id) &&
                    string.Equals(id.GetString(), SpeechTranscriptionOptions.WhisperTinyModel, StringComparison.Ordinal));
            return modelFound
                ? new TranscriptionDependencyStatus(true, "ready")
                : new TranscriptionDependencyStatus(false, "whisper-tiny-unavailable");
        }
        catch (OperationCanceledException)
        {
            return new TranscriptionDependencyStatus(false, "endpoint-timeout");
        }
        catch (HttpRequestException)
        {
            return new TranscriptionDependencyStatus(false, "endpoint-unavailable");
        }
        catch (JsonException)
        {
            return new TranscriptionDependencyStatus(false, "endpoint-invalid-response");
        }
        catch (InvalidDataException)
        {
            return new TranscriptionDependencyStatus(false, "endpoint-invalid-response");
        }
    }
}

internal sealed class LimitedReadStream(Stream inner, long maximumBytes) : Stream
{
    private long _remaining = maximumBytes;

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, count);
        Consume(read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken);
        Consume(read);
        return read;
    }

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush() => throw new NotSupportedException();
    public override Task FlushAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private void Consume(int bytesRead)
    {
        _remaining -= bytesRead;
        if (_remaining < 0)
        {
            throw new InvalidDataException("Foundry Local response exceeded the configured safe limit.");
        }
    }
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
    SpeechTranscriptionOptions options) : BackgroundService
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
            queued.Completion.TrySetResult(new TranscriptionResult(TranscriptionStatus.Canceled));
            return;
        }

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
