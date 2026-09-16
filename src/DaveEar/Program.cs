using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace DaveEar;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var arguments = ParseArguments(args);
        var verbose = CreateVerboseLogger(arguments);
        try
        {
            var paths = DaveEarPaths.FromArguments(arguments);
            return arguments.ContainsKey("--register")
                ? await RegisterAsync(arguments, paths, verbose)
                : arguments.ContainsKey("--diagnose")
                    ? await DiagnoseAsync(paths, verbose)
                    : arguments.ContainsKey("--status")
                        ? await StatusAsync(paths)
                        : await RunAsync(paths, verbose);
        }
        catch (OperationCanceledException) when (arguments.ContainsKey("--run"))
        {
            return 0;
        }
        catch (OperationCanceledException exception)
        {
            WriteError(exception);
            return 1;
        }
        catch (Exception exception)
        {
            WriteError(exception);
            return 1;
        }
    }

    private static async Task<int> RegisterAsync(
        IReadOnlyDictionary<string, string> arguments,
        DaveEarPaths paths,
        Action<string>? verbose)
    {
        var issuer = ParseHttpsUri(Required(arguments, "--issuer"));
        var name = Required(arguments, "--name");
        var locationId = Guid.Parse(Required(arguments, "--location"));
        verbose?.Invoke($"Registration starting for '{name}' at location {locationId:D}.");
        if (File.Exists(paths.ConfigurationPath))
        {
            throw new InvalidOperationException("DaveEar is already registered. Delete its protected state only after revoking the Ear.");
        }

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var enrollment = new DaveEarEnrollmentClient(httpClient, verbose);
        var discovery = await enrollment.DiscoverAsync(issuer, CancellationToken.None);
        verbose?.Invoke("Creating a new DaveEar client certificate request.");
        using var privateKey = RSA.Create(3072);
        var request = new CertificateRequest(
            "CN=DaveEar",
            privateKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        var device = await enrollment.StartDeviceAuthorizationAsync(discovery, DaveEarConfiguration.DefaultClientId, CancellationToken.None);
        Console.WriteLine($"Open {device.VerificationUriComplete ?? device.VerificationUri} and enter code {device.UserCode}.");
        var initialToken = await enrollment.AwaitDeviceAuthorizationAsync(
            discovery,
            DaveEarConfiguration.DefaultClientId,
            device,
            Console.WriteLine,
            CancellationToken.None);
        if (string.IsNullOrWhiteSpace(initialToken.RefreshToken))
        {
            throw new InvalidOperationException("DaveBrain did not issue a refresh token; registration cannot continue.");
        }

        verbose?.Invoke("Administrator consent received; submitting certificate enrollment.");
        var enrolled = await enrollment.EnrollAsync(
            issuer,
            initialToken,
            name,
            locationId,
            PemEncoding.WriteString("CERTIFICATE REQUEST", request.CreateSigningRequest()),
            CancellationToken.None);
        var credentials = new EarCredentials(
            privateKey.ExportPkcs8PrivateKeyPem(),
            enrolled.CertificatePem,
            initialToken.RefreshToken);
        var configuration = DaveEarConfiguration.Create(issuer, name, locationId, enrolled.EarId,
            arguments.GetValueOrDefault("--microphone"));
        var credentialStore = CredentialStoreFactory.Create(paths);
        var tokens = new MtlsAccessTokenProvider(configuration, credentials, discovery, verbose);
        // Require a certificate-presented refresh before anything is persisted as a running Ear.
        verbose?.Invoke("Verifying the issued certificate with an mTLS refresh request.");
        await tokens.GetAsync(CancellationToken.None);
        await credentialStore.SaveAsync(tokens.Credentials, CancellationToken.None);
        try
        {
            await DaveEarConfigurationStore.SaveAsync(paths, configuration);
        }
        catch
        {
            await credentialStore.DeleteAsync();
            throw;
        }

        verbose?.Invoke("Registration state saved successfully.");
        Console.WriteLine($"DaveEar registered with Ear ID {enrolled.EarId:D}. Run with --run.");
        return 0;
    }

    private static async Task<int> DiagnoseAsync(DaveEarPaths paths, Action<string>? verbose)
    {
        verbose?.Invoke("Starting diagnostics.");
        var configuration = await DaveEarConfigurationStore.LoadAsync(paths);
        var credentials = await LoadCredentialsAsync(paths);
        using var discoveryClient = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var discovery = await new DaveEarEnrollmentClient(discoveryClient, verbose).DiscoverAsync(configuration.Issuer, CancellationToken.None);
        using var certificate = EarClientCertificateLoader.Load(
            credentials.CertificatePem,
            credentials.PrivateKeyPem);
        if (certificate.NotAfter <= DateTime.UtcNow)
        {
            throw new InvalidOperationException("DaveEar client certificate has expired; re-enrollment is required.");
        }

        await using var capture = AudioCaptureFactory.Create(configuration.MicrophoneDevice);
        var microphone = await capture.DiagnoseAsync(CancellationToken.None);
        verbose?.Invoke($"Microphone diagnostic completed: {microphone.Code}.");
        var tokens = new MtlsAccessTokenProvider(configuration, credentials, discovery, verbose);
        var uploader = new HttpCommandUploader(configuration, tokens, verbose);
        var heartbeat = await uploader.HeartbeatAsync(configuration.EarId, CancellationToken.None);
        await tokens.PersistRotatedCredentialsAsync(CredentialStoreFactory.Create(paths), CancellationToken.None);
        // This valid silent WAV exercises the protected upload path. A no-speech response is expected.
        var wav = new PcmWavEncoder().Encode(new AudioSegment(Guid.NewGuid(), new float[16_000], 16_000, DateTimeOffset.UtcNow, true));
        var upload = await uploader.UploadAsync(configuration.EarId, Guid.NewGuid(), wav, CancellationToken.None);
        await tokens.PersistRotatedCredentialsAsync(CredentialStoreFactory.Create(paths), CancellationToken.None);
        Console.WriteLine($"configuration=valid microphone={microphone.Code} certificate=valid heartbeat={heartbeat.Code} test-upload={upload.Code}");
        return microphone.IsAvailable && heartbeat.IsEnabled &&
               upload.Disposition is not UploadDisposition.AuthenticationFailure and not UploadDisposition.Ambiguous
            ? 0 : 1;
    }

    private static async Task<int> StatusAsync(DaveEarPaths paths)
    {
        var configuration = await DaveEarConfigurationStore.LoadAsync(paths);
        var status = DaveEarStatusStore.Load(paths) ?? """{"status":"not-running"}""";
        Console.WriteLine($"earId={configuration.EarId:D} issuer={configuration.Issuer} locationId={configuration.LocationId:D} {status}");
        return 0;
    }

    private static async Task<int> RunAsync(DaveEarPaths paths, Action<string>? verbose)
    {
        var configuration = await DaveEarConfigurationStore.LoadAsync(paths);
        var credentialStore = CredentialStoreFactory.Create(paths);
        var credentials = await LoadCredentialsAsync(paths);
        using var discoveryClient = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var discovery = await new DaveEarEnrollmentClient(discoveryClient, verbose).DiscoverAsync(configuration.Issuer, CancellationToken.None);
        await using var capture = AudioCaptureFactory.Create(configuration.MicrophoneDevice);
        verbose?.Invoke(
            $"Starting microphone monitoring for Ear {configuration.EarId:D}; device={configuration.MicrophoneDevice ?? "default"}, " +
            $"threshold={configuration.EnergyThreshold}, endpoint={configuration.EffectiveMtlsEndpoint}.");
        var detector = new EnergyVoiceActivityDetector(configuration.EnergyThreshold);
        var tokens = new MtlsAccessTokenProvider(configuration, credentials, discovery, verbose);
        var service = new DaveEarService(
            configuration,
            capture,
            detector,
            new PcmWavEncoder(),
            new HttpCommandUploader(configuration, tokens, verbose),
            new RetrySpool(paths, configuration),
            tokens,
            credentialStore,
            state => DaveEarStatusStore.Save(paths, state),
            verbose);
        DaveEarStatusStore.Save(paths, service.GetSafeStatus());
        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; shutdown.Cancel(); };
        await service.RunAsync(Console.WriteLine, shutdown.Token);
        return 0;
    }

    private static async Task<EarCredentials> LoadCredentialsAsync(DaveEarPaths paths) =>
        await CredentialStoreFactory.Create(paths).LoadAsync()
        ?? throw new InvalidOperationException("DaveEar credentials are missing; re-enrollment is required.");

    private static void WriteError(Exception exception)
    {
        Console.Error.WriteLine($"DaveEar: {exception.Message}");
        for (var inner = exception.InnerException; inner is not null; inner = inner.InnerException)
        {
            Console.Error.WriteLine($"DaveEar: {inner.GetType().Name}: {inner.Message}");
        }
    }

    private static Dictionary<string, string> ParseArguments(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Unknown argument '{args[index]}'.");
            }
            if (args[index] is "--register" or "--run" or "--diagnose" or "--status" or "--verbose")
            {
                result.Add(args[index], "true");
                continue;
            }
            if (index + 1 == args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Argument {args[index]} requires a value.");
            }
            result.Add(args[index], args[++index]);
        }
        if (result.Keys.Count(key => key is "--register" or "--run" or "--diagnose" or "--status") > 1)
        {
            throw new InvalidOperationException("Specify only one DaveEar operation.");
        }
        return result;
    }

    private static Action<string>? CreateVerboseLogger(IReadOnlyDictionary<string, string> arguments) =>
        arguments.ContainsKey("--verbose")
            ? message => Console.WriteLine($"{DateTimeOffset.Now:O} DaveEar verbose: {message}")
            : null;

    private static string Required(IReadOnlyDictionary<string, string> arguments, string name) =>
        arguments.GetValueOrDefault(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"{name} is required with --register.");

    private static Uri ParseHttpsUri(string value)
    {
        var uri = new Uri(value, UriKind.Absolute);
        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("--issuer must use HTTPS.");
        }
        return uri;
    }
}
