using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MinecraftHost.Core;

namespace MinecraftHost;

/// <summary>
/// Scheduled restarts (Server tab, per server): every N hours counted from a time of day (24 = once a day).
/// While the server runs, players get chat warnings at 5 min, 1 min and 10 s, then it restarts like the Restart button.
/// A slot that comes within 10 minutes of a start is skipped, so a server that was just started isn't restarted.
/// </summary>
public partial class MainWindow
{
    static readonly (int Hours, string Label)[] RestartChoices =
    {
        (24, "Once a day"), (12, "Every 12 hours"), (8, "Every 8 hours"), (6, "Every 6 hours"),
        (4, "Every 4 hours"), (3, "Every 3 hours"), (2, "Every 2 hours"), (1, "Every hour"),
    };
    static readonly int[] WarnSeconds = { 300, 60, 10 };
    static readonly TimeSpan MinUptime = TimeSpan.FromMinutes(10);

    readonly DispatcherTimer _restartTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    DateTime? _nextRestart;
    int _warnedUpTo;           // how many of WarnSeconds have been announced for _nextRestart
    bool _restartLoading;      // the card is being filled in: its change events aren't the user's
    // the schedule, kept here so the timer doesn't read the config every second
    bool _restartEnabled, _restartWarn;
    int _restartEvery = 24;
    TimeSpan _restartAt = TimeSpan.FromHours(4);

    void InitRestartCard()
    {
        _restartLoading = true;
        CmbRestartEvery.ItemsSource = RestartChoices.Select(c => c.Label).ToList();
        _restartLoading = false;
        _restartTimer.Tick += (_, _) => RestartTick();
        _restartTimer.Start();
        InitBackupCard();
        SyncRestartCard();
    }

    /// <summary>Fills the card from the current server's settings (also after switching servers).</summary>
    void SyncRestartCard()
    {
        var config = HostConfig.LoadOrNew();
        _restartEnabled = config.RestartEnabled;
        _restartWarn = config.RestartWarn;
        _restartEvery = RestartChoices.Any(c => c.Hours == config.RestartEveryHours) ? config.RestartEveryHours : 24;
        _restartAt = TryParseTime(config.RestartAt, out var at) ? at : TimeSpan.FromHours(4);

        _restartLoading = true;
        ChkRestart.IsChecked = _restartEnabled;
        ChkRestartWarn.IsChecked = _restartWarn;
        CmbRestartEvery.SelectedIndex = Array.FindIndex(RestartChoices, c => c.Hours == _restartEvery);
        TxtRestartAt.Text = $"{_restartAt:hh\\:mm}";
        _restartLoading = false;
        RestartOptions.Visibility = _restartEnabled ? Visibility.Visible : Visibility.Collapsed;
        RestartAtLabel.Text = _restartEvery == 24 ? "At" : "Starting at";
        ScheduleNextRestart();
        if (CmbBackupEvery.ItemsSource != null) SyncBackupCard();
    }

    void RestartSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_restartLoading || CmbRestartEvery == null) return;
        var config = HostConfig.LoadOrNew();
        config.RestartEnabled = ChkRestart.IsChecked == true;
        config.RestartWarn = ChkRestartWarn.IsChecked == true;
        if (CmbRestartEvery.SelectedIndex >= 0) config.RestartEveryHours = RestartChoices[CmbRestartEvery.SelectedIndex].Hours;
        config.Save();
        SyncRestartCard();
        if (sender == ChkRestart)
            Log.Host(config.RestartEnabled ? "Scheduled restarts are on." : "Scheduled restarts are off.");
    }

    void TxtRestartAt_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SaveRestartTime();
    }

    void TxtRestartAt_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => SaveRestartTime();

    void SaveRestartTime()
    {
        if (_restartLoading) return;
        if (!TryParseTime(TxtRestartAt.Text, out var at))
        {
            RestartNext.Foreground = (Brush)FindResource("Bad");
            RestartNext.Text = "Use a 24-hour time, like 04:00 or 16:30.";
            return;
        }
        var config = HostConfig.LoadOrNew();
        config.RestartAt = $"{at:hh\\:mm}";
        config.Save();
        SyncRestartCard();
    }

    /// <summary>"4", "04", "4:00", "16:30" or "4.30" (24-hour).</summary>
    static bool TryParseTime(string? text, out TimeSpan time)
    {
        time = default;
        var m = Regex.Match(text?.Trim() ?? "", @"^(\d{1,2})(?:[:.](\d{2}))?$");
        if (!m.Success) return false;
        var h = int.Parse(m.Groups[1].Value);
        var min = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0;
        if (h > 23 || min > 59) return false;
        time = new TimeSpan(h, min, 0);
        return true;
    }

    /// <summary>The first restart time after <paramref name="after"/>: the day's slots are at, at+every, at+2·every... (and before it).</summary>
    static DateTime NextSlot(int everyHours, TimeSpan at, DateTime after)
    {
        var every = TimeSpan.FromHours(everyHours);
        var t = after.Date + at;
        while (t - every >= after.Date.AddDays(-1)) t -= every;   // back to the first slot around midnight
        while (t <= after) t += every;
        return t;
    }

    /// <summary>The times of day it restarts, in words.</summary>
    string ScheduleInWords()
    {
        if (_restartEvery == 24) return $"every day at {_restartAt:hh\\:mm}";
        var times = Enumerable.Range(0, 24 / _restartEvery)
            .Select(i => TimeSpan.FromHours((_restartAt.TotalHours + i * _restartEvery) % 24))
            .OrderBy(t => t).Select(t => $"{t:hh\\:mm}").ToList();
        return times.Count <= 6
            ? "every day at " + string.Join(", ", times.Take(times.Count - 1)) + " and " + times[^1]
            : $"every {_restartEvery} hours, on the hour from {_restartAt:hh\\:mm}";
    }

    /// <summary>Works out the next restart while the server runs; otherwise there's none pending.</summary>
    void ScheduleNextRestart()
    {
        if (!_restartEnabled || _state != ServerState.Running || _server == null)
        {
            _nextRestart = null;
            UpdateRestartText();
            return;
        }
        var earliest = DateTime.Now > _startedAt + MinUptime ? DateTime.Now : _startedAt + MinUptime;
        var next = NextSlot(_restartEvery, _restartAt, earliest);
        if (next != _nextRestart)
        {
            _nextRestart = next;
            _warnedUpTo = 0;
            Log.Host($"Next scheduled restart: {When(next)}.");
        }
        UpdateRestartText();
    }

    static string When(DateTime t) =>
        (t.Date == DateTime.Today ? "today" : t.Date == DateTime.Today.AddDays(1) ? "tomorrow" : t.ToString("dddd")) + $" at {t:HH:mm}";

    static string InWords(TimeSpan left)
    {
        if (left.TotalMinutes >= 60) return $"{(int)left.TotalHours} h {left.Minutes} min";
        if (left.TotalSeconds >= 90) return $"{(int)Math.Round(left.TotalMinutes)} minutes";
        if (left.TotalSeconds >= 55) return "1 minute";
        var s = Math.Max(1, (int)Math.Ceiling(left.TotalSeconds));
        return s == 1 ? "1 second" : $"{s} seconds";
    }

    void UpdateRestartText()
    {
        if (RestartNext == null) return;
        RestartNext.Foreground = (Brush)FindResource("Muted");
        RestartNext.Text = !_restartEnabled ? ""
            : _nextRestart is { } next ? $"Next restart: {When(next)} (in {InWords(next - DateTime.Now)})."
            : $"While the server runs, it restarts {ScheduleInWords()}.";
    }

    DateTime _restartTextShown;

    void RestartTick()
    {
        if (_nextRestart is not { } at || _state != ServerState.Running || _server?.IsRunning != true) return;
        var left = at - DateTime.Now;

        // announce the latest warning that's due (a late start can skip the earlier ones)
        if (_restartWarn)
        {
            var due = -1;
            for (var i = 0; i < WarnSeconds.Length; i++) if (left.TotalSeconds <= WarnSeconds[i]) due = i;
            if (due >= _warnedUpTo && left > TimeSpan.Zero)
            {
                _server.Send($"say The server restarts in {InWords(left)}.");
                Log.Host($"Told players: the server restarts in {InWords(left)}.");
                _warnedUpTo = due + 1;
            }
        }

        if (left <= TimeSpan.Zero)
        {
            _nextRestart = null;
            Log.Host("Scheduled restart.");
            if (_restartWarn) _server.Send("say Restarting now. Back in a minute!");
            _restartAfterStop = true;
            _scheduledRestart = true;
            StopServer();
            UpdateRestartText();
            return;
        }
        if (DateTime.Now - _restartTextShown > TimeSpan.FromSeconds(left.TotalMinutes < 2 ? 1 : 20))
        {
            _restartTextShown = DateTime.Now;
            UpdateRestartText();
        }
    }
}
