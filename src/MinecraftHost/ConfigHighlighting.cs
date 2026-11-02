using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Xml;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using ICSharpCode.AvalonEdit.Rendering;
using MinecraftHost.Core;

namespace MinecraftHost;

/// <summary>
/// Code colours for the config editor's Text view: keys, strings, numbers, true/false, comments and section headers,
/// for YAML, .properties, JSON, TOML/INI and HOCON. Built from the theme (Accent for keys, Muted for comments), so it's
/// made again when the theme changes.
/// </summary>
public static class ConfigHighlighting
{
    /// <summary>The colouring for a file, by its extension (null: plain text).</summary>
    public static IHighlightingDefinition? For(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var rules = ext switch
        {
            ".yml" or ".yaml" => Yaml,
            ".properties" => Properties,
            ".json" or ".mcmeta" => Json,
            ".toml" or ".ini" or ".cfg" => Toml,
            ".conf" => Hocon,
            _ => null,
        };
        if (rules == null) return null;
        var light = Themes.Current.Light;
        string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        var colors = $@"
  <Color name=""Comment"" foreground=""{Hex(Themes.Get("Muted"))}"" fontStyle=""italic"" />
  <Color name=""Key"" foreground=""{Hex(Themes.Get("Accent"))}"" />
  <Color name=""Section"" foreground=""{Hex(Themes.Get("Accent"))}"" fontWeight=""bold"" />
  <Color name=""String"" foreground=""{(light ? "#2E7D32" : "#9FE0A8")}"" />
  <Color name=""Number"" foreground=""{(light ? "#A15C00" : "#E8C25A")}"" />
  <Color name=""Bool"" foreground=""{(light ? "#8E24AA" : "#D69CFF")}"" />
  <Color name=""Punct"" foreground=""{Hex(Themes.Get("Muted"))}"" />";
        var xml = $@"<SyntaxDefinition name=""config"" xmlns=""http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008"">{colors}
  <RuleSet ignoreCase=""true"">{rules}</RuleSet>
</SyntaxDefinition>";
        using var reader = XmlReader.Create(new StringReader(xml));
        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }

    const string Strings = @"
    <Span color=""String"" begin=""&quot;"" end=""&quot;""><RuleSet><Span begin=""\\"" end=""."" /></RuleSet></Span>
    <Span color=""String"" begin=""'"" end=""'"" />";
    const string Values = @"
    <Rule color=""Bool"">\b(true|false|yes|no|on|off|null)\b</Rule>
    <Rule color=""Number"">(?&lt;![\w.])-?\d+(\.\d+)?(?![\w.])</Rule>";

    // a comment starts a line or follows a space (so a colour like #FF0000 in a value isn't one)
    const string Yaml = @"
    <Span color=""Comment"" begin=""(?&lt;=^|\s)#"" />" + Strings + @"
    <Rule color=""Key"">^\s*(-\s+)?[^\s#'&quot;\-][^:#]*?(?=:(\s|$))</Rule>
    <Rule color=""Punct"">^\s*-(?=\s|$)</Rule>" + Values;

    const string Properties = @"
    <Span color=""Comment"" begin=""^\s*[#!]"" />
    <Rule color=""Key"">^\s*[^=:\s][^=:]*?(?=\s*[=:])</Rule>" + Values;

    const string Json = @"
    <Rule color=""Key"">&quot;([^&quot;\\]|\\.)*&quot;(?=\s*:)</Rule>" + Strings + Values;

    const string Toml = @"
    <Span color=""Comment"" begin=""(?&lt;=^|\s)[#;]"" />
    <Rule color=""Section"">^\s*\[[^\]]*\]</Rule>" + Strings + @"
    <Rule color=""Key"">^\s*[\w.&quot;-]+(?=\s*=)</Rule>" + Values;

    const string Hocon = @"
    <Span color=""Comment"" begin=""(?&lt;=^|\s)(#|//)"" />" + Strings + @"
    <Rule color=""Key"">^\s*[\w.&quot;-]+(?=\s*[:={])</Rule>" + Values;
}

/// <summary>Marks every match of the find bar's text in the editor (case-insensitive).</summary>
public sealed class FindMarker : DocumentColorizingTransformer
{
    public string Text { get; set; } = "";
    public Brush? Brush { get; set; }

    protected override void ColorizeLine(DocumentLine line)
    {
        if (Text.Length == 0 || Brush == null) return;
        var text = CurrentContext.Document.GetText(line);
        for (var i = text.IndexOf(Text, StringComparison.OrdinalIgnoreCase); i >= 0; i = text.IndexOf(Text, i + Text.Length, StringComparison.OrdinalIgnoreCase))
        {
            var start = line.Offset + i;
            ChangeLinePart(start, start + Text.Length, e => e.BackgroundBrush = Brush);
        }
    }
}
