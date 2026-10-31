using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;

namespace MinecraftHost;

public partial class App : Application
{
    // One copy of the app per folder: two would both manage the same server (and lock each other's files).
    // A second copy started from the same folder asks the first to show itself (even from the tray), then quits.
    static Mutex? _single;
    static EventWaitHandle? _showSignal;

    protected override void OnStartup(StartupEventArgs e)
    {
        var folder = Path.GetDirectoryName(Environment.ProcessPath ?? AppContext.BaseDirectory)!.TrimEnd('\\').ToLowerInvariant();
        var key = "MinecraftSelfHoster-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(folder)))[..16];
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, key + "-show");
        _single = new Mutex(true, key, out var first);
        if (!first)
        {
            _showSignal.Set();
            Shutdown();
            return;
        }
        var listener = new Thread(() =>
        {
            while (_showSignal.WaitOne())
                Dispatcher.BeginInvoke(() => (MainWindow as MinecraftHost.MainWindow)?.BringToFront());
        }) { IsBackground = true, Name = "show-when-opened-again" };
        listener.Start();

        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            // shown inside the main window like every other message; a system box only if the window isn't up
            if (MainWindow is MinecraftHost.MainWindow { IsLoaded: true } main) main.ShowError(args.Exception.Message);
            else MessageBox.Show(args.Exception.Message, "Minecraft Self-Hoster", MessageBoxButton.OK, MessageBoxImage.Error);
        };
        base.OnStartup(e);
    }

    /// <summary>Lets a new copy start while this one is still closing (Restart after an update). Call on the UI thread.</summary>
    public static void ReleaseSingleInstance()
    {
        try { _single?.ReleaseMutex(); } catch { }
        _single?.Dispose();
        _single = null;
    }
}
