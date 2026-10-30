using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MinecraftHost.Core;

/// <summary>One editable setting found in a config file.</summary>
public sealed class ConfigEntry
{
    public required string Section { get; init; }   // parent path, e.g. "settings.spawn-limits"
    public required string Key { get; init; }       // leaf name, e.g. "monsters"
    public required string Path { get; init; }      // full path, e.g. "settings.spawn-limits.monsters"
    public required string Value { get; init; }     // value as the user sees it (unquoted)
    public string? Comment { get; init; }           // comment lines written just above the setting
    public bool IsBool => Value is "true" or "false";

    // where the raw value sits in the text (line-based formats)
    internal int Line { get; init; } = -1;
    internal int Start { get; init; }
    internal int Length { get; init; }
    internal char Quote { get; init; }               // '"', '\'' or '\0'
    internal bool Raw { get; init; }                 // write back exactly as typed (flow lists, numbers in TOML...)
    // JSON
    internal JsonNode? JsonParent { get; init; }
    internal string? JsonProperty { get; init; }
    internal int JsonIndex { get; init; } = -1;
    internal JsonValueKind JsonKind { get; init; }
}

public enum ConfigFormat { None, Yaml, Properties, Json, Hocon, Toml }

/// <summary>Turns a config file into a list of settings, and writes edited values back into the original text.</summary>
public static class ConfigForm
{
    public static ConfigFormat FormatOf(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".yml" or ".yaml" => ConfigFormat.Yaml,
        ".properties" => ConfigFormat.Properties,
        ".json" or ".mcmeta" => ConfigFormat.Json,
        ".conf" or ".hocon" => ConfigFormat.Hocon,
        ".toml" or ".ini" or ".cfg" => ConfigFormat.Toml,
        _ => ConfigFormat.None,
    };

    /// <summary>Parses the text. Throws if it can't be read as its format (e.g. broken JSON).</summary>
    public static List<ConfigEntry> Parse(string path, string text) => FormatOf(path) switch
    {
        ConfigFormat.Yaml => ParseYaml(text),
        ConfigFormat.Properties => ParseProperties(text),
        ConfigFormat.Json => ParseJson(text),
        ConfigFormat.Hocon => ParseHocon(text),
        ConfigFormat.Toml => ParseToml(text),
        _ => new List<ConfigEntry>(),
    };

    /// <summary>Returns the text with the edited values written in. Untouched lines stay byte-for-byte the same.</summary>
    public static string Apply(string path, string text, List<ConfigEntry> entries, IReadOnlyDictionary<ConfigEntry, string> edits)
    {
        var changed = edits.Where(e => e.Value != e.Key.Value).ToList();
        if (changed.Count == 0) return text;
        var format = FormatOf(path);

        if (format == ConfigFormat.Json)
        {
            // apply to a fresh parse each time, so an edit that's been undone leaves no trace
            var fresh = ParseJson(text).ToDictionary(e => e.Path);
            var root = fresh.Values.FirstOrDefault()?.JsonParent?.Root;
            if (root == null) return text;
            foreach (var (entry, value) in changed)
                if (fresh.TryGetValue(entry.Path, out var target)) SetJson(target, value);
            return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        }

        var lines = text.Split('\n');   // keeps any '\r' at the end of each line
        foreach (var (entry, value) in changed)
        {
            var line = lines[entry.Line];
            var raw = format switch
            {
                ConfigFormat.Yaml => FormatYaml(entry, value),
                ConfigFormat.Properties => value.Replace("\\", "\\\\").Replace(":", "\\:").Replace("=", "\\="),
                _ => FormatQuoted(entry, value, '"'),
            };
            lines[entry.Line] = line[..entry.Start] + raw + line[(entry.Start + entry.Length)..];
        }
        return string.Join("\n", lines);
    }

    // ------------------------------------------------------------ YAML

    // key = everything up to a ':' that is followed by a space or the end of the line,
    // so keys like "minecraft:overworld:" (Multiverse) and values like "http://..." both work
    static readonly Regex YamlKey = new(@"^(\s*)(""[^""]*""|'[^']*'|[^\s#].*?)\s*:(?:\s+(.*?))?\s*$", RegexOptions.Compiled);

    static List<ConfigEntry> ParseYaml(string text)
    {
        var result = new List<ConfigEntry>();
        var lines = text.Split('\n');
        var stack = new List<(int Indent, string Key)>();
        var comment = new StringBuilder();
        int blockIndent = -1;   // inside a | or > block scalar, or a list: skip deeper lines

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            var trimmed = line.TrimStart();
            var indent = line.Length - trimmed.Length;
            if (trimmed.Length == 0) { comment.Clear(); continue; }
            if (blockIndent >= 0 && indent > blockIndent) continue;
            blockIndent = -1;
            if (trimmed.StartsWith('#')) { AppendComment(comment, trimmed.TrimStart('#').Trim()); continue; }
            if (trimmed.StartsWith("- ") || trimmed == "-") { comment.Clear(); continue; }   // list items: edit in Text view

            var m = YamlKey.Match(line);
            if (!m.Success) { comment.Clear(); continue; }
            var key = m.Groups[2].Value.Trim('"', '\'');
            while (stack.Count > 0 && stack[^1].Indent >= indent) stack.RemoveAt(stack.Count - 1);
            var section = string.Join(".", stack.Select(s => s.Key));
            var rest = m.Groups[3].Success ? m.Groups[3].Value : "";

            if (rest.Length == 0 || rest.StartsWith('#'))
            {
                stack.Add((indent, key));       // parent of nested settings (or of a list)
                blockIndent = -1;
                comment.Clear();
                continue;
            }
            if (rest.StartsWith('|') || rest.StartsWith('>')) { blockIndent = indent; comment.Clear(); continue; }
            if (rest.StartsWith('&') || rest.StartsWith('*') || rest.StartsWith('!')) { comment.Clear(); continue; }

            var restStart = m.Groups[3].Index;
            var entry = ReadScalar(line, restStart, '#') is { } v
                ? new ConfigEntry
                {
                    Section = section, Key = key, Path = section.Length > 0 ? section + "." + key : key,
                    Value = v.Value, Comment = comment.Length > 0 ? comment.ToString() : null,
                    Line = i, Start = v.Start, Length = v.Length, Quote = v.Quote,
                    Raw = rest.StartsWith('[') || rest.StartsWith('{'),
                }
                : null;
            if (entry != null) result.Add(entry);
            comment.Clear();
        }
        return result;
    }

    static string FormatYaml(ConfigEntry e, string value)
    {
        if (e.Raw) return value;
        if (e.Quote == '"') return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        if (e.Quote == '\'') return "'" + value.Replace("'", "''") + "'";
        // unquoted before: quote only if the new value would otherwise be misread
        var needs = value.Length == 0 || value != value.Trim() || value.Contains(": ") || value.Contains(" #") ||
                    "-?:,[]{}#&*!|>'\"%@`".Contains(value[0]);
        return needs ? "'" + value.Replace("'", "''") + "'" : value;
    }

    // ------------------------------------------------------------ .properties

    static List<ConfigEntry> ParseProperties(string text)
    {
        var result = new List<ConfigEntry>();
        var lines = text.Split('\n');
        var comment = new StringBuilder();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0) { comment.Clear(); continue; }
            if (trimmed[0] is '#' or '!')
            {
                // skip the timestamp header Minecraft writes
                var c = trimmed.TrimStart('#', '!').Trim();
                if (!Regex.IsMatch(c, @"^(Minecraft server properties|\w{3} \w{3} \d+ )")) AppendComment(comment, c);
                continue;
            }
            var m = Regex.Match(line, @"^\s*((?:\\.|[^=:\s\\])+)\s*[=:]\s*(.*)$");
            if (!m.Success) { comment.Clear(); continue; }
            var key = m.Groups[1].Value.Replace("\\", "");
            var raw = m.Groups[2].Value;
            result.Add(new ConfigEntry
            {
                Section = "", Key = key, Path = key,
                Value = Regex.Replace(raw, @"\\(.)", "$1"),
                Comment = comment.Length > 0 ? comment.ToString() : null,
                Line = i, Start = m.Groups[2].Index, Length = raw.Length,
            });
            comment.Clear();
        }
        return result;
    }

    // ------------------------------------------------------------ TOML / INI

    static List<ConfigEntry> ParseToml(string text)
    {
        var result = new List<ConfigEntry>();
        var lines = text.Split('\n');
        var section = "";
        var comment = new StringBuilder();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            var trimmed = line.Trim();
            if (trimmed.Length == 0) { comment.Clear(); continue; }
            if (trimmed[0] is '#' or ';') { AppendComment(comment, trimmed.TrimStart('#', ';').Trim()); continue; }
            var header = Regex.Match(trimmed, @"^\[+\s*([^\]]+?)\s*\]+$");
            if (header.Success) { section = header.Groups[1].Value; comment.Clear(); continue; }
            var m = Regex.Match(line, @"^\s*([A-Za-z0-9_.\-""']+)\s*=\s*");
            if (!m.Success) { comment.Clear(); continue; }
            var key = m.Groups[1].Value.Trim('"', '\'');
            var v = ReadScalar(line, m.Length, '#');
            if (v == null) continue;
            var rawText = line.Substring(v.Value.Start, v.Value.Length);
            result.Add(new ConfigEntry
            {
                Section = section, Key = key, Path = section.Length > 0 ? section + "." + key : key,
                Value = v.Value.Value, Comment = comment.Length > 0 ? comment.ToString() : null,
                Line = i, Start = v.Value.Start, Length = v.Value.Length, Quote = v.Value.Quote,
                Raw = v.Value.Quote == '\0',     // numbers, booleans, arrays: written as typed
            });
            comment.Clear();
        }
        return result;
    }

    // ------------------------------------------------------------ HOCON (.conf, e.g. BlueMap)

    static List<ConfigEntry> ParseHocon(string text)
    {
        var result = new List<ConfigEntry>();
        var lines = text.Split('\n');
        var stack = new List<string>();
        var comment = new StringBuilder();
        var arrayDepth = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            var trimmed = line.Trim();
            if (trimmed.Length == 0) { comment.Clear(); continue; }
            if (trimmed.StartsWith('#') || trimmed.StartsWith("//")) { AppendComment(comment, trimmed.TrimStart('#', '/').Trim()); continue; }
            if (arrayDepth > 0)
            {
                arrayDepth += trimmed.Count(c => c == '[') - trimmed.Count(c => c == ']');
                continue;
            }
            if (trimmed.StartsWith('}')) { if (stack.Count > 0) stack.RemoveAt(stack.Count - 1); comment.Clear(); continue; }

            var open = Regex.Match(trimmed, @"^(""[^""]+""|[A-Za-z0-9_.\-]+)\s*[:=]?\s*\{\s*(\}\s*)?$");
            if (open.Success)
            {
                if (!open.Groups[2].Success) stack.Add(open.Groups[1].Value.Trim('"'));
                comment.Clear();
                continue;
            }
            var m = Regex.Match(line, @"^\s*(""[^""]+""|[A-Za-z0-9_.\-]+)\s*[:=]\s*");
            if (!m.Success) { comment.Clear(); continue; }
            var key = m.Groups[1].Value.Trim('"');
            var rest = line[m.Length..];
            if (rest.TrimStart().StartsWith('['))
            {
                arrayDepth = rest.Count(c => c == '[') - rest.Count(c => c == ']');   // arrays: edit in Text view
                comment.Clear();
                continue;
            }
            var v = ReadScalar(line, m.Length, '#');
            if (v == null) continue;
            var section = string.Join(".", stack);
            result.Add(new ConfigEntry
            {
                Section = section, Key = key, Path = section.Length > 0 ? section + "." + key : key,
                Value = v.Value.Value, Comment = comment.Length > 0 ? comment.ToString() : null,
                Line = i, Start = v.Value.Start, Length = v.Value.Length, Quote = v.Value.Quote,
                Raw = v.Value.Quote == '\0',
            });
            comment.Clear();
        }
        return result;
    }

    // ------------------------------------------------------------ JSON

    static List<ConfigEntry> ParseJson(string text)
    {
        var root = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
                   ?? throw new InvalidOperationException("The file is empty.");
        var result = new List<ConfigEntry>();
        Walk(root, "", result);
        return result;
    }

    static void Walk(JsonNode node, string path, List<ConfigEntry> result)
    {
        if (node is JsonObject obj)
        {
            foreach (var (name, child) in obj.ToList())
            {
                var childPath = path.Length > 0 ? path + "." + name : name;
                if (child is JsonValue) result.Add(JsonEntry(obj, name, -1, child, path, name, childPath));
                else if (child is JsonArray arr && arr.All(a => a is JsonValue)) result.Add(JsonArrayEntry(obj, name, arr, path, childPath));
                else if (child != null) Walk(child, childPath, result);
            }
        }
        else if (node is JsonArray array)
        {
            for (var i = 0; i < array.Count; i++)
            {
                var childPath = $"{path}[{i}]";
                if (array[i] is JsonValue) result.Add(JsonEntry(array, null, i, array[i]!, path, $"[{i}]", childPath));
                else if (array[i] != null) Walk(array[i]!, childPath, result);
            }
        }
    }

    static ConfigEntry JsonEntry(JsonNode parent, string? prop, int index, JsonNode value, string section, string key, string path)
    {
        var kind = value.GetValueKind();
        return new ConfigEntry
        {
            Section = section, Key = key, Path = path,
            Value = kind == JsonValueKind.String ? value.GetValue<string>() : value.ToJsonString(),
            JsonParent = parent, JsonProperty = prop, JsonIndex = index, JsonKind = kind,
        };
    }

    static ConfigEntry JsonArrayEntry(JsonObject parent, string prop, JsonArray arr, string section, string path) => new()
    {
        Section = section, Key = prop, Path = path,
        Value = arr.ToJsonString(),
        Comment = "A list. Keep the [ ] brackets and \"quotes\".",
        JsonParent = parent, JsonProperty = prop, JsonKind = JsonValueKind.Array,
    };

    static void SetJson(ConfigEntry e, string value)
    {
        JsonNode? node = e.JsonKind switch
        {
            JsonValueKind.String => JsonValue.Create(value),
            JsonValueKind.True or JsonValueKind.False when value is "true" or "false" => JsonValue.Create(value == "true"),
            JsonValueKind.Number when double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _) => JsonNode.Parse(value),
            JsonValueKind.Array or JsonValueKind.Null => TryParseJson(value) ?? JsonValue.Create(value),
            _ => TryParseJson(value) ?? JsonValue.Create(value),
        };
        if (e.JsonParent is JsonObject obj) obj[e.JsonProperty!] = node;
        else if (e.JsonParent is JsonArray arr) arr[e.JsonIndex] = node;
    }

    static JsonNode? TryParseJson(string s) { try { return JsonNode.Parse(s); } catch { return null; } }

    // ------------------------------------------------------------ shared helpers

    /// <summary>Reads a scalar value starting at <paramref name="from"/>: a quoted string, or text up to an inline comment.</summary>
    static (string Value, int Start, int Length, char Quote)? ReadScalar(string line, int from, char commentChar)
    {
        var i = from;
        while (i < line.Length && line[i] is ' ' or '\t') i++;
        if (i >= line.Length) return null;
        var q = line[i];
        if (q is '"' or '\'')
        {
            var sb = new StringBuilder();
            for (var j = i + 1; j < line.Length; j++)
            {
                var c = line[j];
                if (q == '\'' && c == '\'' && j + 1 < line.Length && line[j + 1] == '\'') { sb.Append('\''); j++; continue; }
                if (q == '"' && c == '\\' && j + 1 < line.Length) { sb.Append(line[j + 1] switch { 'n' => '\n', 't' => '\t', var x => x }); j++; continue; }
                if (c == q) return (sb.ToString(), i, j - i + 1, q);
                sb.Append(c);
            }
            return null;   // unterminated or multi-line string: edit in Text view
        }
        var end = line.Length;
        var hash = line.IndexOf(" " + commentChar, i, StringComparison.Ordinal);
        if (hash >= 0) end = hash;
        var raw = line[i..end].TrimEnd();
        return (raw, i, raw.Length, '\0');
    }

    static string FormatQuoted(ConfigEntry e, string value, char defaultQuote)
    {
        if (e.Raw) return value;
        var q = e.Quote == '\0' ? defaultQuote : e.Quote;
        return q == '\'' ? "'" + value.Replace("'", "''") + "'" : "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    static void AppendComment(StringBuilder sb, string text)
    {
        if (text.Length == 0 || Regex.IsMatch(text, @"^[-=#*~ ]+$")) return;   // skip divider lines
        if (sb.Length > 0) sb.Append(' ');
        sb.Append(text);
    }
}
