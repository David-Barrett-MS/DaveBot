using DaveHome;

namespace DaveBrain;

public sealed class HueReconciliationService(
    HueHubService hueHubs,
    ILogger<HueReconciliationService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var device in hueHubs.GetMappedLights().Where(device => device.Enabled))
        {
            var result = await hueHubs.ReadLightStateAsync(device, cancellationToken);
            if (!result.Succeeded)
            {
                logger.LogWarning(
                    "Unable to reconcile Hue light {DeviceId}: {Status}",
                    device.Id,
                    result.Status);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
