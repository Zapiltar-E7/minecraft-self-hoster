using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MinecraftHost.Core;

namespace MinecraftHost;

/// <summary>
/// The Connections tab: how players reach the game (playit), plus one entry per plugin or mod that needs a connection of
/// its own, found from what's installed; a list on the left and the picked one's settings on the right. Web pages (maps, Plan) are shared with a Cloudflare link, or a playit TCP tunnel on a
/// paid playit plan; services with their own game port (voice chat, Geyser, Votifier) need a playit tunnel. Tunnel
/// addresses are entered on the cards.
/// </summary>
public partial class MainWindow
{
    List<DetectedService> _services = new();
    // Cloudflare links running now, by service id
    readonly Dictionary<string, (Process Process, string Url)> _webTunnels = new();

    static readonly Regex AddressRx = new(@"^[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)+(:\d+)?$");

    string _connSelected = "minecraft";
    bool _connBuilding;

    /// <summary>Re-reads which services are installed, refills the list and shows the picked one. Cheap once the add-on scan is done.</summary>
    void RebuildConnections() => RebuildConnections(detail: true);

    void RebuildConnections(bool detail)
    {
        _services = _addons == null ? new() : Services.Detect(_addons);
        var config = HostConfig.LoadOrNew();
        PlayitStatus.Text = config.PortForward
            ? $"Forward TCP {ServerFiles.GetServerPort()} on your router to this PC ({Net.LocalIPv4() ?? "its local IP"})."
            : Playit.HasSecret ? "Linked to playit.gg." : "Not linked to playit.gg yet (Tools > Link playit.gg account).";
        BtnPlayitTunnels.Visibility = config.PortForward ? Visibility.Collapsed : Visibility.Visible;
        if (Net.PublicIPv4 == null && (config.PortForward || _services.Any(s => Method(config, s) == ViaForward))) _ = FindPublicAddressAsync();
        _connBuilding = true;
        JoinPlayit.IsChecked = !config.PortForward;
        JoinForward.IsChecked = config.PortForward;
        ConnList.Items.Clear();
        ConnList.Items.Add(ConnItem("minecraft", "Minecraft", $"Java · TCP {ServerFiles.GetServerPort()}", MinecraftStatus(config)));
        foreach (var s in _services)
            ConnList.Items.Add(ConnItem(s.Id, s.Def.Title, $"{(s.Def.Kind == ServiceKind.Web ? "Web page ·" : s.Def.Protocol)} {s.Port}", ServiceStatus(s, config)));
        ConnListNote.Visibility = _services.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ConnList.SelectedItem = ConnList.Items.Cast<ListBoxItem>().FirstOrDefault(i => (string)i.Tag == _connSelected) ?? ConnList.Items[0];
        _connSelected = (string)((ListBoxItem)ConnList.SelectedItem).Tag;
        _connBuilding = false;
        if (detail) ShowConnDetail();
        UpdateTabDots();
    }

    /// <summary>A row of the list: a status dot, the name and its protocol and port.</summary>
    ListBoxItem ConnItem(string id, string title, string detail, (string Text, string Key) status)
    {
        var dot = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = status.Text };
        dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, status.Key);
        DockPanel.SetDock(dot, Dock.Left);
        var sub = new TextBlock { Text = detail, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var item = new ListBoxItem
        {
            Tag = id,
            Content = new DockPanel
            {
                Margin = new Thickness(0, 3, 0, 3),
                Children = { dot, new StackPanel { Children = { new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis }, sub } } },
            },
        };
        System.Windows.Automation.AutomationProperties.SetName(item, title);
        return item;
    }

    void ConnList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_connBuilding || ConnList.SelectedItem is not ListBoxItem { Tag: string id }) return;
        _connSelected = id;
        ShowConnDetail();
        Motion.FadeIn(ConnDetail);
    }

    /// <summary>The right-hand pane: the picked connection's name, what it's for, and its settings.</summary>
    void ShowConnDetail()
    {
        ConnDetail.Children.Clear();
        (ConnectBody.Parent as Panel)?.Children.Remove(ConnectBody);
        var config = HostConfig.LoadOrNew();
        if (_connSelected == "minecraft" || _services.FirstOrDefault(x => x.Id == _connSelected) is not { } s)
        {
            ConnDetail.Children.Add(DetailHeader("Minecraft", (config.PortForward ? "Through your router." : "Through a playit.gg tunnel.") + $" TCP {ServerFiles.GetServerPort()}.", MinecraftStatus(config)));
            ConnectBody.Visibility = Visibility.Visible;
            ConnDetail.Children.Add(ConnectBody);
            return;
        }
        var where = s.Def.Kind == ServiceKind.Web ? $"Web page on port {s.Port}" : $"{s.Def.Protocol} port {s.Port}";
        ConnDetail.Children.Add(DetailHeader(s.Def.Title, $"{s.Def.Purpose} From {s.ProvidedBy}. {where}.", ServiceStatus(s, config)));
        foreach (var part in ServiceParts(s, config)) ConnDetail.Children.Add(part);
    }

    FrameworkElement DetailHeader(string title, string subtitle, (string Text, string Key) status)
    {
        var state = new TextBlock { Text = status.Text, FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        state.SetResourceReference(TextBlock.ForegroundProperty, status.Key is "Accent" or "Warn" ? status.Key : "Muted");
        var dot = new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 1, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, status.Key);
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { dot, state } };
        DockPanel.SetDock(right, Dock.Right);
        var top = new DockPanel { Children = { right, new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold } } };
        return new StackPanel { Margin = new Thickness(0, 0, 0, 14), Children = { top, Muted(subtitle, 2) } };
    }

    // ------------------------------------------------------------ cards

    Brush Res(string key) => (Brush)FindResource(key);

    TextBlock Muted(string text, double top = 0) => new()
    {
        Text = text, Foreground = Res("Muted"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 0),
    };

    /// <summary>Live (working now), Ready (set up, waits for the server), Off, or Needs setup: the dot colour and its word.</summary>
    (string, string) MinecraftStatus(HostConfig c) =>
        ChkTunnel.IsChecked != true ? ("Off", "Idle")
        : !c.PortForward && string.IsNullOrEmpty(c.PlayitAddress) ? ("Needs setup", "Warn")
        : _state == ServerState.Running ? ("Live", "Accent")
        : ("Ready", "Muted");

    (string, string) ServiceStatus(DetectedService s, HostConfig c)
    {
        var method = Method(c, s);
        if (method == ViaForward) return _state == ServerState.Running ? ("Live", "Accent") : ("Ready", "Muted");
        if (method == ViaCloudflare)
            return _webTunnels.ContainsKey(s.Id) ? ("Live", "Accent") : IsShared(c, s.Id) ? ("Ready", "Muted") : ("Off", "Idle");
        c.ServiceAddresses.TryGetValue(s.Id, out var address);
        return string.IsNullOrEmpty(address) ? ("Needs setup", "Warn")
            : _state == ServerState.Running ? ("Live", "Accent")
            : ("Ready", "Muted");
    }

    static T? FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        for (var p = d; p != null; p = VisualTreeHelper.GetParent(p) ?? LogicalTreeHelper.GetParent(p))
            if (p is T t) return t;
        return null;
    }

    Button IconButton(string glyph, string tip, RoutedEventHandler click)
    {
        var b = new Button { Style = (Style)FindResource("IconButton"), Content = glyph, ToolTip = tip };
        b.Click += click;
        DockPanel.SetDock(b, Dock.Right);
        return b;
    }

    TextBlock FieldLabel(string text) => new() { Text = text, Style = (Style)FindResource("FieldLabel") };

    /// <summary>
    /// A service reached through a playit tunnel the user adds (voice chat, Bedrock, votes): the same card as the Minecraft
    /// one. Web pages (maps, Plan) also get a Cloudflare link while the server runs; their playit TCP tunnel needs a paid
    /// playit plan, so that address is optional.
    /// </summary>
    List<UIElement> ServiceParts(DetectedService s, HostConfig config)
    {
        var method = Method(config, s);
        var chosen = new List<UIElement> { MethodChips(s, method) };
        if (method == ViaForward) { chosen.AddRange(ForwardParts(s)); return chosen; }
        if (method == ViaCloudflare) { chosen.AddRange(CloudflareParts(s, config)); return chosen; }
        config.ServiceAddresses.TryGetValue(s.Id, out var address);
        var target = $"127.0.0.1:{s.Port}";

        var box = new TextBox { Style = (Style)FindResource("Input"), FontFamily = (FontFamily)FindResource("Mono"), FontSize = 12.5, Text = address ?? "" };
        var hint = new TextBlock
        {
            Text = "Paste the tunnel's address", Foreground = Res("Muted"), Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false,
            Visibility = string.IsNullOrEmpty(address) ? Visibility.Visible : Visibility.Collapsed,
        };
        box.TextChanged += (_, _) => hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        void Save() => SaveServiceAddress(s, box.Text.Trim());
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Save(); };
        box.LostKeyboardFocus += (_, _) => Save();

        var web = s.Def.Kind == ServiceKind.Web;
        // what friends type in the browser: the tunnel's address as a plain http:// link
        string Link(string a) => a.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? a : "http://" + a;
        var row = new DockPanel();
        if (web)
            row.Children.Add(IconButton("\uE8A7", "Open in your browser", (_, _) =>
                Playit.OpenUrl(box.Text.Trim().Length > 0 ? Link(box.Text.Trim()) : $"http://localhost:{s.Port}")));
        row.Children.Add(IconButton("\uE8C8", web ? "Copy the link" : "Copy", (_, _) =>
        {
            var a = box.Text.Trim();
            if (a.Length > 0) Clipboard.SetText(web ? Link(a) : a);
        }));
        row.Children.Add(new Grid { Children = { box, hint } });

        var open = new Button { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(12, 6, 12, 6), FontWeight = FontWeights.Normal };
        open.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("Icons"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 8, 0) },
                new TextBlock { Text = "Open playit.gg tunnels" },
            },
        };
        open.Click += (_, _) => Playit.OpenUrl("https://playit.gg/account/tunnels");

        var protocol = s.Def.Protocol == "UDP" ? "UDP" : "TCP";
        var help = web
            ? $"A playit TCP tunnel to {target} (paid plan) gives a link that never changes."
            : $"Add a {protocol} tunnel to {target} in playit.gg and paste its address here." +
                   (s.Id == "voicechat" ? " Also sets Simple Voice Chat's voice_host." : "") +
                   (s.Id == "geyser" ? " Bedrock players join with it." : "");
        var parts = chosen;
        parts.Add(FieldLabel(web ? "playit address (paid plan)" : "Public address"));
        parts.Add(row);
        parts.Add(Muted(help, 8));
        parts.Add(open);
        return parts;
    }

    /// <summary>A web page's Cloudflare link: a switch, and the link while the server runs (it changes every start).</summary>
    UIElement[] CloudflareParts(DetectedService s, HostConfig config)
    {
        var shared = IsShared(config, s.Id);
        var running = _webTunnels.TryGetValue(s.Id, out var tunnel);
        var local = $"http://localhost:{s.Port}";

        var toggle = new CheckBox
        {
            Style = (Style)FindResource("Switch"), Margin = new Thickness(0), IsChecked = shared,
            Content = "Share with a link (Cloudflare)", IsEnabled = _state == ServerState.Stopped && _busy == 0,
            ToolTip = "Gives friends a free link to this page. The link changes each time the server starts.",
        };
        var link = new TextBox
        {
            Style = (Style)FindResource("Value"),
            Text = running ? tunnel.Url : shared ? "Link appears when the server starts" : "Sharing is off",
        };
        RoutedEventHandler changed = (_, _) =>
        {
            var c = HostConfig.LoadOrNew();
            var on = toggle.IsChecked == true;
            if (s.Id == "bluemap") c.ShareMap = on;
            else if (on && !c.SharedWeb.Contains(s.Id)) c.SharedWeb.Add(s.Id);
            else if (!on) c.SharedWeb.Remove(s.Id);
            c.Save();
            link.Text = on ? "Link appears when the server starts" : "Sharing is off";   // only this card changes
        };
        toggle.Checked += changed;
        toggle.Unchecked += changed;
        var row = new DockPanel();
        row.Children.Add(IconButton("\uE8A7", running ? "Open in your browser" : $"Open it on this PC ({local})", (_, _) => Playit.OpenUrl(running ? tunnel.Url : local)));
        row.Children.Add(IconButton("\uE8C8", "Copy the link", (_, _) => { if (running) Clipboard.SetText(tunnel.Url); }));
        row.Children.Add(link);

        var parts = new List<UIElement>
        {
            toggle, FieldLabel("Link for friends"), row,
            Muted("Free; changes every server start." + (_state != ServerState.Stopped && !running && shared ? " Starts with the server." : ""), 8),
        };
        if (s.Id == "bluemap" && DiscordTopic.IsAvailable)
        {
            var discord = new CheckBox
            {
                Style = (Style)FindResource("Switch"), Margin = new Thickness(0, 12, 0, 0), IsChecked = config.DiscordMapTopic,
                Content = "Link in Discord",
                ToolTip = "Each new link replaces the \"Live map:\" line in the description of EssentialsX Discord's primary channel. The bot needs the Manage Channels permission there.",
            };
            RoutedEventHandler set = (_, _) =>
            {
                var c = HostConfig.LoadOrNew();
                c.DiscordMapTopic = discord.IsChecked == true;
                c.Save();
                if (c.DiscordMapTopic && running) _ = DiscordTopic.SetMapLinkAsync(tunnel.Url);
            };
            discord.Checked += set;
            discord.Unchecked += set;
            parts.Add(discord);
        }
        return parts.ToArray();
    }

    // ------------------------------------------------------------ how each connection is reached

    const string ViaPlayit = "playit", ViaForward = "forward", ViaCloudflare = "cloudflare";

    /// <summary>
    /// How a plugin's connection is reached: its own choice, or else the Minecraft one (port forwarding, or playit.gg,
    /// which for a web page means its free Cloudflare link).
    /// </summary>
    static string Method(HostConfig c, DetectedService s)
    {
        var web = s.Def.Kind == ServiceKind.Web;
        if (c.ServiceMethods.TryGetValue(s.Id, out var m) && (m is ViaPlayit or ViaForward || m == ViaCloudflare && web)) return m;
        return c.PortForward ? ViaForward : web ? ViaCloudflare : ViaPlayit;
    }

    /// <summary>The choice at the top of a connection's card: Cloudflare (web pages), playit.gg or port forwarding.</summary>
    UIElement MethodChips(DetectedService s, string method)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(-2, 0, 0, 14) };
        var choices = new List<(string Id, string Label, string Tip)>();
        if (s.Def.Kind == ServiceKind.Web) choices.Add((ViaCloudflare, "Cloudflare link", "A free link that changes each time the server starts. No account or router setup."));
        choices.Add((ViaPlayit, "playit.gg", s.Def.Kind == ServiceKind.Web ? "A playit TCP tunnel: a link that stays the same (paid plan)." : "A playit tunnel. Nothing to set up on your router."));
        choices.Add((ViaForward, "Port forwarding", "You forward the port on your router. No playit.gg or Cloudflare needed."));
        foreach (var (id, label, tip) in choices)
        {
            var chip = new RadioButton { Style = (Style)FindResource("FilterChip"), Content = label, ToolTip = tip, GroupName = "via-" + s.Id, IsChecked = id == method };
            chip.Checked += (_, _) => SetMethod(s, id);   // after IsChecked is set, so building the card doesn't save anything
            row.Children.Add(chip);
        }
        return row;
    }

    void SetMethod(DetectedService s, string method)
    {
        if (_connBuilding) return;
        var config = HostConfig.LoadOrNew();
        if (Method(config, s) == method) return;
        config.ServiceMethods[s.Id] = method;
        config.Save();
        Log.Host($"{s.Def.Title}: reached through {(method == ViaForward ? "port forwarding" : method == ViaCloudflare ? "a Cloudflare link" : "playit.gg")}.");
        // voice chat tells players where to connect: the playit address, or (port forwarding) the server's own address
        if (s.Id == "voicechat")
        {
            config.ServiceAddresses.TryGetValue(s.Id, out var address);
            var host = method == ViaPlayit ? address ?? "" : "";
            if (Services.SetVoiceHost(s, host))
                Log.Host($"Set voice_host={host} in Simple Voice Chat's settings" + (_state != ServerState.Stopped ? "; restart the server to use it." : "."));
        }
        // after the click has finished, since the card is rebuilt
        Dispatcher.BeginInvoke(() => { RebuildConnections(); Motion.FadeIn(ConnDetail); }, System.Windows.Threading.DispatcherPriority.Background);
    }

    static bool IsShared(HostConfig config, string id) => id == "bluemap" ? config.ShareMap : config.SharedWeb.Contains(id);

    void SaveServiceAddress(DetectedService s, string address)
    {
        var config = HostConfig.LoadOrNew();
        config.ServiceAddresses.TryGetValue(s.Id, out var old);
        if ((old ?? "") == address) return;
        if (address.Length > 0 && !AddressRx.IsMatch(address))
        {
            Ask($"'{address}' doesn't look like a playit address (for example name.gl.at.ply.gg:12345).", s.Def.Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (address.Length > 0) config.ServiceAddresses[s.Id] = address;
        else config.ServiceAddresses.Remove(s.Id);
        config.Save();
        Log.Host(address.Length > 0 ? $"{s.Def.Title}: public address set to {address}." : $"{s.Def.Title}: public address cleared.");
        if (s.Id == "voicechat")
        {
            if (Services.SetVoiceHost(s, address))
                Log.Host($"Set voice_host={address} in Simple Voice Chat's settings" + (_state != ServerState.Stopped ? "; restart the server to use it." : "."));
            else
                Log.Host("Simple Voice Chat hasn't made its settings file yet. Start the server once, then enter the address again.");
        }
        // after the click that moved focus away (e.g. on Copy) has finished, since the cards are rebuilt
        Dispatcher.BeginInvoke(() => RebuildConnections(), System.Windows.Threading.DispatcherPriority.Background);
    }

    void BtnPlayitTunnels_Click(object sender, RoutedEventArgs e) => Playit.OpenUrl("https://playit.gg/account/tunnels");

    // ------------------------------------------------------------ web links while the server runs

    /// <summary>Opens a Cloudflare link for every shared web page of the installed plugins and mods.</summary>
    async Task StartWebTunnelsAsync()
    {
        var config = HostConfig.LoadOrNew();
        var shared = _services.Where(s => s.Def.Kind == ServiceKind.Web && Method(config, s) == ViaCloudflare && IsShared(config, s.Id)).ToList();
        if (shared.Count == 0) return;
        Cloudflared.StopStale();
        foreach (var s in shared)
        {
            var link = await Cloudflared.StartAsync(s.Port, s.Def.Title, isMap: s.Id == "bluemap");
            if (link == null) continue;
            _webTunnels[s.Id] = link.Value;
            if (s.Id == "bluemap" && config.DiscordMapTopic) _ = DiscordTopic.SetMapLinkAsync(link.Value.Url);
        }
        RefreshConnectionsIfShown();
    }

    /// <summary>Rebuilds the open Connections tab when its links or the server state changed (switches only work while stopped).</summary>
    void RefreshConnectionsIfShown()
    {
        var shown = $"{_state}|{_busy > 0}|{string.Join(",", _webTunnels.Keys)}";
        if (ConnectionsPage?.Visibility != Visibility.Visible || shown == _connShown) return;
        _connShown = shown;
        RebuildConnections();
    }

    string _connShown = "";

    void StopWebTunnels(bool alreadyKilled = false)
    {
        if (_webTunnels.Count == 0) return;
        if (!alreadyKilled)
            foreach (var (process, _) in _webTunnels.Values)
                try { process.Kill(); } catch { }
        _webTunnels.Clear();
        Log.Host("Web page sharing stopped.");
        RefreshConnectionsIfShown();
    }

    // ------------------------------------------------------------ notification dots on the tabs

    /// <summary>
    /// Plugins / Mods: an update is available there. Connections: a plugin or mod added a connection that hasn't been looked at yet.
    /// </summary>
    void UpdateTabDots()
    {
        if (DotPlugins == null) return;
        DotPlugins.Visibility = _addons?.Any(a => a.Kind != AddonKind.Mod && a.Update != null) == true ? Visibility.Visible : Visibility.Collapsed;
        DotMods.Visibility = _addons?.Any(a => a.Kind == AddonKind.Mod && a.Update != null) == true ? Visibility.Visible : Visibility.Collapsed;
        var seen = HostConfig.Load()?.SeenServices ?? new();
        DotConnections.Visibility = _services.Any(s => !seen.Contains(s.Id)) ? Visibility.Visible : Visibility.Collapsed;
    }

    async Task OpenConnectionsTabAsync()
    {
        if (_addons == null) await ScanAddonsAsync();
        RebuildConnections();
        // looking at the tab counts as seeing the new connections
        var config = HostConfig.LoadOrNew();
        var fresh = _services.Select(s => s.Id).Where(id => !config.SeenServices.Contains(id)).ToList();
        if (fresh.Count == 0) return;
        config.SeenServices.AddRange(fresh);
        config.Save();
        UpdateTabDots();
    }
}
