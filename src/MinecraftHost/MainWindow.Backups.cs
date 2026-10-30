using System.Diagnostics;
using System.Windows;
using MinecraftHost.Core;

namespace MinecraftHost;

/// <summary>
/// Automatic world backups (Server tab, per server) and starting again after a crash.
/// While the server runs, a backup turns saving off, flushes the worlds to disk, zips them and turns saving back on,
/// so players aren't interrupted. At a scheduled restart it can back up while the server is down instead.
/// Only "... worlds auto ..." zips are cleaned up, oldest first, down to the number to keep.
/// </summary>
public partial class MainWindow
{
    static readonly (int Hours, string Label)[] BackupEvery =
    {
        (1, "Every hour"), (2, "Every 2 hours"), (3, "Every 3 hours"), (6, "Every 6 hours"), (12, "Every 12 hours"), (24, "Once a day"),
    };
    static readonly int[] BackupKeepChoices = { 3, 5, 7, 10, 14, 30 };

    bool _backupLoading, _backingUp, _scheduledRestart;
    DateTime _backupStatusShown;
    readonly List<DateTime> _crashes = new();

    void InitBackupCard()
    {
        _backupLoading = true;
        CmbBackupEvery.ItemsSource = BackupEvery.Select(c => c.Label).ToList();
        CmbBackupKeep.ItemsSource = BackupKeepChoices.Select(n => $"Last {n}").ToList();
        _backupLoading = false;
        _restartTimer.Tick += (_, _) => BackupTick();
        _restartTimer.Tick += (_, _) => CountdownTick();
        SyncBackupCard();
    }

    void SyncBackupCard()
    {
        var config = HostConfig.LoadOrNew();
        _backupLoading = true;
        ChkBackupAuto.IsChecked = config.BackupAuto;
        ChkBackupOnRestart.IsChecked = config.BackupOnRestart;
        CmbBackupEvery.SelectedIndex = Math.Max(0, Array.FindIndex(BackupEvery, c => c.Hours == config.BackupEveryHours));
        CmbBackupKeep.SelectedIndex = Math.Max(0, Array.IndexOf(BackupKeepChoices, config.BackupKeep));
        ChkCrashRestart.IsChecked = config.CrashRestart;
        _backupLoading = false;
        BackupOptions.Visibility = config.BackupAuto ? Visibility.Visible : Visibility.Collapsed;
        UpdateBackupStatus();
    }

    void BackupSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_backupLoading || CmbBackupEvery == null) return;
        var config = HostConfig.LoadOrNew();
        config.BackupAuto = ChkBackupAuto.IsChecked == true;
        config.BackupOnRestart = ChkBackupOnRestart.IsChecked == true;
        if (CmbBackupEvery.SelectedIndex >= 0) config.BackupEveryHours = BackupEvery[CmbBackupEvery.SelectedIndex].Hours;
        if (CmbBackupKeep.SelectedIndex >= 0) config.BackupKeep = BackupKeepChoices[CmbBackupKeep.SelectedIndex];
        config.Save();
        if (sender == ChkBackupAuto) Log.Host(config.BackupAuto ? "Automatic backups are on." : "Automatic backups are off.");
        if (sender == ChkBackupAuto && config.BackupAuto) Motion.Fold(BackupOptions, true);
        else BackupOptions.Visibility = config.BackupAuto ? Visibility.Visible : Visibility.Collapsed;
        UpdateBackupStatus();
    }

    void CrashRestart_Changed(object sender, RoutedEventArgs e)
    {
        if (_backupLoading) return;
        var config = HostConfig.LoadOrNew();
        config.CrashRestart = ChkCrashRestart.IsChecked == true;
        config.Save();
    }

    /// <summary>When the next automatic backup is due, or null when none is (off, or the server isn't running).</summary>
    DateTime? NextBackup(HostConfig config)
    {
        if (!config.BackupAuto || _state != ServerState.Running) return null;
        var last = ServerFiles.AutoBackups().FirstOrDefault()?.LastWriteTime;
        var due = (last ?? _startedAt) + TimeSpan.FromHours(config.BackupEveryHours);
        var earliest = _startedAt + TimeSpan.FromMinutes(5);   // let a fresh start settle first
        return due < earliest ? earliest : due;
    }

    void UpdateBackupStatus()
    {
        if (BackupStatus == null) return;
        var config = HostConfig.LoadOrNew();
        var autos = ServerFiles.AutoBackups();
        var parts = new List<string>();
        if (_backingUp) parts.Add("Backing up now...");
        else if (autos.FirstOrDefault() is { } last)
            parts.Add($"Last automatic backup: {PlayerHistory.Ago(last.LastWriteTime)} ({DiskUsage.Format(last.Length)}).");
        if (config.BackupAuto)
        {
            if (!_backingUp && NextBackup(config) is { } next) parts.Add($"Next: {When(next)}.");
            else if (!_backingUp && _state != ServerState.Running) parts.Add("Backups run while the server is running.");
            if (autos.Count > 0) parts.Add($"{autos.Count} kept, {DiskUsage.Format(autos.Sum(f => f.Length))} in all.");
        }
        BackupStatus.Text = string.Join(" ", parts);
        BackupStatus.Visibility = parts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        BtnBackupNow.IsEnabled = !_backingUp && _busy == 0 && _state is ServerState.Stopped or ServerState.Running;
    }

    void BackupTick()
    {
        if (DateTime.Now - _backupStatusShown > TimeSpan.FromSeconds(30))
        {
            _backupStatusShown = DateTime.Now;
            UpdateBackupStatus();
            if (_players.Count > 0) UpdatePlayers();   // the "on for 12 min" times
        }
        CheckDiskSpace();
        if (_backingUp || _busy > 0 || _server?.IsRunning != true) return;
        var config = HostConfig.LoadOrNew();
        if (NextBackup(config) is { } next && DateTime.Now >= next) _ = BackupWhileRunningAsync(config.BackupKeep);
    }

    async void BtnBackupNow_Click(object sender, RoutedEventArgs e)
    {
        var keep = HostConfig.LoadOrNew().BackupKeep;
        if (_server?.IsRunning == true && _state == ServerState.Running) await BackupWhileRunningAsync(keep);
        else if (_state == ServerState.Stopped) await BackupStoppedAsync(keep);
    }

    void BtnBackupFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Paths.BackupDir);
        Process.Start(new ProcessStartInfo(Paths.BackupDir) { UseShellExecute = true });
    }

    /// <summary>Saving off, everything written to disk, zip, saving back on.</summary>
    async Task BackupWhileRunningAsync(int keep)
    {
        var server = _server;
        if (_backingUp || server?.IsRunning != true || !RoomForBackup()) return;
        _backingUp = true;
        UpdateBackupStatus();
        Log.Host("Automatic backup: pausing world saving and writing everything to disk...");
        var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Watch(string line) { if (line.Contains("Saved the game") || line.Contains("Saved the world")) saved.TrySetResult(); }
        server.Line += Watch;
        try
        {
            server.Send("save-off");
            server.Send("save-all flush");
            await Task.WhenAny(saved.Task, Task.Delay(TimeSpan.FromSeconds(90)));
            if (!saved.Task.IsCompleted) Log.Host("The server didn't confirm the save in 90 seconds; backing up anyway.");
            await RunBusyAsync(() => Task.Run(() =>
            {
                ServerFiles.BackupWorlds(auto: true);
                ServerFiles.PruneAutoBackups(keep);
            }));
        }
        finally
        {
            server.Line -= Watch;
            if (server.IsRunning) server.Send("save-on");
            _backingUp = false;
            UpdateBackupStatus();
            CheckDiskSpace(force: true);
        }
    }

    async Task BackupStoppedAsync(int keep)
    {
        if (_backingUp || !RoomForBackup()) return;
        _backingUp = true;
        UpdateBackupStatus();
        try
        {
            await RunBusyAsync(() => Task.Run(() =>
            {
                ServerFiles.BackupWorlds(auto: true);
                ServerFiles.PruneAutoBackups(keep);
            }));
        }
        finally
        {
            _backingUp = false;
            UpdateBackupStatus();
        }
    }

    /// <summary>The Restart button or a scheduled restart stopped the server cleanly: back up if asked, then start again.</summary>
    async Task StartAgainAsync()
    {
        var config = HostConfig.LoadOrNew();
        if (_scheduledRestart && config.BackupAuto && config.BackupOnRestart)
        {
            Log.Host("Backing up while the server is down for its scheduled restart...");
            await BackupStoppedAsync(config.BackupKeep);
        }
        _scheduledRestart = false;
        await StartServerAsync(_lastStartSafe);
    }

    /// <summary>The server crashed while running: start it again, unless it's crashing over and over.</summary>
    void MaybeRestartAfterCrash()
    {
        if (HostConfig.LoadOrNew().CrashRestart != true) return;
        _crashes.Add(DateTime.Now);
        _crashes.RemoveAll(t => DateTime.Now - t > TimeSpan.FromMinutes(10));
        if (_crashes.Count > 3)
        {
            Log.Host("It crashed 3 times in 10 minutes, so it isn't being started again automatically. The log above shows why.");
            return;
        }
        Log.Host($"Starting it again in 10 seconds (crash {_crashes.Count} of 3 allowed in 10 minutes). Click Stop to cancel.");
        _crashRestartPending = true;
        _ = Task.Delay(TimeSpan.FromSeconds(10)).ContinueWith(_ => Dispatcher.BeginInvoke(async () =>
        {
            if (!_crashRestartPending || _state != ServerState.Stopped || _busy > 0) return;
            _crashRestartPending = false;
            await StartServerAsync(_lastStartSafe);
        }));
    }

    bool _crashRestartPending;
}
