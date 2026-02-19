using System.Text.Json;
using System.Text.Json.Serialization;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Persistence;

public sealed class FileMemoryStore : IMemoryStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly FileMemoryStoreOptions _options;

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public FileMemoryStore(FileMemoryStoreOptions options)
    {
        _options = options;
        Directory.CreateDirectory(Path.GetDirectoryName(options.GlobalMemoryPath)!);
        Directory.CreateDirectory(options.SessionMemoriesDirectory);
    }

    public async Task<IReadOnlyList<MemoryEntry>> LoadGlobalAsync(CancellationToken ct)
    {
        return await LoadFromFileAsync(_options.GlobalMemoryPath, ct);
    }

    public async Task<IReadOnlyList<MemoryEntry>> LoadSessionAsync(SessionKey sessionKey, CancellationToken ct)
    {
        var path = ResolveSessionMemoryPath(sessionKey);
        return await LoadFromFileAsync(path, ct);
    }

    public async Task<MemoryEntry> AddAsync(MemoryEntry entry, CancellationToken ct)
    {
        var truncatedContent = entry.Content.Length > _options.MaxMemoryContentChars
            ? entry.Content[.._options.MaxMemoryContentChars]
            : entry.Content;
        var finalEntry = entry with { Content = truncatedContent };

        var path = finalEntry.Scope == MemoryScope.Global
            ? _options.GlobalMemoryPath
            : ResolveSessionMemoryPath(new SessionKey(finalEntry.SessionId!));

        var maxEntries = finalEntry.Scope == MemoryScope.Global
            ? _options.MaxGlobalMemories
            : _options.MaxSessionMemories;

        await _gate.WaitAsync(ct);
        try
        {
            var entries = (await LoadFromFileInternalAsync(path, ct)).ToList();
            while (entries.Count >= maxEntries)
            {
                entries.RemoveAt(0);
            }

            entries.Add(finalEntry);
            await SaveToFileAsync(path, entries, ct);
            return finalEntry;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> RemoveAsync(string memoryId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (await TryRemoveFromFileAsync(_options.GlobalMemoryPath, memoryId, ct))
            {
                return true;
            }

            if (!Directory.Exists(_options.SessionMemoriesDirectory))
            {
                return false;
            }

            foreach (var file in Directory.GetFiles(_options.SessionMemoriesDirectory, "*.memories.json"))
            {
                if (await TryRemoveFromFileAsync(file, memoryId, ct))
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<MemoryEntry>> SearchAsync(
        string query,
        MemoryScope? scope,
        SessionKey? sessionKey,
        CancellationToken ct)
    {
        var results = new List<MemoryEntry>();

        if (scope is null or MemoryScope.Global)
        {
            var global = await LoadGlobalAsync(ct);
            results.AddRange(global.Where(m =>
                m.Content.Contains(query, StringComparison.OrdinalIgnoreCase)));
        }

        if (scope is null or MemoryScope.Session)
        {
            if (sessionKey.HasValue)
            {
                var session = await LoadSessionAsync(sessionKey.Value, ct);
                results.AddRange(session.Where(m =>
                    m.Content.Contains(query, StringComparison.OrdinalIgnoreCase)));
            }
        }

        return results;
    }

    private async Task<IReadOnlyList<MemoryEntry>> LoadFromFileAsync(string path, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await LoadFromFileInternalAsync(path, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<MemoryEntry>> LoadFromFileInternalAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        var text = await File.ReadAllTextAsync(path, ct);
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return JsonSerializer.Deserialize<List<MemoryEntry>>(text, _jsonOptions) ?? [];
    }

    private async Task SaveToFileAsync(string path, List<MemoryEntry> entries, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        var content = JsonSerializer.Serialize(entries, _jsonOptions);
        await File.WriteAllTextAsync(tempPath, content, ct);

        if (File.Exists(path))
        {
            File.Replace(tempPath, path, destinationBackupFileName: null);
        }
        else
        {
            File.Move(tempPath, path);
        }
    }

    private async Task<bool> TryRemoveFromFileAsync(string path, string memoryId, CancellationToken ct)
    {
        var entries = (await LoadFromFileInternalAsync(path, ct)).ToList();
        var removed = entries.RemoveAll(e => e.Id.Equals(memoryId, StringComparison.OrdinalIgnoreCase));
        if (removed > 0)
        {
            await SaveToFileAsync(path, entries, ct);
            return true;
        }

        return false;
    }

    private string ResolveSessionMemoryPath(SessionKey key)
    {
        var safeId = string.Concat(key.Value.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_'));
        if (string.IsNullOrWhiteSpace(safeId))
        {
            throw new InvalidOperationException($"Session id '{key.Value}' does not contain valid filename characters.");
        }

        var fullPath = Path.GetFullPath(Path.Combine(_options.SessionMemoriesDirectory, $"{safeId}.memories.json"));
        if (!fullPath.StartsWith(_options.SessionMemoriesDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Session memory path escapes configured root: {fullPath}");
        }

        return fullPath;
    }
}
