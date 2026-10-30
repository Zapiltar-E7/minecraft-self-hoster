using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MinecraftHost.Core;

namespace MinecraftHost;

/// <summary>
/// The Players card (Server tab): who's online and for how long, the players seen recently with their last visit and
/// total play time (PlayerHistory), and a menu on each name: message, teleport, game mode, operator, kick and ban.
/// </summary>
public partial class MainWindow
{
    void UpdatePlayers()
    {
        PlayersPanel.Children.Clear();
        foreach (var name in _players)
        {
            var on = PlayerHistory.OnlineFor(name);
            PlayersPanel.Children.Add(PlayerRow(name, online: true,
                on is { } t ? $"On for {PlayerHistory.Duration(t)}" : null));
        }

        var recent = PlayerHistory.Recent(30);
        if (recent.Count > 0)
        {
            var icon = new TextBlock { Text = "\uE72C" };
            var refresh = new Button
            {
                Style = (Style)FindResource("IconButton"), Content = icon, Width = 26, Height = 24, FontSize = 11,
                ToolTip = "Refresh: read play times from the world again", VerticalAlignment = VerticalAlignment.Center,
            };
            AutomationProperties_Name(refresh, "Refresh recent players");
            refresh.Click += async (_, _) => await RefreshRecentAsync(refresh, icon);
            DockPanel.SetDock(refresh, Dock.Right);
            var header = new DockPanel { Margin = new Thickness(2, _players.Count > 0 ? 8 : 0, 0, 4) };
            header.Children.Add(refresh);
            if (_recentNote != null)
            {
                var note = new TextBlock { Text = _recentNote, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 2, 0) };
                note.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                DockPanel.SetDock(note, Dock.Right);
                header.Children.Add(note);
                if (_recentNoteNew) { Motion.FadeIn(note); _recentNoteNew = false; }
            }
            header.Children.Add(new TextBlock { Text = "RECENTLY", Style = (Style)FindResource("CardTitle"), Margin = new Thickness(0), VerticalAlignment = VerticalAlignment.Center });
            PlayersPanel.Children.Add(header);
            foreach (var p in recent)
                PlayersPanel.Children.Add(PlayerRow(p.Name, online: false,
                    $"{PlayerHistory.Ago(p.LastSeen)} · {PlayerHistory.Duration(TimeSpan.FromSeconds(p.TotalSeconds))} played"));
        }

        NoPlayers.Text = _state switch
        {
            ServerState.Running => "No one is online yet. Players appear here when they join.",
            ServerState.Starting => "The server is starting...",
            _ => "The server isn't running.",
        };
        NoPlayers.Visibility = _players.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (StatusText != null && _state == ServerState.Running) StatusText.Text = $"Running · {_players.Count} online";
        PlayerCount.Text = _state == ServerState.Running ? $"{_players.Count} online" : "";
        SecPlayersCount.Text = _state == ServerState.Running ? _players.Count.ToString() : "";
        UpdateWeekCard();
    }

    /// <summary>The refresh button by "Recently": re-read the world's stats and look up any names still missing.</summary>
    async Task RefreshRecentAsync(Button button, UIElement icon)
    {
        button.IsEnabled = false;
        Motion.Spin(icon, true);
        static string Key(PlayerHistory.Entry p) => $"{p.Name}|{(long)p.TotalSeconds / 60}|{p.LastSeen:g}";
        var before = PlayerHistory.Recent(30).Select(Key).ToHashSet();
        PlayerHistory.ReloadWorldStats();
        await Task.WhenAll(PlayerHistory.NameUnknownPlayersAsync(), Task.Delay(600));   // long enough to see it turn
        var changed = PlayerHistory.Recent(30).Count(p => !before.Contains(Key(p)));
        _recentNote = (changed == 0 ? "No changes" : changed == 1 ? "1 player updated" : $"{changed} players updated")
                      + $" · {DateTime.Now:h:mm tt}";
        _recentNoteNew = true;
        UpdatePlayers();   // draws a new header with the note, so this button doesn't need enabling again
    }

    /// <summary>What the last refresh found, shown beside the button until the server is switched.</summary>
    string? _recentNote;
    bool _recentNoteNew;

    /// <summary>Players in the world's stats that the server's lists don't name: look them up, then redraw.</summary>
    async Task NamePlayersAsync()
    {
        if (await PlayerHistory.NameUnknownPlayersAsync()) UpdatePlayers();
    }

    FrameworkElement PlayerRow(string name, bool online, string? detail)
    {
        var dot = new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 5, 9, 0), VerticalAlignment = VerticalAlignment.Top };
        dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, online ? "Accent" : "Unknown");
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = name, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis });
        if (detail != null)
        {
            var d = new TextBlock { Text = detail, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
            d.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            text.Children.Add(d);
        }
        var chevron = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("Icons"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        chevron.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        DockPanel.SetDock(chevron, Dock.Right);
        var row = new Button
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(10, 6, 8, 6), Margin = new Thickness(0, 0, 0, 4),
            FontWeight = FontWeights.Normal, ToolTip = online ? "Actions for this player" : $"{name} isn't online",
            Content = new DockPanel { Children = { chevron, new DockPanel { Children = { dot, text } } } },
        };
        if (online) row.SetResourceReference(Control.BackgroundProperty, "Field");
        else row.Background = Brushes.Transparent;
        AutomationProperties_Name(row, name);
        row.Click += (_, _) => ShowPlayerMenu(row, name, online);
        return row;
    }

    static void AutomationProperties_Name(DependencyObject d, string name) => System.Windows.Automation.AutomationProperties.SetName(d, name);

    void ShowPlayerMenu(FrameworkElement target, string name, bool online)
    {
        var running = _server?.IsRunning == true && _state == ServerState.Running;
        var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.Bottom };
        MenuItem Item(string header, Action act, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled && running };
            mi.Click += (_, _) => act();
            menu.Items.Add(mi);
            return mi;
        }
        void Send(string command) { Log.Write("> " + command); _server?.Send(command); }

        var history = PlayerHistory.Get(name);
        var info = new MenuItem
        {
            Header = online && PlayerHistory.OnlineFor(name) is { } t ? $"{name} · on for {PlayerHistory.Duration(t)}"
                : history != null ? $"{name} · last seen {PlayerHistory.Ago(history.LastSeen)}" : name,
            IsEnabled = false,
        };
        menu.Items.Add(info);
        if (history != null)
            menu.Items.Add(new MenuItem { Header = $"{PlayerHistory.Duration(PlayerHistory.Total(name))} played, {history.Sessions} visits", IsEnabled = false });
        menu.Items.Add(new Separator());

        if (online)
        {
            Item("Send a message...", () =>
            {
                var msg = Prompt("Message " + name, "Only they see it, as a whisper from the server.", "");
                if (!string.IsNullOrWhiteSpace(msg)) Send($"tell {name} {msg.Trim()}");
            });
            var tp = new MenuItem { Header = "Teleport to", IsEnabled = running && _players.Count > 1 };
            foreach (var other in _players.Where(p => !p.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                var o = other;
                var mi = new MenuItem { Header = o };
                mi.Click += (_, _) => Send($"tp {name} {o}");
                tp.Items.Add(mi);
            }
            menu.Items.Add(tp);
            var gm = new MenuItem { Header = "Game mode", IsEnabled = running };
            foreach (var mode in Gamemodes)
            {
                var m = mode;
                var mi = new MenuItem { Header = Cap(m) };
                mi.Click += (_, _) => Send($"gamemode {m} {name}");
                gm.Items.Add(mi);
            }
            menu.Items.Add(gm);
        }

        var isOp = PlayerLists.Read(PlayerLists.OpsFile).Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        Item(isOp ? "Remove operator" : "Make operator", () =>
        {
            Send((isOp ? "deop " : "op ") + name);
            _ = Task.Delay(1500).ContinueWith(_ => Dispatcher.BeginInvoke(RefreshPlayerLists));
        });
        menu.Items.Add(new Separator());
        if (online)
            Item("Kick...", () =>
            {
                var reason = Prompt("Kick " + name, "They can join again straight away. Reason (optional):", "");
                if (reason != null) Send($"kick {name} {reason.Trim()}".TrimEnd());
            });
        Item("Ban...", () =>
        {
            var reason = Prompt("Ban " + name, "They can't join again until unbanned (pardon " + name + "). Reason (optional):", "");
            if (reason != null) Send($"ban {name} {reason.Trim()}".TrimEnd());
        });
        if (!running)
            menu.Items.Add(new MenuItem { Header = "Start the server to use these", IsEnabled = false });
        menu.IsOpen = true;
    }
}
