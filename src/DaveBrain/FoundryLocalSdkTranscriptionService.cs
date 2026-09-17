using Microsoft.AI.Foundry.Local;
using System.Diagnostics;

namespace DaveBrain;

public sealed class FoundryLocalSdkTranscriptionService(
    SpeechTranscriptionOptions options,
    WavAudioValidator wavValidator,
    ILogger<FoundryLocalSdkTranscriptionService> logger) : IFoundryLocalRuntime
{
    internal const int PcmChunkBytes = SpeechTranscriptionOptions.BytesPerSecond / 10;

    private readonly SemaphoreSlim initializationLock = new(1, 1);
    private IModel? model;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await initializationLock.WaitAsync(cancellationToken);
        try
        {
            if (model is not null && await model.IsLoadedAsync(cancellationToken))
            {
                return;
            }

            if (!FoundryLocalManager.IsInitialized)
            {
                await FoundryLocalManager.CreateAsync(
                    new Configuration
                    {
                        AppName = "DaveBrain",
                        DisableNonessentialTelemetry = true,
                    },
                    logger,
                    cancellationToken);
            }

            var foundry = FoundryLocalManager.Instance;
            await foundry.DownloadAndRegisterEpsAsync().WaitAsync(cancellationToken);
            var catalog = await foundry.GetCatalogAsync(cancellationToken);
            var speechModel = await catalog.GetModelAsync(
                options.Model,
                cancellationToken)
                ?? throw new InvalidOperationException(
                    $"Foundry Local does not offer the {options.Model} speech model.");

            await speechModel.DownloadAsync(null, cancellationToken);
            await speechModel.LoadAsync(cancellationToken);
            Volatile.Write(ref model, speechModel);
        }
        finally
        {
            initializationLock.Release();
        }
    }

    public async Task<TranscriptionResult> TranscribeAsync(
        ReadOnlyMemory<byte> wavAudio,
        CancellationToken cancellationToken = default)
    {
        var validatedWav = wavValidator.Validate(wavAudio.Span);
        if (!validatedWav.Succeeded)
        {
            logger.LogWarning("Foundry Local received invalid WAV audio.");
            return new TranscriptionResult(TranscriptionStatus.Failed);
        }

        var audio = validatedWav.Audio!;
        var pcmAudio = wavAudio.Slice(audio.DataOffset, audio.DataLength);
        var loadedModel = Volatile.Read(ref model);
        if (loadedModel is null)
        {
            return new TranscriptionResult(TranscriptionStatus.Unavailable);
        }

        try
        {
            if (!await loadedModel.IsLoadedAsync(cancellationToken))
            {
                return new TranscriptionResult(TranscriptionStatus.Unavailable);
            }
        }
        catch (FoundryLocalException exception)
        {
            logger.LogWarning(
                exception,
                "Foundry Local could not report speech model status (error code: {ErrorCode}).",
                exception.ErrorCode);
            return new TranscriptionResult(TranscriptionStatus.Unavailable);
        }
        catch (InvalidOperationException exception)
        {
            logger.LogWarning(exception, "Foundry Local could not report speech model status.");
            return new TranscriptionResult(TranscriptionStatus.Unavailable);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);
        var elapsed = Stopwatch.StartNew();
        var stage = "creating the live transcription session";
        var appendedChunks = 0;
        try
        {
            // The native AudioSession streaming API can crash the process when canceled in 2.0.1.
#pragma warning disable CS0618
            var audioClient = await loadedModel.GetAudioClientAsync().WaitAsync(timeout.Token);
#pragma warning restore CS0618
            await using var session = audioClient.CreateLiveTranscriptionSession();
            session.Settings.SampleRate = 16_000;
            session.Settings.Channels = 1;
            session.Settings.Language = "en";

            stage = "starting the live transcription session";
            await session.StartAsync(CancellationToken.None).WaitAsync(timeout.Token);
            var sessionStarted = true;
            var candidates = new List<string?>();
            var resultReader = Task.Run(async () =>
            {
                await foreach (var result in session.GetStream(CancellationToken.None))
                {
                    if (result.IsFinal)
                    {
                        candidates.Add(result.Content?.FirstOrDefault()?.Text);
                    }
                }
            });

            try
            {
                stage = "appending PCM audio";
                foreach (var pcmChunk in SplitPcmForStreaming(pcmAudio))
                {
                    await session.AppendAsync(pcmChunk, CancellationToken.None).AsTask()
                        .WaitAsync(timeout.Token);
                    appendedChunks++;
                    await Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token);
                }

                stage = "waiting for live transcription results";
                await Task.Delay(TimeSpan.FromSeconds(3), timeout.Token);
            }
            finally
            {
                if (sessionStarted)
                {
                    stage = "stopping the live transcription session";
                    await session.StopAsync(CancellationToken.None);
                }
            }

            stage = "completing the live transcription stream";
            await resultReader.WaitAsync(timeout.Token);
            var transcript = CombineTranscriptCandidates(candidates);
            if (string.IsNullOrEmpty(transcript))
            {
                logger.LogWarning("Foundry Local returned no speech result.");
                return new TranscriptionResult(TranscriptionStatus.Failed);
            }

            return TranscriptionResult.Success(
                transcript.Length <= 500 ? transcript : transcript[..500]);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new TranscriptionResult(TranscriptionStatus.Canceled);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning(
                "Foundry Local transcription timed out after {ElapsedMilliseconds} ms while {Stage}; " +
                "PCM duration: {AudioDurationMilliseconds} ms; chunks appended: {AppendedChunks}.",
                elapsed.ElapsedMilliseconds,
                stage,
                audio.Duration.TotalMilliseconds,
                appendedChunks);
            return new TranscriptionResult(TranscriptionStatus.TimedOut);
        }
        catch (FoundryLocalException exception)
        {
            logger.LogWarning(
                exception,
                "Foundry Local could not transcribe the audio (error code: {ErrorCode}).",
                exception.ErrorCode);
            return new TranscriptionResult(TranscriptionStatus.Failed);
        }
        catch (InvalidOperationException exception)
        {
            logger.LogWarning(exception, "Foundry Local could not process the audio.");
            return new TranscriptionResult(TranscriptionStatus.Failed);
        }
    }

    internal static string? CombineTranscriptCandidates(IEnumerable<string?> candidates)
    {
        string? transcript = null;
        foreach (var candidate in candidates)
        {
            var text = candidate?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            if (string.IsNullOrEmpty(transcript) ||
                text.StartsWith(transcript, StringComparison.OrdinalIgnoreCase))
            {
                transcript = text;
            }
            else if (!transcript.EndsWith(text, StringComparison.OrdinalIgnoreCase))
            {
                var overlapLength = 0;
                for (var length = Math.Min(transcript.Length, text.Length); length > 0; length--)
                {
                    if (transcript.EndsWith(text[..length], StringComparison.OrdinalIgnoreCase))
                    {
                        overlapLength = length;
                        break;
                    }
                }

                transcript += overlapLength == 0 ? $" {text}" : text[overlapLength..];
            }
        }

        return transcript;
    }

    internal static IEnumerable<ReadOnlyMemory<byte>> SplitPcmForStreaming(
        ReadOnlyMemory<byte> pcmAudio)
    {
        for (var offset = 0; offset < pcmAudio.Length; offset += PcmChunkBytes)
        {
            yield return pcmAudio.Slice(offset, Math.Min(PcmChunkBytes, pcmAudio.Length - offset));
        }
    }

    public async Task<TranscriptionDependencyStatus> GetDependencyStatusAsync(
        CancellationToken cancellationToken = default)
    {
        var loadedModel = Volatile.Read(ref model);
        if (loadedModel is null)
        {
            return new TranscriptionDependencyStatus(false, "foundry-local-unavailable");
        }

        try
        {
            return await loadedModel.IsLoadedAsync(cancellationToken)
                ? new TranscriptionDependencyStatus(true, "ready")
                : new TranscriptionDependencyStatus(false, "nemotron-speech-unavailable");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (FoundryLocalException exception)
        {
            logger.LogWarning(
                exception,
                "Foundry Local could not report speech model status (error code: {ErrorCode}).",
                exception.ErrorCode);
            return new TranscriptionDependencyStatus(false, "foundry-local-unavailable");
        }
        catch (InvalidOperationException exception)
        {
            logger.LogWarning(exception, "Foundry Local could not report speech model status.");
            return new TranscriptionDependencyStatus(false, "foundry-local-unavailable");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var loadedModel = Interlocked.Exchange(ref model, null);
        if (loadedModel is not null)
        {
            await loadedModel.UnloadAsync(cancellationToken);
        }

        if (FoundryLocalManager.IsInitialized)
        {
            FoundryLocalManager.Instance.Shutdown();
        }
    }
}
