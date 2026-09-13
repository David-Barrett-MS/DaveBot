namespace DaveHome;

public enum DeviceOperationStatus
{
    Succeeded,
    BridgeUnavailable,
    Unauthorized,
    ResourceNotFound,
    InvalidState,
    TimedOut,
}

public sealed record DeviceOperationResult(
    DeviceOperationStatus Status,
    DeviceState? State = null,
    string? Detail = null)
{
    public bool Succeeded => Status == DeviceOperationStatus.Succeeded;
}

public sealed record DeviceStateChange(
    bool? IsOn = null,
    int? Brightness = null,
    DeviceColour? Colour = null,
    int? FanSpeed = null);

public interface ISecretStore
{
    Task<string> StoreAsync(string secret, CancellationToken cancellationToken = default);

    Task<string> GetAsync(string reference, CancellationToken cancellationToken = default);

    Task DeleteAsync(string reference, CancellationToken cancellationToken = default);
}

public interface IDeviceProvider
{
    string DeviceType { get; }

    Task<DeviceOperationResult> GetStateAsync(
        Device device,
        CancellationToken cancellationToken = default);

    Task<DeviceOperationResult> SetStateAsync(
        Device device,
        DeviceStateChange change,
        CancellationToken cancellationToken = default);
}
