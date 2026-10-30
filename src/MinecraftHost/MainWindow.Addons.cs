using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using MinecraftHost.Core;

namespace MinecraftHost;

/// <summary>
/// The Plugins tab (plugins and datapacks) and the Mods tab (Fabric/Forge mods): one page, filtered by kind.
/// Everything has a status dot, an on/off switch and Update buttons. Opening a tab checks online
/// (at most every 5 minutes); the button checks any time.
/// </summary>
public partial class MainWindow
{
    record OnlineInfo(string? FoundOn, bool Listed, UpdateFile? Update, string? PageUrl);

    List<Addon>? _addons;
    ListCollectionView? _addonView;
    Task? _scanTask;
    DateTime? _lastOnlineCheck;
    // results of the last online check, by file hash, so re-reading the folders keeps them
    readonly Dictionary<string, OnlineInfo> _online = new();
    bool _modsMode;   // the page is showing the Mods tab

    string AddonsTabName => _modsMode ? "Mods" : "Plugins & datapacks";

    bool InThisTab(Addon a) => (a.Kind == AddonKind.Mod) == _modsMode;

    /// <summary>Points the shared page at plugins & datapacks or at mods.</summary>
    void SetAddonsMode(bool mods)
    {
        _modsMode = mods;
        AddonsTitle.Text = mods ? "MODS" : "PLUGINS & DATAPACKS";
        AddonSearchHint.Text = mods ? "Search mods..." : "Search plugins and datapacks...";
        AddonsEmptyIcon.Text = mods ? "\uE74C" : "\uEA86";
        AddonsFolderLabel.Text = mods ? "Open mods folder" : "Open plugins folder";
        TxtAddonSearch.Clear();
        _addonView?.Refresh();
        UpdateAddonsEmpty();
        UpdateAddonControls();
    }

    string McVersion
    {
        get
        {
            var v = HostConfig.Load()?.Version;
            return string.IsNullOrEmpty(v) || v == "unknown" ? "this version" : v;
        }
    }

    // ------------------------------------------------------------ list

    Task ScanAddonsAsync()
    {
        if (_scanTask is { IsCompleted: false }) return _scanTask;
        return _scanTask = ScanCoreAsync();
    }

    async Task ScanCoreAsync()
    {
        AddonStatus.Text = _modsMode ? "Reading mods..." : "Reading plugins and datapacks...";
        try
        {
            var config = HostConfig.Load();
            var list = await Task.Run(() => Addons.Scan(config?.Version, config?.Edition));
            foreach (var a in list)
            {
                if (_lastOnlineCheck != null && a.Sha1.Length == 0) a.OnlineChecked = true;   // folder datapacks can't be looked up
                if (!_online.TryGetValue(a.Sha1, out var o)) continue;
                a.OnlineChecked = true;
                a.FoundOn = o.FoundOn;
                a.ListedForServer = o.Listed;
                a.Update = o.Update;
                a.PageUrl = o.PageUrl;
            }
            _addons = list;
            ShowAddons();
        }
        catch (Exception ex)
        {
            AddonStatus.Text = "Could not read the server folder: " + ex.Message;
        }
    }

    void ShowAddons()
    {
        if (_addons == null) return;
        var mc = McVersion;
        var curseForge = !string.IsNullOrWhiteSpace(HostConfig.Load()?.CurseForgeKey);
        foreach (var a in _addons) a.Evaluate(mc, curseForge);

        _addonView = new ListCollectionView(_addons) { Filter = o => MatchesAddonSearch((Addon)o) };
        _addonView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Addon.Group)));
        AddonList.ItemsSource = _addonView;
        UpdateAddonsEmpty();
        ApplyConfigHealth();
        UpdateAddonControls();
    }

    bool MatchesAddonSearch(Addon a)
    {
        var q = TxtAddonSearch.Text.Trim();
        return InThisTab(a) &&
               (q.Length == 0 || a.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || a.FileName.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    void TxtAddonSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        AddonSearchHint.Visibility = TxtAddonSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _addonView?.Refresh();
        UpdateAddonsEmpty();
    }

    /// <summary>Says why the list is empty and what to do about it.</summary>
    void UpdateAddonsEmpty()
    {
        if (_addons == null) { AddonsEmpty.Visibility = Visibility.Collapsed; return; }
        var none = !_addons.Any(InThisTab);
        var noMatch = !none && _addonView is { Count: 0 };
        var edition = HostConfig.Load()?.Edition;
        var runs = _modsMode ? Editions.RunsMods(edition) : Editions.RunsPlugins(edition);
        AddonsEmpty.Visibility = none || noMatch ? Visibility.Visible : Visibility.Collapsed;
        AddonsEmptyTitle.Text = noMatch ? $"Nothing matches \"{TxtAddonSearch.Text.Trim()}\""
            : _modsMode ? "No mods yet" : "No plugins or datapacks yet";
        AddonsEmptyText.Text = noMatch ? "Try a different name, or clear the search."
            : _modsMode
                ? runs ? $"Put {edition} mod .jar files in the server's mods folder. They show up here with their status."
                       : "This server type doesn't run mods. Switch to a mod loader (Fabric, Quilt, Forge or NeoForge) under Server on the Server tab, or install a modpack from Tools."
                : runs ? "Put plugin .jar files in the server's plugins folder, and datapacks in the world's datapacks folder. They show up here with their status."
                       : "This server type doesn't run plugins. Datapacks go in the world's datapacks folder and show up here.";
        BtnAddonsFolder.Visibility = none && runs ? Visibility.Visible : Visibility.Collapsed;
    }

    void BtnAddonsFolder_Click(object sender, RoutedEventArgs e)
    {
        var dir = _modsMode ? Addons.ModsDir : Path.Combine(Paths.ServerDir, "plugins");
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
    }

    async void BtnAddonsRefresh_Click(object sender, RoutedEventArgs e) => await ScanAddonsAsync();

    /// <summary>Buttons, counts and the "stop the server" notice. Called from UpdateView too.</summary>
    void UpdateAddonControls()
    {
        if (AddonsPage == null) return;
        var stopped = _state == ServerState.Stopped && _busy == 0;
        var notes = new List<string>();
        var edition = HostConfig.Load()?.Edition;
        // the list still shows these, but this server type won't load them
        if (_modsMode && edition != null && !Editions.RunsMods(edition) && _addons?.Any(a => a.Kind == AddonKind.Mod && a.Enabled) == true)
            notes.Add($"This server runs {edition}, which can't load mods. Switch to a mod loader (Fabric, Quilt, Forge or NeoForge) on the Server tab to use them.");
        if (!_modsMode && edition != null && !Editions.RunsPlugins(edition) && _addons?.Any(a => a.Kind == AddonKind.Plugin && a.Enabled) == true)
            notes.Add($"This server runs {edition}, which can't load plugins. Datapacks still work.");
        if (!stopped) notes.Add($"Stop the server to switch {(_modsMode ? "mods" : "plugins and datapacks")} on or off, or to update them.");
        AddonNotice.Visibility = notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AddonNoticeText.Text = string.Join("\n", notes);

        var key = HostConfig.Load()?.CurseForgeKey;
        CurseForgeLabel.Text = string.IsNullOrWhiteSpace(key) ? "CurseForge key" : "CurseForge: key set";
        if (_addons == null) return;

        var mine = _addons.Where(InThisTab).ToList();
        var updates = mine.Count(a => a.Update != null);
        BtnUpdateAll.IsEnabled = updates > 0 && stopped;
        UpdateAllLabel.Text = updates > 0 ? $"Update all ({updates})" : "Update all";

        var bad = mine.Count(a => a.Health == Health.Bad);
        var on = _modsMode
            ? $"{mine.Count(a => a.Enabled)} mods on"
            : $"{mine.Count(a => a.Kind == AddonKind.Plugin && a.Enabled)} plugins and {mine.Count(a => a.Kind == AddonKind.Datapack && a.Enabled)} datapacks on";
        UpdateTabDots();
        if (ConnectionsPage.Visibility != Visibility.Visible) _services = Services.Detect(_addons);
        AddonStatus.Text = on +
                           (bad > 0 ? $"  ·  {bad} need attention" : "") +
                           (_lastOnlineCheck is { } t ? $"  ·  checked for updates at {t:t}" : "  ·  not checked for updates yet");
    }

    /// <summary>Gives each config file the status dot of the plugin or datapack it belongs to.</summary>
    void ApplyConfigHealth()
    {
        if (_addons == null || _configFiles == null) return;
        Dictionary<string, Addon> ByName(AddonKind kind) => _addons.Where(a => a.Kind == kind)
            .GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.Enabled).First(), StringComparer.OrdinalIgnoreCase);
        var plugins = ByName(AddonKind.Plugin);
        var packs = ByName(AddonKind.Datapack);
        foreach (var f in _configFiles)
            f.Health = f.Category switch
            {
                ConfigCategory.Plugin => plugins.TryGetValue(f.Group, out var p) ? p.Health : null,
                ConfigCategory.Datapack => packs.TryGetValue(f.Group, out var d) ? d.Health : null,
                _ => null,
            };
    }

    string? WhyNotNow() =>
        _state != ServerState.Stopped || _busy > 0
            ? $"Stop the server first. {(_modsMode ? "Mods" : "Plugins and datapacks")} can only be switched on or off, or updated, while it is stopped."
            : null;

    // ------------------------------------------------------------ on / off

    void AddonToggle_Click(object sender, RoutedEventArgs e)
    {
        var box = (CheckBox)sender;
        if (box.DataContext is not Addon a) return;
        var on = box.IsChecked == true;
        if (on == a.Enabled) return;   // the binding (or a refusal below) put it back in step

        var refuse = WhyNotNow();
        var other = on && a.Kind != AddonKind.Datapack
            ? _addons?.FirstOrDefault(o => o != a && o.Enabled && o.Kind == a.Kind && o.Name.Equals(a.Name, StringComparison.OrdinalIgnoreCase))
            : null;
        if (refuse == null && other != null)
            refuse = $"Another copy of {a.Name} is already on ({other.FileName}). Switch that one off first: two copies of the same {a.Kind.ToString().ToLowerInvariant()} won't load.";
        if (refuse != null)
        {
            box.SetCurrentValue(ToggleButton.IsCheckedProperty, a.Enabled);   // keeps the binding, unlike setting IsChecked
            Ask(refuse, AddonsTabName, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            Addons.SetEnabled(a, on);
            Log.Host($"{(on ? "Switched on" : "Switched off")} {a.Kind.ToString().ToLowerInvariant()} {a.Name} ({Path.GetRelativePath(Paths.ServerDir, a.FullPath)}).");
        }
        catch (Exception ex)
        {
            box.SetCurrentValue(ToggleButton.IsCheckedProperty, a.Enabled);
            Ask($"Could not switch {a.Name} {(on ? "on" : "off")}:\n{ex.Message}", AddonsTabName, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        a.Evaluate(McVersion, !string.IsNullOrWhiteSpace(HostConfig.Load()?.CurseForgeKey));
        ApplyConfigHealth();
        UpdateAddonControls();
    }

    void AddonDelete_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not Addon a) return;
        if (WhyNotNow() is { } refuse)
        {
            Ask(refuse, AddonsTabName, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var kind = a.Kind.ToString().ToLowerInvariant();
        var settings = a.Kind == AddonKind.Plugin && Directory.Exists(Path.Combine(Paths.ServerDir, "plugins", a.Name))
            ? $"\n\nIts settings folder (plugins\\{a.Name}) is kept, in case you add it again."
            : "";
        if (Ask($"Delete the {kind} {a.Name}?\n\n{a.FileName} moves to the app's backups\\deleted folder, so you can put it back later if you need it.{settings}",
                "Delete " + kind, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            var moved = Addons.Delete(a);
            Log.Host($"Deleted {kind} {a.Name}; the file is in {Path.GetRelativePath(Paths.Root, moved)}.");
        }
        catch (Exception ex)
        {
            Ask($"Could not delete {a.Name}:\n{ex.Message}", AddonsTabName, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _addons?.Remove(a);
        ShowAddons();   // the list, counts, dots, config dots and the Connections cards follow
    }

    void AddonShowFile_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not Addon a) return;
        if (File.Exists(a.FullPath) || Directory.Exists(a.FullPath)) Process.Start("explorer.exe", $"/select,\"{a.FullPath}\"");
        else Ask($"{a.FileName} isn't there any more. Press refresh to re-read the folders.", "Show file", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    void AddonWebsite_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is Addon a)
            Playit.OpenUrl(a.WebUrl);
    }

    // ------------------------------------------------------------ checking for updates

    async void BtnCheckUpdates_Click(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync(quiet: false);

    /// <summary>Opening the tab re-reads the folders and checks online, unless a check ran in the last few minutes.</summary>
    async Task OpenAddonsTabAsync()
    {
        if (_lastOnlineCheck is { } last && DateTime.Now - last < TimeSpan.FromMinutes(5)) await ScanAddonsAsync();
        else await CheckForUpdatesAsync(quiet: true);
    }

    Task? _checkTask;

    Task CheckForUpdatesAsync(bool quiet)
    {
        if (_checkTask is { IsCompleted: false }) return _checkTask;   // e.g. flipping tabs while a check runs
        return _checkTask = CheckForUpdatesCoreAsync(quiet);
    }

    async Task CheckForUpdatesCoreAsync(bool quiet)
    {
        await ScanAddonsAsync();
        if (_addons == null) return;
        var config = HostConfig.Load();
        var mc = config?.Version;
        if (string.IsNullOrEmpty(mc) || mc == "unknown")
        {
            if (!quiet) Ask("The server's Minecraft version isn't known, so there is nothing to compare against. Choose the server folder again on the Server tab.",
                "Check for updates", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var curseForge = !string.IsNullOrWhiteSpace(config!.CurseForgeKey);
        BtnCheckUpdates.IsEnabled = false;
        AddonStatus.Text = curseForge ? "Checking Modrinth and CurseForge..." : "Checking Modrinth...";
        try
        {
            await Addons.CheckOnlineAsync(_addons, mc, config.CurseForgeKey);
            _online.Clear();
            foreach (var a in _addons.Where(a => a.Sha1.Length > 0))
                _online[a.Sha1] = new OnlineInfo(a.FoundOn, a.ListedForServer, a.Update, a.PageUrl);
            _lastOnlineCheck = DateTime.Now;
            ShowAddons();
            var updates = _addons.Count(a => a.Update != null);
            if (!quiet) Log.Host(updates == 0 ? "Checked for updates: everything found online is up to date." : $"Checked for updates: {updates} available.");
        }
        catch (Exception ex)
        {
            UpdateAddonControls();
            if (quiet) AddonStatus.Text += "  ·  couldn't check online";
            else Ask("Could not check for updates:\n" + ex.Message, "Check for updates", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { BtnCheckUpdates.IsEnabled = true; }
    }

    void BtnCurseForge_Click(object sender, RoutedEventArgs e)
    {
        var config = HostConfig.LoadOrNew();
        var hadKey = !string.IsNullOrWhiteSpace(config.CurseForgeKey);
        if (!hadKey &&
            Ask("CurseForge doesn't let apps sign you in. Instead, each person gets a free API key:\n\n" +
                "1. Sign in at console.curseforge.com (it opens now).\n2. Open \"API keys\" and copy your key.\n3. Paste it in the next box.\n\n" +
                "Modrinth needs no account, so its updates work without this.", "CurseForge", MessageBoxButton.OKCancel) != MessageBoxResult.OK)
            return;
        if (!hadKey) Playit.OpenUrl("https://console.curseforge.com/");

        var value = Prompt("CurseForge API key", "Paste your CurseForge API key. It is saved in host-config.json next to the app. Leave empty to remove it.",
            config.CurseForgeKey ?? "");
        if (value == null) return;
        config.CurseForgeKey = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        config.Save();
        Log.Host(config.CurseForgeKey == null ? "CurseForge key removed." : "CurseForge key saved. Press Check for updates to use it.");
        UpdateAddonControls();
    }

    // ------------------------------------------------------------ updating

    async void AddonUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is Addon a) await UpdateAddonsAsync(new List<Addon> { a }, confirm: false);
    }

    async void BtnUpdateAll_Click(object sender, RoutedEventArgs e)
    {
        if (_addons != null) await UpdateAddonsAsync(_addons.Where(a => InThisTab(a) && a.Update != null).ToList(), confirm: true);
    }

    async Task UpdateAddonsAsync(List<Addon> items, bool confirm)
    {
        if (items.Count == 0) return;
        if (WhyNotNow() is { } refuse)
        {
            Ask(refuse, "Update", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (confirm)
        {
            var list = string.Join("\n", items.Select(a => $"   •  {a.Name}  →  {a.Update!.Version}  ({a.Update.Source})"));
            if (Ask($"Download and install these updates?\n\n{list}\n\nThe old files are kept in backups.", "Update all",
                    MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        }

        var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        var websiteOnly = new List<Addon>();
        var failed = new List<string>();
        var done = 0;
        _busy++;
        UpdateView();
        BtnCheckUpdates.IsEnabled = false;
        try
        {
            foreach (var a in items)
            {
                AddonStatus.Text = $"Updating {a.Name}...";
                try
                {
                    if (await Addons.UpdateAsync(a, stamp)) done++;
                    else websiteOnly.Add(a);
                }
                catch (Exception ex)
                {
                    failed.Add($"{a.Name}: {ex.Message}");
                    Log.Host($"Could not update {a.Name}: {ex.Message}");
                }
            }
        }
        finally
        {
            _busy--;
            BtnCheckUpdates.IsEnabled = true;
            UpdateView();
        }

        // re-read the folders and look the new files up, so their dots are right
        if (done > 0) await CheckForUpdatesAsync(quiet: true);
        else UpdateAddonControls();

        if (failed.Count > 0)
            Ask("Some updates failed (the old files were left in place):\n\n" + string.Join("\n", failed), "Update", MessageBoxButton.OK, MessageBoxImage.Warning);
        if (websiteOnly.Count > 0 &&
            Ask($"{string.Join(", ", websiteOnly.Select(a => a.Name))}: the author only allows downloads from the CurseForge website.\n\n" +
                $"Open the download page? Put the new file in the {(_modsMode ? "mods" : "plugins")} folder and switch the old one off.",
                "Update", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            foreach (var a in websiteOnly.Where(a => a.Update?.PageUrl != null))
                Playit.OpenUrl(a.Update!.PageUrl! + "/files");
        if (done > 0) Log.Host($"Updated {done} file(s). Start the server to use them.");
    }
}
