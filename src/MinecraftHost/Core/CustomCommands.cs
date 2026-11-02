using System.Text;
using System.Text.RegularExpressions;

namespace MinecraftHost.Core;

/// <summary>One command of the Custom Commands plugin (plugins\CustomCommands\config.yml, under "commands:").</summary>
public sealed class CustomCommand
{
    public string Name = "";
    public string Permission = "";
    public List<string> Aliases = new();
    public string Description = "";
    public bool Enabled = true;
    public int Cooldown;
    /// <summary>"player" or "console": who runs <see cref="Commands"/>.</summary>
    public string Type = "player";
    public List<string> Message = new();
    public List<string> Commands = new();
    public List<CustomSubcommand> Subcommands = new();
}

public sealed class CustomSubcommand
{
    public string Name = "";
    /// <summary>Its name in the file when it was read (null for a new one).</summary>
    public string? OriginalName;
    public string Permission = "";
    public List<string> Message = new();
    public List<string> Commands = new();
}

/// <summary>
/// Reads and writes the Custom Commands plugin's commands. Saving one command rewrites only its own lines in
/// config.yml (after a backup); everything else in the file, comments included, stays as it was.
/// </summary>
public static class CustomCommands
{
    static string Plugins => Path.Combine(Paths.ServerDir, "plugins");
    public static string ConfigFile => Path.Combine(Plugins, "CustomCommands", "config.yml");

    public static bool Installed =>
        Directory.Exists(Plugins) && Directory.GetFiles(Plugins, "Custom-Commands*.jar").Length > 0 || File.Exists(ConfigFile);

    public static bool ValidName(string name) => Regex.IsMatch(name, "^[a-z0-9_-]{1,32}$");

    // ------------------------------------------------------------ the file's layout

    /// <summary>A command's lines in the file: from its "  name:" line to its last line before the next command (comments between them belong to the next).</summary>
    sealed record Block(string Name, int Start, int End);

    static List<Block> Blocks(List<string> lines, out int sectionEnd)
    {
        var blocks = new List<Block>();
        sectionEnd = lines.Count;
        var start = lines.FindIndex(l => l.TrimEnd() == "commands:" || l.StartsWith("commands:"));
        if (start < 0) { sectionEnd = -1; return blocks; }
        string? name = null;
        int blockStart = 0, lastContent = start;
        for (var i = start + 1; i < lines.Count; i++)
        {
            var line = lines[i];
            var t = line.Trim();
            var indent = line.Length - line.TrimStart().Length;
            if (t.Length > 0 && indent == 0 && !t.StartsWith('#')) { sectionEnd = i; break; }   // the next top-level key
            if (t.Length == 0 || t.StartsWith('#')) continue;
            if (indent == 2 && Regex.IsMatch(t, @"^[^\s#:]+:"))
            {
                if (name != null) blocks.Add(new Block(name, blockStart, lastContent));
                name = Unquote(t[..t.IndexOf(':')]);
                blockStart = i;
            }
            lastContent = i;
        }
        if (name != null) blocks.Add(new Block(name, blockStart, lastContent));
        if (sectionEnd == lines.Count) sectionEnd = lastContent + 1;
        return blocks;
    }

    // ------------------------------------------------------------ reading

    public static List<CustomCommand> Read()
    {
        var result = new List<CustomCommand>();
        if (!File.Exists(ConfigFile)) return result;
        var lines = File.ReadAllLines(ConfigFile).ToList();
        foreach (var b in Blocks(lines, out _)) result.Add(Parse(b.Name, lines.GetRange(b.Start + 1, b.End - b.Start)));
        return result;
    }

    static CustomCommand Parse(string name, List<string> body)
    {
        var c = new CustomCommand { Name = name };
        CustomSubcommand? sub = null;
        string? list = null;            // the list being filled: message / commands / alias
        var listIndent = 0;
        var inSubs = false;
        foreach (var raw in body)
        {
            var t = StripComment(raw).TrimEnd();
            if (t.Trim().Length == 0) continue;
            var indent = t.Length - t.TrimStart().Length;
            t = t.Trim();
            if (t.StartsWith("- "))
            {
                var item = Unquote(t[2..]);
                var target = list switch
                {
                    "message" => sub?.Message ?? c.Message,
                    "commands" => sub?.Commands ?? c.Commands,
                    "alias" => c.Aliases,
                    _ => null,
                };
                target?.Add(item);
                continue;
            }
            var colon = t.IndexOf(':');
            if (colon < 0) continue;
            var key = t[..colon].Trim();
            var value = t[(colon + 1)..].Trim();
            if (indent <= 4) { sub = null; inSubs = false; }
            if (!inSubs && indent > 4 && list == null) continue;   // inside an entry this editor doesn't show (arguments...)
            if (indent == 4 && key == "subcommands") { inSubs = true; list = null; continue; }
            if (inSubs && indent == 6) { sub = new CustomSubcommand { Name = Unquote(key), OriginalName = Unquote(key) }; c.Subcommands.Add(sub); list = null; continue; }
            if (inSubs && indent > 8) continue;   // deeper (nested subcommands, argument rules): kept as they are, not shown
            list = null;
            if (key is "message" or "commands" or "alias")
            {
                // a key seen again replaces the first (as YAML reads it)
                var target = key == "message" ? (sub?.Message ?? c.Message) : key == "commands" ? (sub?.Commands ?? c.Commands) : c.Aliases;
                target.Clear();
                if (value.StartsWith('[')) target.AddRange(InlineList(value));
                else if (value.Length > 0) target.Add(Unquote(value));
                else { list = key; listIndent = indent; }
                continue;
            }
            if (sub != null) { if (key == "permission") sub.Permission = Unquote(value); continue; }
            switch (key)
            {
                case "permission": c.Permission = Unquote(value); break;
                case "description": c.Description = Unquote(value); break;
                case "enabled": c.Enabled = !value.Equals("false", StringComparison.OrdinalIgnoreCase); break;
                case "cooldown": int.TryParse(value, out c.Cooldown); break;
                case "type": c.Type = Unquote(value).ToLowerInvariant() == "console" ? "console" : "player"; break;
            }
        }
        return c;
    }

    /// <summary>The line without a trailing # comment (one outside quotes).</summary>
    static string StripComment(string line)
    {
        var quote = '\0';
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quote != '\0') { if (ch == '\\' && quote == '"') i++; else if (ch == quote) quote = '\0'; }
            else if (ch is '"' or '\'') quote = ch;
            else if (ch == '#' && (i == 0 || char.IsWhiteSpace(line[i - 1]))) return line[..i];
        }
        return line;
    }

    static IEnumerable<string> InlineList(string value) =>
        Regex.Matches(value.Trim('[', ']'), "\"((?:[^\"\\\\]|\\\\.)*)\"|'([^']*)'|([^,\\s][^,]*)")
             .Select(m => m.Groups[1].Success ? Unescape(m.Groups[1].Value) : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value.Trim());

    static string Unquote(string v)
    {
        v = v.Trim();
        if (v.Length >= 2 && v[0] == '"' && v[^1] == '"') return Unescape(v[1..^1]);
        if (v.Length >= 2 && v[0] == '\'' && v[^1] == '\'') return v[1..^1].Replace("''", "'");
        return v;
    }

    static string Unescape(string v) => v.Replace("\\\"", "\"").Replace("\\\\", "\\");

    // ------------------------------------------------------------ writing

    static string Q(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    static void List(StringBuilder sb, string indent, string key, List<string> items)
    {
        if (items.Count == 0) return;
        sb.AppendLine($"{indent}{key}:");
        foreach (var i in items) sb.AppendLine($"{indent}  - {Q(i)}");
    }

    static string Yaml(CustomCommand c)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"  {c.Name}:");
        if (c.Permission.Length > 0) sb.AppendLine($"    permission: {Q(c.Permission)}");
        if (c.Aliases.Count > 0) sb.AppendLine($"    alias: [{string.Join(", ", c.Aliases.Select(Q))}]");
        if (c.Description.Length > 0) sb.AppendLine($"    description: {Q(c.Description)}");
        sb.AppendLine($"    enabled: {(c.Enabled ? "true" : "false")}");
        sb.AppendLine($"    cooldown: {Math.Max(0, c.Cooldown)}");
        if (c.Commands.Count > 0) sb.AppendLine($"    type: {Q(c.Type == "console" ? "Console" : "player")}");
        List(sb, "    ", "message", c.Message);
        List(sb, "    ", "commands", c.Commands);
        if (c.Subcommands.Count > 0)
        {
            sb.AppendLine("    subcommands:");
            foreach (var s in c.Subcommands)
            {
                sb.AppendLine($"      {s.Name}:");
                if (s.Permission.Length > 0) sb.AppendLine($"        permission: {Q(s.Permission)}");
                List(sb, "        ", "message", s.Message);
                List(sb, "        ", "commands", s.Commands);
            }
        }
        return sb.ToString().TrimEnd('\r', '\n');
    }

    /// <summary>
    /// Saves one command; null <paramref name="command"/> deletes it. An existing command is changed line by line: only
    /// the settings that differ from the file are rewritten (keeping their comments), and anything this editor doesn't
    /// show (argument rules, nested subcommands) stays as it is. A new one goes at the end of the commands. The file is
    /// backed up first, and keeps its line endings.
    /// </summary>
    public static void Save(string? oldName, CustomCommand? command)
    {
        var raw = File.Exists(ConfigFile) ? File.ReadAllText(ConfigFile) : "";
        var newline = raw.Contains("\r\n") ? "\r\n" : "\n";
        var lines = raw.Length == 0 ? new List<string>() : raw.Replace("\r\n", "\n").Split('\n').ToList();
        var endsWithNewline = lines.Count > 0 && lines[^1].Length == 0;
        if (endsWithNewline) lines.RemoveAt(lines.Count - 1);
        var blocks = Blocks(lines, out var sectionEnd);
        if (sectionEnd < 0) { lines.Add("commands:"); sectionEnd = lines.Count; }
        var block = oldName == null ? null : blocks.FirstOrDefault(b => b.Name == oldName);
        if (block != null && command == null) lines.RemoveRange(block.Start, block.End - block.Start + 1);
        else if (block != null && command != null)
        {
            var region = lines.GetRange(block.Start, block.End - block.Start + 1);
            Patch(region, Parse(block.Name, region.Skip(1).ToList()), command);
            lines.RemoveRange(block.Start, block.End - block.Start + 1);
            lines.InsertRange(block.Start, region);
        }
        else if (command != null)
        {
            var text = Yaml(command).Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            text.Insert(0, "");
            lines.InsertRange(sectionEnd, text);
        }
        Backup();
        File.WriteAllText(ConfigFile, string.Join(newline, lines) + (endsWithNewline || raw.Length == 0 ? newline : ""));
    }

    // ------------------------------------------------------------ changing lines in place

    static int Indent(string line) => line.Length - line.TrimStart().Length;
    static bool Content(string line) => line.Trim().Length > 0 && !line.TrimStart().StartsWith('#');

    /// <summary>The last line of the entry that starts at <paramref name="start"/> (its key line, at <paramref name="indent"/>).</summary>
    static int EntryEnd(List<string> l, int start, int indent)
    {
        var end = start;
        for (var i = start + 1; i < l.Count; i++)
        {
            if (!Content(l[i])) continue;
            if (Indent(l[i]) <= indent && !l[i].TrimStart().StartsWith("- ")) break;
            if (Indent(l[i]) < indent) break;
            end = i;
        }
        return end;
    }

    /// <summary>The child entries named <paramref name="key"/> of the entry at <paramref name="parent"/> (indent + 2), first to last.</summary>
    static List<(int Start, int End)> Children(List<string> l, int parent, int parentIndent, string key)
    {
        var found = new List<(int, int)>();
        var end = EntryEnd(l, parent, parentIndent);
        for (var i = parent + 1; i <= end; i++)
        {
            if (!Content(l[i]) || Indent(l[i]) != parentIndent + 2) continue;
            var t = l[i].Trim();
            var colon = t.IndexOf(':');
            if (colon > 0 && Unquote(t[..colon]) == key) found.Add((i, EntryEnd(l, i, parentIndent + 2)));
        }
        return found;
    }

    /// <summary>The comment at the end of a line (with the spaces before it), or "".</summary>
    static string Comment(string line)
    {
        var bare = StripComment(line);
        return line[bare.TrimEnd().Length..];
    }

    /// <summary>Sets a child entry to these lines: replaces it (and any duplicates), inserts it, or removes it when null.</summary>
    static void Set(List<string> l, int parent, int parentIndent, string key, List<string>? replacement, bool atTop)
    {
        var found = Children(l, parent, parentIndent, key);
        for (var k = found.Count - 1; k >= 1; k--) l.RemoveRange(found[k].Start, found[k].End - found[k].Start + 1);   // duplicates: the last one wins in YAML
        if (found.Count > 0)
        {
            var (start, end) = found[0];
            l.RemoveRange(start, Math.Min(found.Count > 1 ? found[^1].End : end, end) - start + 1);
            if (replacement != null) l.InsertRange(start, replacement);
        }
        else if (replacement != null) l.InsertRange(atTop ? parent + 1 : EntryEnd(l, parent, parentIndent) + 1, replacement);
    }

    static void Scalar(List<string> l, int parent, int parentIndent, string key, string? before, string? after)
    {
        if (before == after) return;
        var pad = new string(' ', parentIndent + 2);
        var old = Children(l, parent, parentIndent, key);
        var comment = old.Count > 0 ? Comment(l[old[^1].Start]) : "";
        Set(l, parent, parentIndent, key, after == null ? null : new List<string> { $"{pad}{key}: {after}{comment}" }, atTop: true);
    }

    static void ListSet(List<string> l, int parent, int parentIndent, string key, List<string> before, List<string> after)
    {
        if (before.SequenceEqual(after)) return;
        var pad = new string(' ', parentIndent + 2);
        List<string>? lines = null;
        if (after.Count > 0)
        {
            var old = Children(l, parent, parentIndent, key);
            lines = new List<string> { $"{pad}{key}:" + (old.Count > 0 ? Comment(l[old[^1].Start]) : "") };
            lines.AddRange(after.Select(x => $"{pad}  - {Q(x)}"));
        }
        Set(l, parent, parentIndent, key, lines, atTop: false);
    }

    /// <summary>Changes a command's lines (region[0] is its "  name:" line) from <paramref name="was"/> to <paramref name="now"/>.</summary>
    static void Patch(List<string> r, CustomCommand was, CustomCommand now)
    {
        if (was.Name != now.Name) r[0] = $"  {now.Name}:" + Comment(r[0]);
        string? Opt(string v) => v.Length == 0 ? null : Q(v);
        Scalar(r, 0, 2, "permission", Opt(was.Permission), Opt(now.Permission));
        Scalar(r, 0, 2, "description", Opt(was.Description), Opt(now.Description));
        Scalar(r, 0, 2, "enabled", was.Enabled ? "true" : "false", now.Enabled ? "true" : "false");
        Scalar(r, 0, 2, "cooldown", was.Cooldown.ToString(), now.Cooldown.ToString());
        if (was.Type != now.Type || (now.Commands.Count > 0 && Children(r, 0, 2, "type").Count == 0))
            Scalar(r, 0, 2, "type", null, Q(now.Type == "console" ? "Console" : "player"));
        if (!was.Aliases.SequenceEqual(now.Aliases))
            Set(r, 0, 2, "alias", now.Aliases.Count == 0 ? null : new List<string> { $"    alias: [{string.Join(", ", now.Aliases.Select(Q))}]" }, atTop: true);
        ListSet(r, 0, 2, "message", was.Message, now.Message);
        ListSet(r, 0, 2, "commands", was.Commands, now.Commands);

        // subcommands: each changed in place, removed ones taken out, new ones added at the end
        var subs = Children(r, 0, 2, "subcommands");
        if (subs.Count == 0 && now.Subcommands.Count == 0) return;
        if (subs.Count == 0) { r.Add("    subcommands:"); subs = Children(r, 0, 2, "subcommands"); }
        var at = subs[^1].Start;
        foreach (var gone in was.Subcommands.Where(o => !now.Subcommands.Any(n => n.OriginalName == o.Name)))
            Set(r, at, 4, gone.Name, null, atTop: false);
        foreach (var n in now.Subcommands)
        {
            var old = n.OriginalName == null ? null : was.Subcommands.FirstOrDefault(o => o.Name == n.OriginalName);
            if (old == null)
            {
                var text = new List<string> { $"      {n.Name}:" };
                if (n.Permission.Length > 0) text.Add($"        permission: {Q(n.Permission)}");
                if (n.Message.Count > 0) { text.Add("        message:"); text.AddRange(n.Message.Select(x => $"          - {Q(x)}")); }
                if (n.Commands.Count > 0) { text.Add("        commands:"); text.AddRange(n.Commands.Select(x => $"          - {Q(x)}")); }
                r.InsertRange(EntryEnd(r, at, 4) + 1, text);
                continue;
            }
            var entry = Children(r, at, 4, old.Name);
            if (entry.Count == 0) continue;
            var s = entry[^1].Start;
            if (old.Name != n.Name) r[s] = $"      {n.Name}:" + Comment(r[s]);
            Scalar(r, s, 6, "permission", Opt(old.Permission), Opt(n.Permission));
            ListSet(r, s, 6, "message", old.Message, n.Message);
            ListSet(r, s, 6, "commands", old.Commands, n.Commands);
        }
        if (now.Subcommands.Count == 0 && Children(r, at, 4, "").Count == 0 && EntryEnd(r, at, 4) == at) r.RemoveAt(at);   // an empty "subcommands:"
    }

    static void Backup()
    {
        if (!File.Exists(ConfigFile)) return;
        var to = Path.Combine(Paths.BackupDir, "configs", DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"), "plugins", "CustomCommands", "config.yml");
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Copy(ConfigFile, to, overwrite: true);
    }
}
