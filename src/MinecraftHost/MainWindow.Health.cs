using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using MinecraftHost.Core;

namespace MinecraftHost;

/// <summary>
/// "What's using TPS?" on the Logs tab: a 60-second spark profile (spark comes with Paper; other servers need the
/// spark plugin or mod), opened in the browser when spark uploads it. And the low disk space warning: a banner on
/// the Server tab when the server's drive gets low, and automatic backups skip themselves rather than fill the drive.
/// </summary>
public partial class MainWindow
{
    static readonly Regex SparkUrlRx = new(@"https://spark\.lucko\.me/[A-Za-z0-9]+", RegexOptions.Compiled);
    Button? _profileButton;
    DateTime? _profileUntil;

    /// <summary>The small button in the TPS tile's header.</summary>
    FrameworkElement ProfileButton()
    {
        _profileButton = new Button
        {
            Content = "What's using it?", Padding = new Thickness(8, 2, 8, 3), FontSize = 11.5, FontWeight = FontWeights.Normal,
            ToolTip = "Records what the server spends its time on for 60 seconds with spark, then opens the report in your browser. " +
                      "The report is uploaded to spark.lucko.me so it can be viewed.",
        };
        _profileButton.SetResourceReference(Control.BackgroundProperty, "Field");
        _profileButton.Click += async (_, _) => await ProfileAsync();
        DockPanel.SetDock(_profileButton, Dock.Right);
        UpdateProfileButton();
        return _profileButton;
    }

    void UpdateProfileButton()
    {
        if (_profileButton == null) return;
        _profileButton.IsEnabled = _profileUntil == null && _server?.IsRunning == true && _state == ServerState.Running;
        _profileButton.Content = _profileUntil is { } until ? $"Recording... {Math.Max(0, (int)Math.Ceiling((until - DateTime.Now).TotalSeconds))} s" : "What's using it?";
    }

    async Task ProfileAsync()
    {
        var server = _server;
        if (server?.IsRunning != true || _profileUntil != null) return;
        var url = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Watch(string line)
        {
            var m = SparkUrlRx.Match(line);
            if (m.Success) url.TrySetResult(m.Value);
            // Paper's background profiler is already running: stopping it uploads what it has, which is just as useful
            else if (line.Contains("already running", StringComparison.OrdinalIgnoreCase) && line.Contains("profiler", StringComparison.OrdinalIgnoreCase))
                server.Send("spark profiler stop");
            else if (line.Contains("Unknown command", StringComparison.OrdinalIgnoreCase) && line.Contains("spark", StringComparison.OrdinalIgnoreCase))
                url.TrySetResult(null);
        }
        server.Line += Watch;
        _profileUntil = DateTime.Now.AddSeconds(60);
        Log.Host("Recording what the server spends its time on for 60 seconds (spark)...");
        try
        {
            server.Send("spark profiler start --timeout 60");
            var ticker = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            ticker.Tick += (_, _) => UpdateProfileButton();
            ticker.Start();
            var done = await Task.WhenAny(url.Task, Task.Delay(TimeSpan.FromSeconds(100)));
            ticker.Stop();
            var link = done == url.Task ? url.Task.Result : null;
            if (link != null)
            {
                Log.Host("spark report: " + link + "  (the slowest parts are at the top; plugins are named in the tree)");
                Playit.OpenUrl(link);
            }
            else Log.Host("spark didn't send a report. It comes with Paper; on other server types install the spark plugin or mod first.");
        }
        finally
        {
            server.Line -= Watch;
            _profileUntil = null;
            UpdateProfileButton();
        }
    }

    // ------------------------------------------------------------ low disk space

    const long LowDiskBytes = 10L << 30;   // 10 GB
    DateTime _diskChecked;
    bool _diskWarned;

    /// <summary>Free space on the server's drive, or -1 when Windows won't say.</summary>
    static long FreeSpace()
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(Paths.ServerDir))!);
            return drive.IsReady ? drive.AvailableFreeSpace : -1;
        }
        catch { return -1; }
    }

    /// <summary>Shows or hides the banner. Runs at startup, every 10 minutes and after backups.</summary>
    void CheckDiskSpace(bool force = false)
    {
        if (!force && DateTime.Now - _diskChecked < TimeSpan.FromMinutes(10)) return;
        _diskChecked = DateTime.Now;
        var free = FreeSpace();
        var low = free >= 0 && free < LowDiskBytes;
        DiskBanner.Visibility = low ? Visibility.Visible : Visibility.Collapsed;
        if (!low) { _diskWarned = false; return; }
        var drive = Path.GetPathRoot(Path.GetFullPath(Paths.ServerDir));
        var backups = ServerFiles.AutoBackups().Sum(f => f.Length);
        DiskBannerText.Text = $"Drive {drive} has {DiskUsage.Format(free)} free. When it fills up the server can't save the world, which can corrupt it." +
                              (backups > 0 ? $" Automatic backups use {DiskUsage.Format(backups)}; keeping fewer frees space." : "");
        if (_diskWarned) return;
        _diskWarned = true;
        Log.Host($"Low disk space: {DiskUsage.Format(free)} free on {drive}.");
        if (!IsVisible) _tray?.Balloon(TitleText.Text, $"Low disk space: {DiskUsage.Format(free)} free on {drive}");
    }

    /// <summary>Before a backup: is there room for it (the last one's size, plus 1 GB to spare)? If not, it's skipped.</summary>
    bool RoomForBackup()
    {
        var free = FreeSpace();
        if (free < 0) return true;
        var last = ServerFiles.AutoBackups().FirstOrDefault()?.Length ?? 0;
        var needed = Math.Max(last * 12 / 10, 512L << 20) + (1L << 30);
        if (free >= needed) return true;
        Log.Host($"Skipped the backup: {DiskUsage.Format(free)} free, and it needs about {DiskUsage.Format(needed)}. Free up space or keep fewer backups.");
        CheckDiskSpace(force: true);
        return false;
    }

    void BtnDiskBanner_Click(object sender, RoutedEventArgs e) => BtnDisk_Click(sender, e);
}
