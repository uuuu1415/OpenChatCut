using System.Windows;

namespace OpenChatCut.Native;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        _ = window.InitializeAsync();
    }
}
