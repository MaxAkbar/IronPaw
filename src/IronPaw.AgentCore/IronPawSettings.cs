using System.Text.Json.Serialization;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Providers;

namespace IronPaw.AgentCore;

/// <summary>User-owned defaults for new runs. Contains no credentials or authority grants.</summary>
public sealed record IronPawSettings
{
    [JsonNumberHandling(JsonNumberHandling.Strict)] public int SchemaVersion { get; init; } = 1;
    public string Provider { get; init; } = "local";
    public string? Model { get; init; }
    [JsonNumberHandling(JsonNumberHandling.Strict)] public long? MaxTotalTokens { get; init; }
    [JsonNumberHandling(JsonNumberHandling.Strict)] public int? MaxExecutionSeconds { get; init; }
    public RuntimeLlmSettings Llm { get; init; } = new();
    public SpecialistRoutingSettings Agents { get; init; } = new();

    public IronPawSettings RequireValid()
    {
        if (SchemaVersion != 1) throw new ArgumentException("Unsupported application settings schema; expected 1.");
        ValidateRoute(Provider, Model, "Default", required: true);
        if (MaxTotalTokens is <= 0 || MaxExecutionSeconds is <= 0)
            throw new ArgumentException("Workflow token and execution limits must be positive when provided.");
        if (Llm is null || Agents is null || Agents.Codegen is null || Agents.Reviewer is null || Agents.WebBrowser is null)
            throw new ArgumentException("Settings sections cannot be null.");
        Llm.Validate();
        ValidateRoute(Agents.Codegen.Provider, Agents.Codegen.Model, "Codegen");
        ValidateRoute(Agents.Reviewer.Provider, Agents.Reviewer.Model, "Reviewer");
        ValidateRoute(Agents.WebBrowser.Provider, Agents.WebBrowser.Model, "WebBrowser");
        return this with { Provider = Provider.ToLowerInvariant(), Agents = Agents with {
            Codegen = Agents.Codegen with { Provider = Agents.Codegen.Provider?.ToLowerInvariant() },
            Reviewer = Agents.Reviewer with { Provider = Agents.Reviewer.Provider?.ToLowerInvariant() },
            WebBrowser = Agents.WebBrowser with { Provider = Agents.WebBrowser.Provider?.ToLowerInvariant() } } };
    }

    private static void ValidateRoute(string? provider, string? model, string name, bool required = false)
    {
        if ((required && provider is null) || (provider is not null &&
            (provider != provider.Trim() || !ProviderDefaults.DefaultModels.ContainsKey(provider))))
            throw new ArgumentException($"{name} provider must identify a supported provider without padding.");
        if (model is not null && (string.IsNullOrWhiteSpace(model) || model != model.Trim() || model.Any(char.IsControl) || model.Length > 256))
            throw new ArgumentException($"{name} model must be an explicit, unpadded model ID without control characters, at most 256 characters.");
    }
}

public sealed record RuntimeLlmSettings
{
    [JsonNumberHandling(JsonNumberHandling.Strict)] public int? ContextWindowTokens { get; init; }
    [JsonNumberHandling(JsonNumberHandling.Strict)] public int? SafetyMarginTokens { get; init; }
    [JsonNumberHandling(JsonNumberHandling.Strict)] public int? MinimumOutputTokensPerRequest { get; init; }
    [JsonNumberHandling(JsonNumberHandling.Strict)] public int? MaxAutoContinuationPasses { get; init; }

    public OpenAiClientOptionOverrides ToOverrides() => new(ContextWindowTokens, SafetyMarginTokens, MinimumOutputTokensPerRequest, MaxAutoContinuationPasses);
    internal void Validate()
    {
        if (ContextWindowTokens is <= 0 || SafetyMarginTokens is < 0 || MinimumOutputTokensPerRequest is <= 0 || MaxAutoContinuationPasses is < 0)
            throw new ArgumentException("LLM context/output limits must be positive; safety margin and continuation count cannot be negative.");
        if (ContextWindowTokens.HasValue && ((SafetyMarginTokens ?? 0) >= ContextWindowTokens || MinimumOutputTokensPerRequest > ContextWindowTokens ||
            (long)(SafetyMarginTokens ?? 0) + (MinimumOutputTokensPerRequest ?? 1) > ContextWindowTokens))
            throw new ArgumentException("LLM context must leave room for both the safety margin and minimum output.");
    }
}

public sealed record SpecialistRoute(string? Provider = null, string? Model = null);
public sealed record SpecialistRoutingSettings
{
    public SpecialistRoute Codegen { get; init; } = new();
    public SpecialistRoute Reviewer { get; init; } = new();
    public SpecialistRoute WebBrowser { get; init; } = new();
}

/// <summary>Only the user profile is discovered automatically; repository settings are never auto-loaded.</summary>
public static class IronPawSettingsLoader
{
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IronPaw", "settings.json");

    public static IronPawSettings Load(string? path = null, Func<string, string?>? environment = null, string? defaultPath = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        var configuredPath = environment("IRONPAW_CONFIG");
        var selected = path ?? (string.IsNullOrWhiteSpace(configuredPath) ? null : configuredPath);
        if (selected is not null && string.IsNullOrWhiteSpace(selected)) throw new ArgumentException("The application settings path cannot be empty.");
        var file = Path.GetFullPath(selected ?? defaultPath ?? DefaultPath);
        if (selected is not null && !File.Exists(file)) throw new FileNotFoundException("The selected application settings file does not exist.", file);
        // Startup may run on the WPF dispatcher. Do not block an async read on that context.
        var settings = File.Exists(file) ? Task.Run(() => TaskGraphDocumentJson.ReadAsync<IronPawSettings>(file, CancellationToken.None)).GetAwaiter().GetResult().RequireValid() : new IronPawSettings();
        string? Value(string name) { var value = environment(name); return string.IsNullOrWhiteSpace(value) ? null : value.Trim(); }
        int? Integer(string name, int? fallback)
        {
            var text = Value(name);
            if (text is null) return fallback;
            if (!int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value))
                throw new ArgumentException($"Invalid {name}; expected an unsigned integer.");
            return value;
        }
        SpecialistRoute Route(SpecialistRoute route, string prefix)
        {
            var provider = Value(prefix + "_PROVIDER");
            var model = Value(prefix + "_MODEL");
            return new(provider ?? route.Provider, model ?? (provider is not null && !string.Equals(provider, route.Provider, StringComparison.OrdinalIgnoreCase) ? null : route.Model));
        }
        var rootProvider = Value("IRONPAW_PROVIDER");
        return (settings with {
            Provider = rootProvider ?? settings.Provider,
            Model = Value("IRONPAW_MODEL") ?? (rootProvider is not null && !string.Equals(rootProvider, settings.Provider, StringComparison.OrdinalIgnoreCase) ? null : settings.Model),
            Llm = settings.Llm with {
                ContextWindowTokens = Integer("IRONPAW_LLM_CONTEXT_WINDOW_TOKENS", settings.Llm.ContextWindowTokens),
                SafetyMarginTokens = Integer("IRONPAW_LLM_SAFETY_MARGIN_TOKENS", settings.Llm.SafetyMarginTokens),
                MinimumOutputTokensPerRequest = Integer("IRONPAW_LLM_MIN_OUTPUT_TOKENS_PER_REQUEST", settings.Llm.MinimumOutputTokensPerRequest),
                MaxAutoContinuationPasses = Integer("IRONPAW_LLM_MAX_AUTO_CONTINUATION_PASSES", settings.Llm.MaxAutoContinuationPasses) },
            Agents = settings.Agents with {
                Codegen = Route(settings.Agents.Codegen, "IRONPAW_CODEGEN"), Reviewer = Route(settings.Agents.Reviewer, "IRONPAW_REVIEWER"),
                WebBrowser = Route(settings.Agents.WebBrowser, "IRONPAW_WEB_BROWSER") }
        }).RequireValid();
    }
}
