namespace MinecraftHost.Core;

/// <summary>A world Multiverse-Core manages (plugins\Multiverse-Core\worlds.yml).</summary>
public sealed record MvWorld(
    string Name, string Environment, string Alias, string GameMode, string Difficulty,
    bool Pvp, bool AllowFlight, bool Hunger, bool AllowWeather, bool Hidden, bool AutoLoad, int PlayerLimit, string Seed);

/// <summary>A world group of Multiverse-Inventories (plugins\Multiverse-Inventories\groups.yml): worlds that share what's listed.</summary>
public sealed record MvGroup(string Name, List<string> Worlds, List<string> Shares, List<string> DisabledShares)
{
    /// <summary>Where players arrive when they come back to this group's worlds (Multiverse-Inventories' last location).</summary>
    public ReturnTo Return =>
        DisabledShares.Contains("last_location", StringComparer.OrdinalIgnoreCase) ? ReturnTo.Spawn
        : Shares.Any(s => s.Equals("all", StringComparison.OrdinalIgnoreCase) || s.Equals("last_location", StringComparison.OrdinalIgnoreCase)) ? ReturnTo.GroupSpot
        : ReturnTo.WorldSpot;
}

/// <summary>Coming back to a group's worlds: where they were anywhere in the group, where they were in that world, or the world's spawn.</summary>
public enum ReturnTo { GroupSpot, WorldSpot, Spawn }

/// <summary>
/// Reads Multiverse's worlds and world groups from its files, for the Worlds page. Changes go through Multiverse's own
/// commands (over RCON, so the replies can be read), which keeps its files the way it writes them.
/// </summary>
public static class Multiverse
{
    static string Plugins => Path.Combine(Paths.ServerDir, "plugins");
    static string WorldsFile => Path.Combine(Plugins, "Multiverse-Core", "worlds.yml");
    static string CoreConfig => Path.Combine(Plugins, "Multiverse-Core", "config.yml");
    static string InvConfig => Path.Combine(Plugins, "Multiverse-Inventories", "config.yml");

    /// <summary>A true/false setting from a Multiverse config file, wherever it sits in it; null if it isn't there.</summary>
    static bool? Flag(string file, string key)
    {
        if (!File.Exists(file)) return null;
        foreach (var raw in File.ReadLines(file))
        {
            var line = raw.Trim();
            if (line.StartsWith(key + ":", StringComparison.Ordinal)) return line[(key.Length + 1)..].Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
        }
        return null;
    }

    /// <summary>Multiverse-Inventories' last location is switched on (it's an optional share, listed under active-optional-shares).</summary>
    public static bool LastLocationOn()
    {
        if (!File.Exists(InvConfig)) return false;
        var inList = false;
        foreach (var raw in File.ReadLines(InvConfig))
        {
            var line = raw.Trim();
            if (line.StartsWith("active-optional-shares:")) { inList = true; if (line.Contains("last_location")) return true; continue; }
            if (!inList) continue;
            if (line.StartsWith("- ")) { if (line[2..].Trim().Trim('\'', '"') == "last_location") return true; }
            else if (line.Length > 0) inList = false;
        }
        return false;
    }

    public static bool LastLocationForAllTeleports => Flag(InvConfig, "apply-last-location-for-all-teleports") ?? true;
    public static bool LastLocationResetOnDeath => Flag(InvConfig, "reset-last-location-on-death") ?? false;
    public static bool LastLocationUngrouped => Flag(InvConfig, "use-optionals-for-ungrouped-worlds") ?? true;
    public static bool RespawnInOverworld => Flag(CoreConfig, "default-respawn-in-overworld") ?? true;
    static string GroupsFile => Path.Combine(Plugins, "Multiverse-Inventories", "groups.yml");

    static bool Installed(string jarPrefix) =>
        Directory.Exists(Plugins) && Directory.GetFiles(Plugins, jarPrefix + "*.jar").Length > 0;

    public static bool HasCore => Installed("multiverse-core") || Installed("Multiverse-Core");
    public static bool HasInventories => Installed("multiverse-inventories") || Installed("Multiverse-Inventories");

    /// <summary>What a world group can share, as the checkboxes show it: the short name Multiverse-Inventories takes, and the shares it stands for.</summary>
    public static readonly (string Id, string Label, string[] Parts)[] Shares =
    {
        ("inventory", "Inventory", new[] { "inventory_contents", "armor_contents", "off_hand" }),
        ("ender_chest", "Ender chest", new[] { "ender_chest" }),
        ("experience", "Experience", new[] { "xp", "total_xp", "lvl" }),
        ("health", "Health", new[] { "hit_points", "max_hit_points", "remaining_air", "maximum_air", "fall_distance", "fire_ticks" }),
        ("hunger", "Hunger", new[] { "food_level", "saturation", "exhaustion" }),
        ("potions", "Potion effects", new[] { "potion_effects" }),
        ("bed_spawn", "Respawn point", new[] { "bed_spawn" }),
        ("advancements", "Advancements", new[] { "advancements" }),
        ("game_statistics", "Statistics", new[] { "game_statistics" }),
        ("recipes", "Recipes", new[] { "recipes" }),
    };

    /// <summary>The share checkboxes that are on for a group's list of shares (which may be "all", short names, or the parts).</summary>
    public static HashSet<string> SharedIds(IEnumerable<string> shares)
    {
        var set = shares.Select(s => s.ToLowerInvariant()).ToHashSet();
        if (set.Contains("all")) return Shares.Select(s => s.Id).ToHashSet();
        return Shares.Where(s => set.Contains(s.Id) || s.Parts.All(set.Contains)).Select(s => s.Id).ToHashSet();
    }

    public static List<MvWorld> ReadWorlds()
    {
        var worlds = new List<MvWorld>();
        if (!File.Exists(WorldsFile)) return worlds;
        string? key = null, section = null;
        Dictionary<string, string> props = new(), readOnly = new();
        void Done()
        {
            if (key == null) return;
            string P(string k, string d = "") => props.TryGetValue(k, out var v) ? v : d;
            bool B(string k, bool d) => props.TryGetValue(k, out var v) ? v.Equals("true", StringComparison.OrdinalIgnoreCase) : d;
            var name = readOnly.TryGetValue("legacy-world-name", out var legacy) && legacy.Length > 0 ? legacy : key[(key.LastIndexOf(':') + 1)..];
            worlds.Add(new MvWorld(name, readOnly.GetValueOrDefault("environment", "normal"), P("alias"), P("gamemode", "survival"), P("difficulty", "normal"),
                B("pvp", true), B("allow-flight", false), B("hunger", true), B("allow-weather", true), B("hidden", false), B("auto-load", true),
                int.TryParse(P("player-limit", "-1"), out var limit) ? limit : -1, readOnly.GetValueOrDefault("seed", "")));
        }
        foreach (var raw in File.ReadAllLines(WorldsFile))
        {
            if (raw.TrimStart().StartsWith('#') || raw.Trim().Length == 0) continue;
            var indent = raw.Length - raw.TrimStart().Length;
            var line = raw.Trim();
            var colon = line.IndexOf(": ", StringComparison.Ordinal);
            if (indent == 0 && line.EndsWith(':'))
            {
                Done();
                key = Unquote(line[..^1]);
                section = null;
                props = new();
                readOnly = new();
            }
            else if (indent == 2)
            {
                if (line.EndsWith(':')) section = line[..^1];
                else if (colon > 0) { props[line[..colon]] = Unquote(line[(colon + 2)..]); section = null; }
            }
            else if (indent == 4 && section == "read-only" && colon > 0) readOnly[line[..colon]] = Unquote(line[(colon + 2)..]);
        }
        Done();
        return worlds;
    }

    public static List<MvGroup> ReadGroups()
    {
        var groups = new List<MvGroup>();
        if (!File.Exists(GroupsFile)) return groups;
        MvGroup? group = null;
        string? section = null;
        var inGroups = false;
        foreach (var raw in File.ReadAllLines(GroupsFile))
        {
            if (raw.TrimStart().StartsWith('#') || raw.Trim().Length == 0) continue;
            var indent = raw.Length - raw.TrimStart().Length;
            var line = raw.Trim();
            if (indent == 0) { inGroups = line == "groups:"; continue; }
            if (!inGroups) continue;
            if (indent == 2 && line.EndsWith(':')) { group = new MvGroup(Unquote(line[..^1]), new(), new(), new()); groups.Add(group); section = null; }
            else if (indent == 4 && line.EndsWith(':')) section = line[..^1];
            else if (group != null && line.StartsWith("- "))
            {
                var item = Unquote(line[2..]);
                if (section == "worlds") group.Worlds.Add(item);
                else if (section == "shares") group.Shares.Add(item);
                else if (section == "disabled-shares") group.DisabledShares.Add(item);
            }
            else if (group != null && indent == 4 && line.EndsWith("[]")) section = null;   // "worlds: []"
        }
        return groups;
    }

    static string Unquote(string v)
    {
        v = v.Trim();
        if (v.Length >= 2 && (v[0] == '\'' && v[^1] == '\'' || v[0] == '"' && v[^1] == '"'))
            v = v[0] == '\'' ? v[1..^1].Replace("''", "'") : v[1..^1];
        return v;
    }

    /// <summary>A name Multiverse and Windows both accept for a new world.</summary>
    public static bool ValidName(string name) => System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z0-9_-]{1,32}$");

    /// <summary>Without colour codes, for showing an alias as plain text.</summary>
    public static string Plain(string text) => System.Text.RegularExpressions.Regex.Replace(text, "[&§](#[0-9a-fA-F]{6}|[0-9a-fk-orA-FK-OR])", "");
}
