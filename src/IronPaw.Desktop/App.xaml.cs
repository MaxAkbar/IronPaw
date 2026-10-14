using System.Configuration;
using System.IO;
using System.Windows;

namespace IronPaw.Desktop;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try { new MainWindow().Show(); }
        catch (Exception error)
        {
            var guidance = error is IOException
                ? "Close any other IronPaw CLI or Desktop host using this workspace, then try again.\n\n"
                : "The workspace's saved workflow state could not be opened.\n\n";
            MessageBox.Show(guidance + error.Message, "IronPaw could not start", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}

