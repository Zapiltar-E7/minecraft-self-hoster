using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MinecraftHost.Core;

public sealed record PlayerEntry(string Uuid, string Name);

/// <summary>
/// whitelist.json and ops.json, edited directly while the server is stopped.
/// While it runs, the window sends whitelist/op commands instead, because the server rewrites these files.
/// </summary>
public static class PlayerLists
{
    public static string WhitelistFile => Path.Combine(Paths.ServerDir, "whitelist.json");
    public static string OpsFile => Path.Combine(Paths.ServerDir, "ops.json");

    public static readonly Regex NameRx = new(@"^\.?[A-Za-z0-9_]{1,16}$");

    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static List<PlayerEntry> Read(string file) =>
        ReadArray(file).Select(n => new PlayerEntry(n?["uuid"]?.GetValue<string>() ?? "", n?["name"]?.GetValue<string>() ?? ""))
            .Where(p => p.Name.Length > 0).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();

    static JsonArray ReadArray(string file)
    {
        try
        {
            if (!File.Exists(file)) return new();
            // the server may have the file open while it runs
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonNode.Parse(stream) as JsonArray ?? new();
        }
        catch { return new(); }
    }

    /// <summary>The account's UUID and correctly-cased name, or null if no such account exists.</summary>
    public static async Task<PlayerEntry?> LookupAsync(string name)
    {
        if (GetProperty("online-mode") == "false") return new PlayerEntry(OfflineUuid(name), name);
        using var resp = await Net.Http.GetAsync($"https://api.mojang.com/users/profiles/minecraft/{Uri.EscapeDataString(name)}");
        if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent) return null;
        resp.EnsureSuccessStatusCode();
        var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync());
        var id = node?["id"]?.GetValue<string>();
        if (id is not { Length: 32 }) return null;
        return new PlayerEntry(Guid.ParseExact(id, "N").ToString(), node!["name"]?.GetValue<string>() ?? name);
    }

    /// <summary>The UUID an offline-mode server gives a name (a version 3 UUID of "OfflinePlayer:name").</summary>
    static string OfflineUuid(string name)
    {
        var h = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
        h[6] = (byte)((h[6] & 0x0f) | 0x30);
        h[8] = (byte)((h[8] & 0x3f) | 0x80);
        var hex = Convert.ToHexString(h).ToLowerInvariant();
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }

    public static void Add(string file, PlayerEntry player, bool op)
    {
        var list = ReadArray(file);
        if (list.Any(n => Same(n, player.Name) || n?["uuid"]?.GetValue<string>() == player.Uuid)) return;
        var entry = new JsonObject { ["uuid"] = player.Uuid, ["name"] = player.Name };
        if (op)
        {
            entry["level"] = int.TryParse(GetProperty("op-permission-level"), out var level) ? level : 4;
            entry["bypassesPlayerLimit"] = false;
        }
        list.Add(entry);
        Write(file, list);
    }

    public static void Remove(string file, string name)
    {
        var list = ReadArray(file);
        foreach (var n in list.Where(n => Same(n, name)).ToList()) list.Remove(n);
        Write(file, list);
    }

    static bool Same(JsonNode? n, string name) => string.Equals(n?["name"]?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase);

    static void Write(string file, JsonArray list)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, list.ToJsonString(Indented));
    }

    // ------------------------------------------------------------ server.properties

    static string PropertiesFile => Path.Combine(Paths.ServerDir, "server.properties");

    static bool _whitelistOn;

    /// <summary>Whether white-list=true; the last known value while the server has the file locked.</summary>
    public static bool WhitelistOn
    {
        get
        {
            var value = GetProperty("white-list");
            if (value != null) _whitelistOn = value == "true";
            return _whitelistOn;
        }
    }

    /// <summary>A value from server.properties, or null if it's missing or the server is writing the file right now.</summary>
    public static string? GetProperty(string key)
    {
        try
        {
            if (!File.Exists(PropertiesFile)) return null;
            using var stream = new FileStream(PropertiesFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
                if (line.StartsWith(key + "=")) return line[(key.Length + 1)..].Trim();
        }
        catch (IOException) { }
        return null;
    }

    /// <summary>Changes one line and leaves the rest of the file as it was.</summary>
    public static void SetProperty(string key, string value)
    {
        var text = File.Exists(PropertiesFile) ? File.ReadAllText(PropertiesFile) : "";
        var newline = text.Contains("\r\n") ? "\r\n" : "\n";   // Minecraft writes \n; keep whatever the file uses
        var lines = text.Length == 0 ? new List<string>() : text.TrimEnd('\r', '\n').Split(newline).ToList();
        var i = lines.FindIndex(l => l.StartsWith(key + "="));
        if (i >= 0) lines[i] = $"{key}={value}";
        else lines.Add($"{key}={value}");
        Directory.CreateDirectory(Paths.ServerDir);
        File.WriteAllText(PropertiesFile, string.Join(newline, lines) + newline);
    }
}
