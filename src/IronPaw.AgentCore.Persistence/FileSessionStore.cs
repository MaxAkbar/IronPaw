using System.Text.Json;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Persistence;

public sealed class FileSessionStore : ISessionStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly FileSessionStoreOptions _options;
    private readonly JsonSerializerOptions _jsonOptions;

    private DateTimeOffset _cacheExpiresAt = DateTimeOffset.MinValue;
    private Dictionary<string, SessionEntry> _cache = new(StringComparer.Ordinal);

    public FileSessionStore(FileSessionStoreOptions options)
    {
        _options = options;
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };
    }

    public async Task<SessionEntry?> GetAsync(SessionKey key, CancellationToken ct)
    {
        var all = await LoadInternalAsync(ct);
        return all.TryGetValue(key.Value, out var entry) ? Clone(entry) : null;
    }

    public async Task UpsertAsync(SessionKey key, Func<SessionEntry?, SessionEntry> mutate, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var all = await LoadInternalCoreAsync(ct);
            all.TryGetValue(key.Value, out var existing);
            var updated = mutate(existing is null ? null : Clone(existing));
            all[key.Value] = Clone(updated) with { UpdatedAt = DateTimeOffset.UtcNow };
            await SaveInternalCoreAsync(all, ct);
            _cache = CloneMap(all);
            _cacheExpiresAt = DateTimeOffset.UtcNow.Add(_options.CacheTtl);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyDictionary<SessionKey, SessionEntry>> LoadAllAsync(CancellationToken ct)
    {
        var map = await LoadInternalAsync(ct);
        return map.ToDictionary(
            item => new SessionKey(item.Key),
            item => Clone(item.Value));
    }

    private async Task<Dictionary<string, SessionEntry>> LoadInternalAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await LoadInternalCoreAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Dictionary<string, SessionEntry>> LoadInternalCoreAsync(CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow < _cacheExpiresAt)
        {
            return CloneMap(_cache);
        }

        var filePath = _options.StoreFilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        if (!File.Exists(filePath))
        {
            _cache = new Dictionary<string, SessionEntry>(StringComparer.Ordinal);
            _cacheExpiresAt = DateTimeOffset.UtcNow.Add(_options.CacheTtl);
            return CloneMap(_cache);
        }

        var retries = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var text = await File.ReadAllTextAsync(filePath, ct);
                if (string.IsNullOrWhiteSpace(text))
                {
                    throw new InvalidDataException("Session store file was empty.");
                }

                var data = JsonSerializer.Deserialize<Dictionary<string, SessionEntry>>(text, _jsonOptions)
                    ?? new Dictionary<string, SessionEntry>(StringComparer.Ordinal);

                _cache = CloneMap(data);
                _cacheExpiresAt = DateTimeOffset.UtcNow.Add(_options.CacheTtl);
                return CloneMap(data);
            }
            catch (Exception ex) when (ShouldRetryRead(ex) && retries < _options.MaxReadRetries)
            {
                retries++;
                await Task.Delay(_options.RetryDelayMilliseconds, ct);
            }
        }
    }

    private async Task SaveInternalCoreAsync(Dictionary<string, SessionEntry> data, CancellationToken ct)
    {
        var filePath = _options.StoreFilePath;
        var directory = Path.GetDirectoryName(filePath)!;
        Directory.CreateDirectory(directory);

        var tempPath = $"{filePath}.{Guid.NewGuid():N}.tmp";
        var content = JsonSerializer.Serialize(data, _jsonOptions);
        await File.WriteAllTextAsync(tempPath, content, ct);

        if (File.Exists(filePath))
        {
            File.Replace(tempPath, filePath, destinationBackupFileName: null);
            return;
        }

        File.Move(tempPath, filePath);
    }

    private static bool ShouldRetryRead(Exception ex)
    {
        return ex is IOException or InvalidDataException or JsonException;
    }

    private static Dictionary<string, SessionEntry> CloneMap(Dictionary<string, SessionEntry> source)
    {
        return source.ToDictionary(
            pair => pair.Key,
            pair => Clone(pair.Value),
            StringComparer.Ordinal);
    }

    private static SessionEntry Clone(SessionEntry entry)
    {
        var diagnostics = entry.Diagnostics is null
            ? null
            : new Dictionary<string, object?>(entry.Diagnostics, StringComparer.Ordinal);

        return entry with { Diagnostics = diagnostics };
    }
}
