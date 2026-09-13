using DaveBrain;
using DaveHome;
using DaveIdentity;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
if (!OperatingSystem.IsWindows())
{
    throw new PlatformNotSupportedException("DaveBrain Phase 1 requires Windows Server.");
}

var stateDirectory = builder.Configuration["DaveBot:StateDirectory"]
    ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "DaveBot");
var issuer = new Uri(builder.Configuration["DaveIdentity:Issuer"] ?? "https://brain.daves.tips");
var speechOptions = SpeechTranscriptionOptions.FromConfiguration(builder.Configuration);
speechOptions.Validate();

builder.WebHost.ConfigureKestrel(options =>
{
    options.ConfigureHttpsDefaults(https => https.ClientCertificateMode = ClientCertificateMode.AllowCertificate);
});

builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/");
    options.Conventions.AllowAnonymousToPage("/Identity/Login");
    options.Conventions.AllowAnonymousToPage("/Identity/AccessDenied");
});
builder.Services.AddAntiforgery();
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = checked(speechOptions.MaximumRequestBytes + 65_536L);
    options.MemoryBufferThreshold = checked(speechOptions.MaximumRequestBytes + 65_536);
});
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(stateDirectory, "keys")))
    .ProtectKeysWithDpapi();
builder.Services.AddSingleton<IHomeStatePersistence>(
    _ => new JsonSnapshotHomeStatePersistence(Path.Combine(stateDirectory, "state.json")));
builder.Services.AddSingleton<IHomeStateStore, HomeStateStore>();
builder.Services.AddSingleton<HomeDataTransferService>();
builder.Services.AddSingleton<ICommandAuditStore, InMemoryCommandAuditStore>();
builder.Services.AddSingleton<ISpeechCommandAuditStore, InMemorySpeechCommandAuditStore>();
builder.Services.AddSingleton<ISecretStore>(
    provider => new DpapiSecretStore(
        provider.GetRequiredService<IDataProtectionProvider>(),
        Path.Combine(stateDirectory, "secrets")));
builder.Services.AddHttpClient<IHueBridgeClient, HueBridgeClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddSingleton<HueHubService>();
builder.Services.AddSingleton<IDeviceProvider>(provider => provider.GetRequiredService<HueHubService>());
builder.Services.AddSingleton<ICommandProcessor, CommandProcessor>();
builder.Services.AddSingleton(speechOptions);
builder.Services.AddSingleton<WavAudioValidator>();
builder.Services.AddHttpClient<ITranscriptionService, FoundryLocalTranscriptionService>(client =>
{
    client.Timeout = Timeout.InfiniteTimeSpan;
});
builder.Services.AddSingleton<SpeechTranscriptionQueue>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<SpeechTranscriptionQueue>());
builder.Services.AddHostedService<HueReconciliationService>();
builder.Services.AddDaveIdentity(new DaveIdentityOptions
{
    Issuer = issuer,
    StatePath = Path.Combine(stateDirectory, "identity.json"),
    TokenSigningCertificateThumbprint =
        builder.Configuration["DaveIdentity:TokenSigningCertificateThumbprint"],
    EarClientCertificateAuthorityThumbprint =
        builder.Configuration["DaveIdentity:EarClientCertificateAuthorityThumbprint"],
    AllowEphemeralCertificates = builder.Environment.IsDevelopment(),
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<IHomeStateStore>().InitializeAsync();
    await scope.ServiceProvider.GetRequiredService<HueHubService>()
        .MigrateLegacyUnassignedLightLocationsAsync();
    var users = scope.ServiceProvider.GetRequiredService<LocalUserService>();
    await users.InitializeAsync();
    var protocolDatabase = scope.ServiceProvider.GetRequiredService<DaveIdentityProtocolDbContext>();
    await protocolDatabase.Database.EnsureCreatedAsync();
    await scope.ServiceProvider.GetRequiredService<OpenIddictProtocolInitializer>().InitializeAsync();
    _ = scope.ServiceProvider.GetRequiredService<EarCertificateAuthority>();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.Use(async (context, next) =>
{
    if (context.User.HasClaim("must_change_password", "true") &&
        !context.Request.Path.StartsWithSegments("/Identity/ChangePassword") &&
        !context.Request.Path.StartsWithSegments("/Identity/Logout"))
    {
        context.Response.Redirect("/Identity/ChangePassword");
        return;
    }

    await next();
});
app.UseAuthorization();

app.MapGet("/health/live", () => Results.Ok(new { status = "live" })).AllowAnonymous();
app.MapGet("/health", async (
    IHomeStateStore homeState,
    IdentityStateStore identityState,
    ITranscriptionService transcription,
    CancellationToken cancellationToken) =>
{
    var dependency = await transcription.GetDependencyStatusAsync(cancellationToken);
    var response = new
    {
        status = dependency.IsAvailable ? "ready" : "unavailable",
        homeRevision = homeState.Current.Revision.Value,
        identityRevision = identityState.Current.Revision,
        transcription = dependency.Code,
        model = SpeechTranscriptionOptions.WhisperTinyModel,
    };
    return dependency.IsAvailable
        ? Results.Ok(response)
        : Results.Json(response, statusCode: StatusCodes.Status503ServiceUnavailable);
}).AllowAnonymous();
app.MapPost(
    "/api/v1/ears/enrollments",
    async (
        EarEnrollmentRequest request,
        ClaimsPrincipal user,
        LocalUserService users,
        EarEnrollmentService enrollment,
        DaveIdentityOptions identityOptions,
        CancellationToken cancellationToken) =>
    {
        var subject = user.FindFirst(OpenIddictConstants.Claims.Subject)?.Value;
        if (!Guid.TryParse(subject, out var userId) ||
            !user.HasScope("davebrain.ear.enroll") ||
            !await users.IsActiveAdministratorAsync(userId, cancellationToken))
        {
            return Results.Forbid();
        }

        var result = await enrollment.EnrollAsync(
            request.Name,
            request.LocationId,
            identityOptions.Issuer.AbsoluteUri,
            subject,
            request.CertificateSigningRequestPem,
            cancellationToken);
        return Results.Created(
            $"/api/v1/ears/{result.Ear.Id}",
            new { earId = result.Ear.Id, certificate = result.CertificatePem, issuer = identityOptions.Issuer });
    })
    .RequireAuthorization(new AuthorizeAttribute
    {
        AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme,
    });
app.MapPost(
    "/api/v1/text-commands",
    async (
        TextCommandRequest request,
        HttpContext httpContext,
        ClaimsPrincipal user,
        IdentityStateStore identityState,
        MtlsCertificateBindingValidator certificateBinding,
        DaveIdentityOptions identityOptions,
        ICommandProcessor commandProcessor,
        CancellationToken cancellationToken) =>
    {
        if (request.EarId == Guid.Empty || !user.HasScope("davebrain.ear.command"))
        {
            return Results.Forbid();
        }

        var ear = await EarRequestAuthorization.FindAuthorizedEarAsync(
            request.EarId,
            httpContext,
            user,
            identityState,
            certificateBinding,
            identityOptions,
            cancellationToken);
        if (ear is null)
        {
            return Results.Forbid();
        }

        var result = await commandProcessor.ProcessAsync(
            request.Transcript,
            new CommandSource($"ear:{ear.Id}", ear.LocationId),
            cancellationToken);
        return Results.Ok(result);
    })
    .RequireAuthorization(new AuthorizeAttribute
    {
        AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme,
    });
app.MapPost(
    "/api/v1/ears/{earId:guid}/heartbeat",
    async (
        Guid earId,
        HttpContext httpContext,
        ClaimsPrincipal user,
        IdentityStateStore identityState,
        MtlsCertificateBindingValidator certificateBinding,
        DaveIdentityOptions identityOptions,
        EarEnrollmentService enrollment,
        CancellationToken cancellationToken) =>
    {
        if (!user.HasScope("davebrain.ear.command"))
        {
            return Results.Forbid();
        }

        var ear = await EarRequestAuthorization.FindAuthorizedEarAsync(
            earId,
            httpContext,
            user,
            identityState,
            certificateBinding,
            identityOptions,
            cancellationToken);
        if (ear is null)
        {
            return Results.Forbid();
        }

        await enrollment.RecordHeartbeatAsync(earId, cancellationToken);
        return Results.Ok(new { enabled = ear.Enabled });
    })
    .RequireAuthorization(new AuthorizeAttribute
    {
        AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme,
    });
app.MapPost(
    "/api/v1/speech-commands",
    async (
        HttpContext httpContext,
        ClaimsPrincipal user,
        IdentityStateStore identityState,
        MtlsCertificateBindingValidator certificateBinding,
        DaveIdentityOptions identityOptions,
        SpeechTranscriptionOptions options,
        WavAudioValidator wavValidator,
        SpeechTranscriptionQueue transcriptionQueue,
        ICommandProcessor commandProcessor,
        ISpeechCommandAuditStore speechAudit,
        CancellationToken cancellationToken) =>
    {
        if (!httpContext.Request.HasFormContentType)
        {
            return Results.BadRequest(new SpeechCommandResponse(
                Guid.NewGuid(),
                SpeechCommandStatus.InvalidAudio,
                ErrorCode: "invalid-content-type"));
        }

        if (httpContext.Request.ContentLength is > 0 &&
            httpContext.Request.ContentLength > options.MaximumRequestBytes + 65_536L)
        {
            return Results.Json(
                new SpeechCommandResponse(
                    Guid.NewGuid(),
                    SpeechCommandStatus.InvalidAudio,
                    ErrorCode: "audio-too-large"),
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        IFormCollection form;
        try
        {
            form = await httpContext.Request.ReadFormAsync(cancellationToken);
        }
        catch (InvalidDataException)
        {
            return Results.BadRequest(new SpeechCommandResponse(
                Guid.NewGuid(),
                SpeechCommandStatus.InvalidAudio,
                ErrorCode: "invalid-form-data"));
        }

        if (!Guid.TryParse(form["earId"], out var earId) ||
            !user.HasScope("davebrain.ear.command"))
        {
            return Results.Forbid();
        }

        var ear = await EarRequestAuthorization.FindAuthorizedEarAsync(
            earId,
            httpContext,
            user,
            identityState,
            certificateBinding,
            identityOptions,
            cancellationToken);
        if (ear is null)
        {
            return Results.Forbid();
        }

        Task AuditAsync(SpeechCommandResponse response) =>
            speechAudit.WriteAsync(
                new SpeechCommandAuditEntry(
                    response.CorrelationId,
                    DateTimeOffset.UtcNow,
                    $"ear:{ear.Id}",
                    response.Status,
                    response.Command?.CorrelationId),
                cancellationToken);

        var audioFile = form.Files.GetFile("audio");
        if (audioFile is null || audioFile.Length > options.MaximumRequestBytes)
        {
            var response = new SpeechCommandResponse(
                Guid.NewGuid(),
                SpeechCommandStatus.InvalidAudio,
                ErrorCode: audioFile is null ? "audio-missing" : "audio-too-large");
            await AuditAsync(response);
            return Results.Json(
                response,
                statusCode: audioFile is null
                    ? StatusCodes.Status400BadRequest
                    : StatusCodes.Status413PayloadTooLarge);
        }

        byte[] audio;
        try
        {
            audio = await SpeechCommandHttp.ReadBoundedAudioAsync(
                audioFile,
                options.MaximumRequestBytes,
                cancellationToken);
        }
        catch (InvalidDataException)
        {
            var response = new SpeechCommandResponse(
                Guid.NewGuid(),
                SpeechCommandStatus.InvalidAudio,
                ErrorCode: "audio-too-large");
            await AuditAsync(response);
            return Results.Json(
                response,
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        var wav = wavValidator.Validate(audio);
        if (!wav.Succeeded)
        {
            var response = new SpeechCommandResponse(
                Guid.NewGuid(),
                SpeechCommandStatus.InvalidAudio,
                ErrorCode: SpeechCommandHttp.ToAudioErrorCode(wav.Failure));
            await AuditAsync(response);
            return Results.BadRequest(response);
        }

        var queued = await transcriptionQueue.EnqueueAsync(audio, cancellationToken);
        if (queued.Status == SpeechQueueStatus.Overloaded)
        {
            httpContext.Response.Headers["Retry-After"] = "2";
            var response = new SpeechCommandResponse(
                queued.CorrelationId,
                SpeechCommandStatus.Overloaded,
                ErrorCode: "transcription-queue-full");
            await AuditAsync(response);
            return Results.Json(
                response,
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        var transcription = queued.Transcription!;
        if (transcription.Status != TranscriptionStatus.Succeeded)
        {
            var response = new SpeechCommandResponse(
                queued.CorrelationId,
                SpeechCommandHttp.ToSpeechCommandStatus(transcription.Status),
                ErrorCode: SpeechCommandHttp.ToTranscriptionErrorCode(transcription.Status),
                TranscriptionOutcome: transcription.Status);
            await AuditAsync(response);
            return Results.Json(
                response,
                statusCode: SpeechCommandHttp.ToHttpStatus(transcription.Status));
        }

        var command = await commandProcessor.ProcessAsync(
            transcription.Transcript,
            new CommandSource($"ear:{ear.Id}", ear.LocationId),
            cancellationToken);
        var completed = new SpeechCommandResponse(
            queued.CorrelationId,
            SpeechCommandStatus.Completed,
            transcription.Transcript,
            command,
            TranscriptionOutcome: transcription.Status);
        await AuditAsync(completed);
        return Results.Ok(completed);
    })
    .RequireAuthorization(new AuthorizeAttribute
    {
        AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme,
    });
app.MapStaticAssets();
app.MapDaveIdentityEndpoints();
app.MapRazorPages()
    .WithStaticAssets();

app.Run();

public partial class Program;

public sealed record EarEnrollmentRequest(
    string Name,
    Guid LocationId,
    string CertificateSigningRequestPem);

public sealed record TextCommandRequest(string Transcript, Guid EarId);

internal static class SpeechCommandHttp
{
    public static async Task<byte[]> ReadBoundedAudioAsync(
        IFormFile audioFile,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var input = audioFile.OpenReadStream();
        using var output = new MemoryStream((int)audioFile.Length);
        var buffer = new byte[81_920];
        var total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return output.ToArray();
            }

            if (read > maximumBytes - total)
            {
                throw new InvalidDataException("Audio exceeded the configured limit.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            total += read;
        }
    }

    public static string ToAudioErrorCode(WavValidationFailure failure) => failure switch
    {
        WavValidationFailure.TooLarge => "audio-too-large",
        WavValidationFailure.TooLong => "audio-too-long",
        WavValidationFailure.UnsupportedFormat => "unsupported-wav-format",
        WavValidationFailure.Empty => "audio-empty",
        WavValidationFailure.Truncated => "truncated-wav",
        _ => "malformed-wav",
    };

    public static SpeechCommandStatus ToSpeechCommandStatus(TranscriptionStatus status) => status switch
    {
        TranscriptionStatus.NoSpeech => SpeechCommandStatus.NoSpeech,
        TranscriptionStatus.Unavailable => SpeechCommandStatus.DependencyUnavailable,
        TranscriptionStatus.TimedOut => SpeechCommandStatus.TimedOut,
        TranscriptionStatus.Canceled => SpeechCommandStatus.Canceled,
        _ => SpeechCommandStatus.TranscriptionFailed,
    };

    public static string ToTranscriptionErrorCode(TranscriptionStatus status) => status switch
    {
        TranscriptionStatus.NoSpeech => "no-speech",
        TranscriptionStatus.Unavailable => "foundry-local-unavailable",
        TranscriptionStatus.TimedOut => "transcription-timeout",
        TranscriptionStatus.Canceled => "request-canceled",
        _ => "transcription-failed",
    };

    public static int ToHttpStatus(TranscriptionStatus status) => status switch
    {
        TranscriptionStatus.NoSpeech => StatusCodes.Status422UnprocessableEntity,
        TranscriptionStatus.Unavailable => StatusCodes.Status503ServiceUnavailable,
        TranscriptionStatus.TimedOut => StatusCodes.Status504GatewayTimeout,
        TranscriptionStatus.Canceled => 499,
        _ => StatusCodes.Status502BadGateway,
    };
}

internal static class EarRequestAuthorization
{
    public static async Task<EarRecord?> FindAuthorizedEarAsync(
        Guid earId,
        HttpContext httpContext,
        ClaimsPrincipal user,
        IdentityStateStore identityState,
        MtlsCertificateBindingValidator certificateBinding,
        DaveIdentityOptions identityOptions,
        CancellationToken cancellationToken)
    {
        var ear = await identityState.FindEarAsync(earId, cancellationToken);
        var certificate = await httpContext.Connection.GetClientCertificateAsync(cancellationToken);
        var subject = user.FindFirst(OpenIddictConstants.Claims.Subject)?.Value;
        var clientId = user.FindFirst("client_id")?.Value;
        var tokenIssuer = user.FindFirst("iss")?.Value;
        return ear is null || certificate is null ||
            !string.Equals(ear.OAuthIssuer.TrimEnd('/'), identityOptions.Issuer.AbsoluteUri.TrimEnd('/'),
                StringComparison.Ordinal) ||
            !string.Equals(tokenIssuer?.TrimEnd('/'), ear.OAuthIssuer.TrimEnd('/'),
                StringComparison.Ordinal) ||
            !string.Equals(subject, ear.OAuthSubject, StringComparison.Ordinal) ||
            !string.Equals(clientId, ear.OAuthClientId, StringComparison.Ordinal) ||
            !await certificateBinding.IsValidAsync(earId, user, certificate, cancellationToken)
            ? null
            : ear;
    }
}
