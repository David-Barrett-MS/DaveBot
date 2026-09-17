using Microsoft.AI.Foundry.Local;

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

            var catalog = await FoundryLocalManager.Instance.GetCatalogAsync(cancellationToken);
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
        try
        {
            using var session = new AudioSession(loadedModel);
            using var request = new Request();
            request.AddItem(AudioItem.CreateFormatDescriptor("pcm", 16_000, 1), takeOwnership: true);
            using var audioQueue = new ItemQueue();
            request.AddItem(audioQueue, takeOwnership: false);
            var responseTask = session.ProcessRequestAsync(request, timeout.Token);
            try
            {
                foreach (var pcmChunk in SplitPcmForStreaming(pcmAudio))
                {
                    PushAudioChunk(audioQueue, pcmChunk);
                }
            }
            finally
            {
                audioQueue.MarkFinished();
            }

            using var response = await responseTask;
            var transcript = CombineTranscriptCandidates(Enumerable.Range(0, response.ItemCount)
                .Select(response.GetItem)
                .Select(item => item switch
                {
                    SpeechResultItem speechResult => speechResult.Text,
                    TextItem textResult => textResult.Text,
                    _ => null,
                }));
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

    private static void PushAudioChunk(ItemQueue audioQueue, ReadOnlyMemory<byte> pcmChunk)
    {
        var audioItem = BytesItem.CreateOwned(pcmChunk);
        try
        {
            audioQueue.Push(audioItem);
        }
        catch
        {
            audioItem.Dispose();
            throw;
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
