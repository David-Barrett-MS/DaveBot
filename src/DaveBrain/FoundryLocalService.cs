using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Hosting;

namespace DaveBrain;

public sealed record FoundryLocalOptions(
    string Executable,
    TimeSpan StartupTimeout)
{
    public static FoundryLocalOptions FromConfiguration(IConfiguration configuration) =>
        new(
            configuration["DaveBot:FoundryLocal:Executable"] ?? "foundry",
            TimeSpan.FromSeconds(
                configuration.GetValue("DaveBot:FoundryLocal:StartupTimeoutSeconds", 180)));

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Executable) ||
            StartupTimeout < TimeSpan.FromSeconds(10) ||
            StartupTimeout > TimeSpan.FromMinutes(15))
        {
            throw new InvalidOperationException("DaveBot:FoundryLocal configuration is invalid.");
        }
    }
}

public interface IFoundryLocalCommandRunner
{
    Task RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken);
}

public sealed class FoundryLocalCommandRunner : IFoundryLocalCommandRunner
{
    public async Task RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process process;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start Foundry Local.");
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException(
                $"Could not start Foundry Local executable '{executable}'. Install Foundry Local or configure DaveBot:FoundryLocal:Executable.",
                exception);
        }

        using (process)
        {
            var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }

            var output = string.Concat(await standardOutput, await standardError).Trim();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Foundry Local command '{executable} {string.Join(' ', arguments)}' failed with exit code {process.ExitCode}: " +
                    (output.Length <= 2_048 ? output : output[..2_048]));
            }
        }
    }
}

public sealed class FoundryLocalService(
    FoundryLocalOptions options,
    SpeechTranscriptionOptions transcriptionOptions,
    IFoundryLocalCommandRunner commands,
    ITranscriptionService transcription,
    ILogger<FoundryLocalService> logger) : IHostedService
{
    private bool serverStarted;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startup.CancelAfter(options.StartupTimeout);
        var port = transcriptionOptions.Endpoint.Port;
        try
        {
            logger.LogInformation("Starting Foundry Local on loopback port {Port}.", port);
            await commands.RunAsync(
                options.Executable,
                ["server", "start", "--port", port.ToString(), "--idle-timeout", "0"],
                startup.Token);
            serverStarted = true;

            logger.LogInformation("Downloading Foundry Local model {Model}.", SpeechTranscriptionOptions.WhisperTinyModel);
            await commands.RunAsync(
                options.Executable,
                ["model", "download", SpeechTranscriptionOptions.WhisperTinyModel],
                startup.Token);

            logger.LogInformation("Loading Foundry Local model {Model}.", SpeechTranscriptionOptions.WhisperTinyModel);
            await commands.RunAsync(
                options.Executable,
                ["model", "load", SpeechTranscriptionOptions.WhisperTinyModel],
                startup.Token);

            while (!startup.IsCancellationRequested)
            {
                var status = await transcription.GetDependencyStatusAsync(startup.Token);
                if (status.IsAvailable)
                {
                    logger.LogInformation("Foundry Local is ready.");
                    return;
                }

                logger.LogInformation("Waiting for Foundry Local: {Status}.", status.Code);
                await Task.Delay(TimeSpan.FromSeconds(1), startup.Token);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await StopServerAsync(CancellationToken.None);
            logger.LogError(
                "Foundry Local did not become ready within {TimeoutSeconds} seconds.",
                options.StartupTimeout.TotalSeconds);
        }
        catch (InvalidOperationException exception)
        {
            await StopServerAsync(CancellationToken.None);
            logger.LogError(exception, "Foundry Local is unavailable; speech transcription is disabled.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => StopServerAsync(cancellationToken);

    private async Task StopServerAsync(CancellationToken cancellationToken)
    {
        if (!serverStarted)
        {
            return;
        }

        logger.LogInformation("Stopping Foundry Local.");
        try
        {
            await commands.RunAsync(options.Executable, ["server", "stop"], cancellationToken);
        }
        finally
        {
            serverStarted = false;
        }
    }
}
