using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Persistence;

namespace IronPaw.AgentCore.Persistence.CSharpDb;

/// <summary>Reads a bounded-in-time snapshot, never writes legacy sources. Identity comes from
/// full session keys or reversible conventional filenames, never from a lossy sanitizer.</summary>
internal sealed class ApplicationJsonImport
{
    internal sealed record ImportRecord(string Kind, string Scope, string Key, object Value);
    private sealed record Source(byte[] Bytes, DateTime LastWriteUtc);
    private readonly string _root;
    private readonly Dictionary<string, Source> _sources;
    public List<ImportRecord> Records { get; } = [];
    private ApplicationJsonImport(string root, Dictionary<string, Source> sources) { _root = root; _sources = sources; }

    public static async Task<ApplicationJsonImport> ReadAsync(string root, CancellationToken ct)
    {
        var sources = await CaptureAsync(root, ct);
        var snapshot = new ApplicationJsonImport(root, sources);
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var sessionIndex = Path.Combine(root, "session-store.json");
        if (sources.TryGetValue(sessionIndex, out var index))
        {
            using var doc = Parse(index, sessionIndex);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw Error(sessionIndex, "expected a session dictionary");
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                RequireSessionKey(property.Name, sessionIndex);
                var entry = Decode<SessionEntry>(property.Value, sessionIndex, "SessionId", "UpdatedAt");
                RequireText(entry.SessionId, sessionIndex, "SessionId");
                identities.Add(property.Name);
                snapshot.Records.Add(new("session", property.Name, property.Name, entry));
            }
        }

        var historyPath = Path.Combine(root, "run-history.jsonl");
        var runIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (sources.TryGetValue(historyPath, out var history))
            foreach (var element in Lines(history, historyPath))
            {
                var run = Decode<RunSummary>(element, historyPath, "RunId", "SessionId", "StartedAt", "CompletedAt", "UserPrompt", "FinalAssistantMessage", "Rounds", "TotalTokens", "ToolsUsed");
                RequireText(run.RunId, historyPath, "RunId");
                RequireSessionKey(run.SessionId, historyPath);
                if (run.ToolsUsed is null || run.UserPrompt is null || run.FinalAssistantMessage is null || !Enum.IsDefined(run.Status))
                    throw Error(historyPath, "invalid run summary fields");
                if (!runIds.Add(run.RunId)) throw Error(historyPath, $"duplicate run ID '{run.RunId}'; reconcile duplicate outcomes before retrying");
                identities.Add(run.SessionId);
                snapshot.Records.Add(new("history", run.SessionId, run.RunId, run));
            }

        var sessionDirectory = Path.Combine(root, "sessions");
        foreach (var (path, source) in sources.Where(x => x.Key.EndsWith(".memories.json", StringComparison.Ordinal) || x.Key == Path.Combine(root, "memories.json")))
        {
            var global = path == Path.Combine(root, "memories.json");
            using var doc = Parse(source, path);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) throw Error(path, "expected a memory array");
            var sessionIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var memory = Decode<MemoryEntry>(item, path, "Id", "Content", "Scope", "CreatedAt");
                RequireText(memory.Id, path, "Id");
                if (memory.Content is null || memory.Scope != (global ? MemoryScope.Global : MemoryScope.Session))
                    throw Error(path, "memory scope does not match its file, or content is missing");
                var scope = "";
                if (!global)
                {
                    RequireSessionKey(memory.SessionId!, path);
                    scope = memory.SessionId!;
                    sessionIds.Add(scope);
                    identities.Add(scope);
                }
                snapshot.Records.Add(new("memory", scope, memory.Id, memory));
            }
            if (sessionIds.Count > 1) throw Error(path, "memories identify multiple sessions");
        }

        // A conventional filename is reversible. Hash names require an independently known full key.
        foreach (var path in sources.Keys.Where(p => Path.GetDirectoryName(p) == sessionDirectory))
        {
            var name = Path.GetFileName(path);
            var stem = name.EndsWith(".memories.json", StringComparison.Ordinal) ? name[..^14] :
                name.EndsWith(".jsonl", StringComparison.Ordinal) ? name[..^6] : "";
            if (stem.Length > 0 && !stem.StartsWith('~') && SessionFileName.For(new SessionKey(stem)) == stem)
                identities.Add(stem);
        }
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var identity in identities)
        {
            var key = new SessionKey(identity);
            var name = SessionFileName.For(key);
            if (!names.TryAdd(name, identity) && names[name] != identity) throw Error(sessionDirectory, "session filename collision");
            // Reuse the JSON adapter's ambiguity detection, including older lossy names.
            try
            {
                SessionFileName.Resolve(sessionDirectory, key, ".jsonl");
                SessionFileName.Resolve(sessionDirectory, key, ".memories.json");
            }
            catch (InvalidOperationException ex) { throw Error(sessionDirectory, ex.Message); }
        }
        foreach (var (path, source) in sources.Where(x => x.Key != sessionIndex && x.Key != historyPath && x.Key != Path.Combine(root, "memories.json")))
        {
            var name = Path.GetFileName(path);
            var memories = name.EndsWith(".memories.json", StringComparison.Ordinal);
            var stem = memories ? name[..^14] : name.EndsWith(".jsonl", StringComparison.Ordinal) ? name[..^6] : "";
            if (Path.GetDirectoryName(path) != sessionDirectory || !names.TryGetValue(stem, out var identity))
                throw Error(path, "unidentified session file; restore its full session key in session-store.json and use the JSON adapter's safe filename");
            if (memories)
            {
                var wrong = snapshot.Records.Any(r => r.Kind == "memory" && r.Scope.Length > 0 &&
                    SessionFileName.For(new SessionKey(r.Scope)) == stem && r.Scope != identity);
                if (wrong) throw Error(path, "memory identity disagrees with its filename");
                // Check every entry directly so a misplaced file cannot become another session's memories.
                using var doc = Parse(source, path);
                foreach (var item in doc.RootElement.EnumerateArray())
                    if (item.GetProperty("SessionId").GetString() != identity) throw Error(path, "memory SessionId disagrees with its filename");
                continue;
            }
            foreach (var element in Lines(source, path))
            {
                var message = Decode<ChatMessage>(element, path, "Role", "Content");
                if (!Enum.IsDefined(message.Role) || message.Content is null) throw Error(path, "invalid transcript role or content");
                snapshot.Records.Add(new("transcript", identity, "", message));
            }
        }
        return snapshot;
    }

    public async Task VerifyUnchangedAsync(CancellationToken ct)
    {
        var current = await CaptureAsync(_root, ct);
        if (current.Count != _sources.Count || current.Any(x => !_sources.TryGetValue(x.Key, out var old) ||
            old.LastWriteUtc != x.Value.LastWriteUtc || !SHA256.HashData(old.Bytes).AsSpan().SequenceEqual(SHA256.HashData(x.Value.Bytes))))
            throw Error(_root, "JSON sources changed during import; close older IronPaw instances and retry");
    }

    private static async Task<Dictionary<string, Source>> CaptureAsync(string root, CancellationToken ct)
    {
        var result = new Dictionary<string, Source>(StringComparer.Ordinal);
        foreach (var path in SourcePaths(root))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var before = File.GetLastWriteTimeUtc(path);
                var bytes = await File.ReadAllBytesAsync(path, ct);
                var after = File.GetLastWriteTimeUtc(path);
                if (before != after) throw Error(path, "source changed while reading; close older IronPaw instances and retry");
                result.Add(path, new(bytes, after));
            }
            catch (IOException ex) { throw Error(path, $"cannot read a stable snapshot: {ex.Message}"); }
        }
        return result;
    }
    private static IEnumerable<string> SourcePaths(string root)
    {
        foreach (var name in new[] { "session-store.json", "memories.json", "run-history.jsonl" })
        {
            var path = Path.Combine(root, name);
            if (File.Exists(path)) yield return path;
        }
        var directory = Path.Combine(root, "sessions");
        if (Directory.Exists(directory))
            foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) yield return path;
    }
    private static string Text(Source source, string path)
    {
        try { return new UTF8Encoding(false, true).GetString(source.Bytes).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException ex) { throw Error(path, $"invalid UTF-8: {ex.Message}"); }
    }
    private static JsonDocument Parse(Source source, string path) => Parse(Text(source, path), path);
    private static JsonDocument Parse(string text, string path)
    {
        try
        {
            var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 256 });
            try { ValidateProperties(doc.RootElement, path); return doc; }
            catch { doc.Dispose(); throw; }
        }
        catch (JsonException ex) { throw Error(path, $"malformed JSON: {ex.Message}"); }
    }
    private static void ValidateProperties(JsonElement value, string path)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Error(path, $"duplicate JSON property '{property.Name}'");
                ValidateProperties(property.Value, path);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) ValidateProperties(item, path);
    }
    private static IEnumerable<JsonElement> Lines(Source source, string path)
    {
        using var reader = new StringReader(Text(source, path));
        var number = 0;
        while (reader.ReadLine() is { } line)
        {
            number++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = Parse(line, $"{path} (line {number})");
            yield return doc.RootElement.Clone();
        }
    }
    private static T Decode<T>(JsonElement element, string path, params string[] required)
    {
        if (element.ValueKind != JsonValueKind.Object || required.Any(p => !element.TryGetProperty(p, out _)))
            throw Error(path, $"{typeof(T).Name} is missing required fields ({string.Join(", ", required)})");
        try { return element.Deserialize<T>(CSharpDbApplicationStore.JsonOptions) ?? throw Error(path, "null record"); }
        catch (JsonException ex) { throw Error(path, $"invalid {typeof(T).Name}: {ex.Message}"); }
    }
    private static void RequireText(string? value, string path, string field)
    { if (string.IsNullOrWhiteSpace(value)) throw Error(path, $"missing {field}"); }
    private static void RequireSessionKey(string value, string path)
    {
        RequireText(value, path, "session key");
        if (new SessionKey(value).Value != value) throw Error(path, "session key has ambiguous leading or trailing whitespace");
    }
    private static InvalidDataException Error(string path, string detail) => new(
        $"Cannot import legacy application data '{path}': {detail}. Original files are unchanged. Repair or relocate this source, close older IronPaw instances, and retry; application import has not completed.");
}
