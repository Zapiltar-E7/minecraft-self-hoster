using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MinecraftHost.Core;

namespace MinecraftHost;

/// <summary>
/// Server → Announcements: messages posted in chat on a schedule while the server runs. Each has its own interval and
/// can skip an empty server. {map} is the web map's link right now (Cloudflare, playit or port forwarding, whichever
/// that connection uses), so an announcement with it never posts an old link; {address} and {online} work the same way.
/// </summary>
public partial class MainWindow
{
    readonly DispatcherTimer _announceTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    readonly Dictionary<string, DateTime> _announceDue = new();   // by announcement id, while the server runs
    readonly HashSet<string> _announceSkipNoted = new();          // "skipped: no map link" said once per run

    static readonly int[] AnnounceIntervals = { 5, 10, 15, 20, 30, 45, 60, 90, 120, 180, 240, 360 };
    static readonly string[] MapServices = { "bluemap", "dynmap", "squaremap", "pl3xmap" };

    void InitAnnouncements()
    {
        _announceTimer.Tick += (_, _) => AnnounceTick();
        _announceTimer.Start();
        RebuildAnnouncements();
    }

    // ------------------------------------------------------------ sending

    void AnnounceTick()
    {
        if (_state != ServerState.Running || _server == null)
        {
            _announceDue.Clear();
            _announceSkipNoted.Clear();
            return;
        }
        var config = HostConfig.Load();
        if (config == null) return;
        var now = DateTime.Now;
        foreach (var a in config.Announcements.Where(a => a.Enabled && a.Text.Trim().Length > 0))
        {
            // the first one goes out one interval after the server is up, then every interval
            if (!_announceDue.TryGetValue(a.Id, out var due)) { _announceDue[a.Id] = now.AddMinutes(a.EveryMinutes); continue; }
            if (now < due) continue;
            _announceDue[a.Id] = now.AddMinutes(a.EveryMinutes);
            if (a.OnlyWithPlayers && _players.Count == 0) continue;
            SendAnnouncement(a, config);
        }
        if (SecAnnounce.IsChecked == true && ServerPage.Visibility == Visibility.Visible) RebuildAnnouncements();   // the "next in" times
    }

    bool SendAnnouncement(Announcement a, HostConfig config)
    {
        var command = Announcements.Command(a.Text, name => AnnounceValue(name, config), Announcements.NewClickFormat(config.Version));
        if (command == null)
        {
            if (_announceSkipNoted.Add(a.Id)) Log.Host($"Skipped an announcement: {Short(a.Text)} has {{map}}, but the web map has no link right now.");
            return false;
        }
        _server?.Send(command);
        return true;
    }

    /// <summary>A placeholder's value now, or null when there's none (the map has no link yet).</summary>
    string? AnnounceValue(string name, HostConfig config) => name switch
    {
        "map" => MapLink(config),
        "address" => FriendAddress(config),
        "online" => _players.Count.ToString(),
        _ => null,
    };

    /// <summary>The web map's link right now, by how its connection is reached; null if it has none.</summary>
    string? MapLink(HostConfig config)
    {
        var map = MapServices.Select(id => _services.FirstOrDefault(s => s.Id == id)).FirstOrDefault(s => s != null);
        if (map == null) return null;
        switch (Method(config, map))
        {
            case ViaCloudflare:
                return _webTunnels.TryGetValue(map.Id, out var tunnel) ? tunnel.Url : null;
            case ViaForward:
                var host = !string.IsNullOrEmpty(config.ForwardAddress) ? config.ForwardAddress.Split(':')[0] : Net.PublicIPv4;
                return host == null ? null : $"http://{host}:{map.Port}";
            default:
                return config.ServiceAddresses.TryGetValue(map.Id, out var address) && address.Length > 0
                    ? (address.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? address : "http://" + address) : null;
        }
    }

    /// <summary>One line for the list: line breaks shown as " / ", long text cut short.</summary>
    static string Short(string text)
    {
        text = text.Replace("\r\n", "\n").Trim().Replace("\n", " / ");
        return text.Length > 60 ? text[..57] + "..." : text;
    }

    // ------------------------------------------------------------ the list

    void RebuildAnnouncements()
    {
        var config = HostConfig.LoadOrNew();
        AnnounceRows.Children.Clear();
        if (config.Announcements.Count == 0)
            AnnounceRows.Children.Add(new SettingRow { Icon = "", Title = "No announcements yet", Hint = "Add one below, or the live map link." });
        foreach (var a in config.Announcements)
            AnnounceRows.Children.Add(AnnounceRow(a));

        var add = new Button { Padding = new Thickness(12, 6, 12, 6), FontWeight = FontWeights.Normal, Content = IconText("", "Add") };
        add.Click += (_, _) => EditAnnouncement(null);
        System.Windows.Automation.AutomationProperties.SetName(add, "Add an announcement");
        AnnounceRows.Children.Add(new SettingRow { Icon = "", Title = "Add an announcement", Hint = "Any message, every few minutes", Content = add });

        // the map link, ready made, until there is one
        if (!config.Announcements.Any(a => a.Text.Contains(Announcements.MapToken)))
        {
            var map = new Button { Padding = new Thickness(12, 6, 12, 6), FontWeight = FontWeight.FromOpenTypeWeight(400), Content = IconText("", "Add") };
            map.Click += (_, _) => SaveAnnouncement(new Announcement { Text = "&bLive map: {map}", EveryMinutes = 30 });
            System.Windows.Automation.AutomationProperties.SetName(map, "Add the live map announcement");
            AnnounceRows.Children.Add(new SettingRow { Icon = "", Title = "Live map link", Hint = "Posts the web map's current link every 30 min", Content = map });
        }
    }

    UIElement AnnounceRow(Announcement a)
    {
        var running = _state == ServerState.Running;
        var hint = Announcements.Every(a.EveryMinutes) + (a.OnlyWithPlayers ? " · when players are online" : "");
        if (running && a.Enabled && _announceDue.TryGetValue(a.Id, out var due))
        {
            var left = Math.Max(1, (int)Math.Ceiling((due - DateTime.Now).TotalMinutes));
            hint += $" · next in {left} min";
        }
        var toggle = new CheckBox { Style = (Style)FindResource("RowSwitch"), IsChecked = a.Enabled, ToolTip = a.Enabled ? "On" : "Off" };
        System.Windows.Automation.AutomationProperties.SetName(toggle, "Announcement on");
        toggle.Checked += (_, _) => SaveAnnouncement(a with { Enabled = true });
        toggle.Unchecked += (_, _) => SaveAnnouncement(a with { Enabled = false });
        var edit = new Button { Style = (Style)FindResource("IconButton"), Content = "", ToolTip = "Edit", Margin = new Thickness(0, 0, 8, 0) };
        edit.Click += (_, _) => EditAnnouncement(a);
        var delete = new Button { Style = (Style)FindResource("IconButton"), Content = "", ToolTip = "Delete", Margin = new Thickness(0, 0, 16, 0) };
        delete.SetResourceReference(Control.ForegroundProperty, "Bad");
        delete.Click += (_, _) =>
        {
            if (Ask($"Delete this announcement?\n\n{Short(a.Text)}", "Announcements", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            var c = HostConfig.LoadOrNew();
            c.Announcements.RemoveAll(x => x.Id == a.Id);
            c.Save();
            _announceDue.Remove(a.Id);
            RebuildAnnouncements();
        };
        System.Windows.Automation.AutomationProperties.SetName(edit, "Edit announcement");
        System.Windows.Automation.AutomationProperties.SetName(delete, "Delete announcement");
        return new SettingRow
        {
            Icon = a.Text.Contains(Announcements.MapToken) ? "" : "",
            Title = Short(Announcements.WithoutCodes(a.Text)),
            Hint = hint,
            Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { edit, delete, toggle } },
        };
    }

    StackPanel IconText(string glyph, string text) => new()
    {
        Orientation = Orientation.Horizontal,
        Children =
        {
            new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("Icons"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 8, 0) },
            new TextBlock { Text = text },
        },
    };

    void SaveAnnouncement(Announcement a)
    {
        var config = HostConfig.LoadOrNew();
        var i = config.Announcements.FindIndex(x => x.Id == a.Id);
        if (i >= 0)
        {
            if (config.Announcements[i].EveryMinutes != a.EveryMinutes || !config.Announcements[i].Enabled) _announceDue.Remove(a.Id);   // restart its countdown
            config.Announcements[i] = a;
        }
        else config.Announcements.Add(a);
        config.Save();
        Dispatcher.BeginInvoke(RebuildAnnouncements);   // after the click that changed it
    }

    // ------------------------------------------------------------ the editor

    /// <summary>A panel to write or change an announcement (null: a new one).</summary>
    void EditAnnouncement(Announcement? existing)
    {
        var a = existing ?? new Announcement();
        // several lines tall: Enter starts a new line, and each line shows as its own line in chat
        var box = new TextBox
        {
            Style = (Style)FindResource("Input"), Text = a.Text, MaxLength = 500,
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 116, Padding = new Thickness(0, 7, 0, 7),
            VerticalContentAlignment = VerticalAlignment.Top, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        System.Windows.Automation.AutomationProperties.SetName(box, "Message");
        // what players see, styled like chat: always dark, the colours are Minecraft's, not the theme's
        var preview = new TextBlock { TextWrapping = TextWrapping.Wrap, FontFamily = (FontFamily)FindResource("Mono"), FontSize = 12, Foreground = Brushes.White };
        var previewBox = new Border { Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1F, 0x22)), CornerRadius = new CornerRadius(7), Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 4, 0, 0), Child = preview };
        void Preview()
        {
            var config = HostConfig.LoadOrNew();
            preview.Inlines.Clear();
            if (box.Text.Trim().Length == 0) { preview.Inlines.Add(new System.Windows.Documents.Run("(nothing yet)") { Foreground = Brushes.Gray }); return; }
            foreach (var p in Announcements.Parse(box.Text))
            {
                var text = p.Token == null ? p.Text : AnnounceValue(p.Token, config) ?? p.Token switch
                {
                    "map" => "(map link)",
                    "address" => "(join address)",
                    _ => "(players online)",
                };
                if (p.Obfuscated) text = new string(text.Select(ch => ch is '\n' or ' ' ? ch : '▒').ToArray());   // it scrambles in game
                var hex = p.Token == "map" && p.Color == null ? "#55FFFF" : Announcements.Hex(p.Color);
                var run = new System.Windows.Documents.Run(text) { Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)) };
                if (p.Bold) run.FontWeight = FontWeights.Bold;
                if (p.Italic) run.FontStyle = FontStyles.Italic;
                var lines = new TextDecorationCollection();
                if (p.Underline || p.Token == "map") lines.Add(TextDecorations.Underline);
                if (p.Strike) lines.Add(TextDecorations.Strikethrough);
                if (lines.Count > 0) run.TextDecorations = lines;
                preview.Inlines.Add(run);
            }
        }
        box.TextChanged += (_, _) => Preview();

        // formatting: wraps the selected text (then resets after it), or goes in at the caret
        void Format(string code)
        {
            var start = box.SelectionStart;
            if (box.SelectionLength > 0)
            {
                var wrapped = code + box.SelectedText + "&r";
                box.SelectedText = wrapped;
                box.CaretIndex = start + wrapped.Length;
            }
            else
            {
                box.Text = box.Text.Insert(start, code);
                box.CaretIndex = start + code.Length;
            }
            box.Focus();
        }
        var tools = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var (code, name, hex) in Motd.Colors)
        {
            var chip = new Button
            {
                Width = 20, Height = 20, Padding = new Thickness(0), Margin = new Thickness(0, 0, 4, 4), ToolTip = name,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
            };
            chip.SetResourceReference(Control.BorderBrushProperty, "FieldBorder");
            System.Windows.Automation.AutomationProperties.SetName(chip, name);
            chip.Click += (_, _) => Format("&" + code);
            tools.Children.Add(chip);
        }
        var custom = new Button { Width = 20, Height = 20, Padding = new Thickness(0), Margin = new Thickness(0, 0, 4, 4), ToolTip = "Any colour... (Minecraft 1.16 and later)" };
        var rainbow = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        foreach (var (hex, at) in new[] { ("#FF5C5C", 0.0), ("#F5C451", 0.25), ("#6CD98A", 0.5), ("#4C8DFF", 0.75), ("#C77DFF", 1.0) })
            rainbow.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(hex), at));
        custom.Background = rainbow;
        System.Windows.Automation.AutomationProperties.SetName(custom, "Any colour");
        custom.Click += (_, _) =>
        {
            var (selStart, selLength) = (box.SelectionStart, box.SelectionLength);
            var picked = PickColor(Color.FromRgb(0x7C, 0xC4, 0xFF), "Text colour", tryOnTheme: false);
            if (picked == null) return;
            box.Select(selStart, selLength);   // the picker took the focus; put the selection back
            Format("&" + picked);
        };
        tools.Children.Add(custom);
        foreach (var (code, label, tip) in new[]
        {
            ('l', "B", "Bold"), ('o', "I", "Italic"), ('n', "U", "Underline"), ('m', "S", "Strikethrough"),
            ('k', "?", "Obfuscated: scrambled, changing letters"), ('r', "×", "Reset to plain"),
        })
        {
            var b = new Button
            {
                Content = label, Width = 24, Height = 20, Padding = new Thickness(0), Margin = new Thickness(code == 'l' ? 6 : 0, 0, 4, 4), FontSize = 11, ToolTip = tip,
                FontWeight = code == 'l' ? FontWeights.Bold : FontWeights.Normal, FontStyle = code == 'o' ? FontStyles.Italic : FontStyles.Normal,
            };
            if (code == 'n') b.Content = new TextBlock { Text = "U", TextDecorations = TextDecorations.Underline };
            if (code == 'm') b.Content = new TextBlock { Text = "S", TextDecorations = TextDecorations.Strikethrough };
            System.Windows.Automation.AutomationProperties.SetName(b, tip.Split(':')[0]);
            b.Click += (_, _) => Format("&" + code);
            tools.Children.Add(b);
        }

        var inserts = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var (label, token, tip) in new[]
        {
            ("Map link", Announcements.MapToken, "The web map's current link, clickable"),
            ("Join address", Announcements.AddressToken, "The address friends join with"),
            ("Players online", Announcements.OnlineToken, "How many players are on"),
        })
        {
            var b = new Button { Content = "+ " + label, ToolTip = tip, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 6), FontWeight = FontWeights.Normal, FontSize = 12 };
            b.Click += (_, _) =>
            {
                var at = box.CaretIndex;
                box.Text = box.Text.Insert(at, token);
                box.CaretIndex = at + token.Length;
                box.Focus();
            };
            inserts.Children.Add(b);
        }

        var every = new ComboBox { Width = 170, HorizontalAlignment = HorizontalAlignment.Left };
        System.Windows.Automation.AutomationProperties.SetName(every, "How often");
        foreach (var m in AnnounceIntervals.Union(new[] { a.EveryMinutes }).OrderBy(x => x)) every.Items.Add(new ComboBoxItem { Content = Announcements.Every(m), Tag = m });
        every.SelectedItem = every.Items.Cast<ComboBoxItem>().First(i => (int)i.Tag == a.EveryMinutes);
        var players = new CheckBox { Style = (Style)FindResource("Switch"), Content = "Only when players are online", IsChecked = a.OnlyWithPlayers, Margin = new Thickness(0, 14, 0, 0) };

        var sent = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        sent.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var sendNow = new Button { Content = IconText("", "Send now"), Padding = new Thickness(12, 6, 12, 6), FontWeight = FontWeights.Normal, IsEnabled = _state == ServerState.Running };
        sendNow.ToolTip = _state == ServerState.Running ? "Post it in chat now, to see how it looks" : "Start the server to send it";
        ToolTipService.SetShowOnDisabled(sendNow, true);
        System.Windows.Automation.AutomationProperties.SetName(sendNow, "Send now");
        sendNow.Click += (_, _) =>
        {
            if (box.Text.Trim().Length == 0) return;
            sent.Text = SendAnnouncement(a with { Text = box.Text }, HostConfig.LoadOrNew()) ? "Sent." : "Not sent: the map has no link right now.";
        };

        Announcement? Result() => box.Text.Trim().Length == 0 ? null : a with
        {
            Text = box.Text.Trim(),
            EveryMinutes = (int)((ComboBoxItem)every.SelectedItem).Tag,
            OnlyWithPlayers = players.IsChecked == true,
        };
        var result = ShowOverlay(close =>
        {
            var panel = new StackPanel();
            panel.Children.Add(Heading(existing == null ? "New announcement" : "Announcement", MessageBoxImage.None));
            panel.Children.Add(new TextBlock { Text = "Message", Style = (Style)FindResource("FieldLabel"), Margin = new Thickness(0, 14, 0, 4) });
            panel.Children.Add(tools);
            panel.Children.Add(box);
            panel.Children.Add(inserts);
            panel.Children.Add(new TextBlock { Text = "Players see", Style = (Style)FindResource("FieldLabel"), Margin = new Thickness(0, 8, 0, 0) });
            panel.Children.Add(previewBox);
            panel.Children.Add(new TextBlock { Text = "How often", Style = (Style)FindResource("FieldLabel"), Margin = new Thickness(0, 14, 0, 4) });
            panel.Children.Add(every);
            panel.Children.Add(players);
            panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0), Children = { sendNow, sent } });
            panel.Children.Add(ButtonRow(new (string, Func<object?>, bool)[] { ("Cancel", () => null, true), ("Save", () => Result(), false) }, close));
            Preview();
            return panel;
        }, 520) as Announcement;
        if (result != null) SaveAnnouncement(result);
    }
}
