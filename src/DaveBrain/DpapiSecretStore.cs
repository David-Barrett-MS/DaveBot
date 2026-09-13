using System.Text;
using DaveHome;
using Microsoft.AspNetCore.DataProtection;

namespace DaveBrain;

public sealed class DpapiSecretStore(
    IDataProtectionProvider dataProtectionProvider,
    string directory) : ISecretStore
{
    private readonly IDataProtector _protector =
        dataProtectionProvider.CreateProtector("DaveBrain.HueSecrets.v1");

    public async Task<string> StoreAsync(string secret, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new ArgumentException("A secret is required.", nameof(secret));
        }

        Directory.CreateDirectory(directory);
        var reference = $"secret-{Guid.NewGuid():N}";
        var path = GetPath(reference);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                useAsync: true))
            {
                var protectedValue = _protector.Protect(secret);
                await stream.WriteAsync(Encoding.UTF8.GetBytes(protectedValue), cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path);
            return reference;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public async Task<string> GetAsync(string reference, CancellationToken cancellationToken = default)
    {
        var path = GetPath(reference);
        var protectedValue = await File.ReadAllTextAsync(path, cancellationToken);
        return _protector.Unprotect(protectedValue);
    }

    public Task DeleteAsync(string reference, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetPath(reference);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private string GetPath(string reference)
    {
        var prefixLength = reference.StartsWith("secret-", StringComparison.Ordinal)
            ? "secret-".Length
            : reference.StartsWith("hue-", StringComparison.Ordinal)
                ? "hue-".Length
                : 0;
        if (prefixLength == 0 ||
            !Guid.TryParseExact(reference[prefixLength..], "N", out _))
        {
            throw new ArgumentException("The secret reference is invalid.", nameof(reference));
        }

        return Path.Combine(directory, $"{reference}.secret");
    }
}
