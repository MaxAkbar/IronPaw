using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using IronPaw.AgentCore;
using IronPaw.AgentCore.Abstractions;
using Microsoft.Win32;

namespace IronPaw.Desktop;

public partial class MainWindow
{
    private readonly CallerIdentity _graphCaller = CallerIdentity.LocalOwner();
    private TaskGraphHostController? _graphs;
    private TaskGraphLaunchDocument? _graphManifest;
    private TaskGraphHostDetail? _graphDetail;
    private string? _activeGraphId;
    private CancellationTokenSource? _graphCancellationTokenSource;
    private Task? _graphTask;
    private bool _changingGraphSelection;
    private int _graphDetailVersion;

    private void InitializeGraphHost()
    {
        _graphs = _bootstrap.Graphs;
        if (_graphs is null) TaskGraphsTab.IsEnabled = false;
        else GraphManifestTextBlock.Text = $"Workspace: {_graphs.WorkspaceRoot}\nManaged graph workspaces: {_graphs.WorkspaceStoreRoot}\nImport a launch manifest to review its exact tasks, shared limits and verification commands.";
    }

    private async void ImportGraph_OnClick(object sender, RoutedEventArgs e)
    {
        var path = SelectJsonFile("Import task graph launch manifest");
        if (path is null) return;
        try
        {
            var document = await TaskGraphDocumentJson.ReadAsync<TaskGraphLaunchDocument>(path, CancellationToken.None);
            _graphManifest = document;
            GraphManifestTextBox.Text = TaskGraphDocumentJson.Serialize(document);
            GraphManifestTextBlock.Text = $"{document.Graph.GraphId}: {document.Graph.Objective}\nTasks: {document.Graph.Tasks.Length}; maximum concurrency: {document.MaxConcurrency}\nProvider/model: {document.Options.Provider}/{document.Options.Model}\nSource: {document.Options.SourceWorkspaceRoot}\nManaged workspaces: {document.Options.WorkspaceStoreRoot}\nVerification profiles: {document.Options.VerificationProfiles.Length}";
            GraphLocalVerificationCheckBox.IsChecked = false;
            GraphStatusTextBlock.Text = "Manifest imported. Review its definition and command profiles before starting.";
        }
        catch (Exception error)
        {
            _graphManifest = null;
            GraphManifestTextBox.Clear();
            GraphStatusTextBlock.Text = error.Message;
        }
        UpdateControls();
    }

    private static string? SelectJsonFile(string title)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = "JSON documents (*.json)|*.json|All files (*.*)|*.*", CheckFileExists = true, Multiselect = false };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private async void StartGraph_OnClick(object sender, RoutedEventArgs e)
    {
        if (_graphs is null || _graphManifest is not { } document) return;
        var allow = GraphLocalVerificationCheckBox.IsChecked == true;
        await ExecuteGraphAsync(document.Graph.GraphId, ct => DesktopGraphCommands.StartAsync(_graphs, document, _graphCaller, allow, ct));
    }

    private async void RefreshGraphs_OnClick(object sender, RoutedEventArgs e) => await RefreshGraphsAsync();

    private async Task RefreshGraphsAsync(string? graphId = null)
    {
        if (_graphs is null) return;
        graphId ??= (GraphsComboBox.SelectedItem as GraphChoice)?.Checkpoint.Graph.GraphId;
        try
        {
            var graphs = await _graphs.ListAsync(_graphCaller, CancellationToken.None);
            _changingGraphSelection = true;
            GraphsComboBox.ItemsSource = graphs.OrderByDescending(graph => graph.UpdatedAt).Select(graph => new GraphChoice(graph)).ToArray();
            GraphsComboBox.SelectedItem = GraphsComboBox.Items.Cast<GraphChoice>().FirstOrDefault(choice => choice.Checkpoint.Graph.GraphId == graphId)
                ?? GraphsComboBox.Items.Cast<GraphChoice>().FirstOrDefault();
            _changingGraphSelection = false;
            await LoadGraphAsync();
        }
        catch (Exception error) { _changingGraphSelection = false; GraphStatusTextBlock.Text = error.Message; }
    }

    private async void GraphSelection_OnChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_changingGraphSelection || _graphs is null) return;
        GraphLocalVerificationCheckBox.IsChecked = false;
        await LoadGraphAsync();
    }

    private async Task LoadGraphAsync()
    {
        var version = ++_graphDetailVersion;
        if (_graphs is null || GraphsComboBox.SelectedItem is not GraphChoice choice)
        {
            _graphDetail = null;
            GraphSummaryTextBlock.Text = "No saved task graphs in this workspace.";
            GraphTasksComboBox.ItemsSource = null;
            GraphDetailTextBox.Clear();
            UpdateControls();
            return;
        }
        _graphDetail = null;
        GraphSummaryTextBlock.Text = "Loading durable graph state…";
        UpdateControls();
        try
        {
            var detail = await _graphs.InspectAsync(choice.Checkpoint.Graph.GraphId, _graphCaller, CancellationToken.None);
            if (version != _graphDetailVersion) return;
            _graphDetail = detail;
            var graph = detail.Checkpoint;
            GraphSummaryTextBlock.Text = $"Graph: {graph.Graph.GraphId}\nState: {detail.Progress.Status}; control: {graph.Control}; fingerprint: {graph.DefinitionFingerprint}\nReady: {string.Join(", ", detail.Progress.ReadyTaskIds)}; active claims: {detail.Progress.ActiveClaims}\n" +
                (detail.Budget is { } budget ? $"Tokens charged: {budget.ChargedTokens}; reserved: {budget.ReservedTokens}; remaining: {budget.RemainingTokens?.ToString() ?? "unlimited"}\nExecution: {budget.ExecutionTime.TotalSeconds:F1}s; remaining: {(budget.RemainingExecutionTime is { } remaining ? $"{remaining.TotalSeconds:F1}s" : "unlimited")}" : "No bound workflow budget.");
            var previousTask = (GraphTasksComboBox.SelectedItem as GraphTaskChoice)?.State.TaskId;
            _changingGraphSelection = true;
            GraphTasksComboBox.ItemsSource = graph.Tasks.Select(task => new GraphTaskChoice(graph.Graph.Tasks.Single(definition => definition.TaskId == task.TaskId), task, graph.DefinitionFingerprint)).ToArray();
            GraphTasksComboBox.SelectedItem = GraphTasksComboBox.Items.Cast<GraphTaskChoice>().FirstOrDefault(task => task.State.TaskId == previousTask)
                ?? GraphTasksComboBox.Items.Cast<GraphTaskChoice>().FirstOrDefault();
            _changingGraphSelection = false;
            LoadGraphTask();
        }
        catch (Exception error) { _changingGraphSelection = false; GraphStatusTextBlock.Text = error.Message; }
        UpdateControls();
    }

    private void GraphTaskSelection_OnChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_changingGraphSelection) LoadGraphTask();
    }

    private void LoadGraphTask()
    {
        if (GraphDetailTextBox is null) return;
        var task = GraphTasksComboBox.SelectedItem as GraphTaskChoice;
        GraphDetailTextBox.Text = _graphDetail is { } detail ? FormatGraphDetail(detail, task) : "";
        GraphUnknownCommandsComboBox.ItemsSource = task?.State.Verifications.Where(command => command.State == TaskVerificationInvocationState.NeedsReconciliation)
            .Select(command => new GraphCommandChoice(command)).ToArray();
        GraphUnknownCommandsComboBox.SelectedIndex = GraphUnknownCommandsComboBox.Items.Count > 0 ? 0 : -1;
        var child = _graphDetail?.Invocations.FirstOrDefault(run => run.RunId == task?.ChildRunId);
        GraphUnknownToolsComboBox.ItemsSource = child?.ToolExecutions?.Where(tool => tool.State == WorkflowToolState.Started && tool.Result is null)
            .Select(tool => new GraphToolChoice(tool)).ToArray();
        GraphUnknownToolsComboBox.SelectedIndex = GraphUnknownToolsComboBox.Items.Count > 0 ? 0 : -1;
        GraphReconcileResultTextBox.Clear();
        GraphReconcileNoteTextBox.Clear();
        UpdateControls();
    }

    private static string FormatGraphDetail(TaskGraphHostDetail detail, GraphTaskChoice? selected)
    {
        var text = new StringBuilder();
        text.AppendLine("Accepted graph definition and host binding:").AppendLine(TaskGraphDocumentJson.Serialize(new { detail.Checkpoint.Graph, detail.Checkpoint.AgentBinding }));
        text.AppendLine().AppendLine("Task states:");
        foreach (var task in detail.Checkpoint.Graph.Tasks)
        {
            var state = detail.Checkpoint.Tasks.Single(item => item.TaskId == task.TaskId);
            text.AppendLine(new GraphTaskChoice(task, state, detail.Checkpoint.DefinitionFingerprint).Label);
        }
        if (selected is not null)
        {
            text.AppendLine().AppendLine($"Selected task: {selected.State.TaskId}; child invocation: {selected.ChildRunId ?? "not created"}")
                .AppendLine(TaskGraphDocumentJson.Serialize(selected.State));
            foreach (var child in detail.Invocations.Where(run => run.RunId == selected.ChildRunId))
                text.AppendLine("Saved child invocation (including tool receipts):").AppendLine(TaskGraphDocumentJson.Serialize(child));
        }
        text.AppendLine().AppendLine("Shared usage and budget:").AppendLine(TaskGraphDocumentJson.Serialize(new { detail.Usage, detail.Budget }));
        text.AppendLine().AppendLine("Graph journal:");
        foreach (var entry in detail.Journal)
            text.AppendLine($"{entry.Timestamp:O} revision={entry.Revision} {entry.EventType} task={entry.TaskId ?? "none"} attempt={entry.AttemptId ?? "none"}");
        return text.ToString();
    }

    private async void ResumeGraph_OnClick(object sender, RoutedEventArgs e)
    {
        if (_graphs is null || _graphDetail is not { } detail) return;
        var allow = GraphLocalVerificationCheckBox.IsChecked == true;
        await ExecuteGraphAsync(detail.Checkpoint.Graph.GraphId, ct => DesktopGraphCommands.ResumeAsync(_graphs, detail.Checkpoint.Graph.GraphId, _graphCaller, allow, ct));
    }

    private async void RecoverGraphTask_OnClick(object sender, RoutedEventArgs e)
    {
        if (_graphs is null || _graphDetail is not { } detail || GraphTasksComboBox.SelectedItem is not GraphTaskChoice task) return;
        var allow = GraphLocalVerificationCheckBox.IsChecked == true;
        await ExecuteGraphAsync(detail.Checkpoint.Graph.GraphId, ct => DesktopGraphCommands.RecoverTaskAsync(_graphs, detail.Checkpoint.Graph.GraphId, task.State.TaskId, _graphCaller, allow, ct));
    }

    private async void PauseGraph_OnClick(object sender, RoutedEventArgs e) => await ControlSelectedGraphAsync(false);
    private async void CancelGraph_OnClick(object sender, RoutedEventArgs e) => await ControlSelectedGraphAsync(true);
    private Task ControlSelectedGraphAsync(bool permanentCancel) => _graphDetail is { } detail
        ? ControlGraphAsync(detail.Checkpoint.Graph.GraphId, permanentCancel) : Task.CompletedTask;

    private async Task ControlGraphAsync(string graphId, bool permanentCancel)
    {
        if (_graphs is null || _controlBusy) return;
        if (_activeRootRunId is not null || (_activeGraphId is { } active && active != graphId))
        {
            GraphStatusTextBlock.Text = "Control the active execution, or wait until it settles before controlling another graph.";
            return;
        }
        _controlBusy = true;
        UpdateControls();
        try
        {
            if (permanentCancel) await _graphs.CancelAsync(graphId, _graphCaller, CancellationToken.None);
            else await _graphs.PauseAsync(graphId, _graphCaller, CancellationToken.None);
            if (_activeGraphId == graphId) _graphCancellationTokenSource?.Cancel();
            GraphStatusTextBlock.Text = permanentCancel ? "Permanent graph cancellation recorded." : "Graph pause recorded; active claims are settling.";
            await RefreshGraphsAsync(graphId);
        }
        catch (Exception error) { GraphStatusTextBlock.Text = error.Message; }
        finally { _controlBusy = false; UpdateControls(); }
    }

    private Task ExecuteGraphAsync(string graphId, Func<CancellationToken, Task<TaskGraphCheckpoint>> action)
    {
        if (_activeGraphId is not null || _activeRootRunId is not null || _controlBusy) return Task.CompletedTask;
        _activeGraphId = graphId;
        _graphCancellationTokenSource = new();
        UpdateControls();
        return _graphTask = ExecuteGraphCoreAsync(graphId, action);
    }

    private async Task ExecuteGraphCoreAsync(string graphId, Func<CancellationToken, Task<TaskGraphCheckpoint>> action)
    {
        try
        {
            var checkpoint = await action(_graphCancellationTokenSource!.Token);
            GraphStatusTextBlock.Text = $"Graph {graphId}: {TaskGraphProgress.Inspect(checkpoint).Status}.";
            AppendLog(GraphStatusTextBlock.Text);
        }
        catch (Exception error) { GraphStatusTextBlock.Text = error.Message; AppendLog($"Graph action failed: {error.Message}"); }
        finally
        {
            _activeGraphId = null;
            _graphTask = null;
            _graphCancellationTokenSource!.Dispose();
            _graphCancellationTokenSource = null;
            GraphLocalVerificationCheckBox.IsChecked = false;
            UpdateControls();
            if (!_closePending) { await RefreshGraphsAsync(graphId); await RefreshWorkflowsAsync(); }
        }
    }

    private void GraphVerification_OnChanged(object sender, RoutedEventArgs e) => UpdateControls();
    private void GraphReconciliationKind_OnChanged(object sender, SelectionChangedEventArgs e) => UpdateControls();

    private async void ImportGraphResult_OnClick(object sender, RoutedEventArgs e)
    {
        var path = SelectJsonFile("Import independently verified outcome");
        if (path is null) return;
        try
        {
            GraphReconcileResultTextBox.Text = GraphReconciliationKindComboBox.SelectedIndex switch
            {
                1 => TaskGraphDocumentJson.Serialize(await TaskGraphDocumentJson.ReadAsync<TaskVerificationCompletion>(path, CancellationToken.None)),
                2 => TaskGraphDocumentJson.Serialize(await TaskGraphDocumentJson.ReadAsync<ToolResult>(path, CancellationToken.None)),
                _ => TaskGraphDocumentJson.Serialize(await TaskGraphDocumentJson.ReadAsync<WorkflowTaskResult>(path, CancellationToken.None))
            };
        }
        catch (Exception error) { GraphStatusTextBlock.Text = error.Message; }
    }

    private async void ReconcileGraph_OnClick(object sender, RoutedEventArgs e)
    {
        if (_graphs is null || _graphDetail is not { } detail || GraphTasksComboBox.SelectedItem is not GraphTaskChoice task
            || _activeGraphId is not null || _activeRootRunId is not null || _controlBusy) return;
        _controlBusy = true;
        UpdateControls();
        try
        {
            var graphId = detail.Checkpoint.Graph.GraphId;
            var note = GraphReconcileNoteTextBox.Text;
            var json = GraphReconcileResultTextBox.Text;
            if (GraphReconciliationKindComboBox.SelectedIndex == 1)
            {
                if (GraphUnknownCommandsComboBox.SelectedItem is not GraphCommandChoice command) throw new ArgumentException("Select a verification command whose outcome is unknown.");
                await _graphs.ReconcileVerificationAsync(graphId, task.State.TaskId, command.Invocation.InvocationId,
                    TaskGraphDocumentJson.Parse<TaskVerificationCompletion>(json), note, _graphCaller, CancellationToken.None);
            }
            else if (GraphReconciliationKindComboBox.SelectedIndex == 2)
            {
                if (GraphUnknownToolsComboBox.SelectedItem is not GraphToolChoice tool) throw new ArgumentException("Select a child tool whose outcome is unknown.");
                await _graphs.ReconcileChildToolAsync(graphId, task.State.TaskId, tool.Execution.ExecutionId,
                    TaskGraphDocumentJson.Parse<ToolResult>(json), note, _graphCaller, CancellationToken.None);
            }
            else await _graphs.ReconcileTaskAsync(graphId, task.State.TaskId, TaskGraphDocumentJson.Parse<WorkflowTaskResult>(json), note, _graphCaller, CancellationToken.None);
            GraphStatusTextBlock.Text = "Established outcome recorded. Resume or recover remains a separate action.";
            await RefreshGraphsAsync(graphId);
        }
        catch (Exception error) { GraphStatusTextBlock.Text = error.Message; }
        finally { _controlBusy = false; UpdateControls(); }
    }

    private void UpdateGraphControls()
    {
        if (!IsInitialized || ReconcileGraphButton is null) return;
        var available = _graphs is not null;
        var idle = _activeGraphId is null && _activeRootRunId is null && !_controlBusy;
        var graph = _graphDetail?.Checkpoint;
        var ownsGraph = graph is not null && graph.Caller.Id == _graphCaller.Id && graph.Caller.IsOwner == _graphCaller.IsOwner;
        var task = (GraphTasksComboBox.SelectedItem as GraphTaskChoice)?.State;
        var controllable = available && ownsGraph && graph!.Control != WorkflowControl.Cancel
            && _graphDetail!.Progress.Status != TaskGraphRunStatus.Succeeded;
        var localAllowed = GraphLocalVerificationCheckBox.IsChecked == true;
        var startNeedsLocal = _graphManifest?.Options.VerificationProfiles.IsEmpty == false;
        var resumeNeedsLocal = graph?.AgentBinding?.VerificationProfiles.IsEmpty == false;
        ImportGraphButton.IsEnabled = available && idle;
        StartGraphButton.IsEnabled = available && idle && _graphManifest is not null && (!startNeedsLocal || localAllowed);
        ResumeGraphButton.IsEnabled = controllable && idle;
        RecoverGraphTaskButton.IsEnabled = controllable && idle && task?.Status == TaskGraphTaskStatus.NeedsReconciliation;
        PauseGraphButton.IsEnabled = CancelGraphButton.IsEnabled = controllable && !_controlBusy && _activeRootRunId is null
            && (_activeGraphId is null || _activeGraphId == graph!.Graph.GraphId);
        GraphLocalVerificationCheckBox.IsEnabled = available && idle && (startNeedsLocal || resumeNeedsLocal);
        var command = GraphReconciliationKindComboBox.SelectedIndex == 1;
        var tool = GraphReconciliationKindComboBox.SelectedIndex == 2;
        GraphUnknownCommandsComboBox.Visibility = command ? Visibility.Visible : Visibility.Collapsed;
        GraphUnknownToolsComboBox.Visibility = tool ? Visibility.Visible : Visibility.Collapsed;
        ReconcileGraphButton.IsEnabled = available && ownsGraph && idle && task is not null && (command
            ? GraphUnknownCommandsComboBox.SelectedItem is GraphCommandChoice
            : tool ? GraphUnknownToolsComboBox.SelectedItem is GraphToolChoice : task.Status == TaskGraphTaskStatus.NeedsReconciliation);
    }

    private sealed record GraphChoice(TaskGraphCheckpoint Checkpoint)
    {
        public string Label => $"{Checkpoint.Graph.GraphId} / {TaskGraphProgress.Inspect(Checkpoint).Status} / {Checkpoint.UpdatedAt.LocalDateTime:g}";
    }
    private sealed record GraphTaskChoice(WorkflowTaskDefinition Definition, TaskGraphTaskState State, string GraphFingerprint)
    {
        public string? ChildRunId => State.AttemptId is { } attempt ? AgentTaskIdentity.ChildRunId(GraphFingerprint, State.TaskId, attempt).Value : null;
        public string Label => $"{State.TaskId} / depends: {(Definition.Dependencies.IsEmpty ? "none" : string.Join(", ", Definition.Dependencies))} / {State.Status} / attempt: {State.AttemptId ?? "none"}";
    }
    private sealed record GraphCommandChoice(TaskVerificationInvocation Invocation)
    {
        public string Label => $"{Invocation.ProfileId}: {Invocation.InvocationId} / {Invocation.State}";
    }
    private sealed record GraphToolChoice(WorkflowToolExecution Execution)
    {
        public string Label => $"{Execution.Call.ToolName}: {Execution.ExecutionId}";
    }
}
