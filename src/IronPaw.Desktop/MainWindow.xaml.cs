using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Providers;
using IronPaw.AgentCore.Tools;
using IronPaw.AgentCore.Mcp;
using IronPaw.AgentCore.Browser;

namespace IronPaw.Desktop;

public partial class MainWindow : Window
{
    private readonly DesktopBootstrap _bootstrap;
    private McpHost? _connectors;
    private readonly IResumableAgentOrchestrator _orchestrator;
    private readonly WorkflowRecoveryController _recovery;
    private CancellationTokenSource? _runCancellationTokenSource;
    private RunId? _activeRootRunId;
    private Task? _runTask;
    private WorkflowRecoveryDetail? _detail;
    private bool _changingSelection;
    private bool _controlBusy;
    private bool _closing;
    private bool _closePending;
    private int _detailVersion;

    public MainWindow() : this(DesktopBootstrapper.Build()) { }

    internal MainWindow(DesktopBootstrap bootstrap)
    {
        _bootstrap = bootstrap;
        InitializeComponent();
        Events = new ObservableCollection<string>();
        DataContext = this;
        WorkspaceTextBlock.Text = _bootstrap.WorkspaceRoot is { } folder ? "Working folder: " + folder : "Working folder selected by the host";
        WorkspaceTextBlock.ToolTip = _bootstrap.WorkspaceRoot;
        var defaults = _bootstrap.Settings;
        ProviderComboBox.SelectedItem = ProviderComboBox.Items.OfType<ComboBoxItem>().Single(item => (string)item.Content == defaults.Provider);
        ModelTextBox.Text = defaults.Model ?? ProviderDefaults.DefaultModels[defaults.Provider];
        TokenLimitTextBox.Text = defaults.MaxTotalTokens?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
        ExecutionLimitTextBox.Text = defaults.MaxExecutionSeconds?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
        _orchestrator = _bootstrap.Orchestrator;
        _recovery = new(_orchestrator, _bootstrap.WorkflowStore);
        InitializeGraphHost();
        InitializeTimeline();
        InitializeEventDelivery();
        AppendSkillDiagnostics(_bootstrap.SkillLoadResult);
        PromptTextBox.Text = "Create a project plan for an agent architecture and write it to Plan.md.";
        Loaded += async (_, _) => { await RefreshWorkflowsAsync(); await RefreshGraphsAsync(); };
        UpdateControls();
    }

    public ObservableCollection<string> Events { get; }

    private async void RunButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_runCancellationTokenSource is not null || _activeGraphId is not null)
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
            ? ProviderDefaults.DefaultModels[provider]
            : ModelTextBox.Text.Trim();

        Events.Clear();
        AssistantOutputTextBox.Text = string.Empty;
        WorkflowBudget budget;
        try { budget = DesktopRunSettings.ParseBudget(TokenLimitTextBox.Text, ExecutionLimitTextBox.Text); }
        catch (ArgumentException error)
        {
            AppendLog(error.Message);
            return;
        }
        AppendLog($"Run queued. provider={provider}, model={model}");
        var runId = RunId.New();
        var connectors = EnableConnectorsCheckBox.IsChecked == true ? _connectors : null;
        var approvalTools = ApprovalToolsTextBox.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var profile = Enum.Parse<ToolProfile>((string)((ComboBoxItem)ToolProfileComboBox.SelectedItem).Tag);
        var policy = connectors?.Grant(new(Profile: profile)) ?? new ToolSelectionPolicy(Profile: profile);
        var browser = _bootstrap.Browser;
        var request = new AgentRunRequest(
                SessionKey: sessionKey,
                UserInput: prompt,
                Provider: provider,
                Model: model,
                ToolPolicy: browser?.Grant(policy) ?? policy,
                Caller: CallerIdentity.LocalOwner(), RequestedRunId: runId, Budget: budget,
                ApprovalRequiredTools: browser?.RequireApprovals(connectors?.RequireApprovals(approvalTools) ?? approvalTools) ?? connectors?.RequireApprovals(approvalTools) ?? approvalTools);
        await ExecuteRunAsync(runId, async ct => await _orchestrator.RunAsync(request, _eventSink, ct));
    }

    private async void CancelButton_OnClick(object sender, RoutedEventArgs e) =>
        await ControlAsync(_activeRootRunId, permanentCancel: true);

    private async void PauseActiveButton_OnClick(object sender, RoutedEventArgs e) =>
        await ControlAsync(_activeRootRunId, permanentCancel: false);

    private Task ExecuteRunAsync(RunId rootRunId, Func<CancellationToken, Task<AgentRunResult?>> action)
    {
        if (_activeRootRunId is not null || _activeGraphId is not null) return Task.CompletedTask;
        _eventSink.Reset();
        _liveText = true;
        _responseTruncated = false;
        AssistantOutputTextBox.Clear();
        _activeRootRunId = rootRunId;
        _runCancellationTokenSource = new();
        UpdateControls();
        return _runTask = ExecuteRunCoreAsync(rootRunId, action);
    }

    private async Task ExecuteRunCoreAsync(RunId rootRunId, Func<CancellationToken, Task<AgentRunResult?>> action)
    {
        var retainBrowser = false;
        try
        {
            var token = _runCancellationTokenSource!.Token;
            var result = await Task.Run(() => action(token));
            retainBrowser = result is null || result.Status is AgentRunStatus.AwaitingInput or AgentRunStatus.Paused;
            if (result is not null)
            {
                _liveText = false;
                DrainUiEvents();
                SetVisibleResponse(result.FinalAssistantMessage);
                AppendLog($"Run status: {result.Status}. run_id={result.RunId.Value}");
            }
            RecoveryStatusTextBlock.Text = "Saved workflow state refreshed.";
        }
        catch (Exception error)
        {
            RecoveryStatusTextBlock.Text = error.Message;
            AppendLog($"Workflow action failed: {error.Message}");
        }
        finally
        {
            if (!retainBrowser && _bootstrap.Browser is { } browser) await browser.ReleaseRunAsync(rootRunId);
            _liveText = false;
            _activeRootRunId = null;
            _runTask = null;
            _runCancellationTokenSource!.Dispose();
            _runCancellationTokenSource = null;
            UpdateControls();
            if (!_closePending) await RefreshWorkflowsAsync(rootRunId.Value);
        }
    }

    private async Task ControlAsync(RunId? rootRunId, bool permanentCancel)
    {
        if (rootRunId is null || _controlBusy) return;
        if (_activeGraphId is not null)
        {
            RecoveryStatusTextBlock.Text = "Use Task graphs to control the active graph.";
            return;
        }
        if (_activeRootRunId is { } active && active != rootRunId.Value)
        {
            RecoveryStatusTextBlock.Text = "Control the active root, or wait until it finishes before controlling another workflow.";
            return;
        }
        _controlBusy = true;
        UpdateControls();
        try
        {
            if (permanentCancel)
            {
                await Task.Run(() => _recovery.CancelAsync(rootRunId.Value, CancellationToken.None));
                if (_activeRootRunId is null && _bootstrap.Browser is { } browser)
                    await browser.ReleaseRunAsync(rootRunId.Value);
            }
            else await Task.Run(() => _recovery.PauseAsync(rootRunId.Value, CancellationToken.None));
            RecoveryStatusTextBlock.Text = permanentCancel ? "Permanent cancellation recorded." : "Pause recorded; active work is settling.";
            await RefreshWorkflowsAsync(rootRunId.Value.Value);
        }
        catch (Exception error) { RecoveryStatusTextBlock.Text = error.Message; }
        finally { _controlBusy = false; UpdateControls(); }
    }

    private async void RefreshWorkflows_OnClick(object sender, RoutedEventArgs e) => await RefreshWorkflowsAsync();

    private async Task RefreshWorkflowsAsync(string? rootId = null)
    {
        rootId ??= (WorkflowsComboBox.SelectedItem as WorkflowChoice)?.Checkpoint.RunId;
        try
        {
            var roots = await Task.Run(() => _recovery.ListRootsAsync(CancellationToken.None));
            _changingSelection = true;
            WorkflowsComboBox.ItemsSource = roots.Select(run => new WorkflowChoice(run)).ToArray();
            WorkflowsComboBox.SelectedItem = WorkflowsComboBox.Items.Cast<WorkflowChoice>()
                .FirstOrDefault(item => item.Checkpoint.RunId == rootId) ?? WorkflowsComboBox.Items.Cast<WorkflowChoice>().FirstOrDefault();
            _changingSelection = false;
            await LoadRootAsync();
        }
        catch (Exception error) { _changingSelection = false; RecoveryStatusTextBlock.Text = error.Message; }
    }

    private async void WorkflowSelection_OnChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_changingSelection && _recovery is not null) await LoadRootAsync();
    }

    private async Task LoadRootAsync()
    {
        if (WorkflowsComboBox.SelectedItem is not WorkflowChoice choice)
        {
            _detail = null;
            InvocationsComboBox.ItemsSource = null;
            WorkflowSummaryTextBlock.Text = "No saved workflows in this workspace.";
            WorkflowDetailTextBox.Clear();
            HumanRequestsComboBox.ItemsSource = null;
            UnknownToolsComboBox.ItemsSource = null;
            UpdateControls();
            return;
        }
        var version = ++_detailVersion;
        _detail = null;
        WorkflowSummaryTextBlock.Text = "Loading saved workflow state…";
        UpdateControls();
        try
        {
            var detail = await Task.Run(() => _recovery.InspectAsync(new RunId(choice.Checkpoint.RunId), CancellationToken.None));
            if (version != _detailVersion) return;
            _changingSelection = true;
            var previous = (InvocationsComboBox.SelectedItem as WorkflowChoice)?.Checkpoint.RunId;
            InvocationsComboBox.ItemsSource = detail.Invocations.Select(run => new WorkflowChoice(run)).ToArray();
            InvocationsComboBox.SelectedItem = InvocationsComboBox.Items.Cast<WorkflowChoice>()
                .FirstOrDefault(item => item.Checkpoint.RunId == previous) ?? InvocationsComboBox.Items.Cast<WorkflowChoice>().First();
            _changingSelection = false;
            await LoadInvocationAsync();
        }
        catch (Exception error) { _changingSelection = false; RecoveryStatusTextBlock.Text = error.Message; }
    }

    private async void InvocationSelection_OnChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_changingSelection && _recovery is not null) await LoadInvocationAsync();
    }

    private async Task LoadInvocationAsync()
    {
        if (InvocationsComboBox.SelectedItem is not WorkflowChoice choice) return;
        var version = ++_detailVersion;
        _detail = null;
        UpdateControls();
        try
        {
            var detail = await Task.Run(() => _recovery.InspectAsync(new RunId(choice.Checkpoint.RunId), CancellationToken.None));
            if (version != _detailVersion) return;
            _detail = detail;
            WorkflowSummaryTextBlock.Text = $"Root: {detail.Root.RunId}\nState: {detail.Root.Status}; control: {detail.Root.Control}; selected: {detail.Selected.Status}\n" +
                $"Shared calls: model {detail.Usage.ModelCalls}/{detail.Root.Budget?.MaxModelCalls}, tools {detail.Usage.ToolCalls}/{detail.Root.Budget?.MaxToolCalls}\n" +
                $"Tokens charged: {detail.Budget.ChargedTokens}; reserved: {detail.Budget.ReservedTokens}; remaining: {detail.Budget.RemainingTokens?.ToString() ?? "unlimited"}\n" +
                $"Execution: {detail.Budget.ExecutionTime.TotalSeconds:F1}s; remaining: {(detail.Budget.RemainingExecutionTime is { } time ? $"{time.TotalSeconds:F1}s" : "unlimited")}";
            WorkflowDetailTextBox.Text = FormatDetail(detail);
            HumanRequestsComboBox.ItemsSource = (detail.Selected.HumanRequests ?? []).Where(request => request.Response is null)
                .Select(request => new HumanRequestChoice(request)).ToArray();
            HumanRequestsComboBox.SelectedIndex = HumanRequestsComboBox.Items.Count > 0 ? 0 : -1;
            UnknownToolsComboBox.ItemsSource = (detail.Selected.ToolExecutions ?? []).Where(tool => tool.Result is null && tool.State == WorkflowToolState.Started)
                .Select(tool => new ToolChoice(tool)).ToArray();
            UnknownToolsComboBox.SelectedIndex = UnknownToolsComboBox.Items.Count > 0 ? 0 : -1;
            UpdateControls();
        }
        catch (Exception error) { RecoveryStatusTextBlock.Text = error.Message; }
    }

    private static string FormatDetail(WorkflowRecoveryDetail detail)
    {
        var text = new StringBuilder().AppendLine($"Root budget: {JsonSerializer.Serialize(detail.Root.Budget)}")
            .AppendLine($"Shared usage: {JsonSerializer.Serialize(detail.Usage)}")
            .AppendLine($"Shared budget ledger: {JsonSerializer.Serialize(detail.Budget)}");
        foreach (var invocation in detail.Invocations)
            text.AppendLine($"{invocation.CreatedAt:O} {invocation.RunId} parent={invocation.ParentRunId ?? "root"} task={invocation.TaskId} agent={invocation.Request.AgentId ?? "main"} state={invocation.Status} control={invocation.Control} lease={invocation.LeaseOwner ?? "none"}");
        text.AppendLine().AppendLine("Selected invocation:").AppendLine($"Prompt: {detail.Selected.Request.UserInput}");
        foreach (var tool in detail.Selected.ToolExecutions ?? [])
            text.AppendLine($"Tool {tool.ExecutionId}: {tool.Call.ToolName} {tool.Call.Arguments}; state={tool.State}; child={tool.ChildRunId ?? "none"}")
                .AppendLine($"Result: {tool.Result?.Content ?? "unrecorded"}; error={tool.Result?.IsError}; evidence={tool.ReconciliationNote}");
        foreach (var request in detail.Selected.HumanRequests ?? [])
            text.AppendLine($"Request {request.RequestId}: {request.Kind}; operation={request.OperationFingerprint}; prompt={request.Prompt}")
                .AppendLine($"Response: {JsonSerializer.Serialize(request.Response)}");
        text.AppendLine().AppendLine("Journal:");
        foreach (var entry in detail.Journal)
            text.AppendLine($"{entry.Timestamp:O} {entry.RunId} revision={entry.Revision} {entry.EventType}");
        if (detail.Selected.Error is not null) text.AppendLine($"Error: {detail.Selected.Error}");
        return text.ToString();
    }

    private void HumanRequestSelection_OnChanged(object sender, SelectionChangedEventArgs e)
    {
        var request = (HumanRequestsComboBox.SelectedItem as HumanRequestChoice)?.Request;
        var tool = _detail?.Selected.ToolExecutions?.FirstOrDefault(tool => tool.ExecutionId == request?.ExecutionId);
        HumanRequestTextBox.Text = request is null ? "No pending request." :
            $"Request: {request.RequestId}\n{request.Prompt}" + (tool is null ? "" : $"\nTool: {tool.Call.ToolName}\nArguments: {tool.Call.Arguments}\nOperation: {request.OperationFingerprint}");
        AnswerTextBox.Clear();
        AnswerTextBox.Visibility = request?.Kind == WorkflowHumanRequestKind.Input ? Visibility.Visible : Visibility.Collapsed;
        UpdateControls();
    }

    private void UnknownToolSelection_OnChanged(object sender, SelectionChangedEventArgs e)
    {
        var tool = (UnknownToolsComboBox.SelectedItem as ToolChoice)?.Execution;
        UnknownToolTextBox.Text = tool is null ? "No unknown tool outcome." : $"Execution: {tool.ExecutionId}\nTool: {tool.Call.ToolName}\nArguments: {tool.Call.Arguments}";
        ReconcileResultTextBox.Clear();
        ReconcileNoteTextBox.Clear();
        ReconcileErrorCheckBox.IsChecked = false;
        UpdateControls();
    }

    private async void ResumeWorkflow_OnClick(object sender, RoutedEventArgs e)
    {
        if (_detail is { } detail) await ExecuteRunAsync(new RunId(detail.Root.RunId),
            async ct => await _recovery.ResumeAsync(new RunId(detail.Root.RunId), _eventSink, ct));
    }

    private async void PauseWorkflow_OnClick(object sender, RoutedEventArgs e) =>
        await ControlAsync(_detail is { } detail ? new RunId(detail.Root.RunId) : null, false);

    private async void CancelWorkflow_OnClick(object sender, RoutedEventArgs e) =>
        await ControlAsync(_detail is { } detail ? new RunId(detail.Root.RunId) : null, true);

    private async void AnswerWorkflow_OnClick(object sender, RoutedEventArgs e)
    {
        if (_detail is not { } detail || HumanRequestsComboBox.SelectedItem is not HumanRequestChoice choice) return;
        var answer = AnswerTextBox.Text;
        await ExecuteRunAsync(new RunId(detail.Root.RunId), async ct => await _recovery.AnswerAndResumeAsync(
            new RunId(detail.Selected.RunId), choice.Request.RequestId, answer, _eventSink, ct));
    }

    private async void ApproveWorkflow_OnClick(object sender, RoutedEventArgs e) => await DecideApprovalAsync(true);
    private async void DenyWorkflow_OnClick(object sender, RoutedEventArgs e) => await DecideApprovalAsync(false);

    private async Task DecideApprovalAsync(bool approved)
    {
        if (_detail is not { } detail || HumanRequestsComboBox.SelectedItem is not HumanRequestChoice choice) return;
        await ExecuteRunAsync(new RunId(detail.Root.RunId), ct => _recovery.DecideApprovalAsync(
            new RunId(detail.Selected.RunId), choice.Request.RequestId, approved, _eventSink, ct));
    }

    private async void ReconcileWorkflow_OnClick(object sender, RoutedEventArgs e)
    {
        if (_detail is not { } detail || UnknownToolsComboBox.SelectedItem is not ToolChoice choice) return;
        var content = ReconcileResultTextBox.Text;
        var note = ReconcileNoteTextBox.Text;
        var isError = ReconcileErrorCheckBox.IsChecked == true;
        await ExecuteRunAsync(new RunId(detail.Root.RunId), async ct => await _recovery.ReconcileAndResumeAsync(
            new RunId(detail.Selected.RunId), choice.Execution.ExecutionId, content, isError, note, _eventSink, ct));
    }

    private void UpdateControls()
    {
        if (RunButton is null || LoadConnectorsButton is null || LoadBrowserButton is null || ToolProfileComboBox is null) return;
        var running = _activeRootRunId is not null;
        var graphRunning = _activeGraphId is not null;
        RunButton.IsEnabled = !running && !graphRunning && !_controlBusy;
        LoadConnectorsButton.IsEnabled = _connectors is null && !running && !graphRunning && !_controlBusy;
        EnableConnectorsCheckBox.IsEnabled = _connectors is not null && !running && !graphRunning && !_controlBusy;
        LoadBrowserButton.IsEnabled = _bootstrap.Browser?.BrowserBinding is null && _bootstrap.Browser is not null && !running && !graphRunning && !_controlBusy;
        ToolProfileComboBox.IsEnabled = !running && !graphRunning && !_controlBusy;
        CancelButton.IsEnabled = PauseActiveButton.IsEnabled = running && !_controlBusy;
        var root = _detail?.Root;
        var controllable = root is not null && root.Result is null && root.Control != WorkflowControl.Cancel;
        var sameActiveRoot = !graphRunning && (!running || _activeRootRunId!.Value.Value == root?.RunId);
        PauseWorkflowButton.IsEnabled = CancelWorkflowButton.IsEnabled = controllable && sameActiveRoot && !_controlBusy;
        var editable = controllable && !running && !graphRunning && !_controlBusy;
        ResumeWorkflowButton.IsEnabled = editable && root?.Status is not (WorkflowStatus.Failed or WorkflowStatus.BudgetExhausted);
        var request = (HumanRequestsComboBox.SelectedItem as HumanRequestChoice)?.Request;
        AnswerButton.IsEnabled = editable && request?.Kind == WorkflowHumanRequestKind.Input;
        ApproveButton.IsEnabled = DenyButton.IsEnabled = editable && request?.Kind == WorkflowHumanRequestKind.ToolApproval;
        ReconcileButton.IsEnabled = !running && !graphRunning && !_controlBusy && UnknownToolsComboBox.SelectedItem is ToolChoice;
        UpdateGraphControls();
    }

    private sealed record WorkflowChoice(WorkflowCheckpoint Checkpoint)
    {
        public string Label => $"{Checkpoint.Request.AgentId ?? "main"} / {Checkpoint.Request.SessionKey.Value} / {Checkpoint.Status} / {Checkpoint.RunId[..Math.Min(12, Checkpoint.RunId.Length)]}";
    }
    private sealed record HumanRequestChoice(WorkflowHumanRequest Request)
    {
        public string Label => $"{(Request.Kind == WorkflowHumanRequestKind.Input ? "Question" : "Approval")}: {Request.RequestId}";
    }
    private sealed record ToolChoice(WorkflowToolExecution Execution)
    {
        public string Label => $"{Execution.Call.ToolName}: {Execution.ExecutionId}";
    }

    private void ProviderComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || ModelTextBox is null)
        {
            return;
        }

        var provider = GetSelectedProvider();
        if (!ProviderDefaults.DefaultModels.TryGetValue(provider, out var recommendedModel))
        {
            return;
        }

        var current = ModelTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(current) || ProviderDefaults.DefaultModels.Values.Contains(current, StringComparer.OrdinalIgnoreCase))
        {
            ModelTextBox.Text = recommendedModel;
        }
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

    private void AppendLog(string message)
    {
        AddVisibleEvent($"{DateTimeOffset.Now:HH:mm:ss.fff} [ui] {message}");
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

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_closePending && !_closing)
        {
            e.Cancel = true;
            base.OnClosing(e);
            return;
        }
        if (!_closing && _activeGraphId is { } graphId)
        {
            e.Cancel = true;
            base.OnClosing(e);
            _closePending = true;
            await ControlGraphAsync(graphId, permanentCancel: false);
            _graphCancellationTokenSource?.Cancel();
            if (_graphTask is { } graphTask)
            {
                try { await graphTask; } catch { /* Already reported by graph execution. */ }
            }
            _closing = true;
            Close();
            return;
        }
        if (!_closing && _activeRootRunId is { } active)
        {
            e.Cancel = true;
            base.OnClosing(e);
            _closePending = true;
            await ControlAsync(active, permanentCancel: false);
            _runCancellationTokenSource?.Cancel();
            if (_runTask is { } task)
            {
                try { await task; } catch { /* Already reported by ExecuteRunAsync. */ }
            }
            _closing = true;
            Close();
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _closing = true;
        StopTimeline();
        StopEventDelivery();
        _runCancellationTokenSource?.Cancel();
        _runCancellationTokenSource?.Dispose();
        _runCancellationTokenSource = null;
        _graphCancellationTokenSource?.Cancel();
        _graphCancellationTokenSource?.Dispose();
        _graphCancellationTokenSource = null;
        try { _connectors?.Dispose(); }
        finally { _bootstrap.Dispose(); }
        base.OnClosed(e);
    }

    private void OpenSettings_OnClick(object sender, RoutedEventArgs e) => WorkflowSettingsTab.IsSelected = true;

    private async void LoadConnectors_OnClick(object sender, RoutedEventArgs e)
    {
        if (_connectors is not null || _activeRootRunId is not null || _activeGraphId is not null || _controlBusy) return;
        var picker = new Microsoft.Win32.OpenFileDialog { Title = "Review connector configuration", Filter = "Connector configuration (*.json)|*.json" };
        if (picker.ShowDialog(this) != true) return;
        _controlBusy = true;
        UpdateControls();
        McpHost? host = null;
        try
        {
            host = await Task.Run(() => McpHost.Load(picker.FileName));
            if (_closePending || _closing) { host.Dispose(); return; }
            var review = new Window { Title = "Review connector access", Owner = this, Width = 780, Height = 600, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var panel = new DockPanel { Margin = new Thickness(16) };
            var enable = new Button { Content = "Use this reviewed configuration", Margin = new Thickness(0, 12, 0, 0) };
            DockPanel.SetDock(enable, Dock.Bottom);
            panel.Children.Add(enable);
            panel.Children.Add(new TextBox { Text = host.Review, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            review.Content = panel;
            enable.Click += (_, _) => { review.DialogResult = true; };
            if (review.ShowDialog() != true) { host.Dispose(); return; }
            _bootstrap.ToolRegistrar.RegisterTools(host.Tools);
            _connectors = host;
            EnableConnectorsCheckBox.IsChecked = true;
            ConnectorsStatusTextBlock.Text = $"{host.ToolNames.Count} reviewed connector tools. Every call requires approval. Restart to change this configuration.";
        }
        catch (Exception error) when (error is ArgumentException or System.IO.IOException or UnauthorizedAccessException or JsonException)
        { host?.Dispose(); ConnectorsStatusTextBlock.Text = "Connector configuration could not be enabled: " + error.Message; }
        finally { _controlBusy = false; UpdateControls(); }
    }

    private async void LoadBrowser_OnClick(object sender, RoutedEventArgs e)
    {
        if (_bootstrap.Browser is not { BrowserBinding: null } browser || _activeRootRunId is not null || _activeGraphId is not null || _controlBusy) return;
        var picker = new Microsoft.Win32.OpenFileDialog { Title = "Review JavaScript browser sites", Filter = "Browser configuration (*.json)|*.json" };
        if (picker.ShowDialog(this) != true) return;
        _controlBusy = true; UpdateControls();
        try
        {
            var configuration = await Task.Run(() => BrowserConfiguration.Load(picker.FileName));
            if (_closing || _closePending) return;
            if (MessageBox.Show(this, configuration.Review + "\n\nEnable these browser permissions?", "Review browser sites", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
            browser.Enable(configuration);
            browser.GrantSpecialist(_bootstrap.AgentRegistry);
            BrowserStatusTextBlock.Text = "JavaScript browsing enabled for the reviewed sites. Choose web browsing or all available tools. Restart to change these sites.";
        }
        catch (Exception error) when (error is ArgumentException or System.IO.IOException or JsonException or UnauthorizedAccessException)
        { BrowserStatusTextBlock.Text = "Browser configuration could not be enabled: " + error.Message; }
        finally { _controlBusy = false; UpdateControls(); }
    }
}
