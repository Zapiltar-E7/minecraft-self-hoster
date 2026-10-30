using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MinecraftHost.Core;

namespace MinecraftHost;

// Server tab: server software (type, version, RAM, launch arguments), whitelist and operators.
public partial class MainWindow
{
    bool _softwareLoading;     // set while the window fills the dropdowns, so their events don't count as choices
    string _playerListsShown = "";

    void InitServerCards()
    {
        _softwareLoading = true;
        CmbEdition.ItemsSource = Editions.All;
        _softwareLoading = false;
        SyncSoftwareCard();
        InitRestartCard();
        InitWorldCard();
    }

    /// <summary>Points the dropdowns and boxes at what is installed now.</summary>
    void SyncSoftwareCard()
    {
        var config = HostConfig.Load();
        _softwareLoading = true;
        CmbEdition.SelectedItem = Editions.All.Contains(config?.Edition) ? config!.Edition : "Paper";
        CmbVersion.ItemsSource = config?.Version is { } v && v != "unknown" ? new List<string> { v } : null;
        CmbVersion.SelectedIndex = CmbVersion.Items.Count > 0 ? 0 : -1;
        TxtJavaArgs.Text = config?.JavaArgs ?? "";
        TxtServerArgs.Text = config?.ServerArgs ?? "";
        ShowLaunchArgs(!string.IsNullOrWhiteSpace(config?.JavaArgs) || !string.IsNullOrWhiteSpace(config?.ServerArgs));
        _softwareLoading = false;
        _ = LoadVersionsAsync();
    }

    /// <summary>Shows the Server tab with the software card in view (from "Set up this PC").</summary>
    void ShowSoftwareCard()
    {
        TabServer.IsChecked = true;
        SecGeneral.IsChecked = true;
        SoftwareCard.BringIntoView();
        CmbEdition.Focus();
    }

    async Task LoadVersionsAsync()
    {
        var edition = CmbEdition.SelectedItem as string ?? "Paper";
        var wanted = CmbVersion.SelectedItem as string ?? HostConfig.Load()?.Version;
        List<string> versions;
        try { versions = await Editions.GetVersionsAsync(edition); }
        catch
        {
            ShowSoftwareNote("Couldn't load the list of versions. Check the internet connection.");
            return;
        }
        if (CmbEdition.SelectedItem as string != edition) return;   // the type changed while loading

        var list = versions.ToList();
        var config = HostConfig.Load();
        // keep a version that's installed but no longer listed (e.g. an old or custom one)
        if (wanted != null && !list.Contains(wanted) && config?.Edition == edition && config.Version == wanted) list.Insert(0, wanted);
        _softwareLoading = true;
        CmbVersion.ItemsSource = list;
        CmbVersion.SelectedItem = wanted != null && list.Contains(wanted) ? wanted : list.FirstOrDefault();
        _softwareLoading = false;
        UpdateSoftwareCard();
    }

    void CmbEdition_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_softwareLoading) return;
        UpdateSoftwareCard();
        _ = LoadVersionsAsync();
    }

    void CmbVersion_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_softwareLoading) UpdateSoftwareCard();
    }

    void ShowSoftwareNote(string? text)
    {
        SoftwareNote.Text = text ?? "";
        SoftwareNote.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>What switching would change, in plain words; empty when nothing to warn about.</summary>
    /// <param name="modpack">For a modpack replacing the software: its own mods replace the old ones, so those notes don't apply.</param>
    static List<string> SwitchWarnings(HostConfig? config, string edition, string version, bool modpack = false)
    {
        var notes = new List<string>();
        if (config?.IsSetUp != true) return notes;
        if (config.Edition != edition)
        {
            if (Editions.RunsPlugins(config.Edition) && !Editions.RunsPlugins(edition))
                notes.Add($"{edition} can't run plugins. They stay in the plugins folder but won't load" +
                          (Editions.RunsMods(edition) ? $"; {edition} uses mods from the mods folder instead." : "."));
            else if (Editions.RunsMods(config.Edition) && !modpack)
                notes.Add($"Your {config.Edition} mods won't load on {edition}.");
        }
        if (config.Modpack is { } pack && !modpack && (config.Edition != edition || config.Version != version))
            notes.Add($"This server runs the modpack {pack}, which is made for {config.Edition} {config.Version}. Changing the software can stop its mods from loading.");
        if (config.Version is { } current && current != "unknown" && VersionComparer.Instance.Compare(version, current) < 0)
            notes.Add($"{version} is older than {current}. A world saved by a newer Minecraft usually can't be opened by an older one and can be damaged.");
        return notes;
    }

    void UpdateSoftwareCard()
    {
        if (CmbEdition == null || CmbVersion == null) return;
        var config = HostConfig.Load();
        var edition = CmbEdition.SelectedItem as string;
        var version = CmbVersion.SelectedItem as string;
        var installed = config?.IsSetUp == true;
        var same = installed && config!.Edition == edition && config.Version == version;

        InstallLabel.Text = edition == null || version == null ? "Install"
            : !installed ? $"Install {edition} {version}"
            : same ? (edition == "Vanilla" ? $"Reinstall Vanilla {version}" : $"Get the latest {edition} {version} build")
            : $"Switch to {edition} {version}";
        BtnInstall.IsEnabled = _state == ServerState.Stopped && _busy == 0 && version != null;
        BtnInstall.Style = (Style)FindResource(same ? typeof(Button) : "AccentButton");
        if (edition != null && version != null) ShowSoftwareNote(string.Join("\n\n", SwitchWarnings(config, edition, version)));

        var total = SystemInfo.TotalMemoryGB;
        var ram = config?.RamGB ?? 4;
        RamText.Text = $"{ram} GB";
        RamNote.Text = $"of {total} GB" + (_state != ServerState.Stopped ? " · applies next start" : "");
        BtnRamDown.IsEnabled = ram > 1;
        BtnRamUp.IsEnabled = ram < MaxRam(total);
    }

    // leave Windows some room
    static int MaxRam(int total) => Math.Max(1, total - 2);

    void ChangeRam(int delta)
    {
        var config = HostConfig.LoadOrNew();
        config.RamGB = Math.Clamp(config.RamGB + delta, 1, MaxRam(SystemInfo.TotalMemoryGB));
        config.Save();
        UpdateView();
    }

    void BtnRamDown_Click(object sender, RoutedEventArgs e) => ChangeRam(-1);
    void BtnRamUp_Click(object sender, RoutedEventArgs e) => ChangeRam(+1);

    // ------------------------------------------------------------ install / switch

    async void BtnInstall_Click(object sender, RoutedEventArgs e)
    {
        if (CmbEdition.SelectedItem is not string edition || CmbVersion.SelectedItem is not string version) return;
        if (_state != ServerState.Stopped || _busy > 0)
        {
            Ask("Stop the server first.", "Server software", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var config = HostConfig.Load();
        var installed = config?.IsSetUp == true;
        var notes = SwitchWarnings(config, edition, version);
        if (installed && config!.Modpack is { } pack && config.Edition == edition && config.Version == version)
            notes.Add($"This server runs the modpack {pack}, which picked its own {edition} build. A newer build usually works, but if mods stop loading, install the modpack again.");
        if (!ServerFiles.EulaAccepted())
            notes.Add("Running a server means you accept the Minecraft EULA (https://aka.ms/MinecraftEULA).");
        var text = InstallLabel.Text + "?" + (notes.Count > 0 ? "\n\n" + string.Join("\n\n", notes) : "");
        var backup = false;
        if (installed)
        {
            var answer = Ask(text + "\n\nBack up your worlds first?", "Server software", MessageBoxButton.YesNoCancel,
                notes.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) return;
            backup = answer == MessageBoxResult.Yes;
        }
        else if (Ask(text, "Server software", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;

        Log.Host($"Installing {edition} {version} into {Paths.ServerDir} ...");
        var required = 0;
        string? java = null;
        HostConfig? result = null;
        var ok = await RunBusyAsync(async () =>
        {
            if (backup) await Task.Run(ServerFiles.BackupWorlds);
            required = await ServerFiles.GetRequiredJavaAsync(version);
            java = await Task.Run(() => Java.Find(required));
            if (Editions.InstallerNeedsJava(edition) && java == null) return;   // the installer needs Java; offered below
            result = await ServerFiles.InstallAsync(edition, version, java);
        });
        if (ok && result == null)
        {
            await OfferJavaInstallAsync(required);
            java = await Task.Run(() => Java.Find(required));
            if (java != null) ok = await RunBusyAsync(async () => result = await ServerFiles.InstallAsync(edition, version, java));
        }
        else if (ok && java == null) await OfferJavaInstallAsync(required);

        if (result != null)
        {
            _players.Clear();
            ResetConfigs();   // config files and the plugin/mod list differ per server type
        }
        SyncSoftwareCard();
        UpdateView();
        await RefreshSetupBannerAsync();
    }

    // ------------------------------------------------------------ launch arguments

    void BtnLaunchArgs_Click(object sender, RoutedEventArgs e) => ShowLaunchArgs(LaunchArgsPanel.Visibility != Visibility.Visible);

    void ShowLaunchArgs(bool show)
    {
        LaunchArgsPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        LaunchArgsChevron.Text = show ? "" : "";   // chevron down / right
    }

    void LaunchArgs_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SaveLaunchArgs();
    }

    void LaunchArgs_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => SaveLaunchArgs();

    void SaveLaunchArgs()
    {
        if (_softwareLoading) return;
        var config = HostConfig.LoadOrNew();
        var java = TxtJavaArgs.Text.Trim();
        var server = TxtServerArgs.Text.Trim();
        if ((config.JavaArgs ?? "") == java && (config.ServerArgs ?? "") == server) return;
        config.JavaArgs = java.Length > 0 ? java : null;
        config.ServerArgs = server.Length > 0 ? server : null;
        config.Save();
        Log.Host("Launch arguments saved" + (_state != ServerState.Stopped ? "; they apply the next time the server starts." : "."));
    }

    /// <summary>Shows a box's grey hint while it's empty. The hint is the box's Tag.</summary>
    void HintedBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // the Tag binding (ElementName) can't resolve while a card is out of the window (another Server section is open),
        // so fall back to the naming rule: a box's hint is called (box name)Hint
        if (sender is TextBox box && (box.Tag as UIElement ?? FindName(box.Name + "Hint") as UIElement) is { } hint)
            hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------ whitelist + operators

    void RefreshPlayerLists()
    {
        if (RequestsPanel != null && _joinRequests.Count > 0) Dispatcher.BeginInvoke(UpdateJoinRequests);
        var whitelist = PlayerLists.Read(PlayerLists.WhitelistFile);
        var ops = PlayerLists.Read(PlayerLists.OpsFile);
        var whitelistOn = PlayerLists.WhitelistOn;
        var shown = string.Join(",", whitelist.Select(p => p.Name)) + "|" + string.Join(",", ops.Select(p => p.Name)) + "|" + whitelistOn + Paths.ServerDir;
        if (shown == _playerListsShown) return;   // UpdateView runs often; only rebuild when something changed
        _playerListsShown = shown;

        _loading = true;
        ChkWhitelist.IsChecked = whitelistOn;
        _loading = false;
        FillPlayerPanel(WhitelistPanel, WhitelistEmpty, whitelist, op: false);
        FillPlayerPanel(OpsPanel, OpsEmpty, ops, op: true);
    }

    void FillPlayerPanel(WrapPanel panel, TextBlock empty, List<PlayerEntry> players, bool op)
    {
        panel.Children.Clear();
        foreach (var player in players)
        {
            var remove = new Button
            {
                Style = (Style)FindResource("IconButton"),
                Content = "",
                FontSize = 9,
                Width = 20, Height = 20,
                Margin = new Thickness(6, 0, 0, 0),
                Background = Brushes.Transparent,
                ToolTip = op ? $"Remove {player.Name} as operator" : $"Remove {player.Name} from the whitelist",
            };
            remove.Click += async (_, _) => await RemovePlayerAsync(player.Name, op);
            panel.Children.Add(new Border
            {
                Background = (Brush)FindResource("Field"),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(10, 2, 4, 2),
                Margin = new Thickness(0, 0, 6, 6),
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children = { new TextBlock { Text = player.Name, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center }, remove },
                },
            });
        }
        empty.Visibility = players.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void TxtPlayerAdd_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) _ = AddPlayerAsync(op: sender == TxtOpsAdd);
    }

    async void BtnPlayerAdd_Click(object sender, RoutedEventArgs e) =>
        await AddPlayerAsync(op: (sender as Button)?.Tag as string == "Ops");

    async Task AddPlayerAsync(bool op)
    {
        var box = op ? TxtOpsAdd : TxtWhitelistAdd;
        var name = box.Text.Trim();
        if (name.Length == 0) return;
        if (!PlayerLists.NameRx.IsMatch(name))
        {
            Ask($"\"{name}\" isn't a Minecraft name. Names are up to 16 letters, numbers and underscores.", op ? "Operators" : "Whitelist",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        box.Clear();

        // the running server owns these files, so let it make the change
        if (_server?.IsRunning == true)
        {
            SendServerCommand(op ? $"op {name}" : $"whitelist add {name}");
            await RefreshPlayerListsSoonAsync();
            return;
        }

        PlayerEntry? player = null;
        if (!await RunBusyAsync(async () => player = await PlayerLists.LookupAsync(name))) return;
        if (player == null)
        {
            Ask($"There's no Minecraft account called \"{name}\".", op ? "Operators" : "Whitelist", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        PlayerLists.Add(op ? PlayerLists.OpsFile : PlayerLists.WhitelistFile, player, op);
        Log.Host(op ? $"{player.Name} is now an operator." : $"Added {player.Name} to the whitelist.");
        RefreshPlayerLists();
    }

    async Task RemovePlayerAsync(string name, bool op)
    {
        if (_server?.IsRunning == true)
        {
            SendServerCommand(op ? $"deop {name}" : $"whitelist remove {name}");
            await RefreshPlayerListsSoonAsync();
            return;
        }
        PlayerLists.Remove(op ? PlayerLists.OpsFile : PlayerLists.WhitelistFile, name);
        Log.Host(op ? $"{name} is no longer an operator." : $"Removed {name} from the whitelist.");
        RefreshPlayerLists();
    }

    void ChkWhitelist_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var on = ChkWhitelist.IsChecked == true;
        // a running server saves the setting itself and has server.properties open
        if (_server?.IsRunning == true) SendServerCommand(on ? "whitelist on" : "whitelist off");
        else PlayerLists.SetProperty("white-list", on ? "true" : "false");
        Log.Host(on ? "Whitelist on: only players on the list can join." : "Whitelist off: anyone with the address can join.");
        _playerListsShown = "";
        if (_server?.IsRunning == true) _ = RefreshPlayerListsSoonAsync();   // the switch would flip back until the server has saved
        else RefreshPlayerLists();
    }

    void SendServerCommand(string command)
    {
        Log.Write("> " + command);
        _server?.Send(command);
    }

    // the server writes whitelist.json / ops.json right after the command; a Mojang lookup can take a moment
    async Task RefreshPlayerListsSoonAsync()
    {
        foreach (var delay in new[] { 700, 1500, 3000 })
        {
            await Task.Delay(delay);
            RefreshPlayerLists();
        }
    }
}
