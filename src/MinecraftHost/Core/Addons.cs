using System.ComponentModel;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MinecraftHost.Core;

public enum AddonKind { Plugin, Datapack, Mod }

/// <summary>Good = works on this server version, Bad = broken or out of date, Unknown = nothing to go on.</summary>
public enum Health { Unknown, Good, Bad }

/// <summary>A newer file for the server's version. Url is null when the author only allows downloads from their website.</summary>
public sealed record UpdateFile(string Source, string Version, string? Url, string FileName, string? Sha1, string? PageUrl);

/// <summary>A plugin jar, a datapack or a mod jar, switched on or off.</summary>
public sealed class Addon : INotifyPropertyChanged
{
    public required AddonKind Kind { get; init; }
    /// <summary>Plugin name from plugin.yml (also its config folder name), or the datapack's file name.</summary>
    public required string Name { get; init; }
    public string? Version { get; init; }
    public required string Sha1 { get; init; }
    public string FullPath { get; set; } = "";
    public string FileName => Path.GetFileName(FullPath);
    public string Title => Version == null ? Name : $"{Name}  {Version}";
    public string Group => Kind switch { AddonKind.Plugin => "PLUGINS", AddonKind.Mod => "MODS", _ => "DATAPACKS" };
    /// <summary>For mods: Fabric, Quilt, Forge or NeoForge, from the jar's metadata.</summary>
    public string? Loader { get; init; }

    public IReadOnlyList<string> Authors { get; init; } = Array.Empty<string>();
    /// <summary>A datapack's description from pack.mcmeta (datapacks have no author field; it's usually named here).</summary>
    public string? Description { get; init; }

    /// <summary>The ⓘ hover: version and author for plugins, description for datapacks.</summary>
    public string Info
    {
        get
        {
            var lines = new List<string>();
            if (Kind != AddonKind.Datapack)
            {
                if (Loader != null) lines.Add("Loader: " + Loader);
                lines.Add("Version: " + (Version ?? "not stated"));
                lines.Add(Authors.Count switch
                {
                    0 => "Author: not stated",
                    1 => "Author: " + Authors[0],
                    <= 4 => "Authors: " + string.Join(", ", Authors),
                    _ => $"Authors: {string.Join(", ", Authors.Take(4))} and {Authors.Count - 4} more",
                });
            }
            else
            {
                lines.Add(string.IsNullOrWhiteSpace(Description) ? "No description" : Description.Trim());
            }
            return string.Join("\n", lines);
        }
    }

    /// <summary>The site the file says it comes from (plugin.yml "website", or a domain in a datapack's description).</summary>
    public string? Website { get; init; }
    /// <summary>The Modrinth / CurseForge project page, once found by a check for updates.</summary>
    public string? PageUrl { get; set; }
    public string WebUrl => PageUrl ?? Website ??
        $"https://modrinth.com/{Kind switch { AddonKind.Plugin => "plugins", AddonKind.Mod => "mods", _ => "datapacks" }}?q={Uri.EscapeDataString(Name)}";
    public string WebTip => PageUrl != null ? $"Open its {FoundOn} page" : Website != null ? $"Open {Website}" : $"Search Modrinth for \"{Name}\"";

    // evidence
    public bool LoadedOk { get; set; }          // enabled cleanly in the last server start on this version
    public string? LogProblem { get; set; }     // failed in the last server start
    public string? FormatProblem { get; set; }  // datapack made for another pack format, or a mod for another loader
    public bool FormatOk { get; set; }
    public bool OnlineChecked { get; set; }
    public string? FoundOn { get; set; }        // "Modrinth" / "CurseForge" when the exact file was recognised
    public bool ListedForServer { get; set; }   // that site lists this exact file for the server's version
    public UpdateFile? Update { get; set; }     // a newer file for the server's version

    bool _enabled;
    public bool Enabled { get => _enabled; set { _enabled = value; Raise(nameof(Enabled)); } }

    public Health Health { get; private set; }
    public string Detail { get; private set; } = "";
    public bool HasUpdate => Update != null;

    public void Evaluate(string mc, bool curseForge)
    {
        (Health, Detail) = Judge(mc, curseForge);
        Raise(nameof(Health)); Raise(nameof(Detail)); Raise(nameof(HasUpdate)); Raise(nameof(Title));
        Raise(nameof(FullPath)); Raise(nameof(WebUrl)); Raise(nameof(WebTip));
    }

    (Health, string) Judge(string mc, bool curseForge)
    {
        if (LogProblem != null) return (Health.Bad, LogProblem + (Update != null ? $" Â· {Update.Version} is available" : ""));
        if (FormatProblem != null) return (Health.Bad, FormatProblem + (Update != null ? $" Â· {Update.Version} is available" : ""));
        if (Update != null)
            return (Health.Bad, ListedForServer || LoadedOk || FormatOk
                ? $"Out of date: {Update.Version} is available on {Update.Source}"
                : $"Not made for {mc}: update to {Update.Version} from {Update.Source}");
        if (ListedForServer) return (Health.Good, $"Up to date, made for {mc} ({FoundOn})");
        if (Kind == AddonKind.Datapack && FormatOk) return (Health.Good, $"Made for {mc}" + (OnlineChecked ? "" : " Â· not checked for updates"));
        if (LoadedOk) return (Health.Good, $"Loaded fine at the last start on {mc}" + (OnlineChecked ? "" : " Â· not checked for updates"));
        if (FoundOn != null) return (Health.Bad, $"No {mc} version on {FoundOn} yet");
        if (!OnlineChecked) return (Health.Unknown, "Not checked yet: press Check for updates");
        var sites = curseForge ? "Modrinth or CurseForge" : "Modrinth";
        return (Health.Unknown, Enabled ? $"Not found on {sites}; start the server to test it" : $"Not found on {sites}; hasn't run on {mc}");
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Finds plugins, datapacks and mods, works out whether they suit the server, and switches them on or off.</summary>
public static class Addons
{
    /// <summary>Where switched-off things go. Old "disabled-until-..." folders are read too.</summary>
    public static string DisabledDir => Path.Combine(Paths.ServerDir, "disabled");
    static string PluginsDir => Path.Combine(Paths.ServerDir, "plugins");
    public static string ModsDir => Path.Combine(Paths.ServerDir, "mods");

    static string LiveDir(AddonKind kind) => kind switch { AddonKind.Plugin => PluginsDir, AddonKind.Mod => ModsDir, _ => DatapacksDir };
    static string SubDir(AddonKind kind) => kind switch { AddonKind.Plugin => "plugins", AddonKind.Mod => "mods", _ => "datapacks" };

    public static string DatapacksDir
    {
        get
        {
            var level = "world";
            var props = Path.Combine(Paths.ServerDir, "server.properties");
            if (File.Exists(props))
                foreach (var line in File.ReadLines(props))
                {
                    var m = Regex.Match(line, @"^level-name=(.+)$");
                    if (m.Success && m.Groups[1].Value.Trim().Length > 0) level = m.Groups[1].Value.Trim();
                }
            return Path.Combine(Paths.ServerDir, level, "datapacks");
        }
    }

    static IEnumerable<string> DisabledRoots()
    {
        if (!Directory.Exists(Paths.ServerDir)) yield break;
        yield return DisabledDir;
        foreach (var d in Directory.GetDirectories(Paths.ServerDir, "disabled-until*")) yield return d;
    }

    // ------------------------------------------------------------ scan

    /// <summary>Lists everything (reads jars and hashes files, so run it off the UI thread).</summary>
    public static List<Addon> Scan(string? mcVersion, string? edition)
    {
        var list = new List<Addon>();

        foreach (var jar in Files(ModsDir, "*.jar")) AddMod(list, jar, true);
        foreach (var root in DisabledRoots())
            foreach (var jar in Files(Path.Combine(root, "mods"), "*.jar")) AddMod(list, jar, false);

        foreach (var jar in Files(PluginsDir, "*.jar")) AddPlugin(list, jar, true);
        foreach (var root in DisabledRoots())
            foreach (var jar in Files(Path.Combine(root, "plugins"), "*.jar")) AddPlugin(list, jar, false);

        var packs = DatapacksDir;
        foreach (var p in Packs(packs))
            if (!Path.GetFileName(p).Equals("bukkit", StringComparison.OrdinalIgnoreCase))   // Paper's own pack for plugins
                AddPack(list, p, true);
        foreach (var root in DisabledRoots())
        {
            var dir = Path.Combine(root, "datapacks");
            if (!Directory.Exists(dir)) continue;
            // old layout kept one folder per world: datapacks\<world>\pack.zip
            var candidates = Packs(dir).Concat(Directory.GetDirectories(dir).Where(d => !IsPack(d)).SelectMany(Packs));
            foreach (var p in candidates)
                if (!list.Any(a => a.Kind == AddonKind.Datapack && a.FileName.Equals(Path.GetFileName(p), StringComparison.OrdinalIgnoreCase)))
                    AddPack(list, p, false);
        }

        if (mcVersion != null) ReadLog(list, mcVersion);
        CheckFormats(list);
        CheckLoaders(list, edition, mcVersion);
        return list;
    }

    static IEnumerable<string> Files(string dir, string pattern) =>
        Directory.Exists(dir) ? Directory.GetFiles(dir, pattern).OrderBy(f => f, StringComparer.OrdinalIgnoreCase) : Enumerable.Empty<string>();

    static bool IsPack(string path) =>
        Directory.Exists(path) ? File.Exists(Path.Combine(path, "pack.mcmeta")) : path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    static IEnumerable<string> Packs(string dir) =>
        Directory.Exists(dir)
            ? Directory.GetFileSystemEntries(dir).Where(IsPack).OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            : Enumerable.Empty<string>();

    static void AddPlugin(List<Addon> list, string jar, bool enabled)
    {
        string? name = null, version = null, website = null;
        var authors = new List<string>();
        try
        {
            using var zip = ZipFile.OpenRead(jar);
            var entry = zip.GetEntry("paper-plugin.yml") ?? zip.GetEntry("plugin.yml");
            if (entry != null)
            {
                using var reader = new StreamReader(entry.Open());
                var inAuthorList = false;
                foreach (var raw in reader.ReadToEnd().Split('\n'))
                {
                    var line = raw.TrimEnd('\r');
                    if (inAuthorList)
                    {
                        // authors:
                        //   - someone
                        var item = Regex.Match(line, @"^\s+-\s*(.+)$");
                        if (item.Success) { authors.Add(Unquote(item.Groups[1].Value)); continue; }
                        inAuthorList = false;
                    }
                    var m = Regex.Match(line, @"^(name|version|website|author|authors):\s*(.*?)\s*$");
                    if (!m.Success) continue;
                    var value = m.Groups[2].Value;
                    switch (m.Groups[1].Value)
                    {
                        case "name": name ??= Unquote(value); break;
                        case "version": version ??= Unquote(value); break;
                        case "website": website ??= AsUrl(Unquote(value)); break;
                        case "author": if (value.Length > 0) authors.Insert(0, Unquote(value)); break;
                        case "authors":
                            if (value.Length == 0) inAuthorList = true;
                            else authors.AddRange(value.Trim('[', ']').Split(',').Select(Unquote).Where(a => a.Length > 0));   // [a, b]
                            break;
                    }
                }
            }
        }
        catch { }   // not a readable jar; still listed by file name
        list.Add(new Addon
        {
            Kind = AddonKind.Plugin,
            Name = name ?? Path.GetFileNameWithoutExtension(jar),
            Version = version,
            Website = website,
            Authors = authors.Distinct().ToList(),
            Sha1 = HashOf(jar),
            FullPath = jar,
            Enabled = enabled,
        });
    }

    static void AddMod(List<Addon> list, string jar, bool enabled)
    {
        string? name = null, version = null, website = null, loader = null;
        var authors = new List<string>();
        try
        {
            using var zip = ZipFile.OpenRead(jar);
            string? Read(string entry)
            {
                var e = zip.GetEntry(entry);
                if (e == null) return null;
                using var r = new StreamReader(e.Open());
                return r.ReadToEnd();
            }

            if ((Read("fabric.mod.json") ?? Read("quilt.mod.json")) is { } json)
            {
                loader = zip.GetEntry("fabric.mod.json") != null ? "Fabric" : "Quilt";
                var root = JsonNode.Parse(json, documentOptions: new() { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true });
                var meta = root?["quilt_loader"]?["metadata"] ?? root;   // quilt nests the details
                name = meta?["name"]?.GetValue<string>() ?? root?["id"]?.GetValue<string>() ?? root?["quilt_loader"]?["id"]?.GetValue<string>();
                version = (root?["version"] ?? root?["quilt_loader"]?["version"])?.GetValue<string>();
                website = meta?["contact"]?["homepage"]?.GetValue<string>() ?? meta?["contact"]?["sources"]?.GetValue<string>();
                if ((meta?["authors"] ?? meta?["contributors"]) is JsonArray people)
                    foreach (var person in people)
                        if ((person is JsonObject o ? o["name"] : person)?.GetValue<string>() is { Length: > 0 } who) authors.Add(who);
            }
            else if ((Read("META-INF/neoforge.mods.toml") ?? Read("META-INF/mods.toml")) is { } toml)
            {
                loader = zip.GetEntry("META-INF/neoforge.mods.toml") != null ? "NeoForge" : "Forge";
                // the first [[mods]] table describes the mod itself
                var start = toml.IndexOf("[[mods]]", StringComparison.Ordinal);
                var section = start >= 0 ? toml[start..] : toml;
                var next = section.IndexOf("[[", 8, StringComparison.Ordinal);
                if (next > 0) section = section[..next];
                static string? Value(string key, string text)
                {
                    var m = Regex.Match(text, $@"(?m)^\s*{key}\s*=\s*(?:""([^""]*)""|'([^']*)')");
                    return m.Success ? (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) : null;
                }
                name = Value("displayName", section) ?? Value("modId", section);
                version = Value("version", section);
                if (version != null && version.Contains("${"))   // "${file.jarVersion}": the jar's manifest has it
                {
                    var mv = Regex.Match(Read("META-INF/MANIFEST.MF") ?? "", @"Implementation-Version:\s*(\S+)");
                    version = mv.Success ? mv.Groups[1].Value : null;
                }
                website = Value("displayURL", section) ?? Value("displayURL", toml);
                if ((Value("authors", section) ?? Value("authors", toml)) is { } names)
                    authors.AddRange(names.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0));
            }
            else if (Read("mcmod.info") is { } info)   // Forge 1.12 and older
            {
                loader = "Forge";
                var node = JsonNode.Parse(info);
                var mod = (node as JsonArray ?? node?["modList"] as JsonArray)?.FirstOrDefault();
                name = mod?["name"]?.GetValue<string>();
                version = mod?["version"]?.GetValue<string>();
                website = mod?["url"]?.GetValue<string>();
                if (mod?["authorList"] is JsonArray people)
                    authors.AddRange(people.Select(x => x?.GetValue<string>() ?? "").Where(x => x.Length > 0));
            }
        }
        catch { }   // not a readable jar; still listed by file name
        list.Add(new Addon
        {
            Kind = AddonKind.Mod,
            Name = name ?? Path.GetFileNameWithoutExtension(jar),
            Version = version,
            Loader = loader,
            Website = website == null ? null : AsUrl(website),
            Authors = authors.Distinct().ToList(),
            Sha1 = HashOf(jar),
            FullPath = jar,
            Enabled = enabled,
        });
    }

    /// <summary>Flags mods made for another loader than the server runs (e.g. a Fabric mod on Forge).</summary>
    static void CheckLoaders(List<Addon> list, string? edition, string? mc)
    {
        if (!Editions.RunsMods(edition)) return;
        foreach (var a in list.Where(a => a.Kind == AddonKind.Mod && a.Loader != null))
        {
            if (!Editions.LoadsMod(edition, a.Loader!, mc)) a.FormatProblem = $"Made for {a.Loader}; this server runs {edition}";
        }
    }

    static void AddPack(List<Addon> list, string path, bool enabled) =>
        list.Add(new Addon
        {
            Kind = AddonKind.Datapack,
            Name = Directory.Exists(path) ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path),
            Website = PackWebsite(path),
            Description = PackDescription(path),
            Sha1 = Directory.Exists(path) ? "" : HashOf(path),
            FullPath = path,
            Enabled = enabled,
        });

    static string Unquote(string s) => s.Trim().Trim('"', '\'').Trim();

    /// <summary>"example.com/x" or "https://example.com/x" as a link; null if it isn't one.</summary>
    static string? AsUrl(string text)
    {
        var m = Regex.Match(text, @"(https?://)?((?:[a-z0-9-]+\.)+[a-z]{2,})(/[^\s""'<>]*)?", RegexOptions.IgnoreCase);
        return m.Success ? (m.Groups[1].Success ? m.Value : "https://" + m.Value) : null;
    }

    static string? ReadMcmeta(string path)
    {
        try
        {
            if (Directory.Exists(path)) return File.ReadAllText(Path.Combine(path, "pack.mcmeta"));
            using var zip = ZipFile.OpenRead(path);
            var entry = zip.GetEntry("pack.mcmeta");
            if (entry == null) return null;
            using var r = new StreamReader(entry.Open());
            return r.ReadToEnd();
        }
        catch { return null; }
    }

    /// <summary>A site named in the pack's description, e.g. Vanilla Tweaks packs say "vanillatweaks.net".</summary>
    static string? PackWebsite(string path) => PackDescription(path) is { } d ? AsUrl(d) : null;

    static string? PackDescription(string path)
    {
        try
        {
            var text = ReadMcmeta(path);
            var description = text == null ? null : JsonNode.Parse(text.Trim())?["pack"]?["description"];
            return description == null ? null : Regex.Replace(PlainText(description), @"[ \t]*\n[ \t]*", "\n").Trim();
        }
        catch { return null; }
    }

    /// <summary>The words of a text component: a string, {"text": ...} or a list of those.</summary>
    static string PlainText(JsonNode node) => node switch
    {
        JsonValue v => v.TryGetValue<string>(out var s) ? s : "",
        JsonArray arr => string.Join(" ", arr.Where(n => n != null).Select(n => PlainText(n!))),
        JsonObject obj => string.Join(" ", new[] { obj["text"], obj["extra"] }.Where(n => n != null).Select(n => PlainText(n!))),
        _ => "",
    };

    static string HashOf(string file)
    {
        try { return Net.HashFile(file, "SHA1"); } catch { return ""; }
    }

    // ------------------------------------------------------------ last server start

    static readonly Regex StartRx = new(@"Starting minecraft server version (\S+)");
    static readonly Regex CouldNotLoadRx = new(@"Could not load (?:plugin )?'(?:plugins[/\\])?([^']+)'");
    static readonly Regex EnableErrorRx = new(@"Error occurred while enabling (\S+)");
    static readonly Regex EnablingRx = new(@"\]: \[[^\]]+\] Enabling (\S+) v");
    static readonly Regex DisablingRx = new(@"\]: \[[^\]]+\] Disabling (\S+) v");
    static readonly Regex DoneRx = new(@"\]: Done \(");

    /// <summary>Reads logs\latest.log for plugins that loaded, failed, or switched themselves off while starting.</summary>
    static void ReadLog(List<Addon> list, string mcVersion)
    {
        var log = Path.Combine(Paths.ServerDir, "logs", "latest.log");
        if (!File.Exists(log)) return;
        string[] lines;
        try
        {
            using var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            lines = new StreamReader(fs).ReadToEnd().Split('\n');
        }
        catch { return; }

        var start = lines.Select(l => StartRx.Match(l)).FirstOrDefault(m => m.Success);
        if (start == null || start.Groups[1].Value != mcVersion) return;   // last run was another version
        var logTime = File.GetLastWriteTimeUtc(log);

        var enabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var problems = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var done = false;
        foreach (var line in lines)
        {
            if (DoneRx.IsMatch(line)) done = true;
            Match m;
            if ((m = CouldNotLoadRx.Match(line)).Success) failedFiles.Add(Path.GetFileName(m.Groups[1].Value));
            else if ((m = EnableErrorRx.Match(line)).Success) problems.TryAdd(m.Groups[1].Value, "Crashed while starting at the last server start");
            else if ((m = EnablingRx.Match(line)).Success) enabled.Add(m.Groups[1].Value);
            else if (!done && (m = DisablingRx.Match(line)).Success && enabled.Contains(m.Groups[1].Value))
                problems.TryAdd(m.Groups[1].Value, "Switched itself off at the last server start (see the log)");
        }

        // a mod that fails to load stops the whole server, so reaching "Done" means every mod loaded
        foreach (var a in list.Where(a => a.Kind == AddonKind.Mod && a.Enabled && done))
            try { if (File.GetLastWriteTimeUtc(a.FullPath) <= logTime) a.LoadedOk = true; } catch { }

        foreach (var a in list.Where(a => a.Kind == AddonKind.Plugin))
        {
            // the file changed since that start, so the log says nothing about this copy
            try { if (File.GetLastWriteTimeUtc(a.FullPath) > logTime) continue; } catch { continue; }
            if (failedFiles.Contains(a.FileName)) a.LogProblem = "Failed to load at the last server start";
            else if (!a.Enabled) continue;   // the name may belong to the enabled copy
            else if (problems.TryGetValue(a.Name, out var p)) a.LogProblem = p;
            else if (enabled.Contains(a.Name)) a.LoadedOk = true;
        }
    }

    // ------------------------------------------------------------ datapack formats

    /// <summary>The data pack format the server jar expects (e.g. 121), from its version.json.</summary>
    public static int? ServerPackFormat()
    {
        try
        {
            using var zip = ZipFile.OpenRead(Paths.ServerJar);
            var entry = zip.GetEntry("version.json");
            if (entry == null) return null;
            using var reader = new StreamReader(entry.Open());
            var pv = JsonNode.Parse(reader.ReadToEnd())?["pack_version"];
            return pv?["data_major"]?.GetValue<int>() ?? pv?["data"]?.GetValue<int>();
        }
        catch { return null; }
    }

    static void CheckFormats(List<Addon> list)
    {
        var server = ServerPackFormat();
        if (server == null) return;
        foreach (var a in list.Where(a => a.Kind == AddonKind.Datapack))
        {
            var range = PackRange(a.FullPath);
            if (range == null) continue;
            var (min, max) = range.Value;
            if (server >= min && server <= max) a.FormatOk = true;
            else a.FormatProblem = min == max
                ? $"Made for pack format {min}; this server needs {server}"
                : $"Made for pack formats {min}-{max}; this server needs {server}";
        }
    }

    /// <summary>Reads pack.mcmeta's supported range (min/max_format, supported_formats or pack_format).</summary>
    static (int, int)? PackRange(string path)
    {
        var text = ReadMcmeta(path);
        try
        {
            var pack = text == null ? null : JsonNode.Parse(text.Trim())?["pack"];
            if (pack == null) return null;

            static int? Major(JsonNode? n) => n switch
            {
                JsonArray arr when arr.Count > 0 => arr[0]!.GetValue<int>(),   // [major, minor]
                JsonValue v => v.GetValue<int>(),
                _ => null,
            };
            var min = Major(pack["min_format"]);
            var max = Major(pack["max_format"]);
            if (min != null && max != null) return (min.Value, max.Value);
            switch (pack["supported_formats"])
            {
                case JsonArray arr when arr.Count == 2: return (arr[0]!.GetValue<int>(), arr[1]!.GetValue<int>());
                case JsonObject obj: return (obj["min_inclusive"]!.GetValue<int>(), obj["max_inclusive"]!.GetValue<int>());
                case JsonValue v: return (v.GetValue<int>(), v.GetValue<int>());
            }
            var f = Major(pack["pack_format"]);
            return f == null ? null : (f.Value, f.Value);
        }
        catch { return null; }
    }

    // ------------------------------------------------------------ checking online (only when the user asks)

    static readonly string[] PluginLoaders = { "paper", "spigot", "bukkit", "purpur", "folia" };
    static readonly string[] ModLoaders = { "fabric", "quilt", "forge", "neoforge" };

    /// <summary>
    /// Looks every file up by hash on Modrinth (no account needed), then on CurseForge if a key is set:
    /// is this exact file made for the server's version, and is there a newer one?
    /// </summary>
    public static async Task CheckOnlineAsync(List<Addon> list, string mc, string? curseForgeKey)
    {
        foreach (var a in list) { a.FoundOn = null; a.ListedForServer = false; a.Update = null; a.PageUrl = null; }
        await CheckModrinthAsync(list.Where(a => a.Sha1.Length > 0).ToList(), mc);
        if (!string.IsNullOrWhiteSpace(curseForgeKey))
            await CheckCurseForgeAsync(list.Where(a => a.FoundOn == null && File.Exists(a.FullPath)).ToList(), mc, curseForgeKey.Trim());
        foreach (var a in list) a.OnlineChecked = true;
    }

    static async Task CheckModrinthAsync(List<Addon> list, string mc)
    {
        var hashes = list.Select(a => a.Sha1).Distinct().ToArray();
        if (hashes.Length == 0) return;
        var known = await Net.PostJsonAsync("https://api.modrinth.com/v2/version_files", new { hashes, algorithm = "sha1" }) as JsonObject
            ?? throw new InvalidOperationException("Modrinth didn't answer. Check the internet connection and try again.");

        var current = new Dictionary<Addon, JsonObject>();
        foreach (var a in list)
        {
            if (known[a.Sha1] is not JsonObject v) continue;
            current[a] = v;
            a.FoundOn = "Modrinth";
            a.PageUrl = $"https://modrinth.com/project/{v["project_id"]?.GetValue<string>()}";
            a.ListedForServer = v["game_versions"] is JsonArray gv && gv.Any(x => x?.GetValue<string>() == mc);
        }

        // ask for updates per loader set, so a Paper build is only replaced by a Paper build (not the Spigot upload)
        foreach (var group in current.GroupBy(kv => LoaderKey(kv.Key, kv.Value)))
        {
            var loaders = group.Key.Split(',');
            var groupHashes = group.Select(kv => kv.Key.Sha1).Distinct().ToArray();
            if (await Net.PostJsonAsync("https://api.modrinth.com/v2/version_files/update",
                    new { hashes = groupHashes, algorithm = "sha1", loaders, game_versions = new[] { mc } }) is not JsonObject updates) continue;
            foreach (var (a, cur) in group)
            {
                if (updates[a.Sha1] is not JsonObject latest || latest["id"]?.GetValue<string>() == cur["id"]?.GetValue<string>()) continue;
                // the same release is often uploaded once per loader; only a later one counts as newer
                if (Date(latest["date_published"]) <= Date(cur["date_published"]).AddHours(1)) continue;
                var file = (latest["files"] as JsonArray)?.OfType<JsonObject>()
                    .OrderByDescending(f => f["primary"]?.GetValue<bool>() == true).FirstOrDefault();
                if (file == null) continue;
                a.Update = new UpdateFile("Modrinth", latest["version_number"]?.GetValue<string>() ?? "a newer version",
                    file["url"]?.GetValue<string>(), file["filename"]!.GetValue<string>(), file["hashes"]?["sha1"]?.GetValue<string>(),
                    $"https://modrinth.com/project/{cur["project_id"]?.GetValue<string>()}");
            }
        }
    }

    static string LoaderKey(Addon a, JsonObject version)
    {
        if (a.Kind == AddonKind.Datapack) return "datapack";
        var known = a.Kind == AddonKind.Mod ? ModLoaders : PluginLoaders;
        // a mod's update must be for the loader its jar was made for
        if (a.Kind == AddonKind.Mod && a.Loader?.ToLowerInvariant() is { } own && known.Contains(own)) return own;
        var mine = (version["loaders"] as JsonArray)?.Select(l => l?.GetValue<string>()).Where(l => l != null && known.Contains(l)).ToList();
        return string.Join(",", mine is { Count: > 0 } ? mine! : known.ToList());
    }

    static DateTime Date(JsonNode? n) =>
        DateTime.TryParse(n?.GetValue<string>(), out var d) ? d.ToUniversalTime() : DateTime.MinValue;

    // CurseForge identifies files by a MurmurHash2 "fingerprint" and needs an API key (console.curseforge.com).
    const string CurseForgeApi = "https://api.curseforge.com/v1";

    static async Task CheckCurseForgeAsync(List<Addon> list, string mc, string key)
    {
        var byPrint = new Dictionary<uint, Addon>();
        foreach (var a in list)
            try { byPrint.TryAdd(Fingerprint(File.ReadAllBytes(a.FullPath)), a); } catch { }
        if (byPrint.Count == 0) return;

        var reply = await CurseForgePostAsync($"{CurseForgeApi}/fingerprints/432", new { fingerprints = byPrint.Keys.ToArray() }, key);
        var matches = (reply?["data"]?["exactMatches"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new();
        if (matches.Count == 0) return;

        // project pages, for files whose authors don't allow downloads from other apps
        var pages = new Dictionary<int, string>();
        var mods = await CurseForgePostAsync($"{CurseForgeApi}/mods", new { modIds = matches.Select(m => m["id"]!.GetValue<int>()).Distinct().ToArray() }, key);
        foreach (var m in (mods?["data"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
            if (m["links"]?["websiteUrl"]?.GetValue<string>() is { } url) pages[m["id"]!.GetValue<int>()] = url;

        foreach (var match in matches)
        {
            if (match["file"] is not JsonObject cur) continue;
            var print = cur["fileFingerprint"]?.GetValue<long>();
            if (print == null || !byPrint.TryGetValue((uint)print.Value, out var a)) continue;
            a.FoundOn = "CurseForge";
            a.PageUrl = pages.GetValueOrDefault(match["id"]!.GetValue<int>());
            a.ListedForServer = ForVersion(cur, mc);
            var latest = (match["latestFiles"] as JsonArray)?.OfType<JsonObject>()
                .Where(f => ForVersion(f, mc) && f["releaseType"]?.GetValue<int>() == 1 && Date(f["fileDate"]) > Date(cur["fileDate"]).AddHours(1))
                .OrderByDescending(f => Date(f["fileDate"])).FirstOrDefault();
            if (latest == null) continue;
            var sha1 = (latest["hashes"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(h => h["algo"]?.GetValue<int>() == 1)?["value"]?.GetValue<string>();
            a.Update = new UpdateFile("CurseForge", latest["displayName"]?.GetValue<string>() ?? "a newer version",
                latest["downloadUrl"]?.GetValue<string>(), latest["fileName"]!.GetValue<string>(), sha1,
                pages.GetValueOrDefault(match["id"]!.GetValue<int>()));
        }
    }

    static bool ForVersion(JsonObject file, string mc) =>
        file["gameVersions"] is JsonArray gv && gv.Any(v => v?.GetValue<string>() == mc);

    static async Task<JsonNode?> CurseForgePostAsync(string url, object body, string key)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("x-api-key", key);
        using var resp = await Net.Http.SendAsync(req);
        if (resp.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("CurseForge didn't accept the API key. Check it under \"CurseForge key\".");
        resp.EnsureSuccessStatusCode();
        return JsonNode.Parse(await resp.Content.ReadAsStringAsync());
    }

    /// <summary>CurseForge's file fingerprint: MurmurHash2 (seed 1) of the file with whitespace bytes removed.</summary>
    public static uint Fingerprint(byte[] data)
    {
        var buf = new byte[data.Length];
        var n = 0;
        foreach (var b in data)
            if (b is not (9 or 10 or 13 or 32)) buf[n++] = b;

        const uint m = 0x5bd1e995;
        uint h = 1u ^ (uint)n;
        var i = 0;
        for (; n - i >= 4; i += 4)
        {
            uint k = (uint)(buf[i] | buf[i + 1] << 8 | buf[i + 2] << 16 | buf[i + 3] << 24);
            k *= m; k ^= k >> 24; k *= m;
            h *= m; h ^= k;
        }
        switch (n - i)
        {
            case 3: h ^= (uint)buf[i + 2] << 16; goto case 2;
            case 2: h ^= (uint)buf[i + 1] << 8; goto case 1;
            case 1: h ^= buf[i]; h *= m; break;
        }
        h ^= h >> 13; h *= m; h ^= h >> 15;
        return h;
    }

    // ------------------------------------------------------------ updating

    /// <summary>
    /// Downloads the newer file (hash-checked) next to the old one and moves the old one to backups\plugins\&lt;time&gt;\.
    /// The server must be stopped. Returns false if the file can only be downloaded from the website.
    /// </summary>
    public static async Task<bool> UpdateAsync(Addon a, string stamp)
    {
        var u = a.Update ?? throw new InvalidOperationException("No update for " + a.Name);
        if (u.Url == null) return false;
        var dir = Path.GetDirectoryName(a.FullPath)!;
        var target = Path.Combine(dir, u.FileName);

        var backupDir = Path.Combine(Paths.BackupDir, SubDir(a.Kind), stamp);
        Directory.CreateDirectory(backupDir);
        var backup = Path.Combine(backupDir, a.FileName);
        File.Copy(a.FullPath, backup, overwrite: true);

        await Net.DownloadAsync(u.Url, target, u.Sha1 != null ? "SHA1" : null, u.Sha1);
        if (!target.Equals(a.FullPath, StringComparison.OrdinalIgnoreCase)) File.Delete(a.FullPath);
        Log.Host($"Updated {a.Name} to {u.Version} ({u.FileName}); the old file is in {Path.GetRelativePath(Paths.Root, backup)}.");
        return true;
    }

    // ------------------------------------------------------------ switching on / off

    /// <summary>
    /// What a dropped file is: a plugin or mod jar (by its metadata) or a datapack (a .zip or folder with pack.mcmeta at the top).
    /// Returns null if it's none of those.
    /// </summary>
    public static Addon? Identify(string path)
    {
        var list = new List<Addon>();
        try
        {
            if (Directory.Exists(path))
            {
                if (File.Exists(Path.Combine(path, "pack.mcmeta"))) AddPack(list, path, true);
            }
            else if (path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            {
                using (var zip = ZipFile.OpenRead(path))
                {
                    bool Has(string entry) => zip.GetEntry(entry) != null;
                    if (Has("paper-plugin.yml") || Has("plugin.yml")) AddPlugin(list, path, true);
                    else if (Has("fabric.mod.json") || Has("quilt.mod.json") || Has("META-INF/mods.toml") || Has("META-INF/neoforge.mods.toml") || Has("mcmod.info"))
                        AddMod(list, path, true);
                }
            }
            else if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                bool pack;
                using (var zip = ZipFile.OpenRead(path)) pack = zip.GetEntry("pack.mcmeta") != null;
                if (pack) AddPack(list, path, true);
            }
        }
        catch { }   // not a readable archive
        return list.FirstOrDefault();
    }

    /// <summary>
    /// Copies a dropped plugin, mod or datapack into the server. If <paramref name="replaces"/> is given, that copy is first moved
    /// to backups\&lt;plugins|datapacks|mods&gt;\&lt;time&gt;\. Returns where it was installed.
    /// </summary>
    public static string Install(Addon dropped, Addon? replaces)
    {
        var dir = LiveDir(dropped.Kind);
        Directory.CreateDirectory(dir);
        if (replaces != null)
        {
            var backup = Path.Combine(Paths.BackupDir, SubDir(replaces.Kind), DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
            Directory.CreateDirectory(backup);
            var to = Path.Combine(backup, replaces.FileName);
            if (Directory.Exists(replaces.FullPath)) Directory.Move(replaces.FullPath, to);
            else File.Move(replaces.FullPath, to, overwrite: true);
        }
        var target = Path.Combine(dir, dropped.FileName);
        if (Directory.Exists(dropped.FullPath)) CopyFolder(dropped.FullPath, target);
        else File.Copy(dropped.FullPath, target, overwrite: true);
        return target;
    }

    static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), overwrite: true);
        foreach (var d in Directory.GetDirectories(from)) CopyFolder(d, Path.Combine(to, Path.GetFileName(d)));
    }

    /// <summary>
    /// "Deletes" a plugin, datapack or mod by moving it to backups\deleted\&lt;time&gt;\, so it can be put back.
    /// Its settings folder is left alone. The server must be stopped. Returns where the file went.
    /// </summary>
    public static string Delete(Addon a)
    {
        var dir = Path.Combine(Paths.BackupDir, "deleted", DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"), SubDir(a.Kind));
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, a.FileName);
        if (Directory.Exists(a.FullPath)) Directory.Move(a.FullPath, target);
        else File.Move(a.FullPath, target, overwrite: true);
        return target;
    }

    /// <summary>Moves the file between the live folder and the disabled folder. The server must be stopped.</summary>
    public static void SetEnabled(Addon a, bool on)
    {
        if (a.Enabled == on) return;
        var targetDir = on ? LiveDir(a.Kind) : Path.Combine(DisabledDir, SubDir(a.Kind));
        var target = Path.Combine(targetDir, a.FileName);
        if (File.Exists(target) || Directory.Exists(target))
            throw new InvalidOperationException($"There is already a {a.FileName} in {Path.GetRelativePath(Paths.ServerDir, targetDir)}.");
        Directory.CreateDirectory(targetDir);
        if (Directory.Exists(a.FullPath)) Directory.Move(a.FullPath, target);
        else File.Move(a.FullPath, target);
        a.FullPath = target;
        a.Enabled = on;
    }
}
