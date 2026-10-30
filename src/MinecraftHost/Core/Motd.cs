using System.Text;

namespace MinecraftHost.Core;

/// <summary>
/// server.properties values are Java "properties" text: \uXXXX for anything outside ASCII (like the § colour sign),
/// and a backslash before = : # !. This turns them into plain text and back, and splits a MOTD into coloured parts.
/// </summary>
public static class Motd
{
    public static string Decode(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var sb = new StringBuilder();
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (c != '\\' || i == raw.Length - 1) { sb.Append(c); continue; }
            var n = raw[++i];
            if (n == 'u' && i + 4 < raw.Length && int.TryParse(raw.AsSpan(i + 1, 4), System.Globalization.NumberStyles.HexNumber, null, out var code))
            {
                sb.Append((char)code);
                i += 4;
            }
            else sb.Append(n switch { 'n' => '\n', 't' => '\t', 'r' => '\r', _ => n });
        }
        return sb.ToString();
    }

    public static string Encode(string text)
    {
        var sb = new StringBuilder();
        foreach (var c in text)
        {
            if (c == '\\') sb.Append("\\\\");
            else if (c == '\n') sb.Append("\\n");
            else if (c is '=' or ':' or '#' or '!') sb.Append('\\').Append(c);
            else if (c < 0x20 || c > 0x7E) sb.Append($"\\u{(int)c:X4}");
            else sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Minecraft's 16 chat colours, by their § code.</summary>
    public static readonly (char Code, string Name, string Hex)[] Colors =
    {
        ('0', "Black", "#000000"), ('1', "Dark blue", "#0000AA"), ('2', "Dark green", "#00AA00"), ('3', "Dark aqua", "#00AAAA"),
        ('4', "Dark red", "#AA0000"), ('5', "Dark purple", "#AA00AA"), ('6', "Gold", "#FFAA00"), ('7', "Grey", "#AAAAAA"),
        ('8', "Dark grey", "#555555"), ('9', "Blue", "#5555FF"), ('a', "Green", "#55FF55"), ('b', "Aqua", "#55FFFF"),
        ('c', "Red", "#FF5555"), ('d', "Light purple", "#FF55FF"), ('e', "Yellow", "#FFFF55"), ('f', "White", "#FFFFFF"),
    };

    public sealed record Part(string Text, string Hex, bool Bold, bool Italic, bool Underline, bool Strike);

    /// <summary>The MOTD as coloured runs, the way the multiplayer screen shows it (grey by default).</summary>
    public static List<Part> Parse(string text)
    {
        var parts = new List<Part>();
        string hex = "#AAAAAA";
        bool bold = false, italic = false, underline = false, strike = false;
        var run = new StringBuilder();
        void Flush() { if (run.Length > 0) parts.Add(new Part(run.ToString(), hex, bold, italic, underline, strike)); run.Clear(); }
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '§' && i + 1 < text.Length)
            {
                Flush();
                var code = char.ToLowerInvariant(text[++i]);
                var color = Colors.FirstOrDefault(c => c.Code == code);
                if (color.Hex != null) { hex = color.Hex; bold = italic = underline = strike = false; }   // a colour resets the styles
                else if (code == 'l') bold = true;
                else if (code == 'o') italic = true;
                else if (code == 'n') underline = true;
                else if (code == 'm') strike = true;
                else if (code == 'r') { hex = "#AAAAAA"; bold = italic = underline = strike = false; }
                continue;
            }
            run.Append(text[i]);
        }
        Flush();
        return parts;
    }
}
