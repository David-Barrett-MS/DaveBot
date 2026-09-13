using System.Diagnostics;
using System.ComponentModel;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace DaveEar;

public sealed record EarCredentials(string PrivateKeyPem, string CertificatePem, string RefreshToken);

public interface ICredentialStore
{
    Task SaveAsync(EarCredentials credentials, CancellationToken cancellationToken = default);
    Task<EarCredentials?> LoadAsync(CancellationToken cancellationToken = default);
    Task DeleteAsync(CancellationToken cancellationToken = default);
}

public static class CredentialStoreFactory
{
    public static ICredentialStore Create(DaveEarPaths paths)
    {
        var identifier = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(paths.Root))).ToLowerInvariant();
        return OperatingSystem.IsWindows()
            ? new DpapiCredentialStore(Path.Combine(paths.Root, "credentials.bin"))
            : new LibSecretCredentialStore($"daveear-{identifier}");
    }
}

[SupportedOSPlatform("windows")]
public sealed class DpapiCredentialStore(string path) : ICredentialStore
{
    public async Task SaveAsync(EarCredentials credentials, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        SecureFile.EnsurePrivateDirectory(Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Credential path requires a directory."));
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(credentials);
        try
        {
            var encrypted = ProtectedData.Protect(plaintext, null, DataProtectionScope.LocalMachine);
            await using var output = SecureFile.CreatePrivateFile(path);
            await output.WriteAsync(encrypted, cancellationToken);
            await output.FlushAsync(cancellationToken);
            output.Flush(flushToDisk: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public async Task<EarCredentials?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        SecureFile.EnsurePrivateFile(path);
        var encrypted = await File.ReadAllBytesAsync(path, cancellationToken);
        byte[] plaintext;
        try
        {
            plaintext = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.LocalMachine);
        }
        catch (CryptographicException exception)
        {
            throw new InvalidOperationException("DaveEar credentials cannot be opened; re-enrollment is required.", exception);
        }

        try
        {
            return JsonSerializer.Deserialize<EarCredentials>(plaintext)
                ?? throw new InvalidOperationException("DaveEar credentials are invalid.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        return Task.CompletedTask;
    }
}

// Linux persists secrets only in the desktop/service Secret Service, never in the state directory.
public sealed class LibSecretCredentialStore(string itemName) : ICredentialStore
{
    private const string Schema = "com.daves.tips.daveear";

    public async Task SaveAsync(EarCredentials credentials, CancellationToken cancellationToken = default)
    {
        var serialized = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(credentials));
        await ExecuteAsync(serialized, cancellationToken, "store", $"--label=DaveEar {itemName}", Schema, "name", itemName);
    }

    public async Task<EarCredentials?> LoadAsync(CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync(null, cancellationToken, "lookup", Schema, "name", itemName);
        return string.IsNullOrWhiteSpace(result)
            ? null
            : JsonSerializer.Deserialize<EarCredentials>(Convert.FromBase64String(result.Trim()));
    }

    public async Task DeleteAsync(CancellationToken cancellationToken = default) =>
        await ExecuteAsync(null, cancellationToken, "clear", Schema, "name", itemName);

    private static async Task<string> ExecuteAsync(
        string? standardInput,
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        try
        {
            var info = new ProcessStartInfo("secret-tool")
            {
                RedirectStandardInput = standardInput is not null,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }

            using var process = Process.Start(info)
                ?? throw new InvalidOperationException("Could not start secret-tool.");
            if (standardInput is not null)
            {
                await process.StandardInput.WriteAsync(standardInput);
                process.StandardInput.Close();
            }

            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"The OS Secret Service is unavailable (secret-tool exit {process.ExitCode}). Install and unlock a Secret Service provider before registering DaveEar.");
            }

            return output;
        }
        catch (Win32Exception exception)
        {
            throw new PlatformNotSupportedException(
                "Linux DaveEar requires libsecret's secret-tool and an unlocked Secret Service provider.", exception);
        }
    }
}

public static class SecureFile
{
    public static void EnsurePrivateDirectory(string directory)
    {
        Directory.CreateDirectory(directory);
        if (OperatingSystem.IsWindows())
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            var identity = System.Security.Principal.WindowsIdentity.GetCurrent().User
                ?? throw new InvalidOperationException("A Windows service identity is required.");
            security.AddAccessRule(new FileSystemAccessRule(
                identity, FileSystemRights.FullControl, AccessControlType.Allow));
            new DirectoryInfo(directory).SetAccessControl(security);
        }
        else
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    public static FileStream CreatePrivateFile(string path)
    {
        EnsurePrivateDirectory(Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("A file directory is required."));
        var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        EnsurePrivateFile(path);
        return stream;
    }

    public static void EnsurePrivateFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            var identity = System.Security.Principal.WindowsIdentity.GetCurrent().User
                ?? throw new InvalidOperationException("A Windows service identity is required.");
            security.AddAccessRule(new FileSystemAccessRule(
                identity, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);
        }
        else
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
