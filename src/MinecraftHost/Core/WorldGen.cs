using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MinecraftHost.Core;

/// <summary>
/// How the server's main world is generated, and making a new one. The choices come from the game itself and from what's
/// installed: world types (world presets) and superflat presets from Minecraft's own jar, mods and datapacks, plus
/// generator plugins on Paper. Settings go into server.properties (level-seed, level-type, generator-settings,
/// generate-structures), and bukkit.yml for a generator plugin; they're used when the world is made.
/// </summary>
public static class WorldGen
{
    /// <param name="Generator">A generator plugin's bukkit.yml name; null for a world preset.</param>
    public sealed record WorldType(string Id, string Label, string Source, string? Generator = null);
    public sealed record FlatPreset(string Id, string Label, JsonObject Settings);
    public sealed record Options(List<WorldType> Types, List<FlatPreset> Flat, List<string> Biomes, List<string> Shapers);

    public const string Flat = "minecraft:flat";
    const string Normal = "minecraft:normal";

    static readonly Dictionary<string, string> VanillaNames = new()
    {
        [Normal] = "Default",
        [Flat] = "Superflat",
        ["minecraft:large_biomes"] = "Large biomes",
        ["minecraft:amplified"] = "Amplified",
        ["minecraft:single_biome_surface"] = "Single biome (plains)",
        ["minecraft:flat_all_dimensions"] = "Superflat in every dimension",
        ["minecraft:debug_all_block_states"] = "Debug mode (every block)",
    };

    /// <summary>Generator plugins whose bukkit.yml name is known: (plugin name, generator, shown as).</summary>
    static readonly (string Plugin, string Generator, string Label)[] GeneratorPlugins =
    {
        ("Iris", "Iris:overworld", "Iris (overworld pack)"),
        ("Terra", "Terra:OVERWORLD", "Terra (overworld pack)"),
        ("TerraformGenerator", "TerraformGenerator", "TerraformGenerator"),
        ("VoidGenPlus", "VoidGenPlus", "Void (VoidGenPlus)"),
        ("VoidWorldGenerator", "VoidWorldGenerator", "Void (VoidWorldGenerator)"),
    };

    static readonly Regex PresetRx = new(@"^data/([^/]+)/worldgen/world_preset/(.+)\.json$", RegexOptions.Compiled);
    static readonly Regex FlatRx = new(@"^data/([^/]+)/worldgen/flat_level_generator_preset/(.+)\.json$", RegexOptions.Compiled);
    static readonly Regex BiomeRx = new(@"^data/([^/]+)/worldgen/biome/(.+)\.json$", RegexOptions.Compiled);
    static readonly Regex ShapesRx = new(@"^data/[^/]+/(worldgen|dimension|dimension_type)/", RegexOptions.Compiled);
    const string ShownTag = "data/minecraft/tags/worldgen/world_preset/normal.json";

    static string LevelName => PlayerLists.GetProperty("level-name") is { Length: > 0 } l ? Unescape(l) : "world";
    static string LevelDir => Path.Combine(Paths.ServerDir, LevelName);

    /// <summary>Everything that can be picked, read from the game's jar, mods, datapacks and plugins. Slow: run it off the UI thread.</summary>
    public static Options Read()
    {
        var types = new List<WorldType>();
        var flat = new List<FlatPreset>();
        var biomes = new SortedSet<string>(StringComparer.Ordinal);
        var shapers = new List<string>();
        var shownFirst = new List<string>();

        void Scan(ZipOrFolder source, string sourceName, bool vanilla)
        {
            var shapes = false;
            foreach (var name in source.Names())
            {
                Match m;
                if ((m = PresetRx.Match(name)).Success)
                {
                    var id = $"{m.Groups[1].Value}:{m.Groups[2].Value}";
                    if (types.All(t => t.Id != id))
                        types.Add(new WorldType(id, VanillaNames.GetValueOrDefault(id) ?? Pretty(m.Groups[2].Value), sourceName));
                }
                else if ((m = FlatRx.Match(name)).Success)
                {
                    var id = $"{m.Groups[1].Value}:{m.Groups[2].Value}";
                    if (flat.Any(f => f.Id == id)) continue;
                    try
                    {
                        if (JsonNode.Parse(source.Read(name))?["settings"] is JsonObject settings)
                            flat.Add(new FlatPreset(id, Pretty(m.Groups[2].Value) + (vanilla ? "" : $" ({sourceName})"), settings));
                    }
                    catch { }
                }
                else if ((m = BiomeRx.Match(name)).Success) biomes.Add($"{m.Groups[1].Value}:{m.Groups[2].Value}");
                else if (vanilla && name == ShownTag)
                {
                    try { shownFirst.AddRange(JsonNode.Parse(source.Read(name))?["values"]?.AsArray().Select(v => v?.GetValue<string>() ?? "") ?? []); }
                    catch { }
                }
                if (!vanilla && ShapesRx.IsMatch(name)) shapes = true;
            }
            if (shapes) shapers.Add(sourceName);
        }

        using (var game = FindGameJar()) if (game != null) Scan(game, "Minecraft", vanilla: true);
        if (types.Count == 0)   // the game's jar wasn't found: Minecraft's own list
            types.AddRange(VanillaNames.Where(v => v.Key != "minecraft:flat_all_dimensions").Select(v => new WorldType(v.Key, v.Value, "Minecraft")));

        var datapacks = Path.Combine(LevelDir, "datapacks");
        if (Directory.Exists(datapacks))
            foreach (var pack in Directory.GetFileSystemEntries(datapacks).Order(StringComparer.OrdinalIgnoreCase))
                using (var source = ZipOrFolder.Open(pack)) if (source != null) Scan(source, Path.GetFileNameWithoutExtension(pack) + " (datapack)", false);
        var mods = Path.Combine(Paths.ServerDir, "mods");
        if (Directory.Exists(mods))
            foreach (var jar in Directory.GetFiles(mods, "*.jar").Order(StringComparer.OrdinalIgnoreCase))
                using (var source = ZipOrFolder.Open(jar)) if (source != null) Scan(source, ModName(jar) + " (mod)", false);

        // Minecraft's usual order first (normal, flat, large biomes, amplified, single biome), then the rest
        var ordered = types.OrderBy(t => shownFirst.IndexOf(t.Id) is var i && i >= 0 ? i : int.MaxValue)
            .ThenBy(t => t.Source == "Minecraft" ? 0 : 1).ThenBy(t => t.Label, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var (plugin, generator, label) in GeneratorPlugins)
            if (InstalledPlugins().Contains(plugin)) ordered.Add(new WorldType("plugin:" + generator, label, plugin + " (plugin)", generator));

        if (biomes.Count == 0) biomes.Add("minecraft:plains");
        return new Options(ordered, flat.OrderBy(f => f.Id.StartsWith("minecraft:") ? 0 : 1).ToList(), biomes.ToList(), shapers);
    }

    // ------------------------------------------------------------ the current settings

    public static string Seed => Unescape(PlayerLists.GetProperty("level-seed") ?? "");
    public static string LevelType => Unescape(PlayerLists.GetProperty("level-type") ?? "") is { Length: > 0 } t ? (t.Contains(':') ? t : "minecraft:" + t.ToLowerInvariant()) : Normal;
    public static bool Structures => PlayerLists.GetProperty("generate-structures") != "false";
    public static JsonObject? FlatSettings
    {
        get
        {
            try { return JsonNode.Parse(Unescape(PlayerLists.GetProperty("generator-settings") ?? "{}")) as JsonObject; }
            catch { return null; }
        }
    }

    public static void SetSeed(string seed) => PlayerLists.SetProperty("level-seed", Escape(seed.Trim()));
    public static void SetStructures(bool on) => PlayerLists.SetProperty("generate-structures", on ? "true" : "false");

    /// <summary>The world type, and for superflat its layers (a preset's settings, with the biome picked).</summary>
    public static void SetType(WorldType type, JsonObject? flatSettings)
    {
        PlayerLists.SetProperty("level-type", Escape(type.Generator != null ? Normal : type.Id));
        PlayerLists.SetProperty("generator-settings", Escape(type.Id == Flat && flatSettings != null ? flatSettings.ToJsonString() : "{}"));
        SetBukkitGenerator(type.Generator);
    }

    /// <summary>The generator plugin set for the main world in bukkit.yml, or null.</summary>
    public static string? BukkitGenerator
    {
        get
        {
            var lines = BukkitLines();
            var (_, _, generator) = FindGenerator(lines);
            return generator < 0 ? null : lines[generator].Split(':', 2)[1].Trim().Trim('"', '\'');
        }
    }

    /// <summary>
    /// worlds:
    ///   world:
    ///     generator: Iris:overworld
    /// Only the generator line is touched (added under the world, or removed); the rest of bukkit.yml stays as it is.
    /// </summary>
    static void SetBukkitGenerator(string? generator)
    {
        var file = Path.Combine(Paths.ServerDir, "bukkit.yml");
        if (generator == null && !File.Exists(file)) return;
        var lines = BukkitLines();
        var (worlds, world, line) = FindGenerator(lines);
        if (generator == null)
        {
            if (line < 0) return;
            lines.RemoveAt(line);
        }
        else if (line >= 0) lines[line] = $"    generator: {generator}";
        else if (world >= 0) lines.Insert(world + 1, $"    generator: {generator}");
        else if (worlds >= 0) lines.InsertRange(worlds + 1, new[] { $"  {LevelName}:", $"    generator: {generator}" });
        else lines.AddRange(new[] { "worlds:", $"  {LevelName}:", $"    generator: {generator}" });
        File.WriteAllText(file, string.Join("\n", lines) + "\n");
    }

    static List<string> BukkitLines()
    {
        try
        {
            var file = Path.Combine(Paths.ServerDir, "bukkit.yml");
            return File.Exists(file) ? File.ReadAllText(file).Replace("\r\n", "\n").TrimEnd('\n').Split('\n').ToList() : new();
        }
        catch { return new(); }
    }

    /// <summary>Line numbers of "worlds:", the main world under it and its "generator:" (-1 when missing).</summary>
    static (int Worlds, int World, int Generator) FindGenerator(List<string> lines)
    {
        var worlds = lines.FindIndex(l => Regex.IsMatch(l, @"^worlds:\s*(#.*)?$"));
        if (worlds < 0) return (-1, -1, -1);
        var world = -1;
        // the lines under "worlds:" are indented (or blank, or comments)
        for (var i = worlds + 1; i < lines.Count && (lines[i].Trim().Length == 0 || char.IsWhiteSpace(lines[i][0]) || lines[i].StartsWith('#')); i++)
        {
            if (lines[i].TrimStart().StartsWith('#') || lines[i].Trim().Length == 0) continue;
            var indent = lines[i].Length - lines[i].TrimStart().Length;
            var key = lines[i].Trim().Split(':')[0].Trim('"', '\'');
            if (indent <= 2)
            {
                if (world >= 0) break;   // the next world: ours had no generator line
                if (key == LevelName) world = i;
            }
            else if (world >= 0 && key == "generator") return (worlds, world, i);
        }
        return (worlds, world, -1);
    }

    // ------------------------------------------------------------ making a new world

    /// <summary>Whether the main world exists yet (the first start makes it).</summary>
    public static bool WorldExists => File.Exists(Path.Combine(LevelDir, "level.dat"));

    /// <summary>
    /// Deletes the main world (overworld, nether and end) so the next start makes a new one with the settings above.
    /// Kept: the world's datapacks, other worlds (Paper 26 keeps them as dimensions inside the main world's folder; with
    /// mods, those dimensions are the world's own and are deleted too),
    /// and, unless <paramref name="freshPlayers"/>, players' inventories, positions, advancements and statistics.
    /// The server must be stopped. Returns what was deleted.
    /// </summary>
    public static List<string> DeleteMainWorld(bool freshPlayers)
    {
        var deleted = new List<string>();
        void Delete(string path)
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else if (File.Exists(path)) File.Delete(path);
            else return;
            deleted.Add(Path.GetRelativePath(Paths.ServerDir, path));
        }

        var dir = LevelDir;
        if (!Directory.Exists(dir)) return deleted;
        var playerParts = new[] { "players", "playerdata", "advancements", "stats" };
        var dimensions = Path.Combine(dir, "dimensions");
        foreach (var entry in Directory.GetFileSystemEntries(dir))
        {
            var name = Path.GetFileName(entry);
            if (name.Equals("datapacks", StringComparison.OrdinalIgnoreCase) || entry == dimensions) continue;
            if (!freshPlayers && playerParts.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            Delete(entry);
        }
        // Paper 26: the main world's own dimensions; any others are other worlds (Multiverse's). With mods there are no
        // other worlds: every dimension there (the Aether, the Twilight Forest...) belongs to this world and goes with it.
        if (Editions.RunsMods(HostConfig.Load()?.Edition)) Delete(dimensions);
        else foreach (var own in new[] { "overworld", "the_nether", "the_end" }) Delete(Path.Combine(dimensions, "minecraft", own));
        // older Paper and Spigot: the nether and end in folders of their own
        foreach (var own in new[] { "_nether", "_the_end" }) Delete(Path.Combine(Paths.ServerDir, LevelName + own));
        return deleted;
    }

    // ------------------------------------------------------------ helpers

    /// <summary>server.properties escapes ":" "=" and "\" (Java's Properties format).</summary>
    static string Unescape(string value) => Regex.Replace(value, @"\\(.)", "$1");
    static string Escape(string value) => Regex.Replace(value, @"([\\:=])", @"\$1");

    static string Pretty(string id)
    {
        var words = id.Split('/').Last().Replace('_', ' ').Replace('-', ' ').Trim();
        return words.Length == 0 ? id : char.ToUpperInvariant(words[0]) + words[1..];
    }

    static string ModName(string jar)
    {
        var name = Path.GetFileNameWithoutExtension(jar);
        var m = Regex.Match(name, @"^(.*?)[-_+ ]+(?:mc)?v?\d");   // "terralith-2.5.1" → "terralith"
        return m.Success && m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : name;
    }

    /// <summary>Names of the plugins in plugins\ (from each jar's plugin.yml or paper-plugin.yml).</summary>
    static HashSet<string> InstalledPlugins()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dir = Path.Combine(Paths.ServerDir, "plugins");
        if (!Directory.Exists(dir)) return names;
        foreach (var jar in Directory.GetFiles(dir, "*.jar"))
            try
            {
                using var zip = ZipFile.OpenRead(jar);
                var yml = zip.GetEntry("paper-plugin.yml") ?? zip.GetEntry("plugin.yml");
                if (yml == null) continue;
                using var reader = new StreamReader(yml.Open());
                var m = Regex.Match(reader.ReadToEnd(), @"^name:\s*[""']?([^""'\r\n#]+)", RegexOptions.Multiline);
                if (m.Success) names.Add(m.Groups[1].Value.Trim());
            }
            catch { }
        return names;
    }

    /// <summary>
    /// The jar with Minecraft's own data: Paper's versions\, Forge's and NeoForge's libraries\, Fabric's .fabric\, or
    /// inside the vanilla server.jar (which bundles the real one under META-INF\versions).
    /// </summary>
    static ZipOrFolder? FindGameJar()
    {
        const string probe = "data/minecraft/worldgen/world_preset/normal.json";
        var dir = Paths.ServerDir;
        var candidates = new List<string>();
        foreach (var sub in new[] { "versions", Path.Combine("libraries", "net", "minecraft"), ".fabric", Path.Combine("libraries", "net", "neoforged"), Path.Combine("libraries", "net", "minecraftforge") })
            if (Directory.Exists(Path.Combine(dir, sub)))
                try { candidates.AddRange(Directory.GetFiles(Path.Combine(dir, sub), "*.jar", SearchOption.AllDirectories)); }
                catch { }
        if (Directory.Exists(dir)) candidates.AddRange(Directory.GetFiles(dir, "*.jar"));
        foreach (var jar in candidates.OrderByDescending(File.GetLastWriteTimeUtc))
        {
            try
            {
                var zip = ZipFile.OpenRead(jar);
                if (zip.GetEntry(probe) != null) return new ZipOrFolder(zip);
                var nested = zip.Entries.FirstOrDefault(e => e.FullName.StartsWith("META-INF/versions/") && e.FullName.EndsWith(".jar"));
                if (nested != null)
                {
                    var memory = new MemoryStream();
                    using (var s = nested.Open()) s.CopyTo(memory);
                    zip.Dispose();
                    var inner = new ZipArchive(memory, ZipArchiveMode.Read);
                    if (inner.GetEntry(probe) != null) return new ZipOrFolder(inner);
                    inner.Dispose();
                }
                else zip.Dispose();
            }
            catch { }
        }
        return null;
    }

    /// <summary>A jar, zip or folder read the same way: file names with "/" and their text.</summary>
    sealed class ZipOrFolder : IDisposable
    {
        readonly ZipArchive? _zip;
        readonly string? _folder;
        public ZipOrFolder(ZipArchive zip) => _zip = zip;
        ZipOrFolder(string folder) => _folder = folder;

        public static ZipOrFolder? Open(string path)
        {
            try
            {
                if (Directory.Exists(path)) return new ZipOrFolder(path);
                if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                    return new ZipOrFolder(ZipFile.OpenRead(path));
            }
            catch { }
            return null;
        }

        public IEnumerable<string> Names() => _zip != null
            ? _zip.Entries.Select(e => e.FullName)
            : Directory.EnumerateFiles(_folder!, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(_folder!, f).Replace('\\', '/'));

        public string Read(string name)
        {
            if (_zip != null)
            {
                using var reader = new StreamReader(_zip.GetEntry(name)!.Open());
                return reader.ReadToEnd();
            }
            return File.ReadAllText(Path.Combine(_folder!, name));
        }

        public void Dispose() => _zip?.Dispose();
    }
}
