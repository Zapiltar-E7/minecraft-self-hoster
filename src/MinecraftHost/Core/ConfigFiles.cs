using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MinecraftHost.Core;

/// <summary>Which sub-tab of the Configs tab a file belongs to.</summary>
public enum ConfigCategory { Server, Plugin, Datapack, Mod }

public sealed record ConfigFile(string Group, string Name, string FullPath, string RelativePath) : System.ComponentModel.INotifyPropertyChanged
{
    public ConfigCategory Category { get; init; }
    /// <summary>For a file inside a zipped datapack: its path in the zip (FullPath is then the zip).</summary>
    public string? ZipEntry { get; init; }
    /// <summary>The name that decides the format (.json, .yml, ...).</summary>
    public string FormatPath => ZipEntry ?? FullPath;
    /// <summary>Identifies the file, including the entry for files inside a zip.</summary>
    public string Key => ZipEntry == null ? FullPath : FullPath + "!" + ZipEntry;

    Health? _health;
    /// <summary>Status of the plugin that owns this file; null for server files or folders with no plugin.</summary>
    public Health? Health
    {
        get => _health;
        set { _health = value; PropertyChanged?.Invoke(this, new(nameof(Health))); }
    }
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    // identity is the file; the changing status must not change equality (lists hash their items)
    public bool Equals(ConfigFile? other) => other != null && Key == other.Key;
    public override int GetHashCode() => Key.GetHashCode();
}

/// <summary>Finds and safely saves the editable config files in the server folder.</summary>
public static class ConfigFiles
{
    static readonly string[] Extensions = { ".yml", ".yaml", ".properties", ".json", ".conf", ".toml", ".txt", ".cfg", ".ini", ".hocon" };

    // folders that hold data, caches or translations rather than settings
    static readonly Regex SkipFolder = new(
        @"^(\.|data$|cache|logs?$|libraries$|versions$|web$|lang$|languages?$|locales?$|translations?$|userdata$|playerdata$|players$|" +
        @"worlds$|groups$|tasks$|defaults$|backups?$|storage$|database$|images$|maps$|tmp|temp$|bstats$|safe-mode-plugins$|disabled-until)",
        RegexOptions.IgnoreCase);

    // files that are data, state or secrets rather than settings
    static readonly Regex SkipFile = new(
        @"(keystore|secret|token|activity\.json$|hashes\.json$|playernames\.json$|usercache\.json$|\.lock$)",
        RegexOptions.IgnoreCase);

    static readonly string[] ServerRootFiles =
    {
        "server.properties", "bukkit.yml", "spigot.yml", "commands.yml", "permissions.yml", "help.yml",
        "ops.json", "whitelist.json", "banned-players.json", "banned-ips.json", "eula.txt",
    };

    const long MaxSize = 1024 * 1024;

    // what a datapack is made of that's worth editing as text
    static readonly string[] DatapackExtensions = { ".json", ".mcmeta", ".mcfunction", ".txt" };

    public static List<ConfigFile> Find()
    {
        var root = Paths.ServerDir;
        var list = new List<ConfigFile>();
        if (!Directory.Exists(root)) return list;

        foreach (var name in ServerRootFiles)
            if (File.Exists(Path.Combine(root, name))) Add(list, ConfigCategory.Server, "Server", root, Path.Combine(root, name));

        var configDir = Path.Combine(root, "config");
        if (Directory.Exists(configDir))
            // Paper keeps its own files here; on Fabric/Forge it's where mods keep theirs (loose, or in a folder per mod)
            foreach (var f in Scan(configDir, 2).OrderBy(f => Path.GetRelativePath(configDir, f).Contains(Path.DirectorySeparatorChar)))
            {
                var rel = Path.GetRelativePath(configDir, f);
                var sub = rel.Contains(Path.DirectorySeparatorChar) ? rel[..rel.IndexOf(Path.DirectorySeparatorChar)] : null;
                if (sub != null) Add(list, ConfigCategory.Mod, sub, Path.Combine(configDir, sub), f);
                else if (Path.GetFileName(f).StartsWith("paper-", StringComparison.OrdinalIgnoreCase)) Add(list, ConfigCategory.Server, "Paper", configDir, f);
                else Add(list, ConfigCategory.Mod, "Mod configs", configDir, f);
            }

        // Forge keeps per-world server settings next to the world's data
        var datapacks = Addons.DatapacksDir;
        var serverConfig = Path.Combine(Path.GetDirectoryName(datapacks)!, "serverconfig");
        if (Directory.Exists(serverConfig))
            foreach (var f in Scan(serverConfig, 1)) Add(list, ConfigCategory.Mod, "World settings (serverconfig)", serverConfig, f);

        var plugins = Path.Combine(root, "plugins");
        if (Directory.Exists(plugins))
            foreach (var dir in Directory.GetDirectories(plugins).OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(dir);
                if (SkipFolder.IsMatch(name)) continue;
                foreach (var f in Scan(dir, 2)) Add(list, ConfigCategory.Plugin, name, dir, f);
            }

        if (Directory.Exists(datapacks))
            foreach (var pack in Directory.GetFileSystemEntries(datapacks).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(pack);
                if (name.Equals("bukkit", StringComparison.OrdinalIgnoreCase)) continue;   // Paper's own pack for plugins
                if (Directory.Exists(pack))
                {
                    foreach (var f in PackFiles(pack))
                        list.Add(new ConfigFile(name, PackShortName(Path.GetRelativePath(pack, f).Replace('\\', '/')), f, Path.GetRelativePath(root, f))
                        {
                            Category = ConfigCategory.Datapack,
                        });
                }
                else if (pack.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    var group = Path.GetFileNameWithoutExtension(pack);
                    try
                    {
                        using var zip = ZipFile.OpenRead(pack);
                        foreach (var e in zip.Entries
                                     .Where(e => e.Length > 0 && e.Length <= MaxSize && DatapackExtensions.Contains(Path.GetExtension(e.FullName).ToLowerInvariant()))
                                     .OrderBy(e => e.FullName != "pack.mcmeta").ThenBy(e => e.FullName, StringComparer.OrdinalIgnoreCase))
                            list.Add(new ConfigFile(group, PackShortName(e.FullName), pack, Path.GetRelativePath(root, pack) + "/" + e.FullName)
                            {
                                Category = ConfigCategory.Datapack,
                                ZipEntry = e.FullName,
                            });
                    }
                    catch { }   // not a readable zip
                }
            }
        return list;
    }

    /// <summary>A datapack file's name in the list: everything lives under data/, so that part is left off.</summary>
    static string PackShortName(string path) => path.StartsWith("data/", StringComparison.OrdinalIgnoreCase) ? path[5..] : path;

    /// <summary>The text files of an unzipped datapack, pack.mcmeta first.</summary>
    static IEnumerable<string> PackFiles(string pack)
    {
        try
        {
            return Directory.EnumerateFiles(pack, "*", SearchOption.AllDirectories)
                .Where(f => DatapackExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()) && new FileInfo(f).Length <= MaxSize)
                .OrderBy(f => Path.GetFileName(f) != "pack.mcmeta").ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch { return Enumerable.Empty<string>(); }
    }

    /// <summary>Name is shown relative to the group's own folder, e.g. "config.yml" or "lang\messages.yml".</summary>
    static void Add(List<ConfigFile> list, ConfigCategory category, string group, string baseDir, string path) =>
        list.Add(new ConfigFile(group, Path.GetRelativePath(baseDir, path), path, Path.GetRelativePath(Paths.ServerDir, path)) { Category = category });

    static IEnumerable<string> Scan(string dir, int depth)
    {
        IEnumerable<string> files;
        try { files = Directory.GetFiles(dir).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList(); }
        catch { yield break; }
        foreach (var f in files)
        {
            if (!Extensions.Contains(Path.GetExtension(f).ToLowerInvariant())) continue;
            if (SkipFile.IsMatch(Path.GetFileName(f))) continue;
            try { if (new FileInfo(f).Length > MaxSize) continue; } catch { continue; }
            yield return f;
        }
        if (depth <= 0) yield break;
        string[] subs;
        try { subs = Directory.GetDirectories(dir); } catch { yield break; }
        foreach (var sub in subs.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            if (SkipFolder.IsMatch(Path.GetFileName(sub))) continue;
            foreach (var f in Scan(sub, depth - 1)) yield return f;
        }
    }

    /// <summary>A loaded file, remembering its encoding and line endings so saving doesn't change them.</summary>
    public sealed class Document
    {
        public required string Path { get; init; }
        /// <summary>The file inside the zip at <see cref="Path"/>, or null for a plain file.</summary>
        public string? Entry { get; init; }
        public required string Text { get; init; }
        public required bool HasBom { get; init; }
        public required bool CrLf { get; init; }
    }

    public static Document Load(ConfigFile file)
    {
        byte[] bytes;
        if (file.ZipEntry == null) bytes = File.ReadAllBytes(file.FullPath);
        else
        {
            using var zip = ZipFile.OpenRead(file.FullPath);
            var entry = zip.GetEntry(file.ZipEntry) ?? throw new FileNotFoundException($"{file.ZipEntry} isn't in {System.IO.Path.GetFileName(file.FullPath)} any more.");
            using var ms = new MemoryStream();
            using (var src = entry.Open()) src.CopyTo(ms);
            bytes = ms.ToArray();
        }
        var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var text = new UTF8Encoding(false).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
        return new Document { Path = file.FullPath, Entry = file.ZipEntry, Text = text, HasBom = bom, CrLf = text.Contains("\r\n") };
    }

    /// <summary>Returns an error message if the text is not valid for its file type, otherwise null.</summary>
    public static string? Validate(string path, string text)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".json" or ".mcmeta")
        {
            try { JsonNode.Parse(text); }
            catch (Exception ex) { return "This JSON is not valid: " + ex.Message; }
        }
        if (ext is ".yml" or ".yaml")
        {
            var lines = text.Replace("\r\n", "\n").Split('\n');
            for (var i = 0; i < lines.Length; i++)
                if (Regex.IsMatch(lines[i], @"^ *\t"))
                    return $"Line {i + 1} is indented with a tab. YAML only allows spaces for indentation.";
        }
        return null;
    }

    /// <summary>Backs up the current file to backups\configs\, then writes the new text.</summary>
    public static string Save(Document doc, string newText)
    {
        var normalized = newText.Replace("\r\n", "\n");
        if (doc.CrLf) normalized = normalized.Replace("\n", "\r\n");

        var backup = System.IO.Path.Combine(Paths.BackupDir, "configs", DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"),
            System.IO.Path.GetRelativePath(Paths.ServerDir, doc.Path));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(backup)!);
        File.Copy(doc.Path, backup, overwrite: true);

        var body = new UTF8Encoding(false).GetBytes(normalized);
        if (doc.Entry != null)
        {
            // a file inside a zipped datapack: the whole zip was backed up above; replace just this entry
            using var zip = ZipFile.Open(doc.Path, ZipArchiveMode.Update);
            zip.GetEntry(doc.Entry)?.Delete();
            var entry = zip.CreateEntry(doc.Entry, CompressionLevel.Optimal);
            using var dst = entry.Open();
            if (doc.HasBom) dst.Write(new byte[] { 0xEF, 0xBB, 0xBF });
            dst.Write(body);
            return backup;
        }
        using (var fs = File.Create(doc.Path))
        {
            if (doc.HasBom) fs.Write(new byte[] { 0xEF, 0xBB, 0xBF });
            fs.Write(body);
        }
        return backup;
    }
}
