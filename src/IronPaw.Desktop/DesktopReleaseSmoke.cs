using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IronPaw.AgentCore;
using IronPaw.Hosting;

namespace IronPaw.Desktop;

internal static class DesktopReleaseSmoke
{
    public static async Task RunAsync(string directory)
    {
        var root = LocalReleaseSmoke.Prepare(directory);
        var state = Path.Combine(root, "state");
        var workspace = Path.Combine(root, "workspace");
        var bootstrap = await Task.Run(() => DesktopBootstrapper.Build(state, workspace, new IronPawSettings()));
        var window = new MainWindow(bootstrap)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
            ShowInTaskbar = false,
            ShowActivated = false
        };
        try
        {
            window.Show();
            await window.RunReleaseSmokeAsync().WaitAsync(TimeSpan.FromSeconds(30));
            window.UpdateLayout();
            RequireUsableInputAndOutput(window);
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var image = File.Create(Path.Combine(root, "desktop.png"));
            encoder.Save(image);
            image.Dispose();
            ((Expander)window.FindName("AdvancedSettingsExpander")).IsExpanded = true;
            ((TabItem)window.FindName("WorkflowSettingsTab")).IsSelected = true;
            window.UpdateLayout();
            RequireUsableInputAndOutput(window);
            bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var settingsImage = new PngBitmapEncoder();
            settingsImage.Frames.Add(BitmapFrame.Create(bitmap));
            using var settingsFile = File.Create(Path.Combine(root, "desktop-settings.png"));
            settingsImage.Save(settingsFile);
        }
        finally { window.Close(); }
        var report = await Task.Run(async () =>
        {
            using var reopened = DesktopBootstrapper.Build(state, workspace, new IronPawSettings());
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            return await LocalReleaseSmoke.VerifyAsync("Desktop", workspace, reopened.WorkflowStore, reopened.SessionStore, reopened.TranscriptStore, reopened.RunHistoryStore, deadline.Token);
        });
        await LocalReleaseSmoke.WriteReportAsync(root, report);
    }

    private static void RequireUsableInputAndOutput(MainWindow window)
    {
        foreach (var name in new[] { "PromptTextBox", "AssistantOutputTextBox" })
            if (((TextBox)window.FindName(name)).ActualHeight < 32)
                throw new InvalidOperationException("The desktop layout collapsed an input or output area.");
    }
}

public partial class MainWindow
{
    internal Task RunReleaseSmokeAsync()
    {
        PromptTextBox.Text = LocalReleaseSmoke.Prompt;
        RunButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        return _runTask ?? throw new InvalidOperationException("Desktop did not admit the local smoke workflow.");
    }
}
