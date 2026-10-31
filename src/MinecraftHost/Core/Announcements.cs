using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MinecraftHost.Core;

/// <summary>A message the server posts in chat on a schedule (Server → Announcements).</summary>
public sealed record Announcement
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    /// <summary>The message, one or more lines. &amp;-codes for colours and styles work (&amp;#RRGGBB for any colour), and {map}, {address} and {online} are filled in when it's sent.</summary>
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
    // a formatting code: &a or §a (colours 0-f, k obfuscated, l bold, m strikethrough, n underline, o italic, r reset),
    // or a custom colour &#RRGGBB (Minecraft 1.16 and later)
    static readonly Regex Code = new("[&§](?:#([0-9a-f]{6})|([0-9a-fk-or]))", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Chat colour names by code, as the JSON text format names them.</summary>
    static readonly Dictionary<char, string> ColorNames = new()
    {
        ['0'] = "black", ['1'] = "dark_blue", ['2'] = "dark_green", ['3'] = "dark_aqua", ['4'] = "dark_red", ['5'] = "dark_purple",
        ['6'] = "gold", ['7'] = "gray", ['8'] = "dark_gray", ['9'] = "blue", ['a'] = "green", ['b'] = "aqua",
        ['c'] = "red", ['d'] = "light_purple", ['e'] = "yellow", ['f'] = "white",
    };

    /// <summary>
    /// A piece of the message with one style. <see cref="Color"/>: a colour code ('0'-'f'), "#RRGGBB", or null for chat's
    /// default. <see cref="Token"/>: the placeholder this piece stands for ("map", "address", "online"), or null for text.
    /// </summary>
    public sealed record Piece(string Text, string? Color, bool Bold, bool Italic, bool Underline, bool Strike, bool Obfuscated, string? Token);

    /// <summary>The message split into styled pieces, the way chat shows it: a colour resets the styles, &amp;r resets everything.</summary>
    public static List<Piece> Parse(string text)
    {
        text = text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        var pieces = new List<Piece>();
        string? color = null;
        bool bold = false, italic = false, underline = false, strike = false, obfuscated = false;
        var run = new System.Text.StringBuilder();
        void Flush() { if (run.Length > 0) pieces.Add(new Piece(run.ToString(), color, bold, italic, underline, strike, obfuscated, null)); run.Clear(); }
        for (var i = 0; i < text.Length;)
        {
            var code = Code.Match(text, i);
            if (code.Success && code.Index == i)
            {
                Flush();
                if (code.Groups[1].Success) { color = "#" + code.Groups[1].Value.ToUpperInvariant(); bold = italic = underline = strike = obfuscated = false; }
                else
                {
                    var c = char.ToLowerInvariant(code.Groups[2].Value[0]);
                    if (ColorNames.ContainsKey(c)) { color = c.ToString(); bold = italic = underline = strike = obfuscated = false; }
                    else if (c == 'l') bold = true;
                    else if (c == 'o') italic = true;
                    else if (c == 'n') underline = true;
                    else if (c == 'm') strike = true;
                    else if (c == 'k') obfuscated = true;
                    else { color = null; bold = italic = underline = strike = obfuscated = false; }   // r
                }
                i += code.Length;
                continue;
            }
            var token = Token.Match(text, i);
            if (token.Success && token.Index == i)
            {
                Flush();
                pieces.Add(new Piece(token.Value, color, bold, italic, underline, strike, obfuscated, token.Groups[1].Value));
                i += token.Length;
                continue;
            }
            run.Append(text[i++]);
        }
        Flush();
        return pieces;
    }

    /// <summary>
    /// The command, or null when a placeholder has no value right now (no map link yet): then it's skipped rather than
    /// posting a stale or empty link. Each piece is its own text component with its style, so custom colours work and
    /// line breaks reach the console escaped inside the JSON (still one command). <paramref name="newClickFormat"/>:
    /// Minecraft 1.21.5 and later spell a link's click action differently.
    /// </summary>
    public static string? Command(string text, Func<string, string?> value, bool newClickFormat)
    {
        var parts = new List<string> { "\"\"" };
        foreach (var p in Parse(text))
        {
            var shown = p.Text;
            if (p.Token != null && value(p.Token) is not { Length: > 0 } v) return null;
            else if (p.Token != null) shown = value(p.Token)!;
            var c = new Dictionary<string, object> { ["text"] = shown };
            if (p.Color != null) c["color"] = p.Color.StartsWith('#') ? p.Color : ColorNames[p.Color[0]];
            if (p.Bold) c["bold"] = true;
            if (p.Italic) c["italic"] = true;
            if (p.Underline) c["underlined"] = true;
            if (p.Strike) c["strikethrough"] = true;
            if (p.Obfuscated) c["obfuscated"] = true;
            if (p.Token == "map")
            {
                // the link: aqua and underlined unless a colour was chosen for it, and clickable
                c.TryAdd("color", "aqua");
                c["underlined"] = true;
                c[newClickFormat ? "click_event" : "clickEvent"] = newClickFormat
                    ? new Dictionary<string, string> { ["action"] = "open_url", ["url"] = shown }
                    : new Dictionary<string, string> { ["action"] = "open_url", ["value"] = shown };
            }
            parts.Add(JsonSerializer.Serialize(c, Json));
        }
        return "tellraw @a [" + string.Join(",", parts) + "]";
    }

    /// <summary>A piece's colour for showing it on screen ("#RRGGBB"), chat's white when it has none.</summary>
    public static string Hex(string? color) =>
        color == null ? "#FFFFFF" : color.StartsWith('#') ? color : Motd.Colors.First(c => c.Code == color[0]).Hex;

    /// <summary>The message as typed, without formatting codes (placeholders kept), for the list.</summary>
    public static string WithoutCodes(string text) => Code.Replace(text, "");

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
