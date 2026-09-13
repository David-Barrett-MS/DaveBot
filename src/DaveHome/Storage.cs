using System.Text.Json;

namespace DaveHome;

public interface IHomeStatePersistence
{
    Task<HomeStateDocument?> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(
        HomeStateDocument document,
        StoreRevision expectedRevision,
        CancellationToken cancellationToken = default);
}

public interface IHomeStateStore
{
    HomeState Current { get; }

    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<HomeState> UpdateAsync(
        Func<HomeState, HomeStateDocument> update,
        StoreRevision expectedRevision,
        CancellationToken cancellationToken = default);
}

public sealed class HomeStateStore(IHomeStatePersistence persistence) : IHomeStateStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HomeState _current = HomeState.FromDocument(HomeStateDocument.Empty);
    private bool _initialized;

    public HomeState Current => _current;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            var document = await persistence.LoadAsync(cancellationToken) ?? HomeStateDocument.Empty;
            _current = HomeState.FromDocument(document);
            _initialized = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<HomeState> UpdateAsync(
        Func<HomeState, HomeStateDocument> update,
        StoreRevision expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureInitialized();
            if (_current.Revision != expectedRevision)
            {
                throw new InvalidOperationException("The home state was changed by another operation.");
            }

            var candidate = update(_current) with
            {
                SchemaVersion = HomeStateDocument.CurrentSchemaVersion,
                Revision = new StoreRevision(_current.Revision.Value + 1),
            };
            var next = HomeState.FromDocument(candidate);
            await persistence.SaveAsync(next.Document, expectedRevision, cancellationToken);
            _current = next;
            return next;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void EnsureInitialized()
    {
        if (!_initialized)
        {
            throw new InvalidOperationException("The home state store has not been initialized.");
        }
    }
}

public sealed class JsonSnapshotHomeStatePersistence(string path) : IHomeStatePersistence
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _backupPath = $"{path}.bak";

    public async Task<HomeStateDocument?> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                return await LoadDocumentWithoutLockAsync(cancellationToken);
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
        HomeStateDocument document,
        StoreRevision expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        HomeState.Validate(document);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var existing = await LoadDocumentWithoutLockAsync(cancellationToken);
            var actualRevision = existing?.Revision ?? StoreRevision.Initial;
            if (actualRevision != expectedRevision)
            {
                throw new InvalidOperationException("The persisted home state revision does not match.");
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

    private async Task<HomeStateDocument?> LoadDocumentWithoutLockAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        return await LoadDocumentAsync(path, cancellationToken);
    }

    private static async Task<HomeStateDocument?> LoadDocumentAsync(
        string documentPath,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(documentPath);
        return await JsonSerializer.DeserializeAsync<HomeStateDocument>(
            stream,
            SerializerOptions,
            cancellationToken);
    }
}

public sealed record CommandAuditEntry(
    Guid Id,
    DateTimeOffset OccurredUtc,
    string Source,
    string Summary,
    bool Succeeded,
    string? Transcript = null);

public interface ICommandAuditStore
{
    Task WriteAsync(CommandAuditEntry entry, CancellationToken cancellationToken = default);

    IReadOnlyList<CommandAuditEntry> ReadRecent(int maximumCount);
}

public sealed class InMemoryCommandAuditStore(int capacity = 500) : ICommandAuditStore
{
    private readonly object _sync = new();
    private readonly Queue<CommandAuditEntry> _entries = new();
    private readonly int _capacity = capacity > 0
        ? capacity
        : throw new ArgumentOutOfRangeException(nameof(capacity));

    public Task WriteAsync(CommandAuditEntry entry, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > _capacity)
            {
                _entries.Dequeue();
            }
        }

        return Task.CompletedTask;
    }

    public IReadOnlyList<CommandAuditEntry> ReadRecent(int maximumCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCount);
        lock (_sync)
        {
            return _entries.Reverse().Take(maximumCount).ToArray();
        }
    }
}
