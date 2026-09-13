using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace DaveEar;

public sealed class HttpCommandUploader(
    DaveEarConfiguration configuration,
    MtlsAccessTokenProvider tokens) : ICommandUploader
{
    public async Task<UploadResult> UploadAsync(
        Guid earId,
        Guid segmentId,
        ReadOnlyMemory<byte> wav,
        CancellationToken cancellationToken)
    {
        try
        {
            using var certificate = X509Certificate2.CreateFromPem(tokens.Credentials.CertificatePem, tokens.Credentials.PrivateKeyPem);
            using var handler = new HttpClientHandler();
            handler.ClientCertificates.Add(certificate);
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(45) };
            var token = await tokens.GetAsync(cancellationToken);
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(earId.ToString()), "earId");
            form.Add(new StringContent(segmentId.ToString()), "segmentId");
            var audio = new ByteArrayContent(wav.ToArray());
            audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(audio, "audio", $"{segmentId:N}.wav");
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri(configuration.EffectiveMtlsEndpoint, "/api/v1/speech-commands"))
            {
                Content = form,
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var correlation = await TryReadCorrelationAsync(response, cancellationToken);
                return new UploadResult(UploadDisposition.Accepted, correlation is null ? "accepted-without-correlation" : "accepted", correlation);
            }
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new UploadResult(UploadDisposition.AuthenticationFailure, "authentication-rejected");
            }
            if ((int)response.StatusCode == 408 || (int)response.StatusCode == 429 || (int)response.StatusCode >= 500)
            {
                return new UploadResult(
                    UploadDisposition.Retry,
                    $"server-{(int)response.StatusCode}",
                    RetryAfter: response.Headers.RetryAfter?.Delta);
            }
            return new UploadResult(UploadDisposition.Rejected, $"server-{(int)response.StatusCode}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A client timeout can occur after DaveBrain accepted the body; it is not safe to resend.
            return new UploadResult(UploadDisposition.Ambiguous, "upload-timeout");
        }
        catch (HttpRequestException)
        {
            // Network failures do not prove that DaveBrain did not receive the request.
            return new UploadResult(UploadDisposition.Ambiguous, "network-outcome-unknown");
        }
        catch (InvalidOperationException) when (tokens.RequiresReenrollment)
        {
            return new UploadResult(UploadDisposition.AuthenticationFailure, "re-enrollment-required");
        }
    }

    public async Task<HeartbeatResult> HeartbeatAsync(Guid earId, CancellationToken cancellationToken)
    {
        try
        {
            using var certificate = X509Certificate2.CreateFromPem(tokens.Credentials.CertificatePem, tokens.Credentials.PrivateKeyPem);
            using var handler = new HttpClientHandler();
            handler.ClientCertificates.Add(certificate);
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri(configuration.EffectiveMtlsEndpoint, $"/api/v1/ears/{earId:D}/heartbeat"))
            {
                Content = new ByteArrayContent([]),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetAsync(cancellationToken));
            using var response = await client.SendAsync(request, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new HeartbeatResult(false, "authentication-rejected");
            }
            if (!response.IsSuccessStatusCode)
            {
                return new HeartbeatResult(true, $"server-{(int)response.StatusCode}");
            }
            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
            var enabled = document.RootElement.TryGetProperty("enabled", out var property) && property.ValueKind == JsonValueKind.True;
            return new HeartbeatResult(enabled, enabled ? "enabled" : "disabled");
        }
        catch (InvalidOperationException) when (tokens.RequiresReenrollment)
        {
            return new HeartbeatResult(false, "re-enrollment-required");
        }
        catch (HttpRequestException)
        {
            return new HeartbeatResult(true, "network-unavailable");
        }
    }

    private static async Task<Guid?> TryReadCorrelationAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
            return document.RootElement.TryGetProperty("correlationId", out var property) &&
                Guid.TryParse(property.GetString(), out var correlation)
                ? correlation : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed record SpoolItem(Guid SegmentId, DateTimeOffset CreatedUtc, int Attempts, DateTimeOffset DueUtc, bool Held);

public sealed class RetrySpool(DaveEarPaths paths, DaveEarConfiguration configuration)
{
    private const string MetadataExtension = ".json";
    private const string AudioExtension = ".wav";

    public async Task<bool> SaveForRetryAsync(
        Guid segmentId,
        ReadOnlyMemory<byte> wav,
        int attempts,
        TimeSpan? serverDelay,
        CancellationToken cancellationToken)
    {
        SecureFile.EnsurePrivateDirectory(paths.SpoolPath);
        await PurgeExpiredAsync(cancellationToken);
        var targetPath = AudioPath(segmentId);
        var existingBytes = Directory.EnumerateFiles(paths.SpoolPath, $"*{AudioExtension}")
            .Where(path => !string.Equals(path, targetPath, StringComparison.OrdinalIgnoreCase))
            .Sum(path => new FileInfo(path).Length);
        if (wav.Length > configuration.MaximumSpoolBytes - existingBytes)
        {
            return false;
        }

        var item = new SpoolItem(
            segmentId,
            DateTimeOffset.UtcNow,
            attempts,
            DateTimeOffset.UtcNow.Add(serverDelay ?? ExponentialBackoff(attempts)),
            Held: false);
        await WriteAsync(item, wav, cancellationToken);
        return true;
    }

    public async Task<bool> SaveHeldAsync(Guid segmentId, ReadOnlyMemory<byte> wav, CancellationToken cancellationToken)
    {
        SecureFile.EnsurePrivateDirectory(paths.SpoolPath);
        await PurgeExpiredAsync(cancellationToken);
        var targetPath = AudioPath(segmentId);
        var existingBytes = Directory.EnumerateFiles(paths.SpoolPath, $"*{AudioExtension}")
            .Where(path => !string.Equals(path, targetPath, StringComparison.OrdinalIgnoreCase))
            .Sum(path => new FileInfo(path).Length);
        if (wav.Length > configuration.MaximumSpoolBytes - existingBytes)
        {
            return false;
        }

        await WriteAsync(new SpoolItem(segmentId, DateTimeOffset.UtcNow, 0, DateTimeOffset.MaxValue, Held: true), wav, cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<(SpoolItem Item, byte[] Wav)>> GetDueAsync(CancellationToken cancellationToken)
    {
        await PurgeExpiredAsync(cancellationToken);
        if (!Directory.Exists(paths.SpoolPath))
        {
            return [];
        }

        var result = new List<(SpoolItem, byte[])>();
        foreach (var metadataPath in Directory.EnumerateFiles(paths.SpoolPath, $"*{MetadataExtension}"))
        {
            try
            {
                SecureFile.EnsurePrivateFile(metadataPath);
                var item = JsonSerializer.Deserialize<SpoolItem>(await File.ReadAllTextAsync(metadataPath, cancellationToken));
                if (item is not null && !item.Held && item.DueUtc <= DateTimeOffset.UtcNow)
                {
                    var audioPath = AudioPath(item.SegmentId);
                    if (File.Exists(audioPath))
                    {
                        SecureFile.EnsurePrivateFile(audioPath);
                        result.Add((item, await File.ReadAllBytesAsync(audioPath, cancellationToken)));
                    }
                    else
                    {
                        File.Delete(metadataPath);
                    }
                }
            }
            catch (JsonException)
            {
                File.Delete(metadataPath);
            }
        }
        return result;
    }

    public Task RemoveAsync(Guid segmentId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Delete(segmentId);
        return Task.CompletedTask;
    }

    private async Task WriteAsync(SpoolItem item, ReadOnlyMemory<byte> wav, CancellationToken cancellationToken)
    {
        SecureFile.EnsurePrivateDirectory(paths.SpoolPath);
        await using (var audio = SecureFile.CreatePrivateFile(AudioPath(item.SegmentId)))
        {
            await audio.WriteAsync(wav, cancellationToken);
            await audio.FlushAsync(cancellationToken);
            audio.Flush(true);
        }
        await using (var metadata = SecureFile.CreatePrivateFile(MetadataPath(item.SegmentId)))
        {
            await JsonSerializer.SerializeAsync(metadata, item, cancellationToken: cancellationToken);
            await metadata.FlushAsync(cancellationToken);
            metadata.Flush(true);
        }
    }

    private Task PurgeExpiredAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(paths.SpoolPath))
        {
            return Task.CompletedTask;
        }
        var expiry = DateTimeOffset.UtcNow.AddHours(-configuration.MaximumSpoolAgeHours);
        foreach (var metadataPath in Directory.EnumerateFiles(paths.SpoolPath, $"*{MetadataExtension}"))
        {
            try
            {
                var item = JsonSerializer.Deserialize<SpoolItem>(File.ReadAllText(metadataPath));
                if (item is null || item.CreatedUtc < expiry)
                {
                    File.Delete(metadataPath);
                    if (item is not null) DeleteAudio(item.SegmentId);
                }
            }
            catch (JsonException)
            {
                File.Delete(metadataPath);
            }
        }
        return Task.CompletedTask;
    }

    private static TimeSpan ExponentialBackoff(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(300, 2 * Math.Pow(2, Math.Clamp(attempt, 0, 7))));
    private string AudioPath(Guid id) => Path.Combine(paths.SpoolPath, $"{id:N}{AudioExtension}");
    private string MetadataPath(Guid id) => Path.Combine(paths.SpoolPath, $"{id:N}{MetadataExtension}");
    private void Delete(Guid id) { DeleteAudio(id); var metadata = MetadataPath(id); if (File.Exists(metadata)) File.Delete(metadata); }
    private void DeleteAudio(Guid id) { var audio = AudioPath(id); if (File.Exists(audio)) File.Delete(audio); }
}
