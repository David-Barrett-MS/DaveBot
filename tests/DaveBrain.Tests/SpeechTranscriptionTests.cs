using System.Net;
using System.Text;
using DaveBrain;
using DaveHome;
using Microsoft.Extensions.Logging.Abstractions;

namespace DaveBrain.Tests;

public sealed class WavAudioValidatorTests
{
    [Theory]
    [InlineData("https://foundry.example.test/v1/audio/transcriptions", "whisper-tiny")]
    [InlineData("http://127.0.0.1:5273/v1/audio/transcriptions", "whisper-base")]
    public void NonLocalEndpointsAndOtherModelsAreRejected(string endpoint, string model)
    {
        var options = new SpeechTranscriptionOptions(
            new Uri(endpoint),
            model,
            TimeSpan.FromSeconds(1),
            1_000_000,
            TimeSpan.FromSeconds(30),
            1,
            TimeSpan.FromSeconds(1));

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void ValidPcmWavIsAccepted()
    {
        var result = CreateValidator().Validate(CreateWav());

        Assert.True(result.Succeeded);
        Assert.Equal(TimeSpan.FromSeconds(1), result.Audio!.Duration);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(43)]
    public void TruncatedWavIsRejected(int length)
    {
        var wav = CreateWav();

        var result = CreateValidator().Validate(wav[..length]);

        Assert.Equal(WavValidationFailure.Truncated, result.Failure);
    }

    [Fact]
    public void MalformedRiffIsRejected()
    {
        var wav = CreateWav();
        wav[0] = (byte)'X';

        Assert.Equal(WavValidationFailure.Malformed, CreateValidator().Validate(wav).Failure);
    }

    [Fact]
    public void InconsistentPcmHeaderIsRejected()
    {
        var wav = CreateWav();
        BitConverter.GetBytes(1u).CopyTo(wav, 28);

        Assert.Equal(WavValidationFailure.UnsupportedFormat, CreateValidator().Validate(wav).Failure);
    }

    [Theory]
    [InlineData(2, 16_000, 16)]
    [InlineData(1, 8_000, 16)]
    [InlineData(1, 16_000, 24)]
    public void UnsupportedPcmFormatIsRejected(ushort channels, uint sampleRate, ushort bitsPerSample)
    {
        Assert.Equal(
            WavValidationFailure.UnsupportedFormat,
            CreateValidator().Validate(CreateWav(channels, sampleRate, bitsPerSample)).Failure);
    }

    [Fact]
    public void OverlongAndOversizedWavAreRejected()
    {
        var validator = CreateValidator(maximumBytes: 70_000, maximumDuration: TimeSpan.FromSeconds(1));

        Assert.Equal(
            WavValidationFailure.TooLong,
            validator.Validate(CreateWav(dataLength: 64_002)).Failure);
        Assert.Equal(
            WavValidationFailure.TooLarge,
            validator.Validate(CreateWav(dataLength: 70_000)).Failure);
    }

    private static WavAudioValidator CreateValidator(
        int maximumBytes = 1_000_000,
        TimeSpan? maximumDuration = null) =>
        new(new SpeechTranscriptionOptions(
            new Uri("http://127.0.0.1:5273/v1/audio/transcriptions"),
            SpeechTranscriptionOptions.WhisperTinyModel,
            TimeSpan.FromSeconds(1),
            maximumBytes,
            maximumDuration ?? TimeSpan.FromSeconds(30),
            2,
            TimeSpan.FromSeconds(1)));

    internal static byte[] CreateWav(
        ushort channels = 1,
        uint sampleRate = 16_000,
        ushort bitsPerSample = 16,
        int dataLength = 32_000)
    {
        var blockAlign = checked((ushort)(channels * bitsPerSample / 8));
        var wav = new byte[44 + dataLength];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(wav, 0);
        BitConverter.GetBytes(wav.Length - 8).CopyTo(wav, 4);
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(wav, 8);
        BitConverter.GetBytes(16).CopyTo(wav, 16);
        BitConverter.GetBytes((ushort)1).CopyTo(wav, 20);
        BitConverter.GetBytes(channels).CopyTo(wav, 22);
        BitConverter.GetBytes(sampleRate).CopyTo(wav, 24);
        BitConverter.GetBytes(sampleRate * blockAlign).CopyTo(wav, 28);
        BitConverter.GetBytes(blockAlign).CopyTo(wav, 32);
        BitConverter.GetBytes(bitsPerSample).CopyTo(wav, 34);
        Encoding.ASCII.GetBytes("data").CopyTo(wav, 36);
        BitConverter.GetBytes(dataLength).CopyTo(wav, 40);
        return wav;
    }
}

public sealed class FoundryLocalTranscriptionServiceTests
{
    [Fact]
    public async Task SendsOnlyWhisperTinyAndMapsTranscript()
    {
        var handler = new DelegateHandler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1/audio/transcriptions", request.RequestUri!.AbsolutePath);
            var body = await request.Content!.ReadAsStringAsync();
            Assert.Contains("whisper-tiny", body, StringComparison.Ordinal);
            return JsonResponse("""{"text":" lights on "}""");
        });
        var service = new FoundryLocalTranscriptionService(
            new HttpClient(handler),
            CreateOptions());

        var result = await service.TranscribeAsync(WavAudioValidatorTests.CreateWav());

        Assert.Equal(TranscriptionStatus.Succeeded, result.Status);
        Assert.Equal("lights on", result.Transcript);
    }

    [Fact]
    public async Task MapsNoTextAndUnavailableDependency()
    {
        var noText = new FoundryLocalTranscriptionService(
            new HttpClient(new DelegateHandler(_ => Task.FromResult(JsonResponse("""{"text":" "}""")))),
            CreateOptions());
        var unavailable = new FoundryLocalTranscriptionService(
            new HttpClient(new DelegateHandler(_ => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)))),
            CreateOptions());

        Assert.Equal(TranscriptionStatus.NoSpeech,
            (await noText.TranscribeAsync(WavAudioValidatorTests.CreateWav())).Status);
        Assert.Equal(TranscriptionStatus.Unavailable,
            (await unavailable.TranscribeAsync(WavAudioValidatorTests.CreateWav())).Status);
    }

    [Fact]
    public async Task MapsTimeoutAndCallerCancellation()
    {
        var timeoutService = new FoundryLocalTranscriptionService(
            new HttpClient(new DelegateHandler(async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return JsonResponse("""{"text":"unreachable"}""");
            })),
            CreateOptions(timeout: TimeSpan.FromMilliseconds(30)));
        var canceledService = new FoundryLocalTranscriptionService(
            new HttpClient(new DelegateHandler(async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return JsonResponse("""{"text":"unreachable"}""");
            })),
            CreateOptions());
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        Assert.Equal(TranscriptionStatus.TimedOut,
            (await timeoutService.TranscribeAsync(WavAudioValidatorTests.CreateWav())).Status);
        Assert.Equal(TranscriptionStatus.Canceled,
            (await canceledService.TranscribeAsync(WavAudioValidatorTests.CreateWav(), canceled.Token)).Status);
    }

    [Fact]
    public async Task ReadinessRequiresWhisperTiny()
    {
        var service = new FoundryLocalTranscriptionService(
            new HttpClient(new DelegateHandler(_ => Task.FromResult(JsonResponse(
                """{"data":[{"id":"some-other-model"}]}""")))),
            CreateOptions());

        var result = await service.GetDependencyStatusAsync();

        Assert.False(result.IsAvailable);
        Assert.Equal("whisper-tiny-unavailable", result.Code);
    }

    private static SpeechTranscriptionOptions CreateOptions(TimeSpan? timeout = null) =>
        new(
            new Uri("http://127.0.0.1:5273/v1/audio/transcriptions"),
            SpeechTranscriptionOptions.WhisperTinyModel,
            timeout ?? TimeSpan.FromSeconds(1),
            1_000_000,
            TimeSpan.FromSeconds(30),
            1,
            TimeSpan.FromSeconds(1));

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

public sealed class FoundryLocalServiceTests
{
    [Fact]
    public async Task StartsAndStopsTheWhisperTinyDaemon()
    {
        var commands = new RecordingFoundryCommands();
        var transcription = new FakeTranscriptionService(_ =>
            Task.FromResult(TranscriptionResult.Success("unused")));
        var service = new FoundryLocalService(
            new FoundryLocalOptions("foundry-test", TimeSpan.FromSeconds(10)),
            new SpeechTranscriptionOptions(
                new Uri("http://127.0.0.1:5273/v1/audio/transcriptions"),
                SpeechTranscriptionOptions.WhisperTinyModel,
                TimeSpan.FromSeconds(1),
                1_000_000,
                TimeSpan.FromSeconds(30),
                1,
                TimeSpan.FromSeconds(1)),
            commands,
            transcription,
            NullLogger<FoundryLocalService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(
        [
            "server start --port 5273 --idle-timeout 0",
            "model download whisper-tiny",
            "model load whisper-tiny",
            "server stop",
        ],
        commands.Commands);
    }

    [Fact]
    public async Task UnavailableFoundryDoesNotPreventServerStartup()
    {
        var commands = new FailingFoundryCommands();
        var service = new FoundryLocalService(
            new FoundryLocalOptions("missing-foundry", TimeSpan.FromSeconds(10)),
            new SpeechTranscriptionOptions(
                new Uri("http://127.0.0.1:5273/v1/audio/transcriptions"),
                SpeechTranscriptionOptions.WhisperTinyModel,
                TimeSpan.FromSeconds(1),
                1_000_000,
                TimeSpan.FromSeconds(30),
                1,
                TimeSpan.FromSeconds(1)),
            commands,
            new FakeTranscriptionService(_ => Task.FromResult(TranscriptionResult.Success("unused"))),
            NullLogger<FoundryLocalService>.Instance);

        await service.StartAsync(CancellationToken.None);

        Assert.Equal(1, commands.Attempts);
    }

    private sealed class RecordingFoundryCommands : IFoundryLocalCommandRunner
    {
        public List<string> Commands { get; } = [];

        public Task RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            Commands.Add(string.Join(' ', arguments));
            return Task.CompletedTask;
        }

    }

    private sealed class FailingFoundryCommands : IFoundryLocalCommandRunner
    {
        public int Attempts { get; private set; }

        public Task RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            Attempts++;
            throw new InvalidOperationException("Foundry Local is not installed.");
        }
    }
}

public sealed class SpeechTranscriptionQueueTests
{
    [Fact]
    public async Task SuccessfulTranscriptionCanUseTheExistingCommandProcessor()
    {
        var transcription = new FakeTranscriptionService(_ =>
            Task.FromResult(TranscriptionResult.Success("lights on")));
        using var queue = CreateQueue(transcription);
        await queue.StartAsync(CancellationToken.None);

        var queued = await queue.EnqueueAsync(WavAudioValidatorTests.CreateWav());
        var commandProcessor = new RecordingCommandProcessor();
        var command = await commandProcessor.ProcessAsync(
            queued.Transcription!.Transcript,
            new CommandSource("ear:unit-test", Guid.NewGuid()));

        Assert.Equal(SpeechQueueStatus.Completed, queued.Status);
        Assert.Equal(TranscriptionStatus.Succeeded, queued.Transcription.Status);
        Assert.Equal("lights on", commandProcessor.Transcript);
        Assert.Equal(CommandOutcome.Completed, command.Outcome);
        await queue.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task QueueMapsServiceTimeoutAndCanceledRequest()
    {
        var transcription = new FakeTranscriptionService(async cancellationToken =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return TranscriptionResult.Success("unreachable");
        });
        using var queue = CreateQueue(transcription, waitTimeout: TimeSpan.FromMilliseconds(30));
        await queue.StartAsync(CancellationToken.None);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        var timedOut = await queue.EnqueueAsync(WavAudioValidatorTests.CreateWav());
        var canceledResult = await queue.EnqueueAsync(WavAudioValidatorTests.CreateWav(), canceled.Token);

        Assert.Equal(TranscriptionStatus.TimedOut, timedOut.Transcription!.Status);
        Assert.Equal(SpeechQueueStatus.Canceled, canceledResult.Status);
        await queue.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task SaturatedQueueReturnsCorrelatedOverload()
    {
        var transcription = new FakeTranscriptionService(async cancellationToken =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return TranscriptionResult.Success("unreachable");
        });
        using var queue = CreateQueue(transcription, capacity: 1);
        await queue.StartAsync(CancellationToken.None);

        var first = queue.EnqueueAsync(WavAudioValidatorTests.CreateWav());
        await transcription.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var second = queue.EnqueueAsync(WavAudioValidatorTests.CreateWav());
        var overloaded = await queue.EnqueueAsync(WavAudioValidatorTests.CreateWav());

        Assert.Equal(SpeechQueueStatus.Overloaded, overloaded.Status);
        Assert.NotEqual(Guid.Empty, overloaded.CorrelationId);

        await queue.StopAsync(CancellationToken.None);
        await Task.WhenAll(first, second);
    }

    private static SpeechTranscriptionQueue CreateQueue(
        FakeTranscriptionService transcription,
        int capacity = 1,
        TimeSpan? waitTimeout = null) =>
        new(
            transcription,
            new SpeechTranscriptionOptions(
                new Uri("http://127.0.0.1:5273/v1/audio/transcriptions"),
                SpeechTranscriptionOptions.WhisperTinyModel,
                TimeSpan.FromSeconds(1),
                1_000_000,
                TimeSpan.FromSeconds(30),
                capacity,
                waitTimeout ?? TimeSpan.FromSeconds(10)));

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
                TextCommandIntent.LightsOn,
                "Kitchen: 1 light",
                CommandOutcome.Completed,
                new SafeUserMessage("completed", "The lights are on.")));
        }
    }
}

internal sealed class FakeTranscriptionService(
    Func<CancellationToken, Task<TranscriptionResult>> transcribe) : ITranscriptionService
{
    public TaskCompletionSource Started { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<TranscriptionResult> TranscribeAsync(
        ReadOnlyMemory<byte> wavAudio,
        CancellationToken cancellationToken = default)
    {
        Started.TrySetResult();
        return transcribe(cancellationToken);
    }

    public Task<TranscriptionDependencyStatus> GetDependencyStatusAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new TranscriptionDependencyStatus(true, "ready"));
}

internal sealed class DelegateHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

    public DelegateHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) :
        this((request, _) => handler(request))
    {
    }

    public DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) =>
        _handler = handler;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) =>
        _handler(request, cancellationToken);
}
