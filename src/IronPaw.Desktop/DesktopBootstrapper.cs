using System.IO;
using IronPaw.AgentCore;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Persistence;
using IronPaw.AgentCore.Providers;
using IronPaw.AgentCore.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace IronPaw.Desktop;

internal static class DesktopBootstrapper
{
    public static DesktopBootstrap Build()
    {
        var paths = ResolvePaths();
        Directory.CreateDirectory(paths.StateRoot);
        Directory.CreateDirectory(paths.SessionsDirectory);
        Directory.CreateDirectory(paths.AppSkillsDirectory);

        var services = new ServiceCollection();
        ConfigureServices(services, paths);

        var serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });

        return new DesktopBootstrap(serviceProvider);
    }

    private static DesktopPaths ResolvePaths()
    {
        var workspaceRoot = Directory.GetCurrentDirectory();
        var appDataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IronPaw");
        var stateRoot = Path.Combine(appDataRoot, "state");
        return new DesktopPaths(
            WorkspaceRoot: workspaceRoot,
            AppSkillsDirectory: Path.Combine(appDataRoot, "skills"),
            StateRoot: stateRoot,
            SessionsDirectory: Path.Combine(stateRoot, "sessions"),
            SessionStorePath: Path.Combine(stateRoot, "session-store.json"));
    }

    private static void ConfigureServices(IServiceCollection services, DesktopPaths paths)
    {
        var skillLoadResult = SkillAddinLoader.LoadFromDirectories(
        [
            Path.Combine(paths.WorkspaceRoot, "addins", "skills"),
            paths.AppSkillsDirectory
        ]);
        services.AddSingleton(skillLoadResult);

        ISessionStore sessionStore = new FileSessionStore(new FileSessionStoreOptions(
            StoreFilePath: paths.SessionStorePath,
            CacheTtl: TimeSpan.FromSeconds(5)));
        ITranscriptStore transcriptStore = new JsonTranscriptStore(paths.SessionsDirectory);

        services.AddSingleton(sessionStore);
        services.AddSingleton(transcriptStore);

        services.AddSingleton<InMemoryToolRegistry>(sp =>
        {
            var tools = BuiltInToolFactory.CreateDefault(
                paths.WorkspaceRoot,
                sp.GetRequiredService<SkillAddinLoadResult>().SkillTools);
            return new InMemoryToolRegistry(tools);
        });
        services.AddSingleton<IToolRegistry>(sp => sp.GetRequiredService<InMemoryToolRegistry>());
        services.AddSingleton<IToolRegistrar>(sp => sp.GetRequiredService<InMemoryToolRegistry>());

        services.AddSingleton<IAgentRegistry, InMemoryAgentRegistry>();
        services.AddSingleton<ISessionLaneScheduler, SessionLaneScheduler>();
        services.AddSingleton<IToolExecutor, ToolExecutor>();
        services.AddSingleton<IToolSchemaNormalizer, ToolSchemaNormalizer>();
        services.AddSingleton<ILlmClient>(_ => LlmClientFactory.Build());
        services.AddSingleton<ITranscriptSanitizer, TranscriptSanitizer>();
        services.AddSingleton<IToolResultTruncator, ToolResultTruncator>();
        services.AddSingleton<ISystemPromptBuilder, DefaultSystemPromptBuilder>();

        services.AddSingleton<IAgentOrchestrator>(sp =>
        {
            var orchestrator = new AgentOrchestrator(
                sp.GetRequiredService<ISessionLaneScheduler>(),
                sp.GetRequiredService<ISessionStore>(),
                sp.GetRequiredService<ITranscriptStore>(),
                sp.GetRequiredService<IToolRegistry>(),
                sp.GetRequiredService<IToolExecutor>(),
                sp.GetRequiredService<IToolSchemaNormalizer>(),
                sp.GetRequiredService<ILlmClient>(),
                sp.GetRequiredService<ITranscriptSanitizer>(),
                sp.GetRequiredService<IToolResultTruncator>(),
                sp.GetRequiredService<ISystemPromptBuilder>());

            RegisterRuntimeTools(sp, paths.AppSkillsDirectory, orchestrator);
            return orchestrator;
        });
    }

    private static void RegisterRuntimeTools(IServiceProvider services, string appSkillsDirectory, IAgentOrchestrator orchestrator)
    {
        var toolRegistrar = services.GetRequiredService<IToolRegistrar>();
        var agentRegistry = services.GetRequiredService<IAgentRegistry>();

        toolRegistrar.RegisterTools([new CreateSkillTool(toolRegistrar, appSkillsDirectory)]);
        RegisterDefaultAgents(agentRegistry);
        toolRegistrar.RegisterTools([
            new InvokeAgentTool(agentRegistry, orchestrator),
            new ListAgentsTool(agentRegistry)
        ]);
    }

    private static void RegisterDefaultAgents(IAgentRegistry registry)
    {
        var codegenProvider = ReadOptionalEnv("IRONPAW_CODEGEN_PROVIDER");
        var codegenModel = ReadOptionalEnv("IRONPAW_CODEGEN_MODEL");
        var reviewerProvider = ReadOptionalEnv("IRONPAW_REVIEWER_PROVIDER");
        var reviewerModel = ReadOptionalEnv("IRONPAW_REVIEWER_MODEL");

        registry.Register(new AgentDefinition(
            AgentId: "codegen",
            DisplayName: "Code Generator",
            Description: "Implements requested code changes with concise implementation notes.",
            SystemPrompt: """
                          You are a specialist coding agent.
                          Implement the delegated task directly in the workspace using available tools.
                          Prefer minimal, correct changes and keep explanations short.
                          If requirements are ambiguous, state assumptions briefly in the final answer.
                          """,
            ToolPolicy: new ToolSelectionPolicy(
                Profile: ToolProfile.Coding,
                DenyList: new[] { "invoke_agent", "list_agents" }),
            Provider: codegenProvider,
            Model: codegenModel,
            MaxRounds: 6,
            MaxOutputTokens: 4096));

        registry.Register(new AgentDefinition(
            AgentId: "reviewer",
            DisplayName: "Code Reviewer",
            Description: "Reviews changes for correctness, regressions, and missing tests.",
            SystemPrompt: """
                          You are a specialist code review agent.
                          Focus on bugs, behavioral regressions, risks, and missing tests.
                          Report findings first, ordered by severity, with file references.
                          If no issues are found, say so explicitly and note residual risks.
                          """,
            ToolPolicy: new ToolSelectionPolicy(
                Profile: ToolProfile.Coding,
                DenyList: new[] { "invoke_agent", "list_agents" }),
            Provider: reviewerProvider,
            Model: reviewerModel,
            MaxRounds: 4,
            MaxOutputTokens: 3072));
    }

    private static string? ReadOptionalEnv(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private sealed record DesktopPaths(
        string WorkspaceRoot,
        string AppSkillsDirectory,
        string StateRoot,
        string SessionsDirectory,
        string SessionStorePath);
}

internal sealed class DesktopBootstrap : IDisposable
{
    private readonly ServiceProvider _serviceProvider;

    public DesktopBootstrap(ServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        // Ensure runtime tools are registered at startup.
        Orchestrator = _serviceProvider.GetRequiredService<IAgentOrchestrator>();
        SkillLoadResult = _serviceProvider.GetRequiredService<SkillAddinLoadResult>();
    }

    public IAgentOrchestrator Orchestrator { get; }

    public SkillAddinLoadResult SkillLoadResult { get; }

    public void Dispose()
    {
        _serviceProvider.Dispose();
    }
}
