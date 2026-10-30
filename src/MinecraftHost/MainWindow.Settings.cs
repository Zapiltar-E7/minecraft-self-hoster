using System.Windows;
using MinecraftHost.Core;

namespace MinecraftHost;

// Settings tab: open with Windows, start the server with the app.
public partial class MainWindow
{
    void InitSettings()
    {
        AppStartup.RefreshPath();
        _loading = true;   // setting the switches fires their Checked events
        ChkOpenAtLogin.IsChecked = AppStartup.IsThisCopy;
        ChkAutoStart.IsChecked = HostConfig.Load()?.AutoStartServer == true;
        ChkSounds.IsChecked = HostConfig.Load()?.PlaySounds != false;
        InitAppearance();
        ChkTray.IsChecked = HostConfig.Load()?.TrayOnMinimize != false;
        ChkNotifyPlayers.IsChecked = HostConfig.Load()?.NotifyPlayers != false;
        InitUpdates();
        _loading = false;
        UpdateStartupNote();
    }

    void UpdateStartupNote()
    {
        // another copy of the app (e.g. in another folder) is the one Windows opens
        var other = AppStartup.IsEnabled && !AppStartup.IsThisCopy;
        OpenAtLoginNote.Text = "Another copy of the app opens with Windows now. Switching this on makes it this copy instead.";
        OpenAtLoginNote.Visibility = other ? Visibility.Visible : Visibility.Collapsed;
    }

    void ChkOpenAtLogin_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var on = ChkOpenAtLogin.IsChecked == true;
        try
        {
            // switching off only removes the entry if it's this copy's
            if (on || AppStartup.IsThisCopy) AppStartup.Set(on);
            Log.Host(on ? "The app will open when Windows starts." : "The app won't open when Windows starts.");
        }
        catch (Exception ex)
        {
            Ask("Windows didn't allow the change: " + ex.Message, "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            _loading = true;
            ChkOpenAtLogin.IsChecked = AppStartup.IsThisCopy;
            _loading = false;
        }
        UpdateStartupNote();
    }

    void ChkAutoStart_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var config = HostConfig.LoadOrNew();
        config.AutoStartServer = ChkAutoStart.IsChecked == true;
        config.Save();
    }

    // ------------------------------------------------------------ system tray

    Tray? _tray;
    bool _trayHinted;

    void InitTray()
    {
        _tray = new Tray(this);
        _tray.Open += ShowFromTray;
        _tray.Menu += ShowTrayMenu;
        StateChanged += (_, _) =>
        {
            if (WindowState != WindowState.Minimized || HostConfig.Load()?.TrayOnMinimize == false) return;
            Hide();
            _tray.Show();
            if (!_trayHinted)
            {
                _trayHinted = true;
                _tray.Balloon(TitleText.Text, "Still here: click the icon to open it again, or right-click to start or stop the server.");
            }
        };
        Closed += (_, _) => _tray.Dispose();
        // opened by Windows at sign-in, minimized: straight to the tray
        if (WindowState == WindowState.Minimized && HostConfig.Load()?.TrayOnMinimize != false) { Hide(); _tray.Show(); }
    }

    void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        _tray?.Hide();
    }

    void ShowTrayMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        System.Windows.Controls.MenuItem Item(string header, Action act, bool enabled = true)
        {
            var mi = new System.Windows.Controls.MenuItem { Header = header, IsEnabled = enabled };
            mi.Click += (_, _) => act();
            menu.Items.Add(mi);
            return mi;
        }
        var title = Item($"{TitleText.Text}: {StatusText.Text}", () => { }, false);
        menu.Items.Add(new System.Windows.Controls.Separator());
        Item("Open", ShowFromTray);
        Item(StartLabel.Text, () => { if (_state != ServerState.Stopped) ShowFromTray(); BtnStart_Click(this, new RoutedEventArgs()); }, BtnStart.IsEnabled);
        Item(StopLabel.Text, () => { ShowFromTray(); BtnStop_Click(this, new RoutedEventArgs()); }, BtnStop.IsEnabled);
        menu.Items.Add(new System.Windows.Controls.Separator());
        Item("Quit", () => { ShowFromTray(); Close(); });
        // the menu must be the foreground window, or clicking elsewhere wouldn't close it
        menu.Opened += (_, _) =>
        {
            if (PresentationSource.FromVisual(menu) is System.Windows.Interop.HwndSource src) SetForegroundWindow(src.Handle);
        };
        menu.IsOpen = true;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);

    void ChkNotifyPlayers_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var config = HostConfig.LoadOrNew();
        config.NotifyPlayers = ChkNotifyPlayers.IsChecked == true;
        config.Save();
    }

    void ChkTray_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var config = HostConfig.LoadOrNew();
        config.TrayOnMinimize = ChkTray.IsChecked == true;
        config.Save();
    }

    void ChkSounds_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var config = HostConfig.LoadOrNew();
        config.PlaySounds = ChkSounds.IsChecked == true;
        config.Save();
        if (config.PlaySounds) Sounds.Success();   // a sample, so you hear what it sounds like
    }

    /// <summary>Starts the server on launch when that's switched on and nothing else is using the port.</summary>
    async Task AutoStartServerAsync()
    {
        var config = HostConfig.Load();
        if (config?.AutoStartServer != true || !config.IsSetUp) return;
        if (IsPortInUse(ServerFiles.GetServerPort()))
        {
            Log.Host("Not starting the server automatically: something is already using its port.");
            return;
        }
        Log.Host("Starting the server automatically (Settings > Start the server when the app opens).");
        await StartServerAsync(false);
    }

    // Settings cards: the same column widths as the Tools tab
    void SettingsPage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (SettingsCol0.Children.Count > 0) return;
        // Startup and Sounds side by side; Appearance stays across the full width below them (its colour tiles need the room)
        foreach (var (card, col) in new[] { (StartupCard, SettingsCol0), (SoundsCard, SettingsCol1) })
        {
            (card.Parent as System.Windows.Controls.Panel)?.Children.Remove(card);
            col.Children.Add(card);
        }
        TrimSectionLines();
    }

    /// <summary>A menu colour tile: the theme's colours, with the highlight picked (or its own) as the accent.</summary>
    public sealed record ThemeChoice(string Name, string Group, System.Windows.Media.Brush Bg, System.Windows.Media.Brush Card,
        System.Windows.Media.Brush Border, System.Windows.Media.Brush Text, System.Windows.Media.Brush Muted, System.Windows.Media.Brush Accent);

    /// <summary>A highlight colour swatch; Name null is Auto (the menu colour's own).</summary>
    public sealed record HighlightChoice(string? Name, string Label, System.Windows.Media.Brush Color, Visibility Auto);

    bool _pickingLook;   // the pickers' selection is being set from code

    void InitAppearance()
    {
        FillLookPickers();
        ChkAnimations.IsChecked = Motion.Enabled;
    }

    /// <summary>
    /// Fills both pickers for the current theme and highlight: the tiles show the highlight picked, and Auto's swatch
    /// shows the theme's own, so they're filled again after either changes.
    /// </summary>
    void FillLookPickers()
    {
        static System.Windows.Media.Brush B(System.Windows.Media.Color c) { var b = new System.Windows.Media.SolidColorBrush(c); b.Freeze(); return b; }
        _pickingLook = true;
        var tiles = Themes.All.Select(t =>
        {
            var c = Themes.Resolve(t, Themes.CurrentHighlight);
            return new ThemeChoice(t.Name, t.Light ? "LIGHT" : "DARK", B(c["Bg"]), B(c["Card"]), B(c["CardBorder"]), B(c["Text"]), B(c["Muted"]), B(c["Accent"]));
        }).ToList();
        var view = new System.Windows.Data.ListCollectionView(tiles);
        view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(ThemeChoice.Group)));
        ThemeList.ItemsSource = view;
        ThemeList.SelectedItem = tiles.First(t => t.Name == Themes.Current.Name);

        var own = Themes.Resolve(Themes.Current)["Accent"];
        var swatches = new List<HighlightChoice> { new(null, $"Auto: {Themes.Current.Name}'s own", B(own), Visibility.Visible) };
        swatches.AddRange(Themes.Highlights.Select(h => new HighlightChoice(h.Name, h.Name, B(Themes.Resolve(Themes.Current, h)["Accent"]), Visibility.Collapsed)));
        HighlightList.ItemsSource = swatches;
        HighlightList.SelectedItem = swatches.First(h => h.Name == Themes.CurrentHighlight?.Name);
        HighlightName.Text = Themes.CurrentHighlight?.Name ?? "Auto";
        _pickingLook = false;
    }

    void ThemeList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loading || _pickingLook || ThemeList.SelectedItem is not ThemeChoice choice || choice.Name == Themes.Current.Name) return;
        ApplyLook(choice.Name, Themes.CurrentHighlight?.Name);
    }

    void HighlightList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loading || _pickingLook || HighlightList.SelectedItem is not HighlightChoice choice || choice.Name == Themes.CurrentHighlight?.Name) return;
        ApplyLook(Themes.Current.Name, choice.Name);
    }

    void ApplyLook(string theme, string? highlight)
    {
        Themes.Apply(theme, highlight, animate: true);
        // after the click is done: refilling the list the click came from, inside its own event, upsets its selection
        Dispatcher.BeginInvoke(FillLookPickers);
        var config = HostConfig.LoadOrNew();
        config.Theme = theme;
        config.Highlight = highlight;
        config.Save();
    }

    void ChkAnimations_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Motion.Enabled = ChkAnimations.IsChecked == true;
        var config = HostConfig.LoadOrNew();
        config.Animations = Motion.Enabled;
        config.Save();
    }

    /// <summary>After a theme change: rebuild the cards that work a colour out in code (the Connections tags).</summary>
    void RefreshThemedCards()
    {
        if (ConnectionsPage.Visibility == Visibility.Visible) RebuildConnections();
    }
}
