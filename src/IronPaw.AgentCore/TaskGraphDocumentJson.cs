using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore;

/// <summary>Local host input, never a model-issued authorization or a persisted checkpoint.</summary>
public sealed record TaskGraphLaunchDocument(WorkflowTaskGraph Graph, TaskGraphAgentOptions Options,
    int MaxConcurrency = 2, int SchemaVersion = 1);

/// <summary>Bounded, strict JSON for reviewed launch documents and explicit reconciliation receipts.</summary>
public static class TaskGraphDocumentJson
{
    public const int MaxDocumentBytes = 4 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public static async Task<T> ReadAsync<T>(string path, CancellationToken ct)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > MaxDocumentBytes) throw new ArgumentException("The graph document exceeds the 4 MiB limit.", nameof(path));
        var bytes = new byte[checked((int)input.Length)];
        await input.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        if (await input.ReadAsync(new byte[1], ct).ConfigureAwait(false) != 0)
            throw new ArgumentException("The graph document changed while being read.", nameof(path));
        var offset = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
        return Parse<T>(Utf8.GetString(bytes.AsSpan(offset)));
    }

    public static T Parse<T>(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > MaxDocumentBytes || Utf8.GetByteCount(json) > MaxDocumentBytes)
            throw new ArgumentException("The graph document exceeds the 4 MiB limit.", nameof(json));
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 128 });
        ValidateNames(document.RootElement);
        return JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException("A non-null JSON document is required.");
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    private static void ValidateNames(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("Duplicate JSON property: " + property.Name);
                ValidateNames(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) ValidateNames(item);
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) {
            WriteIndented = true, MaxDepth = 128, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        return options;
    }
}
