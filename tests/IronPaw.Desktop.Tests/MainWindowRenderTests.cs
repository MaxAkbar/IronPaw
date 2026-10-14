using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IronPaw.AgentCore.Abstractions;
using IronPaw.Desktop;
using IronPaw.AgentCore.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace IronPaw.Desktop.Tests;

public sealed class MainWindowRenderTests
{
    [Fact]
    public Task RecoveryWindowRendersPersistedChildApprovalAndBudgetFromIsolatedState() => RunInSta(() =>
    {
        var temporaryRoot = Path.Combine(Path.GetTempPath(), "IronPaw-desktop-render-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(temporaryRoot);
        MainWindow? window = null;
        DesktopBootstrap? bootstrap = null;
        try
        {
            bootstrap = DesktopBootstrapper.Build(Path.Combine(temporaryRoot, "state"), Path.Combine(temporaryRoot, "workspace"));
            var root = WorkflowRecoveryControllerTests.MakeRun("root-render", "root-render", null) with {
                Status = WorkflowStatus.AwaitingInput, Budget = new(MaxTotalTokens: 20000, MaxExecutionTime: TimeSpan.FromSeconds(120)),
                Usage = new(2, 1, InputTokens: 2400, OutputTokens: 300),
                ModelCalls = [new("render-root-model", [], Attempts: [new("render-root-attempt", 2700, 300, 2700, true, WorkflowReservationState.Settled)])] };
            var child = WorkflowRecoveryControllerTests.MakeRun("child-render", "root-render", "root-render") with {
                Status = WorkflowStatus.AwaitingInput, Usage = new(1, 0, InputTokens: 1200, OutputTokens: 200),
                Request = WorkflowRecoveryControllerTests.MakeRun("child-render", "root-render", "root-render").Request with { SessionKey = new("render-child"), AgentId = "codegen", ParentRunId = new("root-render"), RootRunId = new("root-render") },
                ModelCalls = [new("render-child-model", [], Attempts: [new("render-child-attempt", 1400, 200, 1400, true, WorkflowReservationState.Settled)])],
                ToolExecutions = [new("execution-render", new("call-render", "write_file", JsonSerializer.SerializeToElement(new { path = "Plan.md", content = "Verified design" })), State: WorkflowToolState.Planned)],
                HumanRequests = [new("request-render", WorkflowHumanRequestKind.ToolApproval, "Approve writing the project plan?", "tool:execution-render", DateTimeOffset.UtcNow,
                    ExecutionId: "execution-render", OperationFingerprint: "exact-operation-render")] };
            Task.Run(async () =>
            {
                await bootstrap.WorkflowStore.CreateAsync(root, default);
                await bootstrap.WorkflowStore.CreateAsync(child, default);
            }).GetAwaiter().GetResult();
            window = new MainWindow(bootstrap) { WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000, Top = -10000, ShowInTaskbar = false, ShowActivated = false };
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
            Assert.True(((Button)window.FindName("ApproveButton")).IsEnabled);
            Assert.True(((Button)window.FindName("DenyButton")).IsEnabled);
            Assert.False(((Button)window.FindName("AnswerButton")).IsEnabled);
            Assert.Contains("child-render", ((TextBox)window.FindName("WorkflowDetailTextBox")).Text);
            window.UpdateLayout();
            var content = (FrameworkElement)window.Content;
            var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(content);
            Assert.True(bitmap.PixelWidth >= 980);
            Assert.True(bitmap.PixelHeight >= 600);
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

    [Fact]
    public Task ActiveCancelTargetsOriginalRootWhileBrowserSelectsIdleWorkflow() => RunInSta(() =>
    {
        var store = new WorkflowRecoveryControllerTests.MemoryStore();
        store.Runs["idle-root"] = WorkflowRecoveryControllerTests.MakeRun("idle-root", "idle-root", null);
        var orchestrator = new WorkflowRecoveryControllerTests.FakeOrchestrator(store);
        var pending = new TaskCompletionSource<AgentRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        orchestrator.PendingRun = pending.Task;
        var services = new ServiceCollection().AddSingleton<IResumableAgentOrchestrator>(orchestrator)
            .AddSingleton<IWorkflowStore>(store).AddSingleton(new SkillAddinLoadResult([], [], [])).BuildServiceProvider();
        var window = new MainWindow(new DesktopBootstrap(services)) { WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000, Top = -10000, ShowInTaskbar = false, ShowActivated = false };
        try
        {
            window.Show();
            WaitFor(() => ((ComboBox)window.FindName("WorkflowsComboBox")).Items.Count == 1);
            ((Button)window.FindName("RunButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.NotNull(orchestrator.LastRequest?.RequestedRunId);
            var active = orchestrator.LastRequest!.RequestedRunId!.Value;
            Assert.False(((Button)window.FindName("CancelWorkflowButton")).IsEnabled);
            Assert.True(((Button)window.FindName("CancelButton")).IsEnabled);
            ((Button)window.FindName("CancelWorkflowButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.DoesNotContain("cancel:idle-root", orchestrator.Calls);
            ((Button)window.FindName("CancelButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitFor(() => orchestrator.Calls.Contains("cancel:" + active));
            Assert.Equal(CallerIdentity.LocalOwner(), orchestrator.LastCaller);
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

    private static void WaitFor(Func<bool> condition)
    {
        var frame = new DispatcherFrame();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) => { if (condition() || DateTime.UtcNow >= deadline) frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
        Assert.True(condition(), "The WPF state did not settle before the probe deadline.");
    }
}
