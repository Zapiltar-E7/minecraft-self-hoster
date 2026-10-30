using System.Windows;

namespace MinecraftHost;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            // shown inside the main window like every other message; a system box only if the window isn't up
            if (MainWindow is MinecraftHost.MainWindow { IsLoaded: true } main) main.ShowError(args.Exception.Message);
            else MessageBox.Show(args.Exception.Message, "Minecraft Self-Hoster", MessageBoxButton.OK, MessageBoxImage.Error);
        };
        base.OnStartup(e);
    }
}
