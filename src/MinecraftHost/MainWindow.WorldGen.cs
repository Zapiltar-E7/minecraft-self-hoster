using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MinecraftHost.Core;

namespace MinecraftHost;

/// <summary>
/// The World generation card (Server → World): the seed, the world type (Minecraft's own, plus any a mod, datapack or
/// generator plugin adds), superflat layers and biome, structures, which installed packs reshape the terrain, and
/// Make a new world, which deletes the main world so the next start makes a new one with these settings.
/// </summary>
public partial class MainWindow
{
    WorldGen.Options? _genOptions;
    bool _genLoading;
    int _genRead;

    string _genShown = "";

    /// <summary>When the server starts or stops: the card's Make a new world follows (no new read of the world types).</summary>
    void RefreshGenerationIfShown()
    {
        var shown = $"{_state}|{_server?.IsRunning == true}|{_busy > 0}";
        if (shown == _genShown) return;
        _genShown = shown;
        if (ServerPage?.Visibility == Visibility.Visible && SecWorld?.IsChecked == true) BuildGenerationRows();
    }

    async Task RefreshGenerationAsync()
    {
        if (GenerationRows == null) return;
        var read = ++_genRead;
        if (_genOptions == null)
        {
            GenerationRows.Children.Clear();
            GenerationRows.Children.Add(new SettingRow { Icon = "", Title = "Reading the world types...", Hint = "From Minecraft, mods, datapacks and plugins" });
        }
        WorldGen.Options options;
        try { options = await Task.Run(WorldGen.Read); }
        catch (Exception ex) { Log.Host("Couldn't read the world types: " + ex.Message); return; }
        if (read != _genRead) return;   // a newer refresh is on its way
        _genOptions = options;
        BuildGenerationRows();
    }

    void BuildGenerationRows()
    {
        if (_genOptions is not { } o) return;
        _genLoading = true;
        GenerationRows.Children.Clear();
        var running = _server?.IsRunning == true || _state != ServerState.Stopped;

        // seed
        var seed = new TextBox { Style = (Style)FindResource("Input"), Width = 220, FontFamily = (System.Windows.Media.FontFamily)FindResource("Mono"), Text = WorldGen.Seed };
        System.Windows.Automation.AutomationProperties.SetName(seed, "Seed");
        void SaveSeed() { if (seed.Text.Trim() != WorldGen.Seed) { WorldGen.SetSeed(seed.Text); Log.Host("Seed for the next new world: " + (seed.Text.Trim().Length > 0 ? seed.Text.Trim() : "random")); } }
        seed.LostKeyboardFocus += (_, _) => SaveSeed();
        seed.KeyDown += (_, e) => { if (e.Key == Key.Enter) SaveSeed(); };
        GenerationRows.Children.Add(new SettingRow { Icon = "", Title = "Seed", Hint = "Empty: a random one. Words work too.", Content = seed });

        // world type
        var generator = WorldGen.BukkitGenerator;
        var current = generator != null
            ? o.Types.FirstOrDefault(t => t.Generator == generator) ?? new WorldGen.WorldType("plugin:" + generator, generator, "bukkit.yml", generator)
            : o.Types.FirstOrDefault(t => t.Id == WorldGen.LevelType) ?? new WorldGen.WorldType(WorldGen.LevelType, WorldGen.LevelType, "server.properties");
        var types = o.Types.Contains(current) ? o.Types : o.Types.Append(current).ToList();
        var type = new ComboBox { Width = 260, MaxDropDownHeight = 360 };
        foreach (var t in types)
            type.Items.Add(new ComboBoxItem { Content = t.Label, Tag = t, ToolTip = t.Source == "Minecraft" ? "Minecraft's own" : "From " + t.Source });
        type.SelectedItem = type.Items.Cast<ComboBoxItem>().First(i => i.Tag == current);
        System.Windows.Automation.AutomationProperties.SetName(type, "World type");
        var typeRow = new SettingRow { Icon = "", Title = "World type", Hint = TypeHint(current), Content = type };
        GenerationRows.Children.Add(typeRow);

        // superflat: layers (a preset) and biome
        var settings = WorldGen.FlatSettings is { } s && s["layers"] is JsonArray ? s : null;
        var flat = new ComboBox { Width = 260, MaxDropDownHeight = 360 };
        var matched = settings == null ? o.Flat.FirstOrDefault(f => f.Id == "minecraft:classic_flat") : o.Flat.FirstOrDefault(f => SameLayers(f.Settings, settings));
        if (settings != null && matched == null) flat.Items.Add(new ComboBoxItem { Content = "As set now", Tag = settings });
        foreach (var f in o.Flat) flat.Items.Add(new ComboBoxItem { Content = f.Label, Tag = f.Settings });
        flat.SelectedItem = flat.Items.Cast<ComboBoxItem>().FirstOrDefault(i => i.Tag == (matched?.Settings ?? settings)) ?? (flat.Items.Count > 0 ? flat.Items[0] : null);
        System.Windows.Automation.AutomationProperties.SetName(flat, "Superflat layers");
        var flatRow = new SettingRow { Icon = "", Title = "Superflat layers", Hint = LayersHint((flat.SelectedItem as ComboBoxItem)?.Tag as JsonObject), Content = flat };

        var biome = new ComboBox { Width = 260, MaxDropDownHeight = 360, ItemsSource = o.Biomes };
        var chosenBiome = settings?["biome"]?.GetValue<string>() ?? ((flat.SelectedItem as ComboBoxItem)?.Tag as JsonObject)?["biome"]?.GetValue<string>() ?? "minecraft:plains";
        biome.SelectedItem = o.Biomes.Contains(chosenBiome) ? chosenBiome : o.Biomes.FirstOrDefault();
        System.Windows.Automation.AutomationProperties.SetName(biome, "Superflat biome");
        var biomeRow = new SettingRow { Icon = "", Title = "Superflat biome", Content = biome };
        GenerationRows.Children.Add(flatRow);
        GenerationRows.Children.Add(biomeRow);

        void ShowFlat() => flatRow.Visibility = biomeRow.Visibility = ((type.SelectedItem as ComboBoxItem)?.Tag as WorldGen.WorldType)?.Id == WorldGen.Flat ? Visibility.Visible : Visibility.Collapsed;
        void SaveType()
        {
            if (_genLoading || (type.SelectedItem as ComboBoxItem)?.Tag is not WorldGen.WorldType t) return;
            JsonObject? layers = null;
            if (t.Id == WorldGen.Flat && (flat.SelectedItem as ComboBoxItem)?.Tag is JsonObject preset)
            {
                layers = (JsonObject)preset.DeepClone();
                if (biome.SelectedItem is string b) layers["biome"] = b;
            }
            WorldGen.SetType(t, layers);
            typeRow.Hint = TypeHint(t);
            flatRow.Hint = LayersHint(layers ?? (flat.SelectedItem as ComboBoxItem)?.Tag as JsonObject);
            ShowFlat();
        }
        type.SelectionChanged += (_, _) => SaveType();
        flat.SelectionChanged += (_, _) =>
        {
            // a preset comes with its own biome
            if (!_genLoading && (flat.SelectedItem as ComboBoxItem)?.Tag is JsonObject p && p["biome"]?.GetValue<string>() is { } pb && o.Biomes.Contains(pb))
            {
                _genLoading = true;
                biome.SelectedItem = pb;
                _genLoading = false;
            }
            SaveType();
        };
        biome.SelectionChanged += (_, _) => SaveType();
        ShowFlat();

        // structures
        var structures = new CheckBox { Style = (Style)FindResource("RowSwitch"), IsChecked = WorldGen.Structures };
        System.Windows.Automation.AutomationProperties.SetName(structures, "Structures");
        structures.Checked += (_, _) => { if (!_genLoading) WorldGen.SetStructures(true); };
        structures.Unchecked += (_, _) => { if (!_genLoading) WorldGen.SetStructures(false); };
        GenerationRows.Children.Add(new SettingRow { Icon = "", Title = "Structures", Hint = "Villages, temples, strongholds and the rest", Content = structures });

        // installed packs that change how terrain is made: they apply to any new world, whatever the type
        if (o.Shapers.Count > 0)
            GenerationRows.Children.Add(new SettingRow
            {
                Icon = "", Title = "Also shaping new worlds",
                Hint = string.Join(", ", o.Shapers) + ". Installed packs that change terrain, biomes or dimensions apply to every new world.",
            });

        // make a new world
        var exists = WorldGen.WorldExists;
        var make = new Button
        {
            Padding = new Thickness(12, 6, 12, 6), FontWeight = FontWeights.Normal, Content = IconText("", "Make a new world..."),
            IsEnabled = exists && !running && _busy == 0,
        };
        make.Click += (_, _) => _ = MakeNewWorldAsync();
        GenerationRows.Children.Add(new SettingRow
        {
            Icon = "", Title = "Make a new world",
            Hint = !exists ? "There's no world yet: the first start makes one with these settings."
                : running ? "Stop the server first." : "Deletes this world and makes a new one with these settings.",
            Content = make,
        });

        GenerationNote.Text = "These are used when a world is made: at the first start, or with Make a new world. They don't change the world you have.";
        _genLoading = false;
    }

    static string TypeHint(WorldGen.WorldType t) =>
        t.Generator != null ? $"From the {t.Source.Replace(" (plugin)", "")} plugin (bukkit.yml)"
        : t.Source == "Minecraft" ? "Minecraft's own" : "From " + t.Source;

    /// <summary>"Bedrock 1, stone 230, dirt 5, grass block 1": the layers bottom to top.</summary>
    static string LayersHint(JsonObject? settings)
    {
        if (settings?["layers"] is not JsonArray layers || layers.Count == 0) return "No layers: an empty world";
        var parts = layers.OfType<JsonObject>().Select(l =>
            (l["block"]?.GetValue<string>() ?? "?").Split(':').Last().Replace('_', ' ') + " " + (l["height"]?.ToString() ?? "1")).ToList();
        var text = string.Join(", ", parts);
        return "Bottom to top: " + (text.Length > 0 ? char.ToUpperInvariant(text[0]) + text[1..] : text);
    }

    static bool SameLayers(JsonObject a, JsonObject b) => a["layers"]?.ToJsonString() == b["layers"]?.ToJsonString();

    async Task MakeNewWorldAsync()
    {
        if (_server?.IsRunning == true || _state != ServerState.Stopped) return;
        var level = PlayerLists.GetProperty("level-name") ?? "world";
        var typeLabel = WorldGen.BukkitGenerator is { } g ? g : _genOptions?.Types.FirstOrDefault(t => t.Id == WorldGen.LevelType)?.Label ?? WorldGen.LevelType;
        var seedText = WorldGen.Seed.Length > 0 ? $"seed {WorldGen.Seed}" : "a random seed";
        var backup = Toggle("Back up the worlds first (recommended)", true);
        var fresh = Toggle("Start players fresh: inventories, positions, advancements and statistics", true);
        var start = Toggle("Start the server now to make it", true);
        var go = ShowOverlay(close =>
        {
            var panel = new StackPanel();
            panel.Children.Add(Heading("Make a new world?", MessageBoxImage.Warning));
            panel.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 4),
                Text = $"The world \"{level}\" (its overworld, nether and end) is deleted for good, and the next start makes a new one: {typeLabel}, {seedText}.\n\n" +
                       "The world's datapacks are kept, and so are other worlds (Multiverse's).",
            });
            panel.Children.Add(backup);
            panel.Children.Add(fresh);
            panel.Children.Add(start);
            panel.Children.Add(ButtonRow(new (string, Func<object?>, bool)[] { ("Cancel", () => null, true), ("Delete and make a new world", () => true, false) }, close));
            return panel;
        }, 500);
        if (go is not true) return;

        var ok = await RunBusyAsync(async () =>
        {
            if (backup.IsChecked == true) await Task.Run(ServerFiles.BackupWorlds);
            var deleted = await Task.Run(() => WorldGen.DeleteMainWorld(fresh.IsChecked == true));
            Log.Host($"World \"{level}\" deleted ({deleted.Count} {(deleted.Count == 1 ? "item" : "items")}: {string.Join(", ", deleted)}). The next start makes a new one.");
        });
        _ = RefreshGenerationAsync();
        if (ok && start.IsChecked == true) await StartServerAsync(false);
    }
}
