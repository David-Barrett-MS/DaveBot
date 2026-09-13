using System.Text.Json;

namespace DaveIdentity;

public static class DaveRoles
{
    public const string Admin = "Admin";
    public const string User = "User";
}

public sealed record IdentityUserRecord(
    Guid Id,
    string UserName,
    string NormalizedUserName,
    string PasswordHash,
    IReadOnlyList<string> Roles,
    bool Enabled,
    int AccessFailedCount,
    DateTimeOffset? LockoutEndUtc,
    string SecurityStamp,
    bool MustChangePassword);

public sealed record OAuthClientRecord(
    string ClientId,
    IReadOnlyList<string> AllowedScopes,
    IReadOnlyList<string> AllowedGrantTypes,
    bool Enabled);

public sealed record OAuthGrantRecord(
    Guid Id,
    Guid UserId,
    string ClientId,
    IReadOnlyList<string> Scopes,
    string? MtlsCertificateThumbprint,
    string GrantType,
    string TokenHash,
    DateTimeOffset ExpiresUtc,
    DateTimeOffset? RevokedUtc);

public sealed record EarRecord(
    Guid Id,
    string Name,
    Guid LocationId,
    string OAuthIssuer,
    string OAuthSubject,
    string OAuthClientId,
    string MtlsCertificateThumbprint,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? LastSeenUtc,
    bool Enabled);

public sealed record IdentityStateDocument(
    int SchemaVersion,
    long Revision,
    IReadOnlyList<IdentityUserRecord> Users,
    IReadOnlyList<OAuthClientRecord> Clients,
    IReadOnlyList<OAuthGrantRecord> Grants,
    IReadOnlyList<EarRecord> Ears)
{
    public const int CurrentSchemaVersion = 1;

    public static IdentityStateDocument Empty { get; } = new(
        CurrentSchemaVersion,
        Revision: 0,
        Array.Empty<IdentityUserRecord>(),
        Array.Empty<OAuthClientRecord>(),
        Array.Empty<OAuthGrantRecord>(),
        Array.Empty<EarRecord>());
}

public interface IIdentityUserStore
{
    Task<IdentityUserRecord?> FindByNormalizedUserNameAsync(
        string normalizedUserName,
        CancellationToken cancellationToken = default);
}

public interface IOAuthClientStore
{
    Task<OAuthClientRecord?> FindByClientIdAsync(
        string clientId,
        CancellationToken cancellationToken = default);
}

public interface IOAuthGrantStore
{
    Task RevokeForUserAsync(Guid userId, CancellationToken cancellationToken = default);
}

public interface IIdentityStatePersistence
{
    Task<IdentityStateDocument?> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(
        IdentityStateDocument document,
        long expectedRevision,
        CancellationToken cancellationToken = default);
}

public sealed class JsonSnapshotIdentityPersistence(string path) : IIdentityStatePersistence
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _backupPath = $"{path}.bak";

    public async Task<IdentityStateDocument?> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            try
            {
                return await LoadWithoutLockAsync(cancellationToken);
            }
            catch (JsonException) when (File.Exists(_backupPath))
            {
                return await LoadDocumentAsync(_backupPath, cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        IdentityStateDocument document,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        Validate(document);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var existing = await LoadWithoutLockAsync(cancellationToken);
            if ((existing?.Revision ?? 0) != expectedRevision)
            {
                throw new InvalidOperationException("The persisted identity state revision does not match.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path) ??
                throw new InvalidOperationException("The snapshot path must include a directory."));
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
                    await JsonSerializer.SerializeAsync(
                        stream,
                        document,
                        SerializerOptions,
                        cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                    stream.Flush(flushToDisk: true);
                }

                if (File.Exists(path))
                {
                    File.Replace(temporaryPath, path, _backupPath, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(temporaryPath, path);
                }
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IdentityStateDocument?> LoadWithoutLockAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        return await LoadDocumentAsync(path, cancellationToken);
    }

    private static async Task<IdentityStateDocument?> LoadDocumentAsync(
        string documentPath,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(documentPath);
        return await JsonSerializer.DeserializeAsync<IdentityStateDocument>(
            stream,
            SerializerOptions,
            cancellationToken);
    }

    public static void Validate(IdentityStateDocument document)
    {
        if (document.SchemaVersion != IdentityStateDocument.CurrentSchemaVersion ||
            document.Revision < 0)
        {
            throw new InvalidOperationException("The identity state document is invalid.");
        }

        if (document.Users.GroupBy(user => user.Id).Any(group => group.Count() != 1) ||
            document.Users.GroupBy(user => user.NormalizedUserName).Any(group => group.Count() != 1))
        {
            throw new InvalidOperationException("Identity users must have unique IDs and usernames.");
        }
    }
}

public sealed class IdentityStateStore(
    IIdentityStatePersistence persistence) : IIdentityUserStore, IOAuthClientStore, IOAuthGrantStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IdentityStateDocument _current = IdentityStateDocument.Empty;
    private bool _initialized;

    public IdentityStateDocument Current => _current;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            _current = await persistence.LoadAsync(cancellationToken) ?? IdentityStateDocument.Empty;
            JsonSnapshotIdentityPersistence.Validate(_current);
            _initialized = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IdentityStateDocument> UpdateAsync(
        Func<IdentityStateDocument, IdentityStateDocument> update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureInitialized();
            var next = update(_current) with
            {
                SchemaVersion = IdentityStateDocument.CurrentSchemaVersion,
                Revision = _current.Revision + 1,
            };
            await persistence.SaveAsync(next, _current.Revision, cancellationToken);
            _current = next;
            return next;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<IdentityUserRecord?> FindByNormalizedUserNameAsync(
        string normalizedUserName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInitialized();
        return Task.FromResult(_current.Users.SingleOrDefault(
            user => string.Equals(user.NormalizedUserName, normalizedUserName, StringComparison.Ordinal)));
    }

    public Task<IdentityUserRecord?> FindUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInitialized();
        return Task.FromResult(_current.Users.SingleOrDefault(user => user.Id == userId));
    }

    public Task<OAuthClientRecord?> FindByClientIdAsync(
        string clientId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInitialized();
        return Task.FromResult(_current.Clients.SingleOrDefault(
            client => string.Equals(client.ClientId, clientId, StringComparison.Ordinal)));
    }

    public Task<EarRecord?> FindEarAsync(Guid earId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInitialized();
        return Task.FromResult(_current.Ears.SingleOrDefault(ear => ear.Id == earId));
    }

    public IReadOnlyList<IdentityUserRecord> GetUsers()
    {
        EnsureInitialized();
        return _current.Users
            .OrderBy(user => user.UserName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<EarRecord> GetEars()
    {
        EnsureInitialized();
        return _current.Ears
            .OrderBy(ear => ear.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task RevokeForUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await UpdateAsync(document => document with
        {
            Grants = document.Grants.Select(grant => grant.UserId == userId && grant.RevokedUtc is null
                ? grant with { RevokedUtc = DateTimeOffset.UtcNow }
                : grant).ToArray(),
        }, cancellationToken);
    }

    private void EnsureInitialized()
    {
        if (!_initialized)
        {
            throw new InvalidOperationException("The identity state store has not been initialized.");
        }
    }
}
