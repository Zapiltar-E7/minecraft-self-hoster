using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using MinecraftHost.Core;

namespace MinecraftHost;

/// <summary>
/// Smaller conveniences: tray notifications when players come and go, stopping or restarting with a countdown when
/// people are online, stopping a server left running by an earlier session (over RCON), players the whitelist turned
/// away (with one-click Add), and the weekly summary.
/// </summary>
public partial class MainWindow
{
    // ------------------------------------------------------------ join / leave notifications

    void NotifyPlayer(string name, bool joined)
    {
        if (IsVisible || HostConfig.Load()?.NotifyPlayers == false) return;
        _tray?.Balloon(TitleText.Text, joined ? $"{name} joined ({_players.Count} online)" : $"{name} left ({_players.Count} online)");
    }

    // ------------------------------------------------------------ stop or restart with a countdown

    static readonly int[] CountdownWarnings = { 60, 30, 10, 5 };
    DateTime? _countdownEnd;
    bool _countdownRestart;
    int _countdownWarned;

    /// <summary>
    /// Stop (or Restart) was pressed. With nobody online it happens straight away; otherwise the user picks between
    /// warning players for a minute first and doing it now. Returns false when they cancelled.
    /// </summary>
    bool ConfirmStopWithPlayers(bool restart)
    {
        if (_players.Count == 0) return true;
        var verb = restart ? "Restart" : "Stop";
        var who = _players.Count == 1 ? $"{_players.First()} is online." : $"{_players.Count} players are online.";
        var choice = ShowOverlay(close =>
        {
            var panel = new StackPanel();
            panel.Children.Add(Heading($"{verb} the server", MessageBoxImage.None));
            panel.Children.Add(new TextBlock
            {
                Text = $"{who} Warn them in chat and {verb.ToLower()} in 1 minute, or {verb.ToLower()} now?",
                TextWrapping = TextWrapping.Wrap, Foreground = Res("Muted"), Margin = new Thickness(0, 6, 0, 14),
            });
            panel.Children.Add(ButtonRow(new (string, Func<object?>, bool)[]
            {
                ("Cancel", () => null, true), ($"{verb} now", () => "now", false), ("Warn, then in 1 minute", () => "warn", false),
            }, close));
            return panel;
        }, 460) as string;
        if (choice == "warn") { StartCountdown(restart); return false; }
        return choice == "now";
    }

    void StartCountdown(bool restart)
    {
        _countdownEnd = DateTime.Now.AddSeconds(60);
        _countdownRestart = restart;
        _countdownWarned = 0;
        Log.Host(restart ? "Restarting in 1 minute; players have been told." : "Stopping in 1 minute; players have been told.");
        CountdownTick();
    }

    void CancelCountdown()
    {
        if (_countdownEnd == null) return;
        _countdownEnd = null;
        _server?.Send("say Never mind: the server is staying up.");
        Log.Host("Countdown cancelled.");
        UpdateView();
    }

    void CountdownTick()
    {
        if (_countdownEnd is not { } end) return;
        if (_server?.IsRunning != true || _state != ServerState.Running) { _countdownEnd = null; UpdateView(); return; }
        var left = end - DateTime.Now;
        var what = _countdownRestart ? "restarts" : "stops";
        var due = -1;
        for (var i = 0; i < CountdownWarnings.Length; i++) if (left.TotalSeconds <= CountdownWarnings[i] + 0.5) due = i;
        if (due >= _countdownWarned && left > TimeSpan.Zero)
        {
            _server.Send($"say The server {what} in {InWords(left)}.");
            _countdownWarned = due + 1;
        }
        if (left <= TimeSpan.Zero)
        {
            _countdownEnd = null;
            _server.Send(_countdownRestart ? "say Restarting now. Back in a minute!" : "say Stopping now. See you soon!");
            if (_countdownRestart) _restartAfterStop = true;
            StopServer();
            return;
        }
        StopLabel.Text = $"Cancel ({Math.Ceiling(left.TotalSeconds)} s)";
    }

    // ------------------------------------------------------------ a server left running by an earlier session

    /// <summary>
    /// At startup this server's port is taken. If it's our own server (its RCON answers with the password in its
    /// server.properties), offer to stop it safely, which saves the worlds; otherwise point at Force stop.
    /// </summary>
    async Task OfferStopLeftoverAsync()
    {
        var port = PlayerLists.GetProperty("rcon.port");
        var password = PlayerLists.GetProperty("rcon.password");
        var rconOn = PlayerLists.GetProperty("enable-rcon") == "true" && int.TryParse(port, out _) && !string.IsNullOrEmpty(password);
        Rcon? rcon = rconOn ? await Rcon.ConnectAsync(int.Parse(port!), password!) : null;
        if (rcon == null)
        {
            Log.Host("Note: something is already using this server's port. If it's this server left running from earlier, use Force stop to end it.");
            return;
        }
        using (rcon)
        {
            var reply = Ask("This server is already running, probably left over from an earlier session of the app (the app can't see its console).\n\n" +
                            "Stop it safely now? The worlds are saved first.", "Server already running", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (reply != MessageBoxResult.Yes) { Log.Host("Left the running server alone. Stop it before starting it from here."); return; }
            try
            {
                await rcon.CommandAsync("stop");
                Log.Host("Asked the leftover server to stop (over RCON). It saves and closes by itself in a few seconds.");
            }
            catch (Exception ex) { Log.Host("Couldn't stop it over RCON: " + ex.Message + ". Use Force stop instead."); }
        }
    }

    // ------------------------------------------------------------ players the whitelist turned away

    static readonly Regex[] TurnedAwayRx =
    {
        new(@"Disconnecting ([A-Za-z0-9_]{1,16}) \(.*?(white-?list|not white)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"name=([A-Za-z0-9_]{1,16}).*?lost connection: .*white-?list", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\]: ([A-Za-z0-9_]{1,16}) \(/[\d.:\[\]a-f]+\) lost connection: .*white-?list", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    };
    readonly List<(string Name, DateTime When, int Tries)> _joinRequests = new();

    void CheckTurnedAway(string line)
    {
        if (!line.Contains("white", StringComparison.OrdinalIgnoreCase)) return;
        foreach (var rx in TurnedAwayRx)
        {
            var m = rx.Match(line);
            if (!m.Success) continue;
            var name = m.Groups[1].Value;
            var i = _joinRequests.FindIndex(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) _joinRequests[i] = (name, DateTime.Now, _joinRequests[i].Tries + 1);
            else
            {
                _joinRequests.Add((name, DateTime.Now, 1));
                Log.Host($"{name} tried to join but isn't on the whitelist. Add them from Server > Players.");
                if (!IsVisible) _tray?.Balloon(TitleText.Text, $"{name} tried to join but isn't on the whitelist.");
            }
            UpdateJoinRequests();
            return;
        }
    }

    void UpdateJoinRequests()
    {
        RequestsPanel.Children.Clear();
        var onList = PlayerLists.Read(PlayerLists.WhitelistFile).Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _joinRequests.RemoveAll(r => onList.Contains(r.Name));
        RequestsPanel.Visibility = _joinRequests.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SecPlayersDot.Visibility = RequestsPanel.Visibility;
        if (_joinRequests.Count == 0) return;
        var head = new TextBlock { Text = "Asked to join", FontWeight = FontWeights.SemiBold, FontSize = 12.5, Margin = new Thickness(0, 0, 0, 6) };
        RequestsPanel.Children.Add(head);
        foreach (var (name, when, tries) in _joinRequests.OrderByDescending(r => r.When))
        {
            var add = new Button { Style = (Style)FindResource("AccentButton"), Content = "Add", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(8, 0, 0, 0) };
            add.Click += async (_, _) => { TxtWhitelistAdd.Text = name; await AddPlayerAsync(op: false); UpdateJoinRequests(); };
            var dismiss = IconButton("", "Dismiss", (_, _) => { _joinRequests.RemoveAll(r => r.Name == name); UpdateJoinRequests(); });
            DockPanel.SetDock(add, Dock.Right);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold });
            text.Children.Add(Muted($"{PlayerHistory.Ago(when)}" + (tries > 1 ? $" · tried {tries} times" : ""), 0));
            var row = new Border
            {
                CornerRadius = new CornerRadius(7), Padding = new Thickness(10, 6, 6, 6), Margin = new Thickness(0, 0, 0, 4),
                Child = new DockPanel { Children = { dismiss, add, text } },
            };
            row.SetResourceReference(Border.BackgroundProperty, "Field");
            RequestsPanel.Children.Add(row);
        }
        RequestsPanel.Children.Add(new Border { Height = 1, Margin = new Thickness(-16, 10, -16, 12), Background = Res("CardBorder") });
    }

    // ------------------------------------------------------------ weekly summary

    void UpdateWeekCard()
    {
        if (WeekTiles == null) return;
        var now = Stats.Week(DateTime.Today);
        var before = Stats.Week(DateTime.Today.AddDays(-7));
        WeekTiles.Children.Clear();
        WeekTiles.Children.Add(WeekTile("Players", now.Players.ToString(), Change(now.Players, before.Players)));
        WeekTiles.Children.Add(WeekTile("Hours played", now.Hours.ToString(now.Hours < 10 ? "0.0" : "0"), Change(now.Hours, before.Hours)));
        WeekTiles.Children.Add(WeekTile("Most online", now.Peak.ToString(), null));
        WeekTiles.Children.Add(WeekTile("Average TPS", now.Tps is { } t ? t.ToString("0.0") : "–", null));
        WeekNote.Text = (now.Top.Count > 0 ? "Played most: " + string.Join(", ", now.Top) + ". " : "No one has played in the last 7 days. ")
                        + (now.Crashes == 0 ? "No crashes." : now.Crashes == 1 ? "1 crash." : $"{now.Crashes} crashes.");
    }

    static string? Change(double now, double before)
    {
        if (before <= 0) return null;
        var pct = (now - before) / before * 100;
        return Math.Abs(pct) < 1 ? "same as last week" : $"{(pct > 0 ? "up" : "down")} {Math.Abs(pct):0}% on last week";
    }

    FrameworkElement WeekTile(string label, string value, string? note)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
        panel.Children.Add(Muted(label, 0));
        panel.Children.Add(new TextBlock { Text = value, FontSize = 20, FontWeight = FontWeights.SemiBold });
        if (note != null) panel.Children.Add(new TextBlock { Text = note, FontSize = 11, Foreground = Res("Muted"), TextWrapping = TextWrapping.Wrap });
        return panel;
    }

    /// <summary>Once a week (the first start on or after Monday), last week's numbers in the log.</summary>
    void LogWeeklySummary()
    {
        var config = HostConfig.LoadOrNew();
        var monday = DateTime.Today.AddDays(-(((int)DateTime.Today.DayOfWeek + 6) % 7));
        if (config.LastWeekSummary is { } last && DateTime.TryParse(last, out var d) && d >= monday) return;
        var week = Stats.Week(monday.AddDays(-1));
        config.LastWeekSummary = monday.ToString("yyyy-MM-dd");
        config.Save();
        if (week.Players == 0 && week.Tps == null) return;
        Log.Host($"Last week on {Paths.ServerName}: {week.Players} players, {week.Hours:0.#} hours played, at most {week.Peak} online at once" +
                 (week.Tps is { } t ? $", average TPS {t:0.0}" : "") + (week.Crashes > 0 ? $", {week.Crashes} crashes." : ", no crashes."));
    }
}
