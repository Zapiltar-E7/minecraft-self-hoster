using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MinecraftHost.Core;

/// <summary>
/// Turns on Multiverse-Inventories' advancements share with everyone's current advancements kept by the main worlds'
/// group, wherever each player is. Done with the server stopped, on the files:
///   - a player in the main group needs nothing: Multiverse saves their advancements to that group when they next leave it;
///   - a player anywhere else gets their advancements written into the main group's profile for them (Multiverse's form:
///     the list of awarded criteria), and cleared in the game's own file, so the group they're in starts empty.
/// </summary>
public static class GroupAdvancements
{
    static string Inv => Path.Combine(Paths.ServerDir, "plugins", "Multiverse-Inventories");

    /// <summary>The level's player folders: Minecraft 26+ keeps them under players/, older versions at the top.</summary>
    public static (string Data, string Advancements, string Stats) PlayerDirs(string level)
    {
        var root = Path.Combine(Paths.ServerDir, level);
        return Directory.Exists(Path.Combine(root, "players", "data"))
            ? (Path.Combine(root, "players", "data"), Path.Combine(root, "players", "advancements"), Path.Combine(root, "players", "stats"))
            : (Path.Combine(root, "playerdata"), Path.Combine(root, "advancements"), Path.Combine(root, "stats"));
    }

    /// <summary>The main worlds' group: the one level-name is in.</summary>
    public static MvGroup? MainGroup(string level) =>
        Multiverse.ReadGroups().FirstOrDefault(g => g.Worlds.Contains(level, StringComparer.OrdinalIgnoreCase));

    /// <returns>The players whose advancements were moved to the main group, with the world each is in.</returns>
    public static List<string> TurnOn(string level)
    {
        var main = MainGroup(level) ?? throw new InvalidOperationException($"{level} isn't in a world group, so there's no group to keep the current advancements.");
        if (!Multiverse.SharedIds(main.Shares).Contains("advancements"))
            throw new InvalidOperationException($"The {main.Name} group doesn't share advancements. Tick Advancements in its group settings first.");
        var (dataDir, advDir, _) = PlayerDirs(level);
        var uids = WorldUids();
        var names = Names();
        var moved = new List<string>();
        foreach (var advFile in Directory.Exists(advDir) ? Directory.GetFiles(advDir, "*.json") : Array.Empty<string>())
        {
            var uuid = Path.GetFileNameWithoutExtension(advFile);
            var dat = Path.Combine(dataDir, uuid + ".dat");
            if (!File.Exists(dat)) continue;
            var world = LastWorld(dat, level, uids);
            if (world == null || main.Worlds.Contains(world, StringComparer.OrdinalIgnoreCase)) continue;
            if (!names.TryGetValue(uuid, out var name)) continue;   // Multiverse keeps profiles by name

            var vanilla = JsonNode.Parse(File.ReadAllText(advFile))!.AsObject();
            var criteria = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var (key, value) in vanilla)
                if (key != "DataVersion" && value?["criteria"] is JsonObject c)
                    foreach (var (criterion, _) in c) criteria.Add(criterion);

            var profileFile = Path.Combine(Inv, "groups", main.Name, name + ".json");
            var profile = File.Exists(profileFile) ? JsonNode.Parse(File.ReadAllText(profileFile))!.AsObject() : new JsonObject();
            if (profile["SURVIVAL"] is not JsonObject survival) profile["SURVIVAL"] = survival = new JsonObject();
            survival["advancements"] = new JsonArray(criteria.Select(c => (JsonNode)JsonValue.Create(c)!).ToArray());
            Directory.CreateDirectory(Path.GetDirectoryName(profileFile)!);
            File.WriteAllText(profileFile, profile.ToJsonString());

            // where they are now starts with none (the game's own file, keeping its data version)
            var empty = new JsonObject();
            if (vanilla["DataVersion"] is { } dv) empty["DataVersion"] = dv.DeepClone();
            File.WriteAllText(advFile, empty.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            moved.Add($"{name} ({world})");
        }
        SetActive("advancements", true);
        return moved;
    }

    /// <summary>Adds or removes an optional share in Multiverse-Inventories' config (server stopped), keeping the rest of the file.</summary>
    public static void SetActive(string share, bool on)
    {
        var file = Path.Combine(Inv, "config.yml");
        var text = File.ReadAllText(file);
        var eol = text.Contains("\r\n") ? "\r\n" : "\n";   // the file's own line endings
        var lines = text.Split(eol).ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        var head = lines.FindIndex(l => l.Trim().StartsWith("active-optional-shares:"));
        if (head < 0) throw new InvalidOperationException("Multiverse-Inventories' config has no active-optional-shares list.");
        if (lines[head].Trim() != "active-optional-shares:") throw new InvalidOperationException("Multiverse-Inventories' active-optional-shares isn't a plain list.");
        var end = head + 1;
        while (end < lines.Count && lines[end].TrimStart().StartsWith("- ")) end++;
        var items = lines.GetRange(head + 1, end - head - 1);
        var indent = items.Count > 0 ? items[0][..(items[0].Length - items[0].TrimStart().Length)] : lines[head][..(lines[head].Length - lines[head].TrimStart().Length)];
        var has = items.FindIndex(l => l.Trim()[2..].Trim().Trim('\'', '"') == share);
        if (on && has < 0) lines.Insert(end, indent + "- " + share);
        if (!on && has >= 0) lines.RemoveAt(head + 1 + has);
        File.WriteAllText(file, string.Join(eol, lines) + eol);
    }

    /// <summary>Player names by UUID: Multiverse's own record first (the name its profiles use), then the server's cache.</summary>
    static Dictionary<string, string> Names()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var cache = Path.Combine(Paths.ServerDir, "usercache.json");
        if (File.Exists(cache))
            foreach (var e in JsonNode.Parse(File.ReadAllText(cache))!.AsArray())
                if (e?["uuid"]?.GetValue<string>() is { } u && e["name"]?.GetValue<string>() is { } n) names[u] = n;
        var players = Path.Combine(Inv, "players");
        if (Directory.Exists(players))
            foreach (var f in Directory.GetFiles(players, "*.json"))
                try
                {
                    if (JsonNode.Parse(File.ReadAllText(f))?["playerData"]?["lastKnownName"]?.GetValue<string>() is { Length: > 0 } n)
                        names[Path.GetFileNameWithoutExtension(f)] = n;
                }
                catch { }
        return names;
    }

    /// <summary>Each world folder's uid.dat (older layouts): the id a player file names its world by.</summary>
    static Dictionary<(long, long), string> WorldUids()
    {
        var map = new Dictionary<(long, long), string>();
        foreach (var dir in Directory.GetDirectories(Paths.ServerDir))
        {
            var uid = Path.Combine(dir, "uid.dat");
            if (!File.Exists(uid)) continue;
            var b = File.ReadAllBytes(uid);
            if (b.Length < 16) continue;
            map[(System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(b), System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(b.AsSpan(8)))] = Path.GetFileName(dir);
        }
        return map;
    }

    /// <summary>The world a player file says they're in: the world id (older layouts), or the dimension (26+, where every world is one).</summary>
    public static string? LastWorld(string datFile, string level, Dictionary<(long, long), string>? uids = null)
    {
        using var gz = new GZipStream(File.OpenRead(datFile), CompressionMode.Decompress);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        var top = Nbt.TopLevel(ms.ToArray());
        uids ??= WorldUids();
        if (top.TryGetValue("WorldUUIDMost", out var most) && top.TryGetValue("WorldUUIDLeast", out var least)
            && uids.TryGetValue(((long)most, (long)least), out var byId)) return byId;
        if (top.TryGetValue("Dimension", out var d) && d is string dim)
        {
            var path = dim.Contains(':') ? dim[(dim.IndexOf(':') + 1)..] : dim;
            return path switch { "overworld" => level, "the_nether" => level + "_nether", "the_end" => level + "_the_end", _ => path };
        }
        return null;
    }

    /// <summary>Just enough NBT to read a player file's top-level strings and numbers.</summary>
    static class Nbt
    {
        public static Dictionary<string, object> TopLevel(byte[] data)
        {
            var pos = 0;
            var result = new Dictionary<string, object>();
            if (data[pos++] != 10) return result;
            Str(data, ref pos);
            while (true)
            {
                var type = data[pos++];
                if (type == 0) break;
                var name = Str(data, ref pos);
                var start = pos;
                Skip(data, ref pos, type);
                if (type == 8) { var p = start; result[name] = Str(data, ref p); }
                else if (type == 4) result[name] = System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(data.AsSpan(start));
            }
            return result;
        }

        static int I32(byte[] d, int p) => System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(p));

        static string Str(byte[] d, ref int pos)
        {
            var n = (d[pos] << 8) | d[pos + 1];
            var s = System.Text.Encoding.UTF8.GetString(d, pos + 2, n);
            pos += 2 + n;
            return s;
        }

        static void Skip(byte[] d, ref int pos, byte type)
        {
            switch (type)
            {
                case 1: pos += 1; break;
                case 2: pos += 2; break;
                case 3: case 5: pos += 4; break;
                case 4: case 6: pos += 8; break;
                case 7: pos += 4 + I32(d, pos); break;
                case 8: pos += 2 + ((d[pos] << 8) | d[pos + 1]); break;
                case 9:
                {
                    var inner = d[pos]; var n = I32(d, pos + 1); pos += 5;
                    for (var i = 0; i < n; i++) Skip(d, ref pos, inner);
                    break;
                }
                case 10:
                    while (true)
                    {
                        var t = d[pos++];
                        if (t == 0) break;
                        pos += 2 + ((d[pos] << 8) | d[pos + 1]);
                        Skip(d, ref pos, t);
                    }
                    break;
                case 11: pos += 4 + 4 * I32(d, pos); break;
                case 12: pos += 4 + 8 * I32(d, pos); break;
                default: throw new InvalidDataException("Unknown NBT tag " + type);
            }
        }
    }
}
