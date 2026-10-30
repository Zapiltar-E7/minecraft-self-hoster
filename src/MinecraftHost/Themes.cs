using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MinecraftHost;

/// <summary>
/// Colour themes. Every colour the app uses is a brush in App.xaml's resources (the palette), referenced with
/// DynamicResource in XAML and by <c>Res("…")</c> in code. Switching theme changes those brushes' colours in place,
/// so everything on screen follows, with a short fade when animations are on.
/// </summary>
public static class Themes
{
    public sealed record Theme(string Name, bool Light, Dictionary<string, string> Colors);

    /// <summary>A highlight colour picked on its own, over any theme (Settings → Appearance → Highlight colour).</summary>
    public sealed record Highlight(string Name, string Hex);

    /// <summary>The highlight colours to pick from; none picked means the theme's own.</summary>
    public static readonly Highlight[] Highlights =
    {
        new("Sky", "#7CC4FF"), new("Blue", "#4C8DFF"), new("Indigo", "#6E7BFF"), new("Violet", "#8C7CFF"),
        new("Purple", "#B79CFF"), new("Orchid", "#D07CFF"), new("Pink", "#FF79C6"), new("Rose", "#FF8FA3"),
        new("Red", "#FF5C5C"), new("Coral", "#FF7E5F"), new("Orange", "#FFA14A"), new("Amber", "#F5C451"),
        new("Yellow", "#F2E14C"), new("Lime", "#B6E84A"), new("Green", "#6CD98A"), new("Emerald", "#3DDC84"),
        new("Mint", "#5EE6B0"), new("Teal", "#4FD1C5"), new("Cyan", "#5CE1F2"), new("Ice", "#A9E4FF"),
        new("Sand", "#E3B26B"), new("Copper", "#E5844F"), new("Silver", "#A9B4C2"), new("White", "#E8E8E8"),
    };

    /// <summary>A named highlight, or a custom one saved as "#RRGGBB" (the colour picker).</summary>
    public static Highlight? FindHighlight(string? name)
    {
        if (name is { Length: 7 } && name[0] == '#')
        {
            try { Parse(name); return new Highlight(name.ToUpperInvariant(), name.ToUpperInvariant()); }
            catch (FormatException) { return null; }
        }
        return Highlights.FirstOrDefault(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsCustom(Highlight? h) => h?.Name.StartsWith('#') == true;

    /// <summary>What the palette holds: the base keys each theme lists, the rest worked out from them.</summary>
    static readonly string[] Keys =
    {
        "Bg", "Card", "CardBorder", "Field", "FieldBorder", "Text", "Muted", "Accent", "AccentHover", "OnAccent",
        "Danger", "DangerHover", "OnDanger", "Neutral", "NeutralHover", "Track", "Knob", "Popup", "Selected", "Row",
        "Console", "ConsoleText", "Scrim", "ScrimStrong", "Warn", "WarnText", "WarnBg", "WarnBorder", "WarnEdge",
        "Bad", "Idle", "Unknown",
    };

    static Theme Dark(string name, string bg, string card, string cardBorder, string field, string fieldBorder, string text, string muted,
        string accent, string onAccent, string neutral, string neutralHover, string track, string knob, string popup, string row,
        string console, string consoleText) =>
        new(name, false, new()
        {
            ["Bg"] = bg, ["Card"] = card, ["CardBorder"] = cardBorder, ["Field"] = field, ["FieldBorder"] = fieldBorder,
            ["Text"] = text, ["Muted"] = muted, ["Accent"] = accent, ["OnAccent"] = onAccent, ["Neutral"] = neutral,
            ["NeutralHover"] = neutralHover, ["Track"] = track, ["Knob"] = knob, ["Popup"] = popup, ["Row"] = row,
            ["Console"] = console, ["ConsoleText"] = consoleText,
        });

    public static readonly Theme[] All =
    {
        // the original look: blue-grey with the light blue accent
        new("Default", false, new()
        {
            ["Bg"] = "#16171A", ["Card"] = "#1F2125", ["CardBorder"] = "#2A2D33", ["Field"] = "#26292E", ["FieldBorder"] = "#353941",
            ["Text"] = "#ECEDEF", ["Muted"] = "#8E939C", ["Accent"] = "#7CC4FF", ["AccentHover"] = "#9AD3FF", ["OnAccent"] = "#0E1A24",
            ["Danger"] = "#D83C3E", ["DangerHover"] = "#E5524F", ["OnDanger"] = "#FFFFFF", ["Neutral"] = "#30333A", ["NeutralHover"] = "#3B3F47",
            ["Track"] = "#3A3E46", ["Knob"] = "#C9CCD1", ["Popup"] = "#2B2E34", ["Selected"] = "#243445", ["Row"] = "#24272C",
            ["Console"] = "#121316", ["ConsoleText"] = "#C9CDD3", ["Scrim"] = "#B3101114", ["ScrimStrong"] = "#CC101114",
            ["Warn"] = "#F0B232", ["WarnText"] = "#F0D28A", ["WarnBg"] = "#2B2614", ["WarnBorder"] = "#5C4A12", ["WarnEdge"] = "#B8902A",
            ["Bad"] = "#FF8A8A", ["Idle"] = "#80848E", ["Unknown"] = "#5D626B",
        }),
        Dark("Dark", "#121212", "#1C1C1C", "#2A2A2A", "#242424", "#353535", "#EDEDED", "#9A9A9A", "#7CC4FF", "#0E1A24",
             "#2B2B2B", "#363636", "#3A3A3A", "#CFCFCF", "#262626", "#222222", "#0B0B0B", "#CFCFCF"),
        Dark("OLED", "#000000", "#0B0B0C", "#1D1E21", "#121315", "#2A2C30", "#F2F2F2", "#8C9097", "#7CC4FF", "#06121C",
             "#17181B", "#222428", "#2C2E33", "#C9CCD1", "#111214", "#0F1012", "#000000", "#C9CDD3"),
        new("Light", true, new()
        {
            ["Bg"] = "#F2F3F5", ["Card"] = "#FFFFFF", ["CardBorder"] = "#E1E3E8", ["Field"] = "#F7F8FA", ["FieldBorder"] = "#D3D7DD",
            ["Text"] = "#1D2025", ["Muted"] = "#626872", ["Accent"] = "#1F7FD1", ["AccentHover"] = "#3A92DD", ["OnAccent"] = "#FFFFFF",
            ["Danger"] = "#D43B3D", ["DangerHover"] = "#C2302F", ["Neutral"] = "#E9EBEF", ["NeutralHover"] = "#DEE1E6",
            ["Track"] = "#C6CAD1", ["Knob"] = "#FFFFFF", ["Popup"] = "#FFFFFF", ["Row"] = "#F4F5F7",
            ["Console"] = "#F7F8FA", ["ConsoleText"] = "#2A2E35", ["Scrim"] = "#66101114", ["ScrimStrong"] = "#99101114",
            ["Warn"] = "#C27C0E", ["WarnText"] = "#875600", ["WarnBg"] = "#FFF5DC", ["WarnBorder"] = "#F0D28A", ["WarnEdge"] = "#D9A53A",
            ["Bad"] = "#C93C3C", ["Idle"] = "#7A8089", ["Unknown"] = "#A6ABB3",
        }),
        Dark("Creeper", "#121915", "#19231C", "#253328", "#1F2B22", "#2F3F32", "#E8EFE9", "#8E9D91", "#6CD98A", "#0B1F10",
             "#253228", "#2E3D31", "#34453A", "#CAD4CC", "#212C24", "#1D2720", "#0E1410", "#C5D1C7"),
        Dark("Nether", "#1A1213", "#24191A", "#372627", "#2C1F20", "#402D2F", "#F2E8E8", "#A38E90", "#FF7D6E", "#2A0D09",
             "#342527", "#402E30", "#4A3437", "#D9CBCC", "#2E2122", "#281C1D", "#140D0E", "#D8C8C9"),
        Dark("Amethyst", "#16131C", "#1F1B27", "#2D2738", "#252030", "#383047", "#EEEAF4", "#988FA7", "#B79CFF", "#1A1030",
             "#2C2637", "#372F44", "#3E364B", "#D0CAD9", "#292333", "#221D2B", "#110E16", "#CFC8DA"),
        Dark("Gold", "#18160F", "#211E15", "#322D1F", "#29251A", "#3B3524", "#F3EEE2", "#A39B87", "#F5C451", "#2A1F05",
             "#302B1E", "#3B3525", "#463F2C", "#D8D1BF", "#2A261B", "#25211A", "#110F0A", "#D6CFBD"),
        Make("Diamond", "#111A1C", "#5CF2E5"),
        Make("Emerald", "#101A14", "#3DDC84"),
        Make("Lapis", "#11141F", "#4C7DFF"),
        Make("Redstone", "#1A1111", "#FF4D4D"),
        Make("Copper", "#1A1411", "#E5844F"),
        Make("Prismarine", "#0F1A19", "#63C5B0"),
        Make("Sculk", "#081A20", "#29DFEB"),
        Make("The End", "#14121A", "#E9E4A6"),
        Make("Netherite", "#161415", "#B39DA0"),
        Make("Mushroom", "#1A1213", "#E06666"),
        Make("Cherry Grove", "#1C1418", "#FF9FC7"),
        Make("Deep Dark", "#05090C", "#1FB8C6"),
        Make("Glowstone", "#1A1710", "#FFD15C"),
        Make("Slime", "#111A10", "#8BE36A"),
        Make("Enchanted", "#16111F", "#C77DFF"),
        Make("Ender Pearl", "#0D1716", "#3FBFA8"),
        Make("Blaze", "#1A140D", "#FFA62B"),
        Make("Ice Spikes", "#111820", "#9FE3FF"),
        Make("Ocean", "#0E171C", "#4FD1C5"),
        Make("Midnight", "#0D1222", "#6E9BFF"),
        Make("Sakura", "#1B1418", "#FF8FC0"),
        Make("Lava", "#1A120D", "#FF8A3D"),
        Make("Mint", "#0F1A16", "#5EE6B0"),
        Make("Synthwave", "#170F22", "#FF5CA8"),
        Make("Cyberpunk", "#120F1A", "#F7E733"),
        Make("Forest", "#111710", "#A3D977"),
        Make("Desert", "#1A1712", "#E3B26B"),
        Make("Arctic", "#101820", "#8FD3FF"),
        Make("Coffee", "#18130F", "#C69C6D"),
        Make("Lavender", "#16131D", "#B9A3F5"),
        Make("Rose Gold", "#1C1515", "#F2A7A0"),
        Make("Slate", "#15181D", "#A0AEC0"),
        Make("Crimson", "#190D11", "#E5384F"),
        Make("Sunset", "#1A1214", "#FF7E5F"),
        Make("Aurora", "#0E1619", "#7CF5C4"),
        Make("Galaxy", "#0F0E1C", "#8C7CFF"),
        Make("Toxic", "#10140C", "#B6FF3B"),
        Make("Bubblegum", "#1A1219", "#FF79C6"),
        Make("Steel", "#14171A", "#7FA7C9"),
        Make("Royal", "#11102A", "#E8C25C"),
        Make("Matrix", "#050A06", "#3BFF6E"),
        Make("Mono", "#141414", "#E0E0E0"),
        Make("High Contrast", "#000000", "#FFD60A"),
        Make("Dracula", "#282A36", "#BD93F9"),
        Make("Nord", "#2E3440", "#88C0D0"),
        Make("Solarized Dark", "#002B36", "#2AA198"),
        Make("Monokai", "#272822", "#A6E22E"),
        Make("Gruvbox", "#282828", "#FABD2F"),
        Make("Catppuccin", "#1E1E2E", "#CBA6F7"),
        Make("Tokyo Night", "#1A1B26", "#7AA2F7"),
        Make("One Dark", "#282C34", "#61AFEF"),
        Make("GitHub Dark", "#0D1117", "#58A6FF"),
        Make("Everforest", "#2B3339", "#A7C080"),
        Make("Rose Pine", "#191724", "#EBBCBA"),
        Make("Kanagawa", "#1F1F28", "#7E9CD8"),
        Make("Paper", "#F5F1E8", "#A85A1B", light: true),
        Make("Sky", "#EEF4FA", "#2B7BD8", light: true),
        Make("Mint Light", "#ECF6F1", "#1E9468", light: true),
        Make("Rose Light", "#FBF0F3", "#C93F74", light: true),
        Make("Lavender Light", "#F2EFF9", "#6D4FE0", light: true),
        Make("Solarized Light", "#FDF6E3", "#268BD2", light: true),
        Make("Sand", "#F6F0E4", "#9A6A1E", light: true),
        Make("Snow", "#FAFBFC", "#3A6FD8", light: true),
        Make("Peach", "#FCF1EA", "#D2582A", light: true),
        Make("Lemon", "#FBF8E6", "#8A7400", light: true),
        Make("Sage", "#EFF3EC", "#4F7A3A", light: true),
        Make("Cloud", "#F0F2F5", "#5B6B80", light: true),
    };

    /// <summary>A whole theme from a background and an accent: cards, fields and text are worked out from them.</summary>
    static Theme Make(string name, string bgHex, string accentHex, bool light = false)
    {
        Color bg = Parse(bgHex), accent = Parse(accentHex), white = Colors.White, black = Colors.Black;
        string H(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
        var onAccent = OnAccent(accent);
        if (!light)
        {
            var ink = Parse("#F0F1F3");
            return new(name, false, new()
            {
                ["Bg"] = H(bg), ["Card"] = H(Mix(bg, white, 0.045)), ["CardBorder"] = H(Mix(bg, white, 0.10)),
                ["Field"] = H(Mix(bg, white, 0.075)), ["FieldBorder"] = H(Mix(bg, white, 0.14)),
                ["Text"] = H(Mix(ink, bg, 0.06)), ["Muted"] = H(Mix(ink, bg, 0.44)),
                ["Accent"] = H(accent), ["OnAccent"] = H(onAccent),
                ["Neutral"] = H(Mix(bg, white, 0.11)), ["NeutralHover"] = H(Mix(bg, white, 0.16)),
                ["Track"] = H(Mix(bg, white, 0.19)), ["Knob"] = H(Mix(ink, bg, 0.14)),
                ["Popup"] = H(Mix(bg, white, 0.09)), ["Row"] = H(Mix(bg, white, 0.065)),
                ["Console"] = H(Mix(bg, black, 0.35)), ["ConsoleText"] = H(Mix(ink, bg, 0.14)),
            });
        }
        var text = Parse("#16181C");
        var t = new Dictionary<string, string>
        {
            ["Bg"] = H(bg), ["Card"] = H(Mix(bg, white, 0.85)), ["CardBorder"] = H(Mix(bg, black, 0.08)),
            ["Field"] = H(Mix(bg, white, 0.55)), ["FieldBorder"] = H(Mix(bg, black, 0.14)),
            ["Text"] = H(Mix(text, bg, 0.04)), ["Muted"] = H(Mix(text, bg, 0.45)),
            ["Accent"] = H(accent), ["AccentHover"] = H(Mix(accent, white, 0.14)), ["OnAccent"] = H(onAccent),
            ["Neutral"] = H(Mix(bg, black, 0.045)), ["NeutralHover"] = H(Mix(bg, black, 0.085)),
            ["Track"] = H(Mix(bg, black, 0.18)), ["Knob"] = "#FFFFFF",
            ["Popup"] = H(Mix(bg, white, 0.85)), ["Row"] = H(Mix(bg, white, 0.4)),
            ["Console"] = H(Mix(bg, white, 0.6)), ["ConsoleText"] = H(Mix(text, bg, 0.1)),
            ["Danger"] = "#D43B3D", ["DangerHover"] = "#C2302F",
            ["Scrim"] = "#66101114", ["ScrimStrong"] = "#99101114",
            ["Warn"] = "#C27C0E", ["WarnText"] = "#875600", ["WarnBg"] = "#FFF5DC", ["WarnBorder"] = "#F0D28A", ["WarnEdge"] = "#D9A53A",
            ["Bad"] = "#C93C3C",
        };
        return new(name, true, t);
    }

    public static Theme Find(string? name) => All.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? All[0];

    public static Theme Current { get; private set; } = All[0];

    /// <summary>The highlight colour picked over the theme, or null for the theme's own.</summary>
    public static Highlight? CurrentHighlight { get; private set; }

    /// <summary>Raised after a theme is applied, for colours code works out itself (the status pill, tags).</summary>
    public static event Action? Changed;

    static Dictionary<string, Color> _resolved = Resolve(All[0]);

    /// <summary>A palette colour of the current theme (its final value, even while the fade is still playing).</summary>
    public static Color Get(string key) => _resolved[key];

    /// <summary>Every palette colour of a theme with a highlight colour of its own (null: the theme's), the derived ones filled in.</summary>
    public static Dictionary<string, Color> Resolve(Theme t, Highlight? highlight)
    {
        if (highlight == null) return Resolve(t);
        var accent = Fit(Parse(highlight.Hex), t.Light);
        var colors = new Dictionary<string, string>(t.Colors) { ["Accent"] = Hex(accent), ["OnAccent"] = Hex(OnAccent(accent)) };
        colors.Remove("AccentHover");   // worked out again from the new accent
        colors.Remove("Selected");
        return Resolve(t with { Colors = colors });
    }

    static double Lum(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;

    /// <summary>Dark text on a bright accent, white on a deep one.</summary>
    static Color OnAccent(Color accent) => Lum(accent) > 0.5 ? Mix(accent, Colors.Black, 0.84) : Colors.White;

    /// <summary>A highlight readable on the theme: darkened on a light one, lightened on a dark one when it's too deep.</summary>
    static Color Fit(Color c, bool light)
    {
        for (var i = 0; i < 12 && (light ? Lum(c) > 0.5 : Lum(c) < 0.42); i++)
            c = Mix(c, light ? Colors.Black : Colors.White, 0.1);
        return c;
    }

    static string Hex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>Every palette colour of a theme, the derived ones filled in.</summary>
    public static Dictionary<string, Color> Resolve(Theme t)
    {
        var c = t.Colors.ToDictionary(kv => kv.Key, kv => Parse(kv.Value));
        void Default(string key, Func<Color> make) { if (!c.ContainsKey(key)) c[key] = make(); }
        var dflt = All[0].Colors;
        Default("AccentHover", () => Mix(c["Accent"], t.Light ? Colors.Black : Colors.White, 0.16));
        Default("Danger", () => Parse(dflt["Danger"]));
        Default("DangerHover", () => Parse(dflt["DangerHover"]));
        Default("OnDanger", () => Colors.White);
        Default("Selected", () => Mix(c["Card"], c["Accent"], t.Light ? 0.14 : 0.16));
        foreach (var k in new[] { "Scrim", "ScrimStrong", "Warn", "WarnText", "WarnBg", "WarnBorder", "WarnEdge", "Bad" })
            Default(k, () => Parse(dflt[k]));
        Default("Idle", () => Mix(c["Muted"], c["Bg"], 0.1));
        Default("Unknown", () => Mix(c["Muted"], c["Bg"], 0.4));
        return c;
    }

    /// <summary>
    /// Recolours the palette brushes for a theme (the menu colours) and a highlight colour (null or unknown: the theme's own),
    /// with a short fade when <paramref name="animate"/>.
    /// </summary>
    public static void Apply(string? name, string? highlight, bool animate)
    {
        var theme = Find(name);
        Current = theme;
        CurrentHighlight = FindHighlight(highlight);
        var colors = _resolved = Resolve(theme, CurrentHighlight);
        var res = Application.Current.Resources;
        foreach (var key in Keys)
        {
            var to = colors[key];
            if (res[key] is SolidColorBrush { IsFrozen: false } brush)
            {
                if (animate && Motion.Enabled && brush.Color != to)
                {
                    var anim = new ColorAnimation(brush.Color, to, new Duration(TimeSpan.FromMilliseconds(260)))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
                    };
                    anim.Completed += (_, _) => { brush.Color = to; brush.BeginAnimation(SolidColorBrush.ColorProperty, null); };
                    brush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
                }
                else
                {
                    brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
                    brush.Color = to;
                }
            }
            else res[key] = new SolidColorBrush(to);   // a frozen or missing brush: replace it (DynamicResource follows)
        }
        Changed?.Invoke();
    }

    public static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    public static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        (byte)(a.A + (b.A - a.A) * t), (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    public static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);
}
