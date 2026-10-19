using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using IronPaw.AgentCore;

namespace IronPaw.Desktop;

public partial class MainWindow
{
    private ExecutionTimelineController? _timeline;
    private DispatcherTimer? _timelineTimer;
    private readonly TimelineReadCoordinator _timelineReads = new();
    private readonly CancellationTokenSource _timelineLifetime = new();
    private IReadOnlyList<ExecutionTimelineChoice> _timelineChoices = [];
    private bool _timelineListing;
    private bool _changingTimelineSelection;
    private bool _timelineClosed;

    private void InitializeTimeline()
    {
        _timeline = new(_recovery, _graphs);
        _timelineTimer = new(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(2) };
        _timelineTimer.Tick += TimelineRefreshTick;
        Loaded += async (_, _) => {
            if (ExecutionTimelineTab.IsSelected) await RefreshTimelineAsync();
            UpdateTimelineTimer();
        };
    }

    private async void TimelineTab_OnChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, ActivityTabControl) || _timeline is null) return;
        if (ExecutionTimelineTab.IsSelected) await RefreshTimelineAsync();
        else _timelineReads.Invalidate();
        UpdateTimelineTimer();
    }

    private void TimelineLive_OnChanged(object sender, RoutedEventArgs e) => UpdateTimelineTimer();
    private void UpdateTimelineTimer()
    {
        if (_timelineTimer is null) return;
        if (!_timelineClosed && IsLoaded && ExecutionTimelineTab.IsSelected && TimelineLiveCheckBox.IsChecked == true)
            _timelineTimer.Start();
        else _timelineTimer.Stop();
    }
    private async void TimelineRefreshTick(object? sender, EventArgs e)
    {
        if (_timelineClosed || !ExecutionTimelineTab.IsSelected || _timelineReads.IsReading || _timelineListing) return;
        await RefreshTimelineAsync();
    }
    private async void RefreshTimeline_OnClick(object sender, RoutedEventArgs e) => await RefreshTimelineAsync();

    private async Task RefreshTimelineAsync()
    {
        if (_timeline is null || _timelineClosed || _timelineListing) return;
        _timelineListing = true;
        try
        {
            var token = _timelineLifetime.Token;
            var choices = await Task.Run(() => _timeline.ListAsync(token), token);
            if (_timelineClosed || !ExecutionTimelineTab.IsSelected) return;
            var previous = TimelineRunsComboBox.SelectedItem as ExecutionTimelineChoice;
            var selected = choices.FirstOrDefault(choice => SameTimelineChoice(choice, previous))
                ?? choices.FirstOrDefault(choice => choice.Id == (_activeGraphId ?? _activeRootRunId?.Value))
                ?? choices.FirstOrDefault();
            if (selected is not null && !SameTimelineChoice(selected, previous)) ClearTimeline();
            if (!_timelineChoices.SequenceEqual(choices))
            {
                _changingTimelineSelection = true;
                try { _timelineChoices = choices; TimelineRunsComboBox.ItemsSource = choices; TimelineRunsComboBox.SelectedItem = selected; }
                finally { _changingTimelineSelection = false; }
            }
            if (selected is null) ClearTimeline();
            else await LoadTimelineAsync(selected);
        }
        catch (OperationCanceledException) when (_timelineLifetime.IsCancellationRequested) { }
        catch (Exception failure)
        {
            if (!_timelineClosed) TimelineStatusTextBlock.Text = "Could not refresh saved executions: " + ExecutionTimelineBuilder.Clean(failure.Message, 400);
        }
        finally { _timelineListing = false; }
    }

    private async void TimelineRun_OnChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_changingTimelineSelection || _timelineClosed || _timeline is null) return;
        if (TimelineRunsComboBox.SelectedItem is ExecutionTimelineChoice choice)
        {
            // A different execution must not retain the previous execution's output or success badge.
            ClearTimeline();
            await LoadTimelineAsync(choice);
        }
        else ClearTimeline();
    }

    private Task LoadTimelineAsync(ExecutionTimelineChoice choice)
    {
        if (_timeline is null || _timelineClosed) return Task.CompletedTask;
        TimelineStatusTextBlock.Text = "Reading saved execution…";
        return _timelineReads.ReadAsync(ct => Task.Run(() => _timeline.InspectAsync(choice, ct), ct), snapshot => {
            if (!SameTimelineChoice(choice, TimelineRunsComboBox.SelectedItem as ExecutionTimelineChoice)) return;
            var selectedTask = (TimelineTasksGrid.SelectedItem as ExecutionTimelineTask)?.Id;
            TimelineTitleTextBlock.Text = snapshot.Title;
            TimelineStateTextBlock.Text = snapshot.Status;
            TimelineSummaryTextBlock.Text = snapshot.Summary;
            TimelineProgressBar.Maximum = Math.Max(1, snapshot.TotalTasks);
            TimelineProgressBar.Value = snapshot.AcceptedCount;
            TimelineProgressBar.Visibility = choice.Kind == ExecutionTimelineKind.TaskGraph ? Visibility.Visible : Visibility.Collapsed;
            if (!(TimelineTasksGrid.ItemsSource?.Cast<ExecutionTimelineTask>().SequenceEqual(snapshot.Tasks) ?? false))
            {
                TimelineTasksGrid.ItemsSource = snapshot.Tasks;
                TimelineTasksGrid.SelectedItem = snapshot.Tasks.FirstOrDefault(task => task.Id == selectedTask) ?? snapshot.Tasks.FirstOrDefault();
            }
            if (!(TimelineEventsListBox.ItemsSource?.Cast<ExecutionTimelineEntry>().SequenceEqual(snapshot.Events) ?? false))
                TimelineEventsListBox.ItemsSource = snapshot.Events;
            TimelineEventsTextBlock.Text = snapshot.HiddenEvents == 0 ? $"Saved activity ({snapshot.TotalEvents})"
                : $"Saved activity (latest {snapshot.Events.Length} of {snapshot.TotalEvents})";
            TimelineStatusTextBlock.Text = $"Read at {snapshot.ObservedAt.LocalDateTime:T}. Revision order is preserved within each execution; parallel activity may interleave.";
            TimelineOpenControlsButton.IsEnabled = true;
            ShowTimelineTask();
        }, failure => {
            TimelineStatusTextBlock.Text = "Could not refresh. The last saved view may be out of date. " + ExecutionTimelineBuilder.Clean(failure.Message, 400);
        });
    }

    private void TimelineTask_OnChanged(object sender, SelectionChangedEventArgs e) => ShowTimelineTask();
    private void ShowTimelineTask()
    {
        if (TimelineTaskDetailsTextBox is null) return;
        TimelineTaskDetailsTextBox.Text = (TimelineTasksGrid.SelectedItem as ExecutionTimelineTask)?.Details ?? "Select a task to inspect its saved evidence.";
    }

    private void ClearTimeline()
    {
        _timelineReads.Invalidate();
        TimelineTitleTextBlock.Text = "Choose a saved execution";
        TimelineStateTextBlock.Text = "No execution selected";
        TimelineSummaryTextBlock.Text = "The timeline shows saved progress and evidence. It does not start, resume or retry work.";
        TimelineTasksGrid.ItemsSource = null;
        TimelineEventsListBox.ItemsSource = null;
        TimelineTaskDetailsTextBox.Clear();
        TimelineEventsTextBlock.Text = "Saved activity";
        TimelineProgressBar.Value = 0;
        TimelineProgressBar.Visibility = Visibility.Collapsed;
        TimelineStatusTextBlock.Text = "No saved execution is selected.";
        TimelineOpenControlsButton.IsEnabled = false;
    }

    private async void TimelineOpenControls_OnClick(object sender, RoutedEventArgs e)
    {
        if (TimelineRunsComboBox.SelectedItem is not ExecutionTimelineChoice choice || _timelineClosed) return;
        if (choice.Kind == ExecutionTimelineKind.TaskGraph)
        {
            GraphLocalVerificationCheckBox.IsChecked = false;
            ActivityTabControl.SelectedItem = TaskGraphsTab;
            await RefreshGraphsAsync(choice.Id);
        }
        else
        {
            ActivityTabControl.SelectedIndex = 1;
            await RefreshWorkflowsAsync(choice.Id);
        }
    }

    private static bool SameTimelineChoice(ExecutionTimelineChoice left, ExecutionTimelineChoice? right) =>
        right is not null && left.Kind == right.Kind && left.Id == right.Id;

    private void StopTimeline()
    {
        if (_timelineClosed) return;
        _timelineClosed = true;
        _timelineTimer?.Stop();
        _timelineReads.Dispose();
        _timelineLifetime.Cancel();
        _timelineLifetime.Dispose();
    }
}
