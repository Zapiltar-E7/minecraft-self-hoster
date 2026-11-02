using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MinecraftHost.Core;

/// <summary>A message the server posts in chat on a schedule (Server → Announcements).</summary>
public sealed record Announcement
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    /// <summary>The message, one or more lines. &amp;-codes for colours and styles work (&amp;#RRGGBB for any colour, &lt;gradient:#RRGGBB:#RRGGBB&gt;…&lt;/gradient&gt; for a gradient), and {map}, {address} and {online} are filled in when it's sent.</summary>
    public string Text { get; init; } = "";
    public int EveryMinutes { get; init; } = 30;
    public bool Enabled { get; init; } = true;
    /// <summary>Skip it while nobody is online, so an empty server's log isn't filled with it.</summary>
    public bool OnlyWithPlayers { get; init; } = true;
    /// <summary>"left", "center" or "right": chat has no alignment, so each line is moved over with spaces (Announcements.Pad).</summary>
    public string Align { get; init; } = "left";
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

    // a colour gradient over the text inside: <gradient:#RRGGBB:#RRGGBB>text</gradient>, two colours or more
    static readonly Regex GradientTag = new(@"<gradient((?::#[0-9a-f]{6}){2,})>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    const string GradientEnd = "</gradient>";

    /// <summary>The message split into styled pieces, the way chat shows it: a colour resets the styles, &amp;r resets everything.</summary>
    public static List<Piece> Parse(string text) => ParseRun(text.Replace("\r\n", "\n").Replace('\r', '\n').Trim());

    static List<Piece> ParseRun(string text)
    {
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
            var gradient = GradientTag.Match(text, i);
            if (gradient.Success && gradient.Index == i)
            {
                Flush();
                var from = i + gradient.Length;
                var end = text.IndexOf(GradientEnd, from, StringComparison.OrdinalIgnoreCase);
                var inner = end < 0 ? text[from..] : text[from..end];
                // the styles on before it carry in; after it, the colour and styles are as they were
                var styles = (bold ? "&l" : "") + (italic ? "&o" : "") + (underline ? "&n" : "") + (strike ? "&m" : "") + (obfuscated ? "&k" : "");
                var stops = gradient.Groups[1].Value.Split(':', StringSplitOptions.RemoveEmptyEntries);
                pieces.AddRange(Spread(ParseRun(styles + inner), stops));
                i = end < 0 ? text.Length : end + GradientEnd.Length;
                continue;
            }
            run.Append(text[i++]);
        }
        Flush();
        return pieces;
    }

    /// <summary>Colours each letter along the gradient (a placeholder counts as one); letters next to each other with the same colour and style stay one piece.</summary>
    static IEnumerable<Piece> Spread(List<Piece> pieces, string[] stops)
    {
        var rgb = stops.Select(s => (R: Convert.ToInt32(s[1..3], 16), G: Convert.ToInt32(s[3..5], 16), B: Convert.ToInt32(s[5..7], 16))).ToArray();
        var total = pieces.Sum(p => p.Token != null ? 1 : p.Text.Count(c => c != '\n'));
        var index = 0;
        string At()
        {
            var t = total <= 1 ? 0 : (double)index / (total - 1) * (rgb.Length - 1);
            var k = Math.Min((int)t, rgb.Length - 2);
            var f = t - k;
            int Mix(int a, int b) => (int)Math.Round(a + (b - a) * f);
            return $"#{Mix(rgb[k].R, rgb[k + 1].R):X2}{Mix(rgb[k].G, rgb[k + 1].G):X2}{Mix(rgb[k].B, rgb[k + 1].B):X2}";
        }
        var output = new List<Piece>();
        foreach (var p in pieces)
        {
            if (p.Token != null) { output.Add(p with { Color = At() }); index++; continue; }
            foreach (var ch in p.Text)
            {
                var color = ch == '\n' ? output.LastOrDefault()?.Color ?? At() : At();
                if (ch != '\n') index++;
                var last = output.Count > 0 ? output[^1] : null;
                if (last != null && last.Token == null && last.Color == color && last with { Text = "" } == p with { Text = "", Color = color })
                    output[^1] = last with { Text = last.Text + ch };
                else output.Add(p with { Text = ch.ToString(), Color = color });
            }
        }
        return output;
    }

    /// <summary>
    /// The command, or null when a placeholder has no value right now (no map link yet): then it's skipped rather than
    /// posting a stale or empty link. Each piece is its own text component with its style, so custom colours work and
    /// line breaks reach the console escaped inside the JSON (still one command). <paramref name="newClickFormat"/>:
    /// Minecraft 1.21.5 and later spell a link's click action differently.
    /// </summary>
    public static string? Command(string text, Func<string, string?> value, bool newClickFormat, string align = "left")
    {
        // the pieces with their placeholders filled in, split at line breaks so each line can be moved over on its own
        var lines = new List<List<(Piece P, string Shown)>> { new() };
        foreach (var p in Parse(text))
        {
            if (p.Token != null && value(p.Token) is not { Length: > 0 }) return null;
            var shown = p.Token != null ? value(p.Token)! : p.Text;
            var bits = p.Token != null ? new[] { shown } : shown.Split('\n');
            for (var b = 0; b < bits.Length; b++)
            {
                if (b > 0) lines.Add(new());
                if (bits[b].Length > 0) lines[^1].Add((p, bits[b]));
            }
        }
        var parts = new List<string> { "\"\"" };
        for (var l = 0; l < lines.Count; l++)
        {
            if (l > 0) parts.Add(JsonSerializer.Serialize(new Dictionary<string, object> { ["text"] = "\n" }, Json));
            var pad = Pad(lines[l].Select(x => (x.Shown, x.P.Bold)), align);
            if (pad.Length > 0) parts.Add(JsonSerializer.Serialize(new Dictionary<string, object> { ["text"] = pad }, Json));
            foreach (var (p, shown) in lines[l]) parts.Add(Component(p, shown, newClickFormat));
        }
        return "tellraw @a [" + string.Join(",", parts) + "]";
    }

    static string Component(Piece p, string shown, bool newClickFormat)
    {
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
        return JsonSerializer.Serialize(c, Json);
    }

    // the chat's width in the font's pixels at the default chat width setting; a player's own narrower or wider chat moves it.
    // Centring is the Announcement plugin's <centered> tag's way, so messages line up the same as with it.
    const int ChatWidth = 320;

    /// <summary>Spaces that move a line to the middle or the right of chat (none for left, or when it's too long to move).</summary>
    public static string Pad(IEnumerable<(string Text, bool Bold)> line, string align)
    {
        if (align is not ("center" or "right")) return "";
        // each letter's width and the pixel after it, and one more for each bold letter (spaces too)
        var width = line.Sum(x => x.Text.Sum(ch => CharWidth(ch) + (x.Bold ? 1 : 0)));
        var space = CharWidth(' ');
        if (align == "right")   // whole spaces only, so the line never runs over and wraps
            return ChatWidth - width > 0 ? new string(' ', (ChatWidth - width) / space) : "";
        var half = (ChatWidth - width) / 2;
        return half > 0 ? new string(' ', (half + space - 1) / space) : "";   // spaces until half the room is filled
    }

    /// <summary>A letter's width in Minecraft's chat font, the pixel after it included.</summary>
    static int CharWidth(char c) => c switch
    {
        '!' or '\'' or ',' or '.' or ':' or ';' or '|' or 'i' or 'ı' => 2,
        '`' or 'l' => 3,
        ' ' or '"' or '(' or ')' or '*' or 'I' or '[' or ']' or 't' or '{' or '}' or 'İ' => 4,
        '<' or '>' or 'f' or 'k' => 5,
        '@' => 7,
        _ => 6,
    };

    /// <summary>A piece's colour for showing it on screen ("#RRGGBB"), chat's white when it has none.</summary>
    public static string Hex(string? color) =>
        color == null ? "#FFFFFF" : color.StartsWith('#') ? color : Motd.Colors.First(c => c.Code == color[0]).Hex;

    /// <summary>The message as typed, without formatting codes (placeholders kept), for the list.</summary>
    public static string WithoutCodes(string text) => Code.Replace(GradientTag.Replace(text, "").Replace(GradientEnd, "", StringComparison.OrdinalIgnoreCase), "");

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
