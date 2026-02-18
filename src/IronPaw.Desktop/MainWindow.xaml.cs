using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using IronPaw.AgentCore;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Persistence;
using IronPaw.AgentCore.Providers;
using IronPaw.AgentCore.Tools;

namespace IronPaw.Desktop;

public partial class MainWindow : Window
{
    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromMinutes(5)
    };

    private static readonly IReadOnlyDictionary<string, string> ProviderDefaultModels =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["local"] = "rule-based-v1",
            ["openai"] = "gpt-4o-mini",
            ["ollama"] = "llama3.2",
            ["lmstudio"] = "local-model",
            ["anthropic"] = "claude-3-5-haiku-latest"
        };

    private readonly IAgentOrchestrator _orchestrator;
    private CancellationTokenSource? _runCancellationTokenSource;

    public MainWindow()
    {
        InitializeComponent();
        Events = new ObservableCollection<string>();
        DataContext = this;
        var bootstrap = BuildOrchestrator();
        _orchestrator = bootstrap.Orchestrator;
        AppendSkillDiagnostics(bootstrap.SkillLoadResult);
        PromptTextBox.Text = "Create a project plan for an agent architecture and write it to Plan.md.";
    }

    public ObservableCollection<string> Events { get; }

    private async void RunButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_runCancellationTokenSource is not null)
        {
            return;
        }

        var prompt = PromptTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            MessageBox.Show(this, "Please enter a prompt before running.", "IronPaw");
            return;
        }

        var sessionRaw = SessionKeyTextBox.Text.Trim();
        var sessionKey = string.IsNullOrWhiteSpace(sessionRaw)
            ? new SessionKey("default")
            : new SessionKey(sessionRaw);
        var provider = GetSelectedProvider();
        var model = string.IsNullOrWhiteSpace(ModelTextBox.Text)
            ? ProviderDefaultModels[provider]
            : ModelTextBox.Text.Trim();

        Events.Clear();
        AssistantOutputTextBox.Text = string.Empty;
        RunButton.IsEnabled = false;
        _runCancellationTokenSource = new CancellationTokenSource();
        AppendLog($"Run queued. provider={provider}, model={model}");

        try
        {
            var request = new AgentRunRequest(
                SessionKey: sessionKey,
                UserInput: prompt,
                Provider: provider,
                Model: model,
                ToolPolicy: new ToolSelectionPolicy(
                    Profile: ToolProfile.Coding,
                    SenderIsOwner: true));

            var result = await _orchestrator.RunAsync(
                request,
                new UiEventSink(AppendEvent),
                _runCancellationTokenSource.Token);

            AssistantOutputTextBox.Text = result.FinalAssistantMessage;
            AppendLog($"Run completed: {result.RunId.Value}");
        }
        catch (OperationCanceledException)
        {
            AppendLog("Run canceled.");
        }
        catch (Exception ex)
        {
            AppendLog($"Run failed: {ex.Message}");
            MessageBox.Show(this, ex.ToString(), "Run Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            RunButton.IsEnabled = true;
            _runCancellationTokenSource?.Dispose();
            _runCancellationTokenSource = null;
        }
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        _runCancellationTokenSource?.Cancel();
    }

    private void ProviderComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || ModelTextBox is null)
        {
            return;
        }

        var provider = GetSelectedProvider();
        if (!ProviderDefaultModels.TryGetValue(provider, out var recommendedModel))
        {
            return;
        }

        var current = ModelTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(current) || IsDefaultModel(current))
        {
            ModelTextBox.Text = recommendedModel;
        }
    }

    private static (IAgentOrchestrator Orchestrator, SkillAddinLoadResult SkillLoadResult) BuildOrchestrator()
    {
        var workspaceRoot = Directory.GetCurrentDirectory();
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IronPaw");
        var stateRoot = Path.Combine(appData, "state");
        var sessionsDirectory = Path.Combine(stateRoot, "sessions");
        var sessionStorePath = Path.Combine(stateRoot, "session-store.json");

        Directory.CreateDirectory(stateRoot);
        Directory.CreateDirectory(sessionsDirectory);

        var skillLoadResult = SkillAddinLoader.LoadFromDirectories(
        [
            Path.Combine(workspaceRoot, "addins", "skills"),
            Path.Combine(appData, "skills")
        ]);

        var tools = BuiltInToolFactory.CreateDefault(workspaceRoot, skillLoadResult.SkillTools);
        var toolRegistry = new InMemoryToolRegistry(tools);
        var sessionStore = new FileSessionStore(new FileSessionStoreOptions(
            StoreFilePath: sessionStorePath,
            CacheTtl: TimeSpan.FromSeconds(5)));
        var transcriptStore = new JsonTranscriptStore(sessionsDirectory);
        var laneScheduler = new SessionLaneScheduler();
        var toolExecutor = new ToolExecutor();
        var schemaNormalizer = new ToolSchemaNormalizer();
        var llmClient = BuildLlmRouter();
        var sanitizer = new TranscriptSanitizer();
        var truncator = new ToolResultTruncator();
        var promptBuilder = new DefaultSystemPromptBuilder();

        var orchestrator = new AgentOrchestrator(
            laneScheduler,
            sessionStore,
            transcriptStore,
            toolRegistry,
            toolExecutor,
            schemaNormalizer,
            llmClient,
            sanitizer,
            truncator,
            promptBuilder);

        return (orchestrator, skillLoadResult);
    }

    private static ILLMClient BuildLlmRouter()
    {
        var local = new RuleBasedLlmClient();
        var openAi = new OpenAiLlmClient(
            SharedHttpClient,
            () => Environment.GetEnvironmentVariable("OPENAI_API_KEY"));
        var ollama = new OpenAiLlmClient(
            SharedHttpClient,
            () => ResolveApiKey("OLLAMA_API_KEY", "ollama"),
            ResolveOpenAiCompatibleBaseUrl(
                Environment.GetEnvironmentVariable("OLLAMA_BASE_URL"),
                "http://localhost:11434"));
        var lmStudio = new OpenAiLlmClient(
            SharedHttpClient,
            () => ResolveApiKey("LMSTUDIO_API_KEY", "lm-studio"),
            ResolveOpenAiCompatibleBaseUrl(
                Environment.GetEnvironmentVariable("LMSTUDIO_BASE_URL"),
                "http://localhost:1234"));
        var anthropic = new AnthropicLlmClient(
            SharedHttpClient,
            () => Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"));

        var providers = new Dictionary<string, ILLMClient>(StringComparer.OrdinalIgnoreCase)
        {
            ["local"] = local,
            ["openai"] = openAi,
            ["ollama"] = ollama,
            ["lmstudio"] = lmStudio,
            ["anthropic"] = anthropic
        };

        return new RoutedLlmClient(providers, local);
    }

    private static string ResolveApiKey(string environmentVariableName, string fallback)
    {
        var configured = Environment.GetEnvironmentVariable(environmentVariableName);
        return string.IsNullOrWhiteSpace(configured) ? fallback : configured.Trim();
    }

    private static string ResolveOpenAiCompatibleBaseUrl(string? configured, string fallbackBaseUrl)
    {
        var baseUrl = string.IsNullOrWhiteSpace(configured)
            ? fallbackBaseUrl
            : configured.Trim();

        baseUrl = baseUrl.TrimEnd('/');
        if (baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            baseUrl = baseUrl[..^3];
        }

        return baseUrl;
    }

    private string GetSelectedProvider()
    {
        if (ProviderComboBox.SelectedItem is ComboBoxItem comboBoxItem &&
            comboBoxItem.Content is string provider &&
            !string.IsNullOrWhiteSpace(provider))
        {
            return provider.Trim().ToLowerInvariant();
        }

        return "local";
    }

    private static bool IsDefaultModel(string currentModel)
    {
        return ProviderDefaultModels.Values.Contains(currentModel, StringComparer.OrdinalIgnoreCase);
    }

    private void AppendEvent(AgentEvent evt)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => AppendEvent(evt));
            return;
        }

        var line = new StringBuilder()
            .Append(evt.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff"))
            .Append(" [")
            .Append(evt.Stream)
            .Append('/')
            .Append(evt.Type)
            .Append("] ")
            .Append(FormatData(evt.Data))
            .ToString();

        Events.Add(line);
        EventsListBox.ScrollIntoView(line);
    }

    private void AppendLog(string message)
    {
        Events.Add($"{DateTimeOffset.Now:HH:mm:ss.fff} [ui] {message}");
        EventsListBox.ScrollIntoView(Events.LastOrDefault());
    }

    private void AppendSkillDiagnostics(SkillAddinLoadResult skillLoadResult)
    {
        AppendLog($"Loaded {skillLoadResult.SkillsLoaded} add-in skill(s) from {skillLoadResult.AddinsLoaded} add-in(s).");
        foreach (var diagnostic in skillLoadResult.Diagnostics)
        {
            AppendLog($"skills: {diagnostic}");
        }
    }

    private static string FormatData(IReadOnlyDictionary<string, object?> data)
    {
        return string.Join(
            ", ",
            data.Select(pair => $"{pair.Key}={pair.Value}"));
    }

    private sealed class UiEventSink : IAgentEventSink
    {
        private readonly Action<AgentEvent> _callback;

        public UiEventSink(Action<AgentEvent> callback)
        {
            _callback = callback;
        }

        public ValueTask PublishAsync(AgentEvent evt, CancellationToken ct)
        {
            _callback(evt);
            return ValueTask.CompletedTask;
        }
    }
}
