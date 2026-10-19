using System.Configuration;
using System.IO;
using System.Windows;

namespace IronPaw.Desktop;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.FirstOrDefault() == "--smoke-test")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                if (e.Args.Length != 2) throw new ArgumentException("--smoke-test requires only a new evidence directory.");
                await DesktopReleaseSmoke.RunAsync(e.Args[1]);
                Shutdown(0);
            }
            catch (Exception error) { Console.Error.WriteLine("Release smoke failed: " + error); Shutdown(1); }
            return;
        }
        try
        {
            string workspace;
            if (e.Args.Length == 2 && e.Args[0] == "--workspace")
            {
                if (!Path.IsPathFullyQualified(e.Args[1]) || !Directory.Exists(e.Args[1])) throw new ArgumentException("--workspace requires an existing absolute directory.");
                workspace = Path.GetFullPath(e.Args[1]);
            }
            else
            {
                if (e.Args.Length != 0) throw new ArgumentException("Use --workspace <absolute directory>, or start without arguments to choose a working folder.");
                var picker = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the folder IronPaw can work in", Multiselect = false };
                if (picker.ShowDialog() != true) { Shutdown(0); return; }
                workspace = picker.FolderName;
            }
            var bootstrap = await Task.Run(() => DesktopBootstrapper.Build(workspaceRoot: workspace));
            new MainWindow(bootstrap).Show();
        }
        catch (Exception error)
        {
            var guidance = error is ArgumentException or System.Text.Json.JsonException or FileNotFoundException
                ? "Check the shared IronPaw settings file and IRONPAW_CONFIG, then try again.\n\n"
                : error is IOException
                ? "Close any other IronPaw CLI or Desktop host using this workspace, then try again.\n\n"
                : "The workspace's saved workflow state could not be opened.\n\n";
            MessageBox.Show(guidance + error.Message, "IronPaw could not start", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}

