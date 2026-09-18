using Microsoft.AI.Foundry.Local;
using Microsoft.Extensions.Hosting;

namespace DaveBrain;

public sealed record FoundryLocalOptions(TimeSpan StartupTimeout)
{
    public static FoundryLocalOptions FromConfiguration(IConfiguration configuration) =>
        new(TimeSpan.FromSeconds(
            configuration.GetValue("DaveBot:FoundryLocal:StartupTimeoutSeconds", 180)));

    public void Validate()
    {
        if (StartupTimeout < TimeSpan.FromSeconds(10) ||
            StartupTimeout > TimeSpan.FromMinutes(15))
        {
            throw new InvalidOperationException("DaveBot:FoundryLocal:StartupTimeoutSeconds is invalid.");
        }
    }
}

public interface IFoundryLocalRuntime : ITranscriptionService
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}

public sealed class FoundryLocalService(
    FoundryLocalOptions options,
    IFoundryLocalRuntime runtime,
    ILogger<FoundryLocalService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startup.CancelAfter(options.StartupTimeout);
        try
        {
            logger.LogInformation("Initializing the packaged Foundry Local runtime.");
            await runtime.InitializeAsync(startup.Token);
            logger.LogInformation("Foundry Local speech model is ready.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            await runtime.StopAsync(CancellationToken.None);
            logger.LogError(
                "Foundry Local did not become ready within {TimeoutSeconds} seconds.",
                options.StartupTimeout.TotalSeconds);
        }
        catch (FoundryLocalException exception)
        {
            await runtime.StopAsync(CancellationToken.None);
            logger.LogError(exception, "Foundry Local is unavailable; speech transcription is disabled.");
        }
        catch (InvalidOperationException exception)
        {
            await runtime.StopAsync(CancellationToken.None);
            logger.LogError(exception, "Foundry Local is unavailable; speech transcription is disabled.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => runtime.StopAsync(cancellationToken);
}
