using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MinecraftHost.Core;

/// <summary>A message the server posts in chat on a schedule (Server → Announcements).</summary>
public sealed record Announcement
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    /// <summary>The message. &amp;-colour codes work, and {map}, {address} and {online} are filled in when it's sent.</summary>
    public string Text { get; init; } = "";
    public int EveryMinutes { get; init; } = 30;
    public bool Enabled { get; init; } = true;
    /// <summary>Skip it while nobody is online, so an empty server's log isn't filled with it.</summary>
    public bool OnlyWithPlayers { get; init; } = true;
}

/// <summary>Turns an announcement into the tellraw command that posts it, the map link clickable.</summary>
public static class Announcements
{
    public const string MapToken = "{map}", AddressToken = "{address}", OnlineToken = "{online}";

    static readonly Regex Token = new(@"\{(map|address|online)\}", RegexOptions.Compiled);
    static readonly Regex AmpCode = new("&([0-9a-fk-or])", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// The command, or null when a placeholder has no value right now (no map link yet): then it's skipped rather than
    /// posting a stale or empty link. <paramref name="newClickFormat"/>: Minecraft 1.21.5 and later spell a link's click
    /// action differently.
    /// </summary>
    public static string? Command(string text, Func<string, string?> value, bool newClickFormat)
    {
        text = AmpCode.Replace(text.Replace("\r", " ").Replace("\n", " "), "§$1");   // one console line; &a works like §a
        var parts = new List<string> { "\"\"" };
        var last = 0;
        foreach (Match m in Token.Matches(text))
        {
            if (m.Index > last) parts.Add(Plain(text[last..m.Index]));
            var name = m.Groups[1].Value;
            if (value(name) is not { Length: > 0 } v) return null;
            parts.Add(name == "map" ? Link(v, newClickFormat) : Plain(v));
            last = m.Index + m.Length;
        }
        if (last < text.Length) parts.Add(Plain(text[last..]));
        return "tellraw @a [" + string.Join(",", parts) + "]";
    }

    /// <summary>What players will read, for the editor's preview (placeholders filled in, or shown as missing).</summary>
    public static string Preview(string text, Func<string, string?> value) =>
        Token.Replace(AmpCode.Replace(text, ""), m => value(m.Groups[1].Value) ?? m.Groups[1].Value switch
        {
            "map" => "(the map link, once there is one)",
            "address" => "(the join address)",
            _ => "(players online)",
        }).Replace("§", "");

    /// <summary>The message as typed, without colour codes (placeholders kept), for the list.</summary>
    public static string WithoutCodes(string text) => Regex.Replace(AmpCode.Replace(text, ""), "§[0-9a-fk-or]", "", RegexOptions.IgnoreCase);

    static string Plain(string s) => JsonSerializer.Serialize(new Dictionary<string, object> { ["text"] = s }, Json);

    static string Link(string url, bool newFormat) => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["text"] = url,
        ["color"] = "aqua",
        ["underlined"] = true,
        [newFormat ? "click_event" : "clickEvent"] = newFormat
            ? new Dictionary<string, string> { ["action"] = "open_url", ["url"] = url }
            : new Dictionary<string, string> { ["action"] = "open_url", ["value"] = url },
    }, Json);

    /// <summary>Minecraft 1.21.5 changed text components' click events (and the year-numbered versions after it keep that).</summary>
    public static bool NewClickFormat(string? version)
    {
        if (string.IsNullOrEmpty(version) || !version.StartsWith("1.")) return true;
        var p = version.Split('.', '-', ' ');
        int.TryParse(p.ElementAtOrDefault(1), out var minor);
        int.TryParse(p.ElementAtOrDefault(2), out var patch);
        return minor > 21 || minor == 21 && patch >= 5;
    }

    public static string Every(int minutes) => minutes < 60 ? $"Every {minutes} min"
        : minutes % 60 == 0 ? $"Every {minutes / 60} h" : $"Every {minutes / 60} h {minutes % 60} min";
}
