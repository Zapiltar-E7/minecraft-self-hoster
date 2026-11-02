using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using MinecraftHost.Core;

namespace MinecraftHost;

/// <summary>
/// The Worlds page: Multiverse's worlds and world groups. Read from Multiverse's files (so it shows while the server is
/// stopped too); changes are Multiverse's own commands sent over RCON while the server runs, so the replies (and the
/// confirmation code a delete asks for) can be read.
/// </summary>
public partial class MainWindow
{
    static readonly string[] GameModes = { "survival", "creative", "adventure", "spectator" };
    static readonly string[] DifficultiesMv = { "peaceful", "easy", "normal", "hard" };
    bool _worldsBusy;

    static string Cap1(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();
    static string EnvLabel(string env) => env.ToLowerInvariant() switch { "nether" => "Nether", "the_end" => "The End", _ => "Overworld" };

    bool WorldsLive => _state == ServerState.Running && _server?.IsRunning == true && !_worldsBusy;

    /// <summary>The server's own worlds (level-name and its nether and end): never deleted from here.</summary>
    static bool MainWorld(string name)
    {
        var level = PlayerLists.GetProperty("level-name") ?? "world";
        return name == level || name == level + "_nether" || name == level + "_the_end";
    }

    string _worldsShown = "";

    /// <summary>Rebuilds the open Worlds page when the server starts or stops, so its buttons follow without leaving the page.</summary>
    void RefreshWorldsIfShown()
    {
        var shown = $"{_state}|{_server?.IsRunning == true}|{_busy > 0}";
        if (ServerPage?.Visibility != Visibility.Visible || SecManage?.IsChecked != true || shown == _worldsShown) return;
        RefreshWorlds();
    }

    void RefreshWorlds()
    {
        if (WorldRows == null) return;
        _worldsShown = $"{_state}|{_server?.IsRunning == true}|{_busy > 0}";
        WorldRows.Children.Clear();
        GroupRows.Children.Clear();
        TravelRows.Children.Clear();
        if (!Multiverse.HasCore)
        {
            var find = new Button { Padding = new Thickness(12, 6, 12, 6), FontWeight = FontWeights.Normal, Content = IconText("", "Multiverse-Core") };
            find.Click += (_, _) => Playit.OpenUrl("https://modrinth.com/plugin/multiverse-core");
            WorldRows.Children.Add(new SettingRow { Icon = "", Title = "Multiverse-Core isn't installed", Hint = "It lets the server have more worlds than the usual three. Drop its jar on the window to install it.", Content = find });
            WorldsHint.Text = "";
            GroupsCard.Visibility = Visibility.Collapsed;
            TravelCard.Visibility = Visibility.Collapsed;
            return;
        }

        var live = WorldsLive;
        WorldsHint.Text = _worldsBusy ? WorldsHint.Text
            : live ? "Changes go to Multiverse straight away."
            : "Start the server to create, change or delete worlds and groups.";
        var worlds = Multiverse.ReadWorlds();
        var groups = Multiverse.HasInventories ? Multiverse.ReadGroups() : new();
        foreach (var w in worlds)
        {
            var group = groups.FirstOrDefault(g => g.Worlds.Contains(w.Name, StringComparer.OrdinalIgnoreCase));
            var hint = $"{EnvLabel(w.Environment)} · {Cap1(w.GameMode)} · {Cap1(w.Difficulty)}"
                       + (group != null ? $" · group {group.Name}" : "") + (w.Hidden ? " · hidden" : "") + (!w.AutoLoad ? " · not loaded at start" : "");
            var alias = Multiverse.Plain(w.Alias);
            var edit = SmallIcon("", "Edit " + w.Name, live, () => EditWorld(w));
            var delete = SmallIcon("", MainWorld(w.Name) ? "The server's main worlds can't be deleted here" : "Delete " + w.Name, live && !MainWorld(w.Name), () => _ = DeleteWorldAsync(w));
            delete.SetResourceReference(Control.ForegroundProperty, "Bad");
            WorldRows.Children.Add(new SettingRow
            {
                Icon = w.Environment.ToLowerInvariant() switch { "nether" => "", "the_end" => "", _ => "" },
                Title = alias.Length > 0 && alias != w.Name ? $"{w.Name}  ({alias})" : w.Name,
                Hint = hint,
                Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { edit, delete } },
            });
        }
        var create = new Button { Padding = new Thickness(12, 6, 12, 6), FontWeight = FontWeights.Normal, Content = IconText("", "Create..."), IsEnabled = live };
        System.Windows.Automation.AutomationProperties.SetName(create, "Create a world");
        create.Click += (_, _) => _ = CreateWorldAsync(groups);
        WorldRows.Children.Add(new SettingRow { Icon = "", Title = "Create a world", Hint = "Normal, flat, nether or end, with any seed", Content = create });

        BuildTravelRows(live);
        GroupsCard.Visibility = Multiverse.HasInventories ? Visibility.Visible : Visibility.Collapsed;
        foreach (var g in groups)
        {
            var shared = Multiverse.SharedIds(g.Shares);
            var shares = shared.Count == Multiverse.Shares.Length ? "everything"
                : shared.Count == 0 ? "nothing" : string.Join(", ", Multiverse.Shares.Where(s => shared.Contains(s.Id)).Select(s => s.Label.ToLowerInvariant()));
            var edit = SmallIcon("", "Edit group " + g.Name, live, () => _ = EditGroupAsync(g, worlds));
            var delete = SmallIcon("", g.Name == "default" ? "The default group can't be deleted here" : "Delete group " + g.Name, live && g.Name != "default", () => _ = DeleteGroupAsync(g));
            delete.SetResourceReference(Control.ForegroundProperty, "Bad");
            GroupRows.Children.Add(new SettingRow
            {
                Icon = "",
                Title = g.Name,
                Hint = (g.Worlds.Count == 0 ? "No worlds" : string.Join(", ", g.Worlds)) + " · shares " + shares + " · " + ReturnLabel(g.Return).ToLowerInvariant(),
                Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { edit, delete } },
            });
        }
        var newGroup = new Button { Padding = new Thickness(12, 6, 12, 6), FontWeight = FontWeights.Normal, Content = IconText("", "Create..."), IsEnabled = live };
        System.Windows.Automation.AutomationProperties.SetName(newGroup, "Create a world group");
        newGroup.Click += (_, _) => _ = EditGroupAsync(null, worlds);
        GroupRows.Children.Add(new SettingRow { Icon = "", Title = "Create a world group", Hint = "Worlds in a group share inventories and more", Content = newGroup });
    }

    static string ReturnLabel(ReturnTo r) => r switch
    {
        ReturnTo.GroupSpot => "Back where they were in this group",
        ReturnTo.WorldSpot => "Back where they were in each world",
        _ => "At the world's spawn",
    };

    /// <summary>
    /// When players change worlds: Multiverse-Inventories' last location (players return to where they left off) and
    /// Multiverse-Core's respawn rule. Each switch sends its command straight away.
    /// </summary>
    void BuildTravelRows(bool live)
    {
        TravelCard.Visibility = Multiverse.HasCore ? Visibility.Visible : Visibility.Collapsed;
        var lastOn = Multiverse.LastLocationOn();
        SettingRow Row(string icon, string title, string hint, bool on, bool enabled, Func<bool, string> command)
        {
            var sw = new CheckBox { Style = (Style)FindResource("RowSwitch"), IsChecked = on, IsEnabled = enabled };
            System.Windows.Automation.AutomationProperties.SetName(sw, title);
            RoutedEventHandler changed = (_, _) =>
            {
                var value = sw.IsChecked == true;
                if (value == on) return;
                _ = RunMvAsync("Saving...", async () => Check(await MvAsync(command(value))));
            };
            sw.Checked += changed;
            sw.Unchecked += changed;
            return new SettingRow { Icon = icon, Title = title, Hint = hint, Content = sw };
        }
        if (Multiverse.HasInventories)
        {
            TravelRows.Children.Add(Row("\uE7C1", "Return players to where they left off",
                "Coming back to a world, they arrive where they were instead of at its spawn. Each group can choose (below).",
                lastOn, live, _ => "mvinv toggle last_location"));
            TravelRows.Children.Add(Row("\uE8AB", "For every teleport", "Off: only with /mv tp ll:<world>",
                Multiverse.LastLocationForAllTeleports, live && lastOn, v => $"mvinv config apply-last-location-for-all-teleports {v.ToString().ToLowerInvariant()}"));
            TravelRows.Children.Add(Row("\uE7BA", "Forget the spot when a player dies", "So they don't come back to where they died",
                Multiverse.LastLocationResetOnDeath, live && lastOn, v => $"mvinv config reset-last-location-on-death {v.ToString().ToLowerInvariant()}"));
            TravelRows.Children.Add(Row("\uE909", "Worlds not in a group remember too", "Each one keeps its own spot",
                Multiverse.LastLocationUngrouped, live && lastOn, v => $"mvinv config use-optionals-for-ungrouped-worlds {v.ToString().ToLowerInvariant()}"));
        }
        TravelRows.Children.Add(Row("\uE945", "Respawn in the overworld after dying in the nether or end", "Like vanilla Minecraft. Off: they respawn in the same world.",
            Multiverse.RespawnInOverworld, live, v => $"mv config default-respawn-in-overworld {v.ToString().ToLowerInvariant()}"));
    }

    Button SmallIcon(string glyph, string tip, bool enabled, Action click)
    {
        var b = new Button { Style = (Style)FindResource("IconButton"), Content = glyph, ToolTip = tip, Margin = new Thickness(8, 0, 0, 0), IsEnabled = enabled };
        ToolTipService.SetShowOnDisabled(b, true);
        System.Windows.Automation.AutomationProperties.SetName(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    // ------------------------------------------------------------ sending commands

    /// <summary>Runs a Multiverse command over RCON and returns its reply; answers a "/mv confirm &lt;code&gt;" prompt itself.</summary>
    async Task<string> MvAsync(string command, int timeoutSeconds = 15)
    {
        if (_rcon == null || !_rcon.Connected)
        {
            _rconInfo ??= Rcon.EnsureEnabled();
            if (_rconInfo is not { } info) throw new InvalidOperationException("RCON isn't on yet. Restart the server once, then try again.");
            _rcon?.Dispose();
            _rcon = await Rcon.ConnectAsync(info.Port, info.Password) ?? throw new InvalidOperationException("Couldn't reach the server's RCON.");
        }
        Log.Host("Multiverse: " + command);
        var reply = await _rcon.CommandAsync(command, timeoutSeconds);
        var confirm = Regex.Match(reply, @"mv confirm (\d+)");
        if (confirm.Success) reply = await _rcon.CommandAsync(confirm.Value, timeoutSeconds);
        return reply.Trim();
    }

    /// <summary>Runs the commands in order with the page marked busy, then re-reads Multiverse's files.</summary>
    async Task RunMvAsync(string doing, Func<Task> work)
    {
        _worldsBusy = true;
        WorldsHint.Text = doing;
        RefreshWorlds();
        try { await work(); }
        catch (Exception ex)
        {
            Log.Host("Multiverse: " + ex.Message);
            Ask("That didn't work: " + ex.Message, "Worlds", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            await Task.Delay(800);   // Multiverse saves its files just after replying
            _worldsBusy = false;
            RefreshWorlds();
        }
    }

    /// <summary>Throws with Multiverse's own words when a reply says it failed.</summary>
    static void Check(string reply)
    {
        if (Regex.IsMatch(reply, @"(?i)\b(error|failed|invalid|unknown|not found|does not exist|already exists|cannot|can't)\b"))
            throw new InvalidOperationException(reply.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? reply);
    }

    // ------------------------------------------------------------ worlds

    ComboBox Choice(IEnumerable<string> items, string selected, string name)
    {
        var c = new ComboBox { Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var i in items) c.Items.Add(new ComboBoxItem { Content = Cap1(i), Tag = i });
        c.SelectedItem = c.Items.Cast<ComboBoxItem>().FirstOrDefault(i => string.Equals((string)i.Tag, selected, StringComparison.OrdinalIgnoreCase)) ?? c.Items[0];
        System.Windows.Automation.AutomationProperties.SetName(c, name);
        return c;
    }

    static string Picked(ComboBox c) => (string)((ComboBoxItem)c.SelectedItem).Tag;

    TextBlock Label(string text, double top = 12) => new() { Text = text, Style = (Style)FindResource("FieldLabel"), Margin = new Thickness(0, top, 0, 4) };

    CheckBox Toggle(string text, bool on) => new() { Style = (Style)FindResource("Switch"), Content = text, IsChecked = on, Margin = new Thickness(0, 8, 0, 0) };

    sealed record NewWorld(string Name, string Environment, string Terrain, string Seed, bool Structures, string GameMode, string Difficulty, string? Group);

    async Task CreateWorldAsync(List<MvGroup> groups)
    {
        var existing = Multiverse.ReadWorlds().Select(w => w.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var name = new TextBox { Style = (Style)FindResource("Input"), MaxLength = 32 };
        System.Windows.Automation.AutomationProperties.SetName(name, "World name");
        var error = new TextBlock { FontSize = 12, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
        error.SetResourceReference(TextBlock.ForegroundProperty, "Bad");
        var env = new ComboBox { Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var (id, label) in new[] { ("normal", "Overworld"), ("nether", "Nether"), ("the_end", "The End") }) env.Items.Add(new ComboBoxItem { Content = label, Tag = id });
        env.SelectedIndex = 0;
        System.Windows.Automation.AutomationProperties.SetName(env, "Type");
        var terrain = new ComboBox { Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var (id, label) in new[] { ("normal", "Normal"), ("flat", "Flat"), ("large_biomes", "Large biomes"), ("amplified", "Amplified") }) terrain.Items.Add(new ComboBoxItem { Content = label, Tag = id });
        terrain.SelectedIndex = 0;
        System.Windows.Automation.AutomationProperties.SetName(terrain, "Terrain");
        env.SelectionChanged += (_, _) => terrain.IsEnabled = Picked(env) == "normal";
        var seed = new TextBox { Style = (Style)FindResource("Input"), Width = 260, HorizontalAlignment = HorizontalAlignment.Left, FontFamily = (System.Windows.Media.FontFamily)FindResource("Mono") };
        System.Windows.Automation.AutomationProperties.SetName(seed, "Seed");
        var structures = Toggle("Villages, temples and other structures", true);
        var mode = Choice(GameModes, "survival", "Game mode");
        var difficulty = Choice(DifficultiesMv, "normal", "Difficulty");
        var group = new ComboBox { Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
        group.Items.Add(new ComboBoxItem { Content = "None", Tag = "" });
        foreach (var g in groups) group.Items.Add(new ComboBoxItem { Content = g.Name, Tag = g.Name });
        group.SelectedIndex = 0;
        System.Windows.Automation.AutomationProperties.SetName(group, "Group");

        string? Problem()
        {
            var n = name.Text.Trim();
            if (n.Length == 0) return "Give it a name.";
            if (!Multiverse.ValidName(n)) return "Letters, numbers, - and _ only (no spaces), up to 32.";
            if (existing.Contains(n)) return "There's already a world with that name.";
            return null;
        }
        var made = ShowOverlay(close =>
        {
            var panel = new StackPanel();
            panel.Children.Add(Heading("Create a world", MessageBoxImage.None));
            panel.Children.Add(Label("Name", 14));
            panel.Children.Add(name);
            panel.Children.Add(error);
            panel.Children.Add(Label("Type", 8));
            panel.Children.Add(env);
            panel.Children.Add(Label("Terrain"));
            panel.Children.Add(terrain);
            panel.Children.Add(Label("Seed (empty for a random one)"));
            panel.Children.Add(seed);
            panel.Children.Add(structures);
            panel.Children.Add(Label("Game mode", 14));
            panel.Children.Add(mode);
            panel.Children.Add(Label("Difficulty"));
            panel.Children.Add(difficulty);
            if (Multiverse.HasInventories)
            {
                panel.Children.Add(Label("Group (shares inventories with its worlds)"));
                panel.Children.Add(group);
            }
            panel.Children.Add(ButtonRow(new (string, Func<object?>, bool)[]
            {
                ("Cancel", () => null, true),
                ("Create", () =>
                {
                    if (Problem() is { } p) { error.Text = p; return "retry"; }
                    return new NewWorld(name.Text.Trim(), Picked(env), Picked(env) == "normal" ? Picked(terrain) : "normal", seed.Text.Trim(),
                        structures.IsChecked == true, Picked(mode), Picked(difficulty), Picked(group) is { Length: > 0 } gr ? gr : null);
                }, false),
            }, result => { if (result as string != "retry") close(result); }));
            return panel;
        }, 460) as NewWorld;
        if (made == null) return;

        await RunMvAsync($"Creating {made.Name}... this can take a minute.", async () =>
        {
            var command = $"mv create {made.Name} {made.Environment}";
            if (made.Seed.Length > 0) command += " --seed " + Regex.Replace(made.Seed, @"\s", "");
            if (made.Terrain != "normal") command += " --world-type " + made.Terrain;
            if (!made.Structures) command += " --no-structures";
            var reply = await MvAsync(command, timeoutSeconds: 300);
            if (!reply.Contains("created", StringComparison.OrdinalIgnoreCase)) Check(reply.Length > 0 ? reply : "error: no reply from Multiverse");
            Check(await MvAsync($"mv modify {made.Name} set gamemode {made.GameMode}"));
            Check(await MvAsync($"mv modify {made.Name} set difficulty {made.Difficulty}"));
            if (made.Group != null) Check(await MvAsync($"mvinv add-worlds {made.Group} {made.Name}"));
            Log.Host($"World {made.Name} created. Players can go there with /mv tp {made.Name} (operators), or through a portal.");
        });
    }

    void EditWorld(MvWorld w)
    {
        var alias = new TextBox { Style = (Style)FindResource("Input"), Text = w.Alias, MaxLength = 64 };
        System.Windows.Automation.AutomationProperties.SetName(alias, "Display name");
        var mode = Choice(GameModes, w.GameMode, "Game mode");
        var difficulty = Choice(DifficultiesMv, w.Difficulty, "Difficulty");
        var pvp = Toggle("Players can hurt each other (PVP)", w.Pvp);
        var flight = Toggle("Allow flight", w.AllowFlight);
        var hunger = Toggle("Hunger", w.Hunger);
        var weather = Toggle("Weather", w.AllowWeather);
        var hidden = Toggle("Hidden from the world list", w.Hidden);
        var autoLoad = Toggle("Loaded when the server starts", w.AutoLoad);
        var limit = new TextBox { Style = (Style)FindResource("Input"), Width = 90, HorizontalAlignment = HorizontalAlignment.Left, Text = w.PlayerLimit < 0 ? "" : w.PlayerLimit.ToString() };
        System.Windows.Automation.AutomationProperties.SetName(limit, "Player limit");

        var save = ShowOverlay(close =>
        {
            var panel = new StackPanel();
            panel.Children.Add(Heading(w.Name, MessageBoxImage.None));
            panel.Children.Add(new TextBlock { Text = $"{EnvLabel(w.Environment)}" + (w.Seed.Length > 0 ? $" · seed {w.Seed}" : ""), FontSize = 12, Foreground = Res("Muted"), Margin = new Thickness(0, 2, 0, 0) });
            panel.Children.Add(Label("Display name (& colour codes work, empty for the folder name)", 14));
            panel.Children.Add(alias);
            panel.Children.Add(Label("Game mode"));
            panel.Children.Add(mode);
            panel.Children.Add(Label("Difficulty"));
            panel.Children.Add(difficulty);
            foreach (var t in new[] { pvp, flight, hunger, weather, hidden, autoLoad }) panel.Children.Add(t);
            panel.Children.Add(Label("Player limit (empty for no limit)", 14));
            panel.Children.Add(limit);
            panel.Children.Add(ButtonRow(new (string, Func<object?>, bool)[] { ("Cancel", () => null, true), ("Save", () => "save", false) }, close));
            return panel;
        }, 460) as string;
        if (save == null) return;

        var changes = new List<(string Property, string Value)>();
        void Diff(string property, string before, string after) { if (!string.Equals(before, after, StringComparison.OrdinalIgnoreCase)) changes.Add((property, after)); }
        Diff("alias", w.Alias, alias.Text.Trim());
        Diff("gamemode", w.GameMode, Picked(mode));
        Diff("difficulty", w.Difficulty, Picked(difficulty));
        Diff("pvp", w.Pvp.ToString(), (pvp.IsChecked == true).ToString());
        Diff("allow-flight", w.AllowFlight.ToString(), (flight.IsChecked == true).ToString());
        Diff("hunger", w.Hunger.ToString(), (hunger.IsChecked == true).ToString());
        Diff("allow-weather", w.AllowWeather.ToString(), (weather.IsChecked == true).ToString());
        Diff("hidden", w.Hidden.ToString(), (hidden.IsChecked == true).ToString());
        Diff("auto-load", w.AutoLoad.ToString(), (autoLoad.IsChecked == true).ToString());
        Diff("player-limit", w.PlayerLimit.ToString(), int.TryParse(limit.Text.Trim(), out var n) && n >= 0 ? n.ToString() : "-1");
        if (changes.Count == 0) return;
        _ = RunMvAsync($"Saving {w.Name}...", async () =>
        {
            foreach (var (property, value) in changes)
            {
                // true/false as Multiverse writes them; text with spaces (a display name) in quotes, or only its first word is kept
                var shown = value.ToLowerInvariant() is "true" or "false" ? value.ToLowerInvariant()
                    : value.Contains(' ') ? "\"" + value.Replace("\"", "") + "\"" : value;
                Check(await MvAsync(value.Length == 0 ? $"mv modify {w.Name} reset {property}" : $"mv modify {w.Name} set {property} {shown}"));
            }
        });
    }

    async Task DeleteWorldAsync(MvWorld w)
    {
        if (Ask($"Delete the world {w.Name} for good?\n\nEverything built in it is lost. Players in it are moved to the main world.",
                "Delete world", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var backup = Ask("Back up the worlds first? (Recommended.)", "Delete world", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (backup == MessageBoxResult.Cancel) return;
        if (backup == MessageBoxResult.Yes) await BackupWhileRunningAsync(HostConfig.LoadOrNew().BackupKeep);
        await RunMvAsync($"Deleting {w.Name}...", async () =>
        {
            foreach (var g in Multiverse.ReadGroups().Where(g => g.Worlds.Contains(w.Name, StringComparer.OrdinalIgnoreCase)))
                await MvAsync($"mvinv remove-worlds {g.Name} {w.Name}");
            var reply = await MvAsync($"mv delete {w.Name} --remove-players");
            Check(reply);
            Log.Host($"World {w.Name} deleted.");
        });
    }

    // ------------------------------------------------------------ world groups

    async Task EditGroupAsync(MvGroup? existing, List<MvWorld> worlds)
    {
        var name = new TextBox { Style = (Style)FindResource("Input"), MaxLength = 32 };
        System.Windows.Automation.AutomationProperties.SetName(name, "Group name");
        var error = new TextBlock { FontSize = 12, Margin = new Thickness(0, 4, 0, 0) };
        error.SetResourceReference(TextBlock.ForegroundProperty, "Bad");
        var otherGroups = Multiverse.ReadGroups().Where(g => g.Name != existing?.Name).ToList();
        var worldBoxes = worlds.Select(w =>
        {
            var elsewhere = otherGroups.FirstOrDefault(g => g.Worlds.Contains(w.Name, StringComparer.OrdinalIgnoreCase));
            var box = Toggle(w.Name + (elsewhere != null ? $"  (now in {elsewhere.Name})" : ""), existing?.Worlds.Contains(w.Name, StringComparer.OrdinalIgnoreCase) == true);
            box.Tag = w.Name;
            return box;
        }).ToList();
        var shared = existing == null ? Multiverse.Shares.Select(s => s.Id).ToHashSet() : Multiverse.SharedIds(existing.Shares);
        var shareBoxes = Multiverse.Shares.Select(s => { var b = Toggle(s.Label, shared.Contains(s.Id)); b.Tag = s.Id; return b; }).ToList();
        var returnTo = new ComboBox { Width = 300, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var r in new[] { ReturnTo.GroupSpot, ReturnTo.WorldSpot, ReturnTo.Spawn }) returnTo.Items.Add(new ComboBoxItem { Content = ReturnLabel(r), Tag = r });
        returnTo.SelectedIndex = (int)(existing?.Return ?? ReturnTo.GroupSpot);
        System.Windows.Automation.AutomationProperties.SetName(returnTo, "When players come back");

        var save = ShowOverlay(close =>
        {
            var panel = new StackPanel();
            panel.Children.Add(Heading(existing == null ? "Create a world group" : "Group " + existing.Name, MessageBoxImage.None));
            if (existing == null)
            {
                panel.Children.Add(Label("Name", 14));
                panel.Children.Add(name);
                panel.Children.Add(error);
            }
            panel.Children.Add(Label("Worlds in it", 12));
            var w = new UniformGrid { Columns = 2 };
            foreach (var b in worldBoxes) w.Children.Add(b);
            panel.Children.Add(w);
            panel.Children.Add(Label("They share", 16));
            var s = new UniformGrid { Columns = 2 };
            foreach (var b in shareBoxes) s.Children.Add(b);
            panel.Children.Add(s);
            panel.Children.Add(new TextBlock { Text = "Anything not shared is kept separately for each group: a player's survival inventory stays out of creative worlds.", FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Foreground = Res("Muted"), Margin = new Thickness(0, 10, 0, 0) });
            panel.Children.Add(Label("When players come back to these worlds", 16));
            panel.Children.Add(returnTo);
            if (!Multiverse.LastLocationOn())
                panel.Children.Add(new TextBlock { Text = "Returning to where they left off is switched off for the whole server (When players change worlds), so they arrive at spawn either way.", FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Foreground = Res("WarnText"), Margin = new Thickness(0, 6, 0, 0) });
            panel.Children.Add(ButtonRow(new (string, Func<object?>, bool)[]
            {
                ("Cancel", () => null, true),
                (existing == null ? "Create" : "Save", () =>
                {
                    if (existing == null)
                    {
                        var n = name.Text.Trim();
                        if (!Multiverse.ValidName(n)) { error.Text = "Letters, numbers, - and _ only, up to 32."; return "retry"; }
                        if (otherGroups.Any(g => g.Name.Equals(n, StringComparison.OrdinalIgnoreCase))) { error.Text = "There's already a group with that name."; return "retry"; }
                    }
                    return "save";
                }, false),
            }, result => { if (result as string != "retry") close(result); }));
            return panel;
        }, 480) as string;
        if (save == null) return;

        var group = existing?.Name ?? name.Text.Trim();
        var wantWorlds = worldBoxes.Where(b => b.IsChecked == true).Select(b => (string)b.Tag).ToList();
        var wantShares = shareBoxes.Where(b => b.IsChecked == true).Select(b => (string)b.Tag).ToHashSet();
        var wantReturn = (ReturnTo)((ComboBoxItem)returnTo.SelectedItem).Tag;
        await RunMvAsync($"Saving group {group}...", async () =>
        {
            if (existing == null) Check(await MvAsync($"mvinv create-group {group}"));
            var had = existing?.Worlds ?? new();
            var add = wantWorlds.Where(x => !had.Contains(x, StringComparer.OrdinalIgnoreCase)).ToList();
            var remove = had.Where(x => !wantWorlds.Contains(x, StringComparer.OrdinalIgnoreCase)).ToList();
            // a world is in one group at a time: take it out of the one it's in now
            foreach (var x in add)
                foreach (var g in otherGroups.Where(g => g.Worlds.Contains(x, StringComparer.OrdinalIgnoreCase)))
                    await MvAsync($"mvinv remove-worlds {g.Name} {x}");
            if (add.Count > 0) Check(await MvAsync($"mvinv add-worlds {group} {string.Join(",", add)}"));
            if (remove.Count > 0) Check(await MvAsync($"mvinv remove-worlds {group} {string.Join(",", remove)}"));
            var before = existing == null ? new HashSet<string>() : Multiverse.SharedIds(existing.Shares);
            var hadReturn = existing?.Return ?? ReturnTo.WorldSpot;
            // last location is a share too: shared, all the group's worlds have one remembered spot
            var shareSpot = wantReturn == ReturnTo.GroupSpot;
            if (!before.SetEquals(wantShares) || (hadReturn == ReturnTo.GroupSpot) != shareSpot)
            {
                // start from nothing, then share what's ticked (handles "all" and the long share names alike)
                if (existing != null && existing.Shares.Count > 0) await MvAsync($"mvinv remove-shares {group} {string.Join(",", existing.Shares)}");
                if (wantShares.Count == Multiverse.Shares.Length && shareSpot) Check(await MvAsync($"mvinv add-shares {group} all"));
                else
                {
                    // the exact share names, not Multiverse's short ones ("inventory" would bring the ender chest along too)
                    var parts = Multiverse.Shares.Where(x => wantShares.Contains(x.Id)).SelectMany(x => x.Parts).ToList();
                    if (shareSpot) parts.Add("last_location");
                    if (parts.Count > 0) Check(await MvAsync($"mvinv add-shares {group} {string.Join(",", parts)}"));
                }
            }
            // back at spawn: last location switched off for this group (it wins over sharing)
            if (wantReturn == ReturnTo.Spawn && hadReturn != ReturnTo.Spawn) Check(await MvAsync($"mvinv add-disabled-shares {group} last_location"));
            if (wantReturn != ReturnTo.Spawn && hadReturn == ReturnTo.Spawn) Check(await MvAsync($"mvinv remove-disabled-shares {group} last_location"));
        });
    }

    async Task DeleteGroupAsync(MvGroup g)
    {
        if (Ask($"Delete the world group {g.Name}?\n\nIts worlds stay; they just stop sharing with each other. Players' saved inventories for the group are kept by Multiverse.",
                "Delete group", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await RunMvAsync($"Deleting group {g.Name}...", async () => Check(await MvAsync($"mvinv delete-group {g.Name}")));
    }
}
