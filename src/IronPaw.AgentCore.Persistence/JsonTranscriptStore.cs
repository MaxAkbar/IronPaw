using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Persistence;

public sealed class JsonTranscriptStore : ITranscriptStore
{
    private readonly string _sessionsDirectory;

    // One gate per session so concurrent operations on different sessions don't block each other.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);

    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        Converters =
        {
            new JsonStringEnumConverter()
        }
    };

    public JsonTranscriptStore(string sessionsDirectory)
    {
        _sessionsDirectory = Path.GetFullPath(sessionsDirectory);
        Directory.CreateDirectory(_sessionsDirectory);
    }

    public async Task<IReadOnlyList<ChatMessage>> LoadAsync(SessionKey key, CancellationToken ct)
    {
        var path = ResolvePath(key);
        if (!File.Exists(path))
        {
            return [];
        }

        var gate = GetGate(key);
        await gate.WaitAsync(ct);
        try
        {
            var messages = new List<ChatMessage>();
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync(ct);
                if (line is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var message = JsonSerializer.Deserialize<ChatMessage>(line, _serializerOptions);
                if (message is not null)
                {
                    messages.Add(message);
                }
            }

            return messages;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task AppendAsync(SessionKey key, IEnumerable<ChatMessage> messages, CancellationToken ct)
    {
        var path = ResolvePath(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var gate = GetGate(key);
        await gate.WaitAsync(ct);
        try
        {
            await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            await using var writer = new StreamWriter(stream);
            foreach (var message in messages)
            {
                ct.ThrowIfCancellationRequested();
                var line = JsonSerializer.Serialize(message, _serializerOptions);
                await writer.WriteLineAsync(line);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task ReplaceAsync(SessionKey key, IEnumerable<ChatMessage> messages, CancellationToken ct)
    {
        var path = ResolvePath(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";

        var gate = GetGate(key);
        await gate.WaitAsync(ct);
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await using (var writer = new StreamWriter(stream))
            {
                foreach (var message in messages)
                {
                    ct.ThrowIfCancellationRequested();
                    var line = JsonSerializer.Serialize(message, _serializerOptions);
                    await writer.WriteLineAsync(line);
                }
            }

            if (File.Exists(path))
            {
                File.Replace(tempPath, path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tempPath, path);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private SemaphoreSlim GetGate(SessionKey key) =>
        _gates.GetOrAdd(key.Value, _ => new SemaphoreSlim(1, 1));

    private string ResolvePath(SessionKey key)
    {
        var safeId = string.Concat(key.Value.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_'));
        if (string.IsNullOrWhiteSpace(safeId))
        {
            throw new InvalidOperationException($"Session id '{key.Value}' does not contain valid filename characters.");
        }

        var fullPath = Path.GetFullPath(Path.Combine(_sessionsDirectory, $"{safeId}.jsonl"));
        if (!fullPath.StartsWith(_sessionsDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Session transcript path escapes configured root: {fullPath}");
        }

        return fullPath;
    }
}
