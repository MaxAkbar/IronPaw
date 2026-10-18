using System.Collections.Immutable;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IronPaw.AgentCore;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Persistence.CSharpDb;
using IronPaw.AgentCore.Tools;
using IronPaw.Desktop;
using Microsoft.Extensions.DependencyInjection;

namespace IronPaw.Desktop.Tests;

public sealed class TaskGraphDesktopTests
{
    [Fact]
    public Task GraphWindowRendersDependenciesArtifactsReceiptsBudgetAndChildIdentity() => RunInSta(() =>
    {
        using var scope = new Scope();
        var graph = scope.Accept("render-graph", command: true);
        var claim = Background(() => scope.Store.ClaimTaskAsync(graph.Graph.GraphId, CallerIdentity.LocalOwner(), default))!;
        var artifact = new WorkflowTaskArtifact("output", "producer", TaskArtifactKind.File,
            Path.Combine(scope.Root, "sealed", "result.txt"), "sha256:" + new string('b', 64), "revision-render");
        var candidate = new WorkflowTaskResult(graph.Graph.GraphId, graph.DefinitionFingerprint, "producer",
            WorkflowTaskStatus.AwaitingVerification, "revision-render", "Captured generated result") { Artifacts = [artifact] };
        Background(() => scope.Store.RecordTaskResultAsync(claim, candidate, default));
        var admitted = Background(() => scope.Store.BeginTaskVerificationAsync(claim, "build", default));
        var output = "bounded build output";
        var log = new TaskVerificationLog(output, "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(output))), Encoding.UTF8.GetByteCount(output), false);
        Background(() => scope.Store.CompleteTaskVerificationAsync(claim, admitted.Invocation.InvocationId,
            new(TaskCommandOutcome.Passed, "desktop-test-worker", "Observed successful build", 0, log, EmptyLog()), default));
        Background(() => scope.Store.AbandonTaskClaimAsync(claim, "Leave accepted receipt for inspection", default));
        var window = scope.ShowWindow();
        WaitFor(() => ((ComboBox)window.FindName("GraphTasksComboBox")).Items.Count == 2);
        ((TabControl)window.FindName("ActivityTabControl")).SelectedIndex = 2;
        var detail = ((TextBox)window.FindName("GraphDetailTextBox")).Text;
        Assert.Contains("producer", detail);
        Assert.Contains("consumer / depends: producer", detail);
        Assert.Contains("result.txt", detail);
        Assert.Contains("bounded build output", detail);
        Assert.Contains("verification_receipt", detail);
        Assert.Contains(AgentTaskIdentity.ChildRunId(graph.DefinitionFingerprint, "producer", claim.AttemptId).Value, detail);
        Assert.Contains("Tokens charged: 0", ((TextBlock)window.FindName("GraphSummaryTextBlock")).Text);
        Assert.False(((CheckBox)window.FindName("GraphLocalVerificationCheckBox")).IsChecked);
        Assert.True(((Button)window.FindName("ResumeGraphButton")).IsEnabled);
        Assert.Empty(((ComboBox)window.FindName("WorkflowsComboBox")).Items);
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        Assert.True(bitmap.PixelWidth >= 980);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var screenshotPath = Path.Combine(Path.GetTempPath(), "IronPaw-desktop-task-graphs-smoke.png");
        using (var file = File.Create(screenshotPath)) encoder.Save(file);
        Console.WriteLine("Desktop graph render: " + screenshotPath);
    });

    [Fact]
    public Task ActiveGraphPauseCannotBeRedirectedBySelectingAnotherGraph() => RunInSta(() =>
    {
        var manager = new BlockingWorkspaces();
        using var scope = new Scope(manager);
        scope.Accept("active-graph");
        scope.Accept("idle-graph");
        var window = scope.ShowWindow();
        WaitFor(() => ((ComboBox)window.FindName("GraphsComboBox")).Items.Count == 2);
        SelectGraph(window, "active-graph");
        ((Button)window.FindName("ResumeGraphButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        WaitFor(() => manager.Started.Task.IsCompleted);
        Assert.False(((Button)window.FindName("RunButton")).IsEnabled);
        SelectGraph(window, "idle-graph");
        Assert.False(((Button)window.FindName("PauseGraphButton")).IsEnabled);
        // Raised routed events exercise the handler guard as well as button state.
        ((Button)window.FindName("PauseGraphButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(WorkflowControl.None, Background(() => scope.Store.GetTaskGraphAsync("idle-graph", default))!.Control);
        SelectGraph(window, "active-graph");
        Assert.True(((Button)window.FindName("PauseGraphButton")).IsEnabled);
        ((Button)window.FindName("PauseGraphButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        WaitFor(() => manager.Cancelled && ((Button)window.FindName("RunButton")).IsEnabled);
        var paused = Background(() => scope.Store.GetTaskGraphAsync("active-graph", default))!;
        Assert.Equal(WorkflowControl.Pause, paused.Control);
        Assert.Equal(TaskGraphTaskStatus.NeedsReconciliation, paused.Tasks[0].Status);
        Assert.Null(paused.Tasks[0].LeaseToken);
        Assert.Equal(WorkflowControl.None, Background(() => scope.Store.GetTaskGraphAsync("idle-graph", default))!.Control);
    });

    [Fact]
    public Task ClosingDesktopPausesActiveGraphAndSettlesItsOriginalClaim() => RunInSta(() =>
    {
        var manager = new BlockingWorkspaces();
        using var scope = new Scope(manager);
        scope.Accept("close-graph");
        var window = scope.ShowWindow();
        WaitFor(() => ((ComboBox)window.FindName("GraphsComboBox")).Items.Count == 1);
        ((Button)window.FindName("ResumeGraphButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        WaitFor(() => manager.Started.Task.IsCompleted);
        window.Close();
        WaitFor(() => manager.Cancelled && !window.IsVisible);
        // Window closure disposes the host, so verify the persisted state after reopening it.
        var reopened = Background(() => CSharpDbWorkflowStore.OpenAsync(scope.DatabasePath));
        try
        {
            var graph = Background(() => reopened.GetTaskGraphAsync("close-graph", default))!;
            Assert.Equal(WorkflowControl.Pause, graph.Control);
            Assert.Null(graph.Tasks[0].LeaseToken);
            Assert.Equal(TaskGraphTaskStatus.NeedsReconciliation, graph.Tasks[0].Status);
        }
        finally { Task.Run(reopened.Dispose).GetAwaiter().GetResult(); }
    });

    [Fact]
    public async Task WorkflowRecoveryCannotExposeOrControlGraphLedgerAnchors()
    {
        using var scope = new Scope();
        var graph = scope.Accept("graph-root");
        var normal = WorkflowRecoveryControllerTests.MakeRun("ordinary", "ordinary", null);
        await scope.Store.CreateAsync(normal, default);
        var orchestrator = new StoreBrowser(scope.Store);
        var recovery = new WorkflowRecoveryController(orchestrator, scope.Store);
        Assert.Equal("ordinary", Assert.Single(await recovery.ListRootsAsync(default)).RunId);
        await Assert.ThrowsAsync<WorkflowConflictException>(() => recovery.PauseAsync(new(graph.AgentBinding!.RootRunId), default));
        await Assert.ThrowsAsync<WorkflowConflictException>(() => recovery.CancelAsync(new(graph.AgentBinding!.RootRunId), default));
        Assert.Equal(WorkflowControl.None, (await scope.Store.GetTaskGraphAsync("graph-root", default))!.Control);
    }

    [Fact]
    public async Task DesktopProviderFactoryIsNotReadUntilModelWorkActuallyStarts()
    {
        var calls = 0;
        var deferred = new DeferredDesktopLlmClient(() => { calls++; throw new ArgumentException("Invalid provider environment"); });
        using var scope = new Scope(client: deferred);
        scope.Accept("readonly-graph");
        Assert.Single(await scope.Controller.ListAsync(CallerIdentity.LocalOwner(), default));
        await scope.Controller.InspectAsync("readonly-graph", CallerIdentity.LocalOwner(), default);
        await scope.Controller.PauseAsync("readonly-graph", CallerIdentity.LocalOwner(), default);
        await scope.Controller.CancelAsync("readonly-graph", CallerIdentity.LocalOwner(), default);
        Assert.Equal(0, calls);
        Assert.Throws<ArgumentException>(() => deferred.StreamAsync(ProviderRequest("LOCAL"), default));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("opneai")]
    [InlineData("")]
    [InlineData(null)]
    public void UnknownProviderAtModelDispatchCannotConstructFactoryOrFallBack(string? provider)
    {
        var calls = 0;
        var deferred = new DeferredDesktopLlmClient(() => { calls++; return new UnusedClient(); });

        Assert.Throws<ArgumentException>(() => deferred.StreamAsync(ProviderRequest(provider!), default));

        Assert.Equal(0, calls);
    }

    private static LlmRequest ProviderRequest(string provider) => new(new("desktop-provider-probe"), new("desktop-provider-probe"),
        provider, "rule-based-v1", "Inspect saved graph routing", [], [], TimeSpan.FromSeconds(1));

    [Fact]
    public async Task MisspelledProviderCannotAdmitGraphOrCallGitOrProvider()
    {
        var workspaces = new PreflightWorkspaces();
        using var scope = new Scope(workspaces);
        var document = new TaskGraphLaunchDocument(new("unknown-launch", "Exercise provider preflight", [new("task", "Write output")]),
            new(scope.Controller.WorkspaceRoot, scope.Controller.WorkspaceStoreRoot, "HEAD", "opneai", "some-model",
                new(ToolProfile.Coding), new()));

        var error = await Assert.ThrowsAsync<ArgumentException>(() => DesktopGraphCommands.StartAsync(scope.Controller,
            document, CallerIdentity.LocalOwner(), false, default));

        Assert.Contains("opneai", error.Message);
        Assert.Empty(await scope.Store.ListTaskGraphsAsync(default));
        Assert.Equal(0, scope.Client.Calls);
        Assert.Equal(0, workspaces.ResolveCalls);
        Assert.Equal(0, workspaces.ExecutionCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownSavedProviderCannotResumeOrRecoverModelWork(bool recover)
    {
        var workspaces = new PreflightWorkspaces();
        using var scope = new Scope(workspaces);
        var graph = scope.Accept("unknown-execution", provider: "opneai");
        TaskGraphClaim? claim = null;
        if (recover)
        {
            claim = await scope.Store.ClaimTaskAsync(graph.Graph.GraphId, CallerIdentity.LocalOwner(), default);
            await scope.Store.AbandonTaskClaimAsync(claim!, "Explicit recovery needed", default);
        }

        await Assert.ThrowsAsync<ArgumentException>(() => recover
            ? DesktopGraphCommands.RecoverTaskAsync(scope.Controller, graph.Graph.GraphId, "producer", CallerIdentity.LocalOwner(), false, default)
            : DesktopGraphCommands.ResumeAsync(scope.Controller, graph.Graph.GraphId, CallerIdentity.LocalOwner(), false, default));

        var saved = (await scope.Store.GetTaskGraphAsync(graph.Graph.GraphId, default))!;
        Assert.Null(saved.Tasks[0].LeaseToken);
        Assert.Equal(recover ? TaskGraphTaskStatus.NeedsReconciliation : TaskGraphTaskStatus.Pending, saved.Tasks[0].Status);
        Assert.Equal(claim?.AttemptId, saved.Tasks[0].AttemptId);
        Assert.Equal(0, workspaces.ExecutionCalls);
        Assert.Equal(0, scope.Client.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownSavedProviderDoesNotBlockCandidateOnlyResumeOrGrantCommandAuthority(bool command)
    {
        var workspaces = new PreflightWorkspaces();
        using var scope = new Scope(workspaces);
        var graph = scope.Accept("unknown-candidate", command: command, provider: "opneai", includeConsumer: false);
        var claim = (await scope.Store.ClaimTaskAsync(graph.Graph.GraphId, CallerIdentity.LocalOwner(), default))!;
        await scope.Store.RecordTaskResultAsync(claim, new(graph.Graph.GraphId, graph.DefinitionFingerprint, "producer",
            WorkflowTaskStatus.AwaitingVerification, "candidate-revision", "Only read-only candidate verification remains"), default);
        await scope.Store.AbandonTaskClaimAsync(claim, "Leave the candidate for explicit resume", default);

        var resumed = await DesktopGraphCommands.ResumeAsync(scope.Controller, graph.Graph.GraphId, CallerIdentity.LocalOwner(), false, default);

        Assert.Equal(TaskGraphTaskStatus.AwaitingAcceptance, resumed.Tasks[0].Status);
        Assert.Equal(TaskAcceptanceOutcome.Inconclusive, resumed.Tasks[0].Acceptance!.Outcome);
        Assert.Empty(resumed.Tasks[0].Verifications);
        Assert.Equal(0, workspaces.ExecutionCalls);
        Assert.Equal(0, scope.Client.Calls);
        Assert.Equal(1, workspaces.ReadOnlyCalls);
    }

    [Fact]
    public Task VerificationReconciliationRecordsEvidenceWithoutResumingOrCallingProvider() => RunInSta(() =>
    {
        using var scope = new Scope();
        var graph = scope.Accept("reconcile-graph", command: true);
        var claim = Background(() => scope.Store.ClaimTaskAsync(graph.Graph.GraphId, CallerIdentity.LocalOwner(), default))!;
        Background(() => scope.Store.RecordTaskResultAsync(claim, new(graph.Graph.GraphId, graph.DefinitionFingerprint, "producer",
            WorkflowTaskStatus.AwaitingVerification, "revision-reconcile", "Candidate pending command outcome"), default));
        var command = Background(() => scope.Store.BeginTaskVerificationAsync(claim, "build", default));
        Background(() => scope.Store.AbandonTaskClaimAsync(claim, "Worker outcome unavailable", default));
        var window = scope.ShowWindow();
        WaitFor(() => ((ComboBox)window.FindName("GraphTasksComboBox")).Items.Count == 2);
        ((ComboBox)window.FindName("GraphReconciliationKindComboBox")).SelectedIndex = 1;
        Assert.True(((Button)window.FindName("ReconcileGraphButton")).IsEnabled);
        ((TextBox)window.FindName("GraphReconcileResultTextBox")).Text = TaskGraphDocumentJson.Serialize(new TaskVerificationCompletion(
            TaskCommandOutcome.Failed, "independent-review", "Observed exit code 7", 7, EmptyLog(), EmptyLog()));
        ((TextBox)window.FindName("GraphReconcileNoteTextBox")).Text = "Checked the captured process exit and command logs";
        ((Button)window.FindName("ReconcileGraphButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        WaitFor(() => ((TextBlock)window.FindName("GraphStatusTextBlock")).Text.Contains("Established outcome recorded"));
        var updated = Background(() => scope.Store.GetTaskGraphAsync(graph.Graph.GraphId, default))!;
        Assert.Equal(TaskGraphTaskStatus.AwaitingAcceptance, updated.Tasks[0].Status);
        Assert.Null(updated.Tasks[0].LeaseToken);
        var receipt = Assert.Single(updated.Tasks[0].Verifications);
        Assert.Equal(command.Invocation.InvocationId, receipt.InvocationId);
        Assert.Equal(TaskVerificationInvocationState.Completed, receipt.State);
        Assert.Equal(TaskCommandOutcome.Failed, receipt.Completion!.Outcome);
        Assert.Equal("Checked the captured process exit and command logs", receipt.ReconciliationNote);
        Assert.Equal(0, scope.Client.Calls);
    });

    private static TaskVerificationLog EmptyLog() => new("", TaskVerificationIdentity.EmptyLogDigest, 0, false);

    private static void SelectGraph(MainWindow window, string graphId)
    {
        var combo = (ComboBox)window.FindName("GraphsComboBox");
        combo.SelectedItem = combo.Items.Cast<object>().Single(item => item.ToString()!.Contains($"GraphId = {graphId},", StringComparison.Ordinal));
        WaitFor(() => ((TextBlock)window.FindName("GraphSummaryTextBlock")).Text.StartsWith("Graph: " + graphId + "\n", StringComparison.Ordinal)
            && ((ComboBox)window.FindName("GraphTasksComboBox")).Items.Count > 0);
    }

    private static T Background<T>(Func<Task<T>> work) => Task.Run(work).GetAwaiter().GetResult();

    private sealed class Scope : IDisposable
    {
        private DesktopBootstrap? _bootstrap;
        private MainWindow? _window;
        public Scope(ITaskWorkspaceManager? manager = null, ILlmClient? client = null)
        {
            Root = Path.Combine(Path.GetTempPath(), "IronPaw-desktop-graphs-" + Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(Path.Combine(Root, "workspace"));
            Store = Background(() => CSharpDbWorkflowStore.OpenAsync(DatabasePath));
            Controller = new(Store, Store, client ?? Client, Path.Combine(Root, "workspace"), Path.Combine(Root, "managed"), manager);
        }
        public string Root { get; }
        public string DatabasePath => Path.Combine(Root, "workflows.cdb");
        public CSharpDbWorkflowStore Store { get; }
        public TaskGraphHostController Controller { get; }
        public UnusedClient Client { get; } = new();
        public TaskGraphCheckpoint Accept(string id, bool command = false, string provider = "local", bool includeConsumer = true)
        {
            var graph = new WorkflowTaskGraph(id, "Exercise desktop graph control", [
                new("producer", "Create result") { Resources = new() { ReadPaths = ["."], WritePaths = ["result.txt"] },
                    ExpectedArtifacts = [new("output", "Captured result", TaskArtifactKind.File, "result.txt", Required: false)],
                    AcceptanceCriteria = [new("build-check", "The build command succeeds")] },
                new("consumer", "Use accepted output") { Dependencies = ["producer"] }
            ]);
            if (!includeConsumer) graph = graph with { Tasks = [graph.Tasks[0]] };
            var binding = new TaskGraphAgentBinding(AgentTaskIdentity.RootRunId(id).Value, Controller.WorkspaceRoot,
                new string('0', 40), Controller.WorkspaceStoreRoot, provider, "rule-based-v1", new(ToolProfile.Coding),
                new(MaxTotalTokens: 20000, MaxExecutionTime: TimeSpan.FromSeconds(120))) {
                DependencyMode = TaskDependencyMode.AcceptedChanges,
                VerificationAuthority = command ? TaskVerificationAuthority.TrustedLocalProcess : TaskVerificationAuthority.None,
                VerificationProfiles = command ? [new("build", Path.GetFullPath(Environment.ProcessPath!), "sha256:" + new string('a', 64), [], TimeSpan.FromSeconds(10))] : [],
                CommandVerificationRules = command ? [new("producer", "build-check", "build")] : []
            };
            return Background(() => Store.AcceptAgentTaskGraphAsync(graph, CallerIdentity.LocalOwner(), 1, binding, default));
        }
        public MainWindow ShowWindow()
        {
            var services = new ServiceCollection().AddSingleton<IResumableAgentOrchestrator>(new StoreBrowser(Store))
                .AddSingleton<IWorkflowStore>(_ => Store).AddSingleton(Controller).AddSingleton(new SkillAddinLoadResult([], [], [])).BuildServiceProvider();
            _bootstrap = new(services);
            var window = new MainWindow(_bootstrap) { WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000, Top = -10000, ShowInTaskbar = false, ShowActivated = false };
            _window = window;
            window.Show();
            return window;
        }
        public void Dispose()
        {
            if (_window is { IsVisible: true } window)
            {
                window.Close();
                WaitFor(() => !window.IsVisible);
            }
            _bootstrap?.Dispose();
            Task.Run(Store.Dispose).GetAwaiter().GetResult();
            // This scope owns the exact GUID directory created above.
            Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class BlockingWorkspaces : ITaskWorkspaceManager
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Cancelled { get; private set; }
        public Task<string> ResolveCommitAsync(string root, string revision, CancellationToken ct) => Task.FromResult(new string('0', 40));
        public Task<TaskWorkspace> OpenOrCreateAsync(TaskGraphAgentBinding binding, TaskGraphClaim claim, CancellationToken ct) => WaitAsync(ct);
        public Task<TaskWorkspace> PrepareAsync(TaskGraphAgentBinding binding, TaskGraphClaim claim, ImmutableArray<TaskDependencyInput> dependencies, CancellationToken ct) => WaitAsync(ct);
        private async Task<TaskWorkspace> WaitAsync(CancellationToken ct)
        {
            Started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
            throw new InvalidOperationException("The test workspace should only finish by cancellation.");
        }
        public Task<TaskWorkspaceCapture> CaptureAsync(TaskWorkspace workspace, ImmutableArray<TaskArtifactRequirement> requirements, CancellationToken ct) => throw new InvalidOperationException("Unexpected capture");
        public Task<bool> VerifyAsync(TaskWorkspace workspace, WorkflowTaskResult result, CancellationToken ct) => throw new InvalidOperationException("Unexpected verification");
    }

    private sealed class PreflightWorkspaces : ITaskWorkspaceManager
    {
        public int ResolveCalls { get; private set; }
        public int ExecutionCalls { get; private set; }
        public int ReadOnlyCalls { get; private set; }
        public Task<string> ResolveCommitAsync(string root, string revision, CancellationToken ct)
        {
            ResolveCalls++;
            return Task.FromResult(new string('0', 40));
        }
        public Task<TaskWorkspace> OpenOrCreateAsync(TaskGraphAgentBinding binding, TaskGraphClaim claim, CancellationToken ct) => UnexpectedExecution();
        public Task<TaskWorkspace> PrepareAsync(TaskGraphAgentBinding binding, TaskGraphClaim claim, ImmutableArray<TaskDependencyInput> dependencies, CancellationToken ct) => UnexpectedExecution();
        private Task<TaskWorkspace> UnexpectedExecution()
        {
            ExecutionCalls++;
            throw new InvalidOperationException("This provider preflight must not execute task work.");
        }
        public Task<TaskWorkspace> OpenExistingAsync(TaskGraphAgentBinding binding, TaskGraphClaim claim, CancellationToken ct)
        {
            ReadOnlyCalls++;
            return Task.FromResult(new TaskWorkspace(claim.GraphId, claim.GraphFingerprint, claim.Task.TaskId,
                claim.AttemptId, binding.BaseCommit, Path.Combine(binding.WorkspaceStoreRoot, "workspace"), binding.WorkspaceStoreRoot));
        }
        public Task<TaskWorkspaceCapture> CaptureAsync(TaskWorkspace workspace, ImmutableArray<TaskArtifactRequirement> requirements, CancellationToken ct) => throw new InvalidOperationException("Unexpected capture");
        public Task<bool> VerifyAsync(TaskWorkspace workspace, WorkflowTaskResult result, CancellationToken ct) => Task.FromResult(true);
    }

    internal sealed class UnusedClient : ILlmClient
    {
        public int Calls { get; private set; }
        public async IAsyncEnumerable<LlmDelta> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            Calls++;
            await Task.CompletedTask;
            throw new InvalidOperationException("This desktop probe must not contact a provider.");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    private sealed class StoreBrowser(IWorkflowStore store) : IResumableAgentOrchestrator
    {
        public Task<IReadOnlyList<WorkflowCheckpoint>> ListWorkflowsAsync(CancellationToken ct) => store.ListAsync(ct);
        public Task<AgentRunResult> RunAsync(AgentRunRequest request, IAgentEventSink sink, CancellationToken ct) => throw new InvalidOperationException("Unexpected normal run");
        public Task<AgentRunResult> ResumeAsync(RunId runId, CallerIdentity caller, IAgentEventSink sink, CancellationToken ct) => throw new InvalidOperationException("Unexpected normal resume");
        public Task PauseAsync(RunId runId, CallerIdentity caller, CancellationToken ct) => throw new InvalidOperationException("Unexpected normal pause");
        public Task CancelAsync(RunId runId, CallerIdentity caller, CancellationToken ct) => throw new InvalidOperationException("Unexpected normal cancel");
        public Task AnswerAsync(RunId runId, string requestId, string answer, CallerIdentity caller, CancellationToken ct) => throw new InvalidOperationException("Unexpected normal answer");
        public Task DecideApprovalAsync(RunId runId, string requestId, bool approved, CallerIdentity caller, CancellationToken ct) => throw new InvalidOperationException("Unexpected normal approval");
        public Task ReconcileToolAsync(RunId runId, string executionId, ToolResult result, string note, CallerIdentity caller, CancellationToken ct) => throw new InvalidOperationException("Unexpected normal reconciliation");
    }

    private static Task RunInSta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try { action(); completion.SetResult(); }
            catch (Exception error) { completion.SetException(error); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static void WaitFor(Func<bool> condition)
    {
        var frame = new DispatcherFrame();
        var deadline = DateTime.UtcNow.AddSeconds(12);
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) => { if (condition() || DateTime.UtcNow >= deadline) frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
        Assert.True(condition(), "The WPF graph state did not settle before the probe deadline.");
    }
}
