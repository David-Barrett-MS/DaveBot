using System.Text.Json;

namespace DaveEar;

public sealed record DaveEarConfiguration(
    Uri Issuer,
    string ClientId,
    Guid EarId,
    Guid LocationId,
    string Name,
    string? MicrophoneDevice,
    int EnergyThreshold,
    int OnsetMilliseconds,
    int PreRollMilliseconds,
    int SilenceBoundaryMilliseconds,
    int MaximumSegmentMilliseconds,
    int QueueCapacity,
    int MaximumSpoolBytes,
    int MaximumSpoolAgeHours)
{
    public const string DefaultClientId = "DaveEar";

    public static DaveEarConfiguration Create(
        Uri issuer,
        string name,
        Guid locationId,
        Guid earId,
        string? microphoneDevice = null) =>
        new(
            issuer, DefaultClientId, earId, locationId, name, microphoneDevice,
            EnergyThreshold: 650,
            OnsetMilliseconds: 100,
            PreRollMilliseconds: 300,
            SilenceBoundaryMilliseconds: 1000,
            MaximumSegmentMilliseconds: 30_000,
            QueueCapacity: 4,
            MaximumSpoolBytes: 20 * 1024 * 1024,
            MaximumSpoolAgeHours: 24);

    public void Validate(bool requireEnrollment = true)
    {
        if (!Issuer.IsAbsoluteUri || !string.Equals(Issuer.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("DaveBrain issuer must be an absolute HTTPS URI.");
        }

        if (string.IsNullOrWhiteSpace(ClientId) || string.IsNullOrWhiteSpace(Name) ||
            LocationId == Guid.Empty || (requireEnrollment && EarId == Guid.Empty) ||
            EnergyThreshold is < 1 or > 32_767 ||
            OnsetMilliseconds is < 20 or > 2_000 ||
            PreRollMilliseconds is < 0 or > 2_000 ||
            SilenceBoundaryMilliseconds is < 1_000 or > 10_000 ||
            MaximumSegmentMilliseconds is < 1_000 or > 120_000 ||
            MaximumSegmentMilliseconds <= SilenceBoundaryMilliseconds ||
            QueueCapacity is < 1 or > 64 ||
            MaximumSpoolBytes is < 44_000 or > 512 * 1024 * 1024 ||
            MaximumSpoolAgeHours is < 1 or > 24 * 30)
        {
            throw new InvalidOperationException("DaveEar configuration contains an unsafe or invalid value.");
        }
    }
}

public sealed class DaveEarPaths
{
    public DaveEarPaths(string root)
    {
        Root = Path.GetFullPath(root);
        ConfigurationPath = Path.Combine(Root, "config.json");
        SpoolPath = Path.Combine(Root, "spool");
    }

    public string Root { get; }
    public string ConfigurationPath { get; }
    public string SpoolPath { get; }
    public string StatusPath => Path.Combine(Root, "status.json");

    public static DaveEarPaths FromArguments(IReadOnlyDictionary<string, string> arguments)
    {
        var root = arguments.GetValueOrDefault("--state-dir");
        if (string.IsNullOrWhiteSpace(root))
        {
            root = OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DaveBot", "DaveEar")
                : Path.Combine(
                    Environment.GetEnvironmentVariable("XDG_STATE_HOME")
                        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state"),
                    "daveear");
        }

        return new DaveEarPaths(root);
    }
}

public static class DaveEarStatusStore
{
    public static void Save(DaveEarPaths paths, object status)
    {
        SecureFile.EnsurePrivateDirectory(paths.Root);
        var temporary = $"{paths.StatusPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = SecureFile.CreatePrivateFile(temporary))
            {
                JsonSerializer.Serialize(stream, status);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, paths.StatusPath, overwrite: true);
            SecureFile.EnsurePrivateFile(paths.StatusPath);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static string? Load(DaveEarPaths paths)
    {
        if (!File.Exists(paths.StatusPath)) return null;
        SecureFile.EnsurePrivateFile(paths.StatusPath);
        return File.ReadAllText(paths.StatusPath);
    }
}

public static class DaveEarConfigurationStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<DaveEarConfiguration> LoadAsync(DaveEarPaths paths, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(paths.ConfigurationPath))
        {
            throw new InvalidOperationException("DaveEar is not registered. Run --register first.");
        }

        SecureFile.EnsurePrivateFile(paths.ConfigurationPath);
        await using var input = new FileStream(paths.ConfigurationPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var configuration = await JsonSerializer.DeserializeAsync<DaveEarConfiguration>(input, Options, cancellationToken)
            ?? throw new InvalidOperationException("DaveEar configuration is invalid.");
        configuration.Validate();
        return configuration;
    }

    public static async Task SaveAsync(
        DaveEarPaths paths,
        DaveEarConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        configuration.Validate();
        SecureFile.EnsurePrivateDirectory(paths.Root);
        var temporary = $"{paths.ConfigurationPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var output = SecureFile.CreatePrivateFile(temporary))
            {
                await JsonSerializer.SerializeAsync(output, configuration, Options, cancellationToken);
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }

            File.Move(temporary, paths.ConfigurationPath, overwrite: true);
            SecureFile.EnsurePrivateFile(paths.ConfigurationPath);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
