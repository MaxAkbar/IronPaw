using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore;
using IronPaw.AgentCore.Persistence.CSharpDb;
using IronPaw.Desktop;
using IronPaw.AgentCore.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace IronPaw.Desktop.Tests;

[TestClass]
public sealed class MainWindowRenderTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task RealWorkflowCanFinishAndPersistWhileTheDesktopDispatcherIsBlocked(bool resume) => RunInSta(() =>
    {
        var directory = Directory.CreateTempSubdirectory("IronPaw-desktop-background-");
        using var scheduler = new SessionLaneScheduler();
        var store = Task.Run(() => CSharpDbWorkflowStore.OpenAsync(Path.Combine(directory.FullName, "workflows.cdb"))).GetAwaiter().GetResult();
        var client = new HeldLocalClient();
        var application = new CSharpDbApplicationStore(new(Path.Combine(directory.FullName, "application.cdb")));
        var runtime = new AgentOrchestrator(scheduler,
            application, application, new InMemoryToolRegistry([]), new ToolExecutor(),
            new ToolSchemaNormalizer(), client, new TranscriptSanitizer(), new ToolResultTruncator(), new DefaultSystemPromptBuilder(),
            runHistoryStore: application, workflowStore: store);
        MainWindow? window = null;
        try
        {
            if (resume)
            {
                var id = RunId.New();
                var request = new AgentRunRequest(new("background-resume"), "Complete a local response", "local", "rule-based-v1",
                    new(ToolProfile.Coding), CallerIdentity.LocalOwner(), RequestedRunId: id);
                Task.Run(() => store.CreateAsync(new(id.Value, id.Value, null, "background-task", request, DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow, Status: WorkflowStatus.Interrupted, Budget: new()), default)).GetAwaiter().GetResult();
            }
            var services = new ServiceCollection().AddSingleton<IResumableAgentOrchestrator>(runtime).AddSingleton<IWorkflowStore>(store)
                .AddSingleton(new SkillAddinLoadResult([], [], [])).BuildServiceProvider();
            window = new MainWindow(new DesktopBootstrap(services))
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000,
                Top = -10000,
                ShowInTaskbar = false,
                ShowActivated = false
            };
            window.Show();
            if (resume)
            {
                WaitFor(() => ((Button)window.FindName("ResumeWorkflowButton")).IsEnabled);
                ((Button)window.FindName("ResumeWorkflowButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            else ((Button)window.FindName("RunButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            // Do not pump the dispatcher from admission through saved completion.
            var runId = client.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            Assert.IsFalse(client.CapturedDispatcher);
            client.Release.TrySetResult();
            var saved = Task.Run(async () =>
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (true)
                {
                    var checkpoint = await store.GetAsync(runId, deadline.Token);
                    if (checkpoint?.Status == WorkflowStatus.Succeeded) return checkpoint;
                    await Task.Delay(10, deadline.Token);
                }
            }).GetAwaiter().GetResult();
            Assert.IsNull(saved.LeaseOwner);
            Assert.AreEqual(AgentRunStatus.Succeeded, saved.Result!.Status);
            Assert.AreEqual(HeldLocalClient.Response, saved.Result.FinalAssistantMessage);
            Assert.AreEqual(1, saved.Usage!.ModelCalls);
            Assert.IsFalse(((Button)window.FindName("RunButton")).IsEnabled);
            Assert.IsEmpty(((TextBox)window.FindName("AssistantOutputTextBox")).Text);
            WaitFor(() => ((Button)window.FindName("RunButton")).IsEnabled);
            Assert.AreEqual(HeldLocalClient.Response, ((TextBox)window.FindName("AssistantOutputTextBox")).Text);
            Assert.IsNotNull(application.GetAsync(saved.Request.SessionKey, default).GetAwaiter().GetResult());
            Assert.AreEqual(HeldLocalClient.Response, application.LoadAsync(saved.Request.SessionKey, default).GetAwaiter().GetResult()[^1].Content);
            Assert.AreEqual(AgentRunStatus.Succeeded, application.GetAsync(runId.Value, default).GetAwaiter().GetResult()!.Status);
        }
        finally
        {
            client.Release.TrySetResult();
            window?.Close();
            if (window is not null) WaitFor(() => !window.IsVisible);
            Task.Run(store.Dispose).GetAwaiter().GetResult();
            directory.Delete(true);
        }
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task EventFloodCompletesWithoutPumpingDispatcher_FinalResultAndCloseRejectLateText(bool stress) => RunInSta(() =>
    {
        var store = new WorkflowRecoveryControllerTests.MemoryStore();
        var agent = new WorkflowRecoveryControllerTests.FakeOrchestrator(store);
        var final = new TaskCompletionSource<AgentRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        agent.PendingRun = final.Task;
        var services = new ServiceCollection().AddSingleton<IResumableAgentOrchestrator>(agent).AddSingleton<IWorkflowStore>(store)
            .AddSingleton(new SkillAddinLoadResult([], [], [])).BuildServiceProvider();
        var window = new MainWindow(new DesktopBootstrap(services))
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000,
            Top = -10000,
            ShowInTaskbar = false,
            ShowActivated = false
        };
        try
        {
            window.Show();
            ((Button)window.FindName("RunButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitFor(() => agent.LastRequest is not null);
            var run = agent.LastRequest!.RequestedRunId!.Value;
            var sink = Assert.IsExactInstanceOfType<UiEventSink>(agent.LastSink);
            if (stress)
            {
                var failures = 0;
                window.Events.CollectionChanged += (_, _) => { if (failures++ == 0) throw new InvalidOperationException("display observer failed"); };
            }
            // Keep the dispatcher blocked: producers must finish without any UI callback.
            Task.Run(async () =>
            {
                for (var i = 0; i < 10000; i++) await sink.PublishAsync(new(run, new("default"), AgentEventStream.Assistant,
                    "assistant_delta", DateTimeOffset.UtcNow, EventData.Of(("text", "x"))), default);
            }).WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            Assert.IsEmpty(((TextBox)window.FindName("AssistantOutputTextBox")).Text);
            WaitFor(() => ((TextBox)window.FindName("AssistantOutputTextBox")).Text.Length > 0);
            Assert.IsInRange(1, MainWindow.MaxVisibleEvents, window.Events.Count);
            for (var i = 0; i < 600; i++) sink.PublishAsync(new(run, new("default"), AgentEventStream.Lifecycle,
                "event_" + i, DateTimeOffset.UtcNow, EventData.Of()), default).GetAwaiter().GetResult();
            WaitFor(() => window.Events.Any(line => line.Contains("event_599", StringComparison.Ordinal)));
            Assert.AreEqual(MainWindow.MaxVisibleEvents, window.Events.Count);
            var finalText = stress ? new string('Z', 90000) : "Authoritative final response";
            final.SetResult(agent.Result with { RunId = run, FinalAssistantMessage = finalText });
            sink.PublishAsync(new(run, new("default"), AgentEventStream.Assistant, "assistant_delta", DateTimeOffset.UtcNow,
                EventData.Of(("text", "late text must not alter final output"))), default).GetAwaiter().GetResult();
            WaitFor(() => ((Button)window.FindName("RunButton")).IsEnabled);
            var shown = ((TextBox)window.FindName("AssistantOutputTextBox")).Text;
            if (stress)
            {
                Assert.StartsWith(new string('Z', MainWindow.MaxVisibleResponseCharacters), shown);
                Assert.Contains("Response shortened", shown);
                Assert.IsTrue(shown.Length < MainWindow.MaxVisibleResponseCharacters + 100);
            }
            else Assert.AreEqual(finalText, shown);
            if (!stress)
            {
                window.UpdateLayout();
                var content = (FrameworkElement)window.Content;
                var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(content);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                var screenshotPath = Path.Combine(Path.GetTempPath(), "IronPaw-desktop-live-events-smoke.png");
                using (var file = File.Create(screenshotPath)) encoder.Save(file);
                Console.WriteLine("Desktop live events render: " + screenshotPath);
            }
            window.Close();
            sink.PublishAsync(new(run, new("default"), AgentEventStream.Lifecycle, "late_after_close", DateTimeOffset.UtcNow, EventData.Of()), default).GetAwaiter().GetResult();
            Assert.AreEqual(0, sink.PendingCount);
        }
        finally
        {
            final.TrySetResult(agent.Result);
            WaitFor(() => ((Button)window.FindName("RunButton")).IsEnabled);
            window.Close();
        }
    });

    [TestMethod]
    public Task ApplicationPersistenceCanBeAwaitedFromBlockedDispatcherWithoutExpandingTools() => RunInSta(() =>
    {
        var root = Directory.CreateTempSubdirectory("IronPaw-desktop-application-").FullName;
        try
        {
            var state = Path.Combine(root, "state");
            var key = new SessionKey("desktop-storage");
            using (var host = DesktopBootstrapper.Build(state, root))
            {
                Assert.IsExactInstanceOfType<CSharpDbApplicationStore>(host.SessionStore);
                Assert.AreSame<object>(host.SessionStore, host.TranscriptStore);
                Assert.AreSame<object>(host.SessionStore, host.RunHistoryStore);
                Assert.AreSame<object>(host.SessionStore, host.MemoryStore);
                Assert.IsExactInstanceOfType<CSharpDbAgentMessageBus>(host.MessageBus);
                // Synchronously await on a real dispatcher: store operations must move themselves off it.
                host.SessionStore.UpsertAsync(key, _ => new(key.Value, DateTimeOffset.UtcNow), default).WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
                host.TranscriptStore.AppendAsync(key, [new(ChatRole.User, "saved")], default).WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
                var tools = host.ToolRegistry.ResolveTools(new(ToolProfile.Full, SenderIsOwner: true)).Select(t => t.Descriptor.Name).Order();
                Assert.AreSequenceEqual(new[] { "current_time", "request_input", "list_files", "read_file", "write_file", "web_navigate", "web_extract_structured",
                    "review_runs", "review_run_detail", "create_skill", "send_agent_message", "read_agent_messages", "invoke_agent", "list_agents" }.Order(), tools);
            }
            using var reopened = DesktopBootstrapper.Build(state, root);
            Assert.IsNotNull(reopened.SessionStore.GetAsync(key, default).WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult());
            Assert.AreEqual("saved", Assert.ContainsSingle(reopened.TranscriptStore.LoadAsync(key, default).WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult()).Content);
        }
        finally { Directory.Delete(root, true); }
    });

    [TestMethod]
    public Task SharedSettingsPrefillNewRunControlsWithoutExecutingAProvider() => RunInSta(() =>
    {
        var temporaryRoot = Path.Combine(Path.GetTempPath(), "IronPaw-desktop-settings-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(temporaryRoot);
        MainWindow? window = null;
        DesktopBootstrap? bootstrap = null;
        try
        {
            var settingsFile = Path.Combine(temporaryRoot, "settings.json");
            File.WriteAllText(settingsFile, """
                {"provider":"openai","model":"configured-model","maxTotalTokens":12345,"maxExecutionSeconds":90,
                 "agents":{"reviewer":{"provider":"anthropic","model":"review-model"}}}
                """);
            // Read on an STA dispatcher to catch startup deadlocks as well as configuration drift.
            var settings = IronPaw.AgentCore.IronPawSettingsLoader.Load(settingsFile, _ => null);
            bootstrap = DesktopBootstrapper.Build(Path.Combine(temporaryRoot, "state"), Path.Combine(temporaryRoot, "workspace"), settings);
            window = new MainWindow(bootstrap);
            Assert.AreEqual("openai", (string)((ComboBoxItem)((ComboBox)window.FindName("ProviderComboBox")).SelectedItem).Content);
            Assert.AreEqual("configured-model", ((TextBox)window.FindName("ModelTextBox")).Text);
            Assert.AreEqual("12345", ((TextBox)window.FindName("TokenLimitTextBox")).Text);
            Assert.AreEqual("90", ((TextBox)window.FindName("ExecutionLimitTextBox")).Text);
            Assert.AreEqual("review-model", bootstrap.Settings.Agents.Reviewer.Model);
            Assert.IsEmpty(Task.Run(() => bootstrap.WorkflowStore.ListAsync(default)).GetAwaiter().GetResult());
        }
        finally
        {
            window?.Close(); bootstrap?.Dispose(); Directory.Delete(temporaryRoot, recursive: true);
        }
    });

    [TestMethod]
    public Task RecoveryWindowRendersPersistedChildApprovalAndBudgetFromIsolatedState() => RunInSta(() =>
    {
        var temporaryRoot = Path.Combine(Path.GetTempPath(), "IronPaw-desktop-render-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(temporaryRoot);
        MainWindow? window = null;
        DesktopBootstrap? bootstrap = null;
        try
        {
            bootstrap = DesktopBootstrapper.Build(Path.Combine(temporaryRoot, "state"), Path.Combine(temporaryRoot, "workspace"));
            var root = WorkflowRecoveryControllerTests.MakeRun("root-render", "root-render", null) with
            {
                Status = WorkflowStatus.AwaitingInput,
                Budget = new(MaxTotalTokens: 20000, MaxExecutionTime: TimeSpan.FromSeconds(120)),
                Usage = new(2, 1, InputTokens: 2400, OutputTokens: 300),
                ModelCalls = [new("render-root-model", [], Attempts: [new("render-root-attempt", 2700, 300, 2700, true, WorkflowReservationState.Settled)])]
            };
            var child = WorkflowRecoveryControllerTests.MakeRun("child-render", "root-render", "root-render") with
            {
                Status = WorkflowStatus.AwaitingInput,
                Usage = new(1, 0, InputTokens: 1200, OutputTokens: 200),
                Request = WorkflowRecoveryControllerTests.MakeRun("child-render", "root-render", "root-render").Request with { SessionKey = new("render-child"), AgentId = "codegen", ParentRunId = new("root-render"), RootRunId = new("root-render") },
                ModelCalls = [new("render-child-model", [], Attempts: [new("render-child-attempt", 1400, 200, 1400, true, WorkflowReservationState.Settled)])],
                ToolExecutions = [new("execution-render", new("call-render", "write_file", JsonSerializer.SerializeToElement(new { path = "Plan.md", content = "Verified design" })), State: WorkflowToolState.Planned)],
                HumanRequests = [new("request-render", WorkflowHumanRequestKind.ToolApproval, "Approve writing the project plan?", "tool:execution-render", DateTimeOffset.UtcNow,
                    ExecutionId: "execution-render", OperationFingerprint: "exact-operation-render")]
            };
            Task.Run(async () =>
            {
                await bootstrap.WorkflowStore.CreateAsync(root, default);
                await bootstrap.WorkflowStore.CreateAsync(child, default);
            }).GetAwaiter().GetResult();
            window = new MainWindow(bootstrap)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000,
                Top = -10000,
                ShowInTaskbar = false,
                ShowActivated = false
            };
            window.Show();
            WaitFor(() => window.FindName("InvocationsComboBox") is ComboBox { Items.Count: 2 });
            var tabs = (TabControl)window.FindName("ActivityTabControl");
            tabs.SelectedIndex = 1;
            var invocations = (ComboBox)window.FindName("InvocationsComboBox");
            invocations.SelectedIndex = 1;
            WaitFor(() => ((TextBox)window.FindName("HumanRequestTextBox")).Text.Contains("exact-operation-render"));
            Assert.Contains("Plan.md", ((TextBox)window.FindName("HumanRequestTextBox")).Text);
            Assert.Contains("root-render", ((TextBlock)window.FindName("WorkflowSummaryTextBlock")).Text);
            Assert.Contains("Tokens charged: 4100", ((TextBlock)window.FindName("WorkflowSummaryTextBlock")).Text);
            Assert.IsTrue(((Button)window.FindName("ApproveButton")).IsEnabled);
            Assert.IsTrue(((Button)window.FindName("DenyButton")).IsEnabled);
            Assert.IsFalse(((Button)window.FindName("AnswerButton")).IsEnabled);
            Assert.Contains("child-render", ((TextBox)window.FindName("WorkflowDetailTextBox")).Text);
            window.UpdateLayout();
            var content = (FrameworkElement)window.Content;
            var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(content);
            Assert.IsTrue(bitmap.PixelWidth >= 980);
            Assert.IsTrue(bitmap.PixelHeight >= 600);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var screenshotPath = Path.Combine(Path.GetTempPath(), "IronPaw-desktop-recovery-smoke.png");
            using (var file = File.Create(screenshotPath)) encoder.Save(file);
            Console.WriteLine("Desktop render: " + screenshotPath);
        }
        finally
        {
            window?.Close();
            bootstrap?.Dispose();
            // This test owns the GUID-named directory and never opens the normal user's state.
            Directory.Delete(temporaryRoot, recursive: true);
        }
    });

    [TestMethod]
    public Task ActiveCancelTargetsOriginalRootWhileBrowserSelectsIdleWorkflow() => RunInSta(() =>
    {
        var store = new WorkflowRecoveryControllerTests.MemoryStore();
        store.Runs["idle-root"] = WorkflowRecoveryControllerTests.MakeRun("idle-root", "idle-root", null);
        var orchestrator = new WorkflowRecoveryControllerTests.FakeOrchestrator(store);
        var pending = new TaskCompletionSource<AgentRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        orchestrator.PendingRun = pending.Task;
        var services = new ServiceCollection().AddSingleton<IResumableAgentOrchestrator>(orchestrator)
            .AddSingleton<IWorkflowStore>(store).AddSingleton(new SkillAddinLoadResult([], [], [])).BuildServiceProvider();
        var window = new MainWindow(new DesktopBootstrap(services))
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000,
            Top = -10000,
            ShowInTaskbar = false,
            ShowActivated = false
        };
        try
        {
            window.Show();
            WaitFor(() => ((ComboBox)window.FindName("WorkflowsComboBox")).Items.Count == 1);
            ((Button)window.FindName("RunButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitFor(() => orchestrator.LastRequest is not null);
            Assert.IsNotNull(orchestrator.LastRequest?.RequestedRunId);
            var active = orchestrator.LastRequest!.RequestedRunId!.Value;
            Assert.IsFalse(((Button)window.FindName("CancelWorkflowButton")).IsEnabled);
            Assert.IsTrue(((Button)window.FindName("CancelButton")).IsEnabled);
            ((Button)window.FindName("CancelWorkflowButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.DoesNotContain("cancel:idle-root", orchestrator.Calls);
            ((Button)window.FindName("CancelButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitFor(() => orchestrator.Calls.Contains("cancel:" + active));
            Assert.AreEqual(CallerIdentity.LocalOwner(), orchestrator.LastCaller);
            Assert.DoesNotContain("cancel:idle-root", orchestrator.Calls);
            pending.SetResult(orchestrator.Result with { RunId = active, Status = AgentRunStatus.Cancelled });
            WaitFor(() => ((Button)window.FindName("RunButton")).IsEnabled);
        }
        finally
        {
            pending.TrySetResult(orchestrator.Result);
            WaitFor(() => ((Button)window.FindName("RunButton")).IsEnabled);
            window.Close();
        }
    });

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

    private sealed class HeldLocalClient : ILlmClient
    {
        public const string Response = "Durably completed while the UI thread was blocked.";
        public TaskCompletionSource<RunId> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool CapturedDispatcher { get; private set; }
        public async IAsyncEnumerable<LlmDelta> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            CapturedDispatcher = SynchronizationContext.Current is DispatcherSynchronizationContext;
            yield return new AssistantTextDelta(Response);
            Started.TrySetResult(request.RunId);
            await Release.Task.WaitAsync(ct).ConfigureAwait(false);
            yield return new UsageDelta(5, 3, 0, 0);
            yield return new CompletedDelta();
        }
    }

    private static void WaitFor(Func<bool> condition)
    {
        var frame = new DispatcherFrame();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) => { if (condition() || DateTime.UtcNow >= deadline) frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
        Assert.IsTrue(condition(), "The WPF state did not settle before the probe deadline.");
    }
}
