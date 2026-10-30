using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MinecraftHost.Core;

namespace MinecraftHost;

public partial class MainWindow : Window
{
    enum ServerState { Stopped, Starting, Running, Stopping }

    ServerState _state = ServerState.Stopped;
    int _busy;                      // background tasks in progress
    MinecraftServer? _server;
    Process? _tunnel;               // playit agent we started (null if reused or off)
    bool _closeAfterStop;
    bool _restartAfterStop;         // Restart: start again once the server has exited
    bool _lastStartSafe;            // ...in the same mode as before
    DateTime _startedAt;            // crash reports newer than this belong to the current start
    bool _forceStopping;            // Force stop killed it: not a crash
    bool _loading;
    readonly SortedSet<string> _players = new(StringComparer.OrdinalIgnoreCase);
    readonly DispatcherTimer _logTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };

    // Chat plugins (EssentialsX) replace "X joined the game" / "X left the game", so the server's own connection lines count
    // too: "X[/1.2.3.4:5678] logged in with entity id 67" and "X lost connection: Disconnected".
    static readonly Regex JoinedRx = new(@"\]: ([A-Za-z0-9_.]{1,16})(?: joined the game|\[/[^\]]*\] logged in with entity id)", RegexOptions.Compiled);
    static readonly Regex LeftRx = new(@"\]: ([A-Za-z0-9_.]{1,16}) (?:left the game|lost connection:)", RegexOptions.Compiled);

    public MainWindow()
    {
        var look = HostConfig.Load();
        Motion.Enabled = look?.Animations != false;
        Themes.Apply(look?.Theme, look?.Highlight, animate: false);
        InitializeComponent();
        Themes.Changed += () => { DarkTitleBar.Apply(this); UpdateView(); RefreshThemedCards(); };
        // the open page's highlight and the open section's underline slide to the one picked
        Motion.Track(NavMarker, NavLayer, NavButtons, underline: false);
        Motion.Track(SecMarker, SecLayer, new[] { SecGeneral, SecPlayers, SecWorld, SecUpkeep }, underline: true);
        Motion.Track(CfgMarker, CfgLayer, new[] { CfgServer, CfgPlugins, CfgDatapacks, CfgMods }, underline: true);
        _logTimer.Tick += (_, _) => DrainLog();
        _logTimer.Start();

        var config = HostConfig.Load();
        _loading = true;   // the switches' Checked events fire here; don't save config for these
        ChkTunnel.IsChecked = config?.UseTunnel ?? true;
        _loading = false;

        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        // opened by Windows at sign-in: stay out of the way on the taskbar
        if (Environment.GetCommandLineArgs().Contains(AppStartup.Flag)) WindowState = WindowState.Minimized;
        Activated += (_, _) => UpdateServerIcon();   // picks up a server-icon.png added or changed meanwhile
        Loaded += async (_, _) =>
        {
            UpdateServerIcon();
            InitConfigTab();
            InitServerCards();
            InitSettings();
            InitPerformance();
            InitTray();
            UpdateJoinRequests();
            LogWeeklySummary();
            CheckDiskSpace(force: true);
            _ = NamePlayersAsync();
            Log.Host($"Server folder: {Paths.ServerDir}");
            Log.Host("Ready.");
            UpdateView();
            if (config?.IsSetUp == true && IsPortInUse(ServerFiles.GetServerPort())) await OfferStopLeftoverAsync();
            await RefreshSetupBannerAsync();
            _ = CheckForUpdatesAsync(quiet: true);   // in the background, for the dots on the Plugins / Mods / Connections tabs
            await AutoStartServerAsync();
        };
    }

    // ------------------------------------------------------------ editable name

    const string DefaultName = "Minecraft Server Host";

    void TitleText_Click(object sender, MouseButtonEventArgs e)
    {
        TitleEdit.Text = TitleText.Text;
        TitleText.Visibility = Visibility.Hidden;   // hidden, not collapsed, so the header doesn't jump
        TitleEdit.Visibility = Visibility.Visible;
        TitleEdit.Focus();
        TitleEdit.SelectAll();
    }

    void TitleEdit_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) EndTitleEdit(save: true);
        else if (e.Key == Key.Escape) EndTitleEdit(save: false);
    }

    // clicking empty space doesn't move keyboard focus, so finish the edit on any click outside the box
    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        if (TitleEdit.IsVisible && !TitleEdit.IsMouseOver) EndTitleEdit(save: true);
        base.OnPreviewMouseDown(e);
    }

    void TitleEdit_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => EndTitleEdit(save: true);

    void EndTitleEdit(bool save)
    {
        if (TitleEdit.Visibility != Visibility.Visible) return;
        TitleEdit.Visibility = Visibility.Collapsed;
        TitleText.Visibility = Visibility.Visible;
        if (save)
        {
            var name = TitleEdit.Text.Trim();
            var config = HostConfig.LoadOrNew();
            var oldName = Paths.ServerName;
            config.Name = name.Length == 0 || name == DefaultName ? null : name;   // empty resets to the default
            config.Save();
            if (Paths.ServerName != oldName) RenameServerFolders(oldName);
        }
        UpdateView();
    }

    // ------------------------------------------------------------ tabs

    UIElement? _shownPage;
    int _navIndex;   // the open sidebar button, so the next page slides in from the side it's on

    /// <summary>The sidebar's page buttons, top to bottom.</summary>
    RadioButton[] NavButtons => new[] { TabServer, TabLog, TabConnections, TabAddons, TabMods, TabConfigs, TabTools, TabSettings };
    string? _iconShown;

    /// <summary>The header icon: the current server's server-icon.png (what players see in their server list), else the creeper.</summary>
    void UpdateServerIcon()
    {
        var file = Path.Combine(Paths.ServerDir, "server-icon.png");
        DateTime stamp;
        try { stamp = File.Exists(file) ? File.GetLastWriteTimeUtc(file) : default; } catch { stamp = default; }
        var key = stamp == default ? "app" : file + "|" + stamp.Ticks;
        if (key == _iconShown) return;
        _iconShown = key;
        ImageSource source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/app.png"));
        if (stamp != default)
        {
            try
            {
                var image = new System.Windows.Media.Imaging.BitmapImage();
                image.BeginInit();
                image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;   // don't keep the file locked
                image.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreImageCache;
                image.UriSource = new Uri(file);
                image.EndInit();
                image.Freeze();
                source = image;
            }
            catch { }   // not a readable image: keep the creeper
        }
        ServerIcon.Source = source;
        ServerIconMask.ImageSource = source;
        ServerIcon.ToolTip = stamp == default ? null : "This server's icon (server-icon.png)";
        if (IconPreview != null) UpdateIconPreview();
    }

    void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (ServerPage == null || ConfigPage == null || AddonsPage == null || ToolsPage == null || LogPage == null || SettingsPage == null || ConnectionsPage == null) return;   // fires during InitializeComponent
        var configs = TabConfigs.IsChecked == true;
        var plugins = TabAddons.IsChecked == true;
        var mods = TabMods.IsChecked == true;
        var addons = plugins || mods;   // one page, filtered to plugins & datapacks or to mods
        var tools = TabTools.IsChecked == true;
        var log = TabLog.IsChecked == true;
        var settings = TabSettings.IsChecked == true;
        var connections = TabConnections.IsChecked == true;
        ServerPage.Visibility = !configs && !addons && !tools && !log && !settings && !connections ? Visibility.Visible : Visibility.Collapsed;
        ConnectionsPage.Visibility = connections ? Visibility.Visible : Visibility.Collapsed;
        if (connections) _ = OpenConnectionsTabAsync();
        SettingsPage.Visibility = settings ? Visibility.Visible : Visibility.Collapsed;
        ConfigPage.Visibility = configs ? Visibility.Visible : Visibility.Collapsed;
        AddonsPage.Visibility = addons ? Visibility.Visible : Visibility.Collapsed;
        ToolsPage.Visibility = tools ? Visibility.Visible : Visibility.Collapsed;
        LogPage.Visibility = log ? Visibility.Visible : Visibility.Collapsed;
        PageTitle.Text = configs ? "Configs" : plugins ? "Plugins" : mods ? "Mods" : tools ? "Tools" : log ? "Logs"
                       : settings ? "Settings" : connections ? "Connections" : "Server";
        // the new page (and its title) slides in from the side of the sidebar it was picked towards
        var index = Array.IndexOf(NavButtons, sender);
        if (index >= 0 && index != _navIndex)
        {
            var dy = index > _navIndex ? 14 : -14;
            _navIndex = index;
            foreach (var page in new UIElement[] { ServerPage, ConnectionsPage, SettingsPage, ConfigPage, AddonsPage, ToolsPage, LogPage })
                if (page.Visibility == Visibility.Visible) { _shownPage = page; Motion.PageIn(page, 0, dy); }
            Motion.PageIn(PageTitle, 0, dy / 2);
        }
        if (configs)
        {
            if (!_configsLoaded) LoadConfigList();
            if (_addons == null) _ = ScanAddonsAsync();   // for the status dots
        }
        else if (addons)
        {
            SetAddonsMode(mods);
            _ = OpenAddonsTabAsync();                      // re-read each time: files or the log may have changed
        }
        else if (log) Dispatcher.BeginInvoke(ScrollLogToEnd, DispatcherPriority.Loaded);
    }

    // Server tab: the open section's cards, in two columns when there's room, otherwise one
    void ServerPage_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutServerCards(animate: false);

    void ServerSection_Checked(object sender, RoutedEventArgs e)
    {
        if (ServerCol0 == null) return;   // fires during InitializeComponent
        LayoutServerCards(animate: true);
    }

    bool? _serverTwo;
    RadioButton? _serverLaidOut;

    void LayoutServerCards(bool animate)
    {
        var section = SecPlayers.IsChecked == true ? SecPlayers : SecWorld.IsChecked == true ? SecWorld : SecUpkeep.IsChecked == true ? SecUpkeep : SecGeneral;
        var two = ServerCards.ActualWidth >= 640 || ServerCards.ActualWidth == 0;
        if (two == _serverTwo && section == _serverLaidOut) return;
        var changed = section != _serverLaidOut;
        var sections = new[] { SecGeneral, SecPlayers, SecWorld, SecUpkeep };
        var dx = _serverLaidOut == null || Array.IndexOf(sections, section) > Array.IndexOf(sections, _serverLaidOut) ? 18 : -18;
        _serverTwo = two;
        _serverLaidOut = section;
        var (left, right) = section == SecPlayers ? (new FrameworkElement[] { PlayersCard, WeekCard }, new FrameworkElement[] { WhitelistCard, OpsCard })
            : section == SecWorld ? (new FrameworkElement[] { ListingCard }, new FrameworkElement[] { GameplayCard })
            : section == SecUpkeep ? (new FrameworkElement[] { RestartCard }, new FrameworkElement[] { BackupCard })
            : (new FrameworkElement[] { SoftwareCard }, new FrameworkElement[] { MemoryCard, FolderCard });
        foreach (var card in new FrameworkElement[] { SoftwareCard, MemoryCard, FolderCard, PlayersCard, WeekCard, WhitelistCard, OpsCard, ListingCard, GameplayCard, RestartCard, BackupCard })
            (card.Parent as Panel)?.Children.Remove(card);
        foreach (var card in left) ServerCol0.Children.Add(card);
        foreach (var card in right) (two ? ServerCol1 : ServerCol0).Children.Add(card);
        ServerGap1.Width = new GridLength(two ? 12 : 0);
        ServerCol1Width.Width = two ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        TrimSectionLines();
        if (changed)
        {
            ServerCards.ScrollToTop();
            if (animate) Motion.PageIn(ServerCards, dx, 0);   // from the side of the section tab picked
        }
    }

    // ------------------------------------------------------------ section lines

    readonly HashSet<Border> _sections = new();

    /// <summary>
    /// A section's hairline separates it from the one below, so the last shown section of each column has none.
    /// Run after sections are dealt into columns; a section showing or hiding runs it again.
    /// </summary>
    void TrimSectionLines()
    {
        var style = (Style)FindResource("CardStyle");
        void Walk(DependencyObject node)
        {
            if (node is Panel panel)
            {
                var cards = panel.Children.OfType<Border>().Where(b => b.Style == style).ToList();
                foreach (var card in cards)
                    if (_sections.Add(card)) card.IsVisibleChanged += (_, _) => Dispatcher.BeginInvoke(TrimSectionLines, DispatcherPriority.Loaded);
                var last = cards.LastOrDefault(c => c.Visibility == Visibility.Visible);
                foreach (var card in cards) card.BorderThickness = new Thickness(0, 0, 0, card == last ? 0 : 1);
            }
            foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()) Walk(child);
        }
        Walk(MainContent);
    }

    // Tools tab cards: three columns when there's room, otherwise two
    void ToolsPage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var three = ToolsPage.ActualWidth >= 900;
        if (three == (ToolsCol2.Children.Count > 0)) return;
        foreach (var col in new[] { ToolsCol0, ToolsCol1, ToolsCol2 }) col.Children.Clear();
        ToolsCol0.Children.Add(ToolsPcCard);
        ToolsCol1.Children.Add(ToolsServerCard);
        (three ? ToolsCol2 : ToolsCol1).Children.Add(ToolsMoveCard);
        ToolsGap2.Width = new GridLength(three ? 12 : 0);
        ToolsCol2Width.Width = three ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        TrimSectionLines();
    }

    // ------------------------------------------------------------ set up this PC

    async Task RefreshSetupBannerAsync()
    {
        try
        {
            var missing = (await Requirements.CheckAsync()).Where(i => !i.Ok && !i.Optional).Select(i => i.Title).ToList();
            SetupBanner.Visibility = missing.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            SetupBannerText.Text = "Missing: " + string.Join(", ", missing) + ".";
        }
        catch { SetupBanner.Visibility = Visibility.Collapsed; }
    }

    async void BtnRequirements_Click(object sender, RoutedEventArgs e)
    {
        if (_state != ServerState.Stopped || _busy > 0)
        {
            Ask("Stop the server first.", "Set up this PC", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new RequirementsPanel();
        ShowOverlay(close => { dlg.Close = close; return dlg; }, 580);
        UpdateView();
        if (dlg.Next == RequirementsPanel.NextStep.ChooseFolder) BtnChooseFolder_Click(sender, e);
        else if (dlg.Next == RequirementsPanel.NextStep.InstallServer) ShowSoftwareCard();
        await RefreshSetupBannerAsync();
    }

    // ------------------------------------------------------------ force stop

    void BtnKill_Click(object sender, RoutedEventArgs e)
    {
        var targets = ForceStop.FindTargets(_server?.IsRunning == true ? _server.Process : null);
        if (targets.Count == 0)
        {
            Ask("Nothing to stop: no server or tunnel from this app is running.", "Force stop", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var list = string.Join("\n", targets.Select(t => $"   •  {t.Description}  (PID {t.Pid})"));
        if (Ask($"Force stop these right now?\n\n{list}\n\nThe server is killed without saving, so changes since its last autosave (up to 5 minutes) can be lost. " +
                "Use Stop instead if the server still responds.", "Force stop", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        _forceStopping = _server != null;   // ours reports its exit; that isn't a crash
        ForceStop.Kill(targets);
        _restartAfterStop = false;
        _tunnel = null;        // already killed; nothing left for StopHelpers to do
        StopWebTunnels(alreadyKilled: true);
        if (_server == null)   // a server this app lost track of; ours reports through its Exited event
        {
            _state = ServerState.Stopped;
            _players.Clear();
        }
        UpdateView();
    }

    // ------------------------------------------------------------ console + players

    void DrainLog()
    {
        var any = false;
        var playersChanged = false;
        var follow = LogAtEnd();
        for (var n = 0; n < 500 && Log.Lines.TryDequeue(out var line); n++)
        {
            AddLogLine(line);
            any = true;
            // both the connection line and a join message can appear: only the first one counts
            var m = JoinedRx.Match(line);
            if (m.Success)
            {
                if (PlayerJoined(m.Groups[1].Value)) playersChanged = true;
                continue;
            }
            // "lost connection" is also how a whitelist refusal ends, for someone who never got in
            m = LeftRx.Match(line);
            if (m.Success && _players.Contains(m.Groups[1].Value))
            {
                PlayerLeft(m.Groups[1].Value);
                playersChanged = true;
                continue;
            }
            CheckTurnedAway(line);
        }
        if (playersChanged) UpdatePlayers();
        if (!any) return;
        TrimLog();
        if (follow) ScrollLogToEnd();
    }

    bool PlayerJoined(string name)
    {
        if (!_players.Add(name)) return false;
        PlayerHistory.Joined(name);
        NotifyPlayer(name, joined: true);
        return true;
    }

    void PlayerLeft(string name)
    {
        if (!_players.Remove(name)) return;
        PlayerHistory.Left(name);
        NotifyPlayer(name, joined: false);
    }

    void UpdateView()
    {
        var config = HostConfig.Load();
        var running = _state != ServerState.Stopped;

        var color = _state switch
        {
            ServerState.Running => "Accent",
            ServerState.Starting or ServerState.Stopping => "Warn",
            _ => _busy > 0 ? "Warn" : "Idle",
        };
        StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, color);
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, color);
        StatusPill.Background = new SolidColorBrush(Themes.WithAlpha(Themes.Get(color), 0x2A));
        StatusText.Text = _busy > 0 && !running ? "Working..." : _state == ServerState.Running ? $"Running · {_players.Count} online" : _state.ToString();
        Title = TitleText.Text = string.IsNullOrWhiteSpace(config?.Name) ? DefaultName : config!.Name!;
        if (MotdName != null) MotdName.Text = TitleText.Text;   // the server list preview
        InfoText.Text = config?.IsSetUp == true
            ? $"{config.Edition} {config.Version}" + (config.Modpack is { } pack ? $"   ·   {pack}" : "") + $"   ·   {config.RamGB} GB RAM   ·   Java {config.RequiredJava}+"
            : "No server set up yet - install one under Server software, or choose a server folder";

        var idle = !running && _busy == 0;
        var canRestart = _state == ServerState.Running && _server != null && !_restartAfterStop;
        StartLabel.Text = canRestart ? "Restart" : "Start";
        StartIcon.Text = canRestart ? "" : "";
        BtnStart.ToolTip = canRestart ? "Stop the server safely (saving the worlds) and start it again" : null;
        BtnStart.IsEnabled = canRestart || idle;
        BtnSafe.IsEnabled = BtnLink.IsEnabled =
            BtnChooseFolder.IsEnabled = BtnRequirements.IsEnabled = BtnExport.IsEnabled = BtnImport.IsEnabled =
            BtnModpackSoftware.IsEnabled = idle;
        ChkTunnel.IsEnabled = idle;
        BtnStop.IsEnabled = _state is ServerState.Starting or ServerState.Running && _server != null || _crashRestartPending;
        if (_countdownEnd == null) StopLabel.Text = "Stop";
        BtnSend.IsEnabled = TxtCommand.IsEnabled = _server?.IsRunning == true;
        TxtCommandHint.Text = _server?.IsRunning == true ? "Type a server command, e.g. op YourName (no slash needed)" : "Start the server to send commands";

        TxtFriend.Text = FriendAddress(config) ?? (config?.PortForward == true ? "Finding your public address..." : "Not set - use Tools > Set public address");

        TxtFolder.Text = Paths.ServerDir;
        TxtFolder.ToolTip = Paths.ServerDir;
        TxtJar.Text = File.Exists(Paths.ServerJar) ? $"Runs {Paths.ServerJarName}" : "No server jar in this folder yet";
        UpdatePlayers();
        UpdateSoftwareCard();
        UpdateBackupStatus();
        UpdateProfileButton();
        _tray?.SetTip($"{TitleText.Text}: {StatusText.Text}" + (_state == ServerState.Running ? $", {_players.Count} online" : ""));
        RefreshConnectionsIfShown();
        RefreshPlayerLists();
        UpdateAddonControls();
    }

    /// <summary>Runs work with the busy indicator on; errors go to the log.</summary>
    async Task<bool> RunBusyAsync(Func<Task> work)
    {
        _busy++; UpdateView();
        try { await work(); return true; }
        catch (Exception ex) { Log.Host("ERROR: " + ex.Message); return false; }
        finally { _busy--; UpdateView(); }
    }

    static bool IsPortInUse(int port)
    {
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync("127.0.0.1", port).Wait(300) && client.Connected;
        }
        catch { return false; }
    }


    // ------------------------------------------------------------ start / stop

    async Task StartServerAsync(bool safe)
    {
        _crashRestartPending = false;
        var config = HostConfig.Load();
        if (config?.IsSetUp != true)
        {
            Ask("No server is set up in the server folder yet.\n\nInstall one under Server software on the Server tab, or choose a folder that already has a server.",
                "Not set up", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!ServerFiles.EulaAccepted())
        {
            if (Ask("This server hasn't accepted the Minecraft EULA (https://aka.ms/MinecraftEULA).\n\nDo you accept the Minecraft EULA?",
                    "Minecraft EULA", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            ServerFiles.AcceptEula();
        }

        var useTunnel = ChkTunnel.IsChecked == true && !config.PortForward;   // port forwarding: the router does it
        var backup = false;
        if (safe)
        {
            var answer = Ask("Safe mode loads the world with the vanilla datapack only" + (config.Edition is "Paper" or "Purpur" ? " and no plugins" : "") +
                ".\n\nContent from datapacks may be lost if the world is saved in safe mode.\n\nBack up your worlds first?",
                "Safe mode", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
            if (answer == MessageBoxResult.Cancel) return;
            backup = answer == MessageBoxResult.Yes;
            if (useTunnel) useTunnel = Ask("Open the playit tunnel so friends can join while in safe mode?", "Safe mode", MessageBoxButton.YesNo) == MessageBoxResult.Yes;
        }
        var shareWeb = !safe || useTunnel;
        if (_addons == null) await ScanAddonsAsync();   // which plugins have web pages to share
        _services = Services.Detect(_addons ?? new());

        _state = ServerState.Starting;
        _lastStartSafe = safe;
        _players.Clear();
        Log.Host("Preparing to start" + (safe ? " in SAFE MODE" : "") + "...");
        string? java = null;
        var ok = await RunBusyAsync(async () =>
        {
            if (backup) await Task.Run(ServerFiles.BackupWorlds);
            java = await Task.Run(() => Java.Find(config.RequiredJava));
            if (java == null) return;
            if (useTunnel)
            {
                await Playit.EnsureInstalledAsync();
                if (!Playit.HasSecret) await Playit.ClaimAsync();
                _tunnel = Playit.Start();
            }
            if (shareWeb) await StartWebTunnelsAsync();
        });

        if (!ok || java == null)
        {
            _state = ServerState.Stopped;
            StopHelpers();
            UpdateView();
            if (ok && java == null) await OfferJavaInstallAsync(config.RequiredJava);
            return;
        }

        try
        {
            _server = new MinecraftServer();
            _server.Ready += () => Dispatcher.BeginInvoke(() =>
            {
                _state = ServerState.Running;
                Log.Host("Server is ready. Type commands below (e.g. \"op YourName\").");
                Sounds.Success();
                UpdateView();
                ScheduleNextRestart();
                if (!IsVisible) _tray?.Balloon(TitleText.Text, "The server is online.");
                ResetPerformance();
            });
            _server.Exited += code => Dispatcher.BeginInvoke(() => OnServerExited(code));
            _startedAt = DateTime.Now;
            PrepareStats();
            _server.Start(java, config, safe);
        }
        catch (Exception ex)
        {
            Log.Host("ERROR: could not start Java: " + ex.Message);
            _server = null;
            _state = ServerState.Stopped;
            StopHelpers();
        }
        UpdateView();
    }

    void OnServerExited(int code)
    {
        DrainLog();
        Log.Host($"Server stopped (exit code {code}).");
        // nobody asked it to stop: it failed while starting, or crashed (an in-game /stop exits with 0)
        var unexpected = !_forceStopping && (_state == ServerState.Starting || (_state == ServerState.Running && code != 0));
        _forceStopping = false;
        var failedStart = unexpected && _state == ServerState.Starting;
        if (unexpected)
        {
            Log.Host("The server shut down unexpectedly. The lines above show why.");
            Sounds.Error();
            if (!failedStart) Stats.Crash();
            if (!IsVisible) _tray?.Balloon(TitleText.Text, "The server shut down unexpectedly.");
        }
        _nextRestart = null;
        _server = null;
        _state = ServerState.Stopped;
        _players.Clear();
        PlayerHistory.AllLeft();
        StopStats();
        SyncWorldCard();   // a first start writes server.properties
        StopHelpers();
        UpdateView();
        if (_closeAfterStop) Close();
        else if (_restartAfterStop)
        {
            _restartAfterStop = false;
            if (code == 0) _ = StartAgainAsync();
            else { _scheduledRestart = false; Log.Host("Not restarting, because the server didn't stop cleanly."); }
        }
        else if (unexpected && !failedStart) MaybeRestartAfterCrash();
        else if (failedStart && Editions.RunsMods(HostConfig.Load()?.Edition) && Modpacks.FindCrashingMods(Paths.ServerDir, _startedAt) is { Count: > 0 } culprits)
            _ = Dispatcher.BeginInvoke(() => OfferSwitchOffCrashedMods(culprits));
        else SyncServerFolderName();   // a rename while it ran
    }

    void StopServer()
    {
        if (_server?.IsRunning != true) return;
        _state = ServerState.Stopping;
        _countdownEnd = null;
        Log.Host("Stopping the server (saving worlds)...");
        try { _server.Send("stop"); }
        catch (Exception ex)
        {
            // the console pipe broke: ask over RCON instead
            Log.Host("Couldn't reach the server's console (" + ex.Message + "); stopping it over RCON.");
            _ = Task.Run(async () =>
            {
                if (int.TryParse(PlayerLists.GetProperty("rcon.port"), out var port) && PlayerLists.GetProperty("rcon.password") is { Length: > 0 } pw
                    && await Rcon.ConnectAsync(port, pw) is { } rcon)
                    using (rcon) await rcon.CommandAsync("stop");
                else Log.Host("RCON didn't answer either. Use Force stop.");
            });
        }
        UpdateView();
    }

    void StopHelpers()
    {
        if (_tunnel != null)
        {
            try { _tunnel.Kill(); } catch { }
            _tunnel = null;
            Log.Host("playit tunnel stopped.");
        }
        StopWebTunnels();
    }

    async Task OfferJavaInstallAsync(int major)
    {
        if (Ask($"This Minecraft version needs Java {major} or newer, and none was found.\n\n" +
                $"Download Eclipse Temurin Java {major} now? It goes into the app's tools folder (about 50 MB) and needs no admin rights.",
                "Java needed", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        if (await RunBusyAsync(() => Java.InstallPortableAsync(major))) Log.Host("Java is ready. Click Start.");
        await RefreshSetupBannerAsync();
    }

    // ------------------------------------------------------------ server folder

    async void BtnChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose the Minecraft server folder",
            InitialDirectory = Directory.Exists(Paths.ServerDir) ? Paths.ServerDir : Paths.Root,
        };
        if (dlg.ShowDialog(this) != true) return;
        var dir = dlg.FolderName;

        var info = ServerFiles.InspectFolder(dir);
        var config = HostConfig.LoadOrNew();
        if (info.JarName == null)
        {
            if (Ask($"No Minecraft server was found in:\n{dir}\n\nUse this folder anyway? You can then install a server into it under Server software.",
                    "Server folder", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            config.Edition = null;
            config.Version = null;
            config.ServerJar = null;
        }
        else
        {
            config.ServerJar = info.JarName;
            config.Edition = info.Edition;
            config.Version = info.Version ?? "unknown";
            config.RequiredJava = info.JavaMajor ?? config.RequiredJava;
        }
        config.ServerDir = Path.GetFullPath(dir).TrimEnd('\\').Equals(Paths.DefaultServerDir, StringComparison.OrdinalIgnoreCase) ? null : dir;
        config.Save();
        _players.Clear();
        ResetConfigs();
        Log.Host($"Server folder set to {dir}");
        if (info.JarName != null) Log.Host($"Found {info.Edition} {info.Version ?? "(unknown version)"} in {info.JarName}.");

        // older jars don't say which Java they need; ask Mojang
        if (info.JarName != null && info.JavaMajor == null && info.Version != null)
            await RunBusyAsync(async () =>
            {
                var java = await ServerFiles.GetRequiredJavaAsync(info.Version);
                var c = HostConfig.LoadOrNew(); c.RequiredJava = java; c.Save();
                Log.Host($"This version needs Java {java}+.");
            });
        UpdateView();
        await RefreshSetupBannerAsync();
    }

    void BtnFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Paths.ServerDir);
        Process.Start(new ProcessStartInfo(Paths.ServerDir) { UseShellExecute = true });
    }

    // ------------------------------------------------------------ buttons

    async void BtnStart_Click(object sender, RoutedEventArgs e)
    {
        if (_state == ServerState.Running && _server != null)
        {
            if (_countdownEnd != null || !ConfirmStopWithPlayers(restart: true)) return;
            Log.Host("Restarting...");
            _restartAfterStop = true;
            StopServer();
        }
        else await StartServerAsync(false);
    }
    async void BtnSafe_Click(object sender, RoutedEventArgs e) => await StartServerAsync(true);
    void BtnStop_Click(object sender, RoutedEventArgs e)
    {
        if (_countdownEnd != null) { CancelCountdown(); return; }
        if (_state == ServerState.Running && !ConfirmStopWithPlayers(restart: false)) return;
        if (_crashRestartPending && _state == ServerState.Stopped)
        {
            _crashRestartPending = false;   // the start after a crash, cancelled
            Log.Host("Not starting it again.");
            UpdateView();
            return;
        }
        StopServer();
    }

    async void BtnBackup_Click(object sender, RoutedEventArgs e) =>
        await RunBusyAsync(() => Task.Run(ServerFiles.BackupWorlds));

    async void BtnLink_Click(object sender, RoutedEventArgs e) =>
        await RunBusyAsync(() => Playit.ClaimAsync());

    void BtnAddress_Click(object sender, RoutedEventArgs e)
    {
        var config = HostConfig.LoadOrNew();
        if (config.PortForward) { ChangeForwardAddress(config); return; }
        var value = Prompt("Public address",
            $"Paste the address from playit.gg/account/tunnels (e.g. name.gl.joinmc.link or 147.185.221.5:12345).\n\nYour tunnel must point to 127.0.0.1:{ServerFiles.GetServerPort()}. Leave empty to clear it.",
            config.PlayitAddress ?? "");
        if (value == null) return;
        var addr = value.Trim();
        if (addr.Length > 0 && !Regex.IsMatch(addr, @"^[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)+(:\d+)?$"))
        {
            Ask($"'{addr}' doesn't look like a playit address.", "Public address", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        config.PlayitAddress = addr;
        config.Save();
        UpdateView();
        if (ConnectionsPage.Visibility == Visibility.Visible) RebuildConnections();
    }

    void BtnCopyFriend_Click(object sender, RoutedEventArgs e)
    {
        var addr = FriendAddress(HostConfig.Load());
        if (!string.IsNullOrEmpty(addr)) Clipboard.SetText(addr);
    }


    void BtnClear_Click(object sender, RoutedEventArgs e) => ClearLog();

    void ChkTunnel_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var config = HostConfig.LoadOrNew();
        config.UseTunnel = ChkTunnel.IsChecked == true;
        config.Save();
        if (ConnectionsPage.Visibility == Visibility.Visible) RebuildConnections(detail: false);   // the list's status dot
    }


    void SendCommand()
    {
        var cmd = TxtCommand.Text.Trim().TrimStart('/');
        TxtCommand.Clear();
        if (cmd.Length == 0 || _server?.IsRunning != true) return;
        Log.Write("> " + cmd);
        RememberCommand(cmd);
        if (cmd == "stop") { _state = ServerState.Stopping; UpdateView(); }
        _server.Send(cmd);
    }

    void BtnSend_Click(object sender, RoutedEventArgs e) => SendCommand();

    void TxtCommand_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SendCommand();
    }

    // ------------------------------------------------------------ closing

    protected override void OnClosing(CancelEventArgs e)
    {
        // a panel is waiting for an answer: answer it first
        if (_overlays.Count > 0) { e.Cancel = true; return; }
        if (_taskCts != null)
        {
            e.Cancel = true;
            if (Ask("An export or import is still running. Stop it and close?", "Close", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                _closeAfterTask = true;
                _taskCts?.Cancel();
            }
            return;
        }
        if (!ConfirmDiscardConfig()) { e.Cancel = true; return; }
        if (_server?.IsRunning == true)
        {
            e.Cancel = true;
            if (Ask("The server is still running. Stop it safely and close?", "Close", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            {
                _closeAfterStop = true;
                StopServer();
            }
            return;
        }
        if (_busy > 0)
        {
            e.Cancel = true;
            Ask("Please wait for the current task to finish.", "Close", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        StopHelpers();
        base.OnClosing(e);
    }
}
