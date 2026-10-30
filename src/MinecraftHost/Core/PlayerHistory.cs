using System.Text.Json;

namespace MinecraftHost.Core;

/// <summary>
/// When each player was last on and how long they've played, per server. The totals come from the world's own
/// statistics (world\players\stats\(uuid).json, the same "play time" players see in game), so they include
/// everything before the app was used; join and leave lines (kept in data\(server name) players.json) fill in
/// the session going on now and players the stats don't have yet.
/// </summary>
public static class PlayerHistory
{
    public sealed class Entry
    {
        public string Name { get; set; } = "";
        public DateTime FirstSeen { get; set; }
        public DateTime LastSeen { get; set; }
        public double TotalSeconds { get; set; }
        public int Sessions { get; set; }
    }

    static string File_ => Path.Combine(Paths.Root, "data", Paths.SafeName(Paths.ServerName) + " players.json");

    static Dictionary<string, Entry>? _entries;
    static string? _loadedFor;
    // who's on now, and since when
    static readonly Dictionary<string, DateTime> Online = new(StringComparer.OrdinalIgnoreCase);

    static Dictionary<string, Entry> Entries
    {
        get
        {
            if (_entries == null || _loadedFor != File_)
            {
                _loadedFor = File_;
                try
                {
                    _entries = File.Exists(File_)
                        ? (JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(File_)) ?? new()).ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase)
                        : new(StringComparer.OrdinalIgnoreCase);
                }
                catch { _entries = new(StringComparer.OrdinalIgnoreCase); }
            }
            return _entries;
        }
    }

    public static void Joined(string name)
    {
        var now = DateTime.Now;
        Online[name] = now;
        if (!Entries.TryGetValue(name, out var e)) Entries[name] = e = new Entry { Name = name, FirstSeen = now };
        e.Name = name;
        e.LastSeen = now;
        e.Sessions++;
        Save();
        Stats.Seen(name, Online.Count);
    }

    public static void Left(string name)
    {
        if (!Online.Remove(name, out var since)) return;
        var e = Entries.GetValueOrDefault(name);
        if (e == null) return;
        e.TotalSeconds += (DateTime.Now - since).TotalSeconds;
        Stats.Played(DateTime.Now - since);
        e.LastSeen = DateTime.Now;
        Save();
    }

    /// <summary>The server stopped: everyone still listed has left.</summary>
    public static void AllLeft()
    {
        foreach (var name in Online.Keys.ToList()) Left(name);
    }

    public static TimeSpan? OnlineFor(string name) => Online.TryGetValue(name, out var since) ? DateTime.Now - since : null;

    /// <summary>What's known about a player: the app's own record merged with the world's statistics.</summary>
    public static Entry? Get(string name)
    {
        var own = Entries.GetValueOrDefault(name);
        var world = WorldStats().GetValueOrDefault(name);
        if (world == null) return own;
        return new Entry
        {
            Name = own?.Name ?? world.Name,
            FirstSeen = own?.FirstSeen ?? default,
            LastSeen = own != null && own.LastSeen > world.Saved ? own.LastSeen : world.Saved,
            TotalSeconds = Math.Max(world.Seconds, own?.TotalSeconds ?? 0),
            Sessions = Math.Max(world.Visits, own?.Sessions ?? 0),
        };
    }

    /// <summary>Played time including the session going on now.</summary>
    public static TimeSpan Total(string name)
    {
        var world = WorldStats().GetValueOrDefault(name);
        if (world != null)
        {
            // the stats file is written at each autosave: add what's been played since then (or since joining)
            var extra = Online.TryGetValue(name, out var since) ? DateTime.Now - (since > world.Saved ? since : world.Saved) : TimeSpan.Zero;
            return TimeSpan.FromSeconds(Math.Max(world.Seconds + Math.Max(0, extra.TotalSeconds), (Entries.GetValueOrDefault(name)?.TotalSeconds ?? 0) + (OnlineFor(name)?.TotalSeconds ?? 0)));
        }
        return TimeSpan.FromSeconds((Entries.GetValueOrDefault(name)?.TotalSeconds ?? 0) + (OnlineFor(name)?.TotalSeconds ?? 0));
    }

    /// <summary>Players who've been on this server and aren't online now, most recent first.</summary>
    public static List<Entry> Recent(int count) =>
        Entries.Keys.Concat(WorldStats().Keys).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(n => !Online.ContainsKey(n)).Select(n => Get(n)!).Where(e => e != null)
            .OrderByDescending(e => e.LastSeen).Take(count).ToList();

    // ------------------------------------------------------------ the world's own statistics

    sealed record WorldPlayer(string Name, double Seconds, int Visits, DateTime Saved);
    static Dictionary<string, WorldPlayer>? _world;
    static DateTime _worldRead;
    static string? _worldFor;

    /// <summary>
    /// Play time per player from every world's stats folder (world\players\stats on Paper 26 and newer, world\stats
    /// before), named through usercache.json. Read again at most once a minute.
    /// </summary>
    static Dictionary<string, WorldPlayer> WorldStats()
    {
        if (_world != null && _worldFor == Paths.ServerDir && DateTime.Now - _worldRead < TimeSpan.FromMinutes(1)) return _world;
        _worldFor = Paths.ServerDir;
        _worldRead = DateTime.Now;
        var result = new Dictionary<string, WorldPlayer>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // names by uuid: the server's own lists (usercache forgets players after a month), then names looked up before
            var names = new Dictionary<string, string>(NameCache, StringComparer.OrdinalIgnoreCase);
            foreach (var list in new[] { "banned-players.json", "ops.json", "whitelist.json", "usercache.json" })
            {
                var path = Path.Combine(Paths.ServerDir, list);
                if (!File.Exists(path)) continue;
                try
                {
                    foreach (var e in JsonDocument.Parse(File.ReadAllText(path)).RootElement.EnumerateArray())
                        if (e.TryGetProperty("uuid", out var u) && e.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } nm) names[u.GetString()!] = nm;
                }
                catch { }
            }
            Unnamed.Clear();

            var dirs = Directory.Exists(Paths.ServerDir)
                ? Directory.GetDirectories(Paths.ServerDir).Where(d => File.Exists(Path.Combine(d, "level.dat")))
                    .SelectMany(w => new[] { Path.Combine(w, "players", "stats"), Path.Combine(w, "stats") }).Where(Directory.Exists)
                : Enumerable.Empty<string>();
            foreach (var file in dirs.SelectMany(d => Directory.GetFiles(d, "*.json")))
            {
                var uuid = Path.GetFileNameWithoutExtension(file);
                if (!names.TryGetValue(uuid, out var name)) { Unnamed.Add(uuid); continue; }
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var doc = JsonDocument.Parse(stream);
                if (!doc.RootElement.TryGetProperty("stats", out var stats) || !stats.TryGetProperty("minecraft:custom", out var custom)) continue;
                long ticks = custom.TryGetProperty("minecraft:play_time", out var t) ? t.GetInt64()
                           : custom.TryGetProperty("minecraft:play_one_minute", out var t2) ? t2.GetInt64() : 0;   // the name before 1.17
                var visits = custom.TryGetProperty("minecraft:leave_game", out var l) ? l.GetInt32() : 0;
                var player = new WorldPlayer(name, ticks / 20.0, visits, File.GetLastWriteTime(file));
                if (!result.TryGetValue(name, out var had) || had.Seconds < player.Seconds) result[name] = player;
            }
        }
        catch { }
        return _world = result;
    }

    /// <summary>Another server was picked: start from its own file.</summary>
    public static void Reset()
    {
        AllLeft();
        _entries = null;
        _world = null;
    }

    static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
            File.WriteAllText(File_, JsonSerializer.Serialize(Entries.Values.OrderBy(e => e.Name).ToList(), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    // ------------------------------------------------------------ names for players the server has forgotten

    static readonly HashSet<string> Unnamed = new(StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> Asked = new(StringComparer.OrdinalIgnoreCase);
    static Dictionary<string, string>? _nameCache;
    static string NameCacheFile => Path.Combine(Paths.Root, "data", "player-names.json");

    /// <summary>uuid → name, from earlier lookups (data\player-names.json, shared by all servers).</summary>
    static Dictionary<string, string> NameCache
    {
        get
        {
            if (_nameCache != null) return _nameCache;
            try { _nameCache = File.Exists(NameCacheFile) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(NameCacheFile)) : null; }
            catch { }
            return _nameCache ??= new(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Asks Mojang's profile service (the one Minecraft uses for names and skins) for the names of players who have
    /// stats in the world but aren't in any of the server's lists. Each uuid is asked once per run; answers are kept.
    /// Returns true when a name was found, so the list can be redrawn.
    /// </summary>
    public static async Task<bool> NameUnknownPlayersAsync()
    {
        WorldStats();
        var found = false;
        foreach (var uuid in Unnamed.Where(u => Asked.Add(u)).ToList())
        {
            try
            {
                var profile = await Net.GetJsonAsync($"https://sessionserver.mojang.com/session/minecraft/profile/{uuid.Replace("-", "")}");
                if (profile["name"]?.GetValue<string>() is { Length: > 0 } name)
                {
                    NameCache[uuid] = name;
                    found = true;
                }
            }
            catch { }   // offline, or an account that no longer exists
        }
        if (!found) return false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(NameCacheFile)!);
            File.WriteAllText(NameCacheFile, JsonSerializer.Serialize(NameCache, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
        _world = null;   // read the stats again with the new names
        return true;
    }

    /// <summary>Read the world's stats again now (not up to a minute later) and ask again for names not found before.</summary>
    public static void ReloadWorldStats()
    {
        _world = null;
        Asked.Clear();
    }

    public static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours} h ago";
        if (d.TotalDays < 2) return "yesterday";
        if (d.TotalDays < 30) return $"{(int)d.TotalDays} days ago";
        return t.ToString("d MMM yyyy");
    }

    public static string Duration(TimeSpan d) =>
        d.TotalDays >= 2 ? $"{(int)d.TotalDays} days {d.Hours} h"
        : d.TotalHours >= 1 ? $"{(int)d.TotalHours} h {d.Minutes} min"
        : d.TotalMinutes >= 1 ? $"{(int)d.TotalMinutes} min" : "under a minute";
}
