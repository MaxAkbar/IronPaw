using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IronPaw.AgentCore.Browser;

public sealed record BrowserConfiguration(
    int SchemaVersion,
    IReadOnlyList<string> AllowedOrigins,
    bool AllowPrivateNetwork = false,
    bool AllowMutations = false,
    int MaxSessions = 8,
    int TimeoutSeconds = 30,
    int MaxRequestsPerAction = 64,
    int MaxResponseBytes = 2_097_152,
    int MaxSessionBytes = 16_777_216)
{
    public static BrowserConfiguration Load(string path)
    {
        using var file = new FileStream(Path.GetFullPath(path), FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > 65_536) throw new ArgumentException("Browser configuration exceeds 64 KiB.");
        using var reader = new StreamReader(file, new UTF8Encoding(false, true));
        var json = reader.ReadToEnd();
        using var document = JsonDocument.Parse(json, new() { MaxDepth = 8 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("Browser configuration must be an object.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in document.RootElement.EnumerateObject())
            if (!names.Add(property.Name)) throw new ArgumentException("Browser configuration has duplicate property names.");
        return (document.Deserialize<BrowserConfiguration>(JsonOptions) ?? throw new ArgumentException("Browser configuration must be an object.")).RequireValid();
    }
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    { PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, NumberHandling = JsonNumberHandling.Strict };

    public BrowserConfiguration RequireValid()
    {
        if (SchemaVersion != 1 || AllowedOrigins is null || AllowedOrigins.Count is < 1 or > 32 || MaxSessions is < 1 or > 16 ||
            TimeoutSeconds is < 1 or > 120 || MaxRequestsPerAction is < 1 or > 256 || MaxResponseBytes is < 4096 or > 4_194_304 ||
            MaxSessionBytes < MaxResponseBytes || MaxSessionBytes > 67_108_864)
            throw new ArgumentException("Browser configuration has an unsupported schema, missing origins or out-of-range limits.");
        var origins = AllowedOrigins.Select(origin =>
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 ||
                uri.AbsolutePath != "/" || (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback && AllowPrivateNetwork)))
                throw new ArgumentException("Browser sites must be exact HTTPS origins. Explicit loopback HTTP may be used for local sites.");
            return uri.GetLeftPart(UriPartial.Authority);
        }).Order(StringComparer.Ordinal).ToArray();
        if (origins.Distinct(StringComparer.Ordinal).Count() != origins.Length) throw new ArgumentException("Browser origins must be distinct.");
        return this with { AllowedOrigins = Array.AsReadOnly(origins) };
    }
    [JsonIgnore] public string Binding => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(RequireValid(), JsonOptions)));
    [JsonIgnore]
    public string Review => $"JavaScript browser sites:\n{string.Join("\n", RequireValid().AllowedOrigins)}\n" +
        $"Requests that change data: {(AllowMutations ? "enabled, with approval for navigation operations" : "blocked")}\n" +
        $"Private network: {AllowPrivateNetwork}\nSessions: {MaxSessions}; action timeout: {TimeoutSeconds}s\n" +
        $"Requests per action: {MaxRequestsPerAction}; response bytes: {MaxResponseBytes}; session bytes: {MaxSessionBytes}\nGrant: {Binding}";
}
