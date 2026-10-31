using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MinecraftHost.Core;

/// <summary>Folder layout. App files live next to the exe; the server folder can be anywhere.</summary>
public static class Paths
{
    public static readonly string Root = AppContext.BaseDirectory.TrimEnd('\\', '/');
    public static readonly string DefaultServerDir = Path.Combine(Root, "server");
    public static string ServerDir { get; private set; } = DefaultServerDir;
    public static string ServerJarName { get; private set; } = "server.jar";
    public static string ToolsDir => Path.Combine(Root, "tools");
    public static string BackupsRoot => Path.Combine(Root, "backups");
    /// <summary>Each server keeps its backups in backups\&lt;its name&gt;.</summary>
    public static string BackupDir => Path.Combine(BackupsRoot, SafeName(ServerName));
    /// <summary>The current server's name, else the name of its folder.</summary>
    public static string ServerName { get; private set; } = "server";
    /// <summary>Folders for new servers: servers\&lt;name&gt;.</summary>
    public static string ServersDir => Path.Combine(Root, "servers");
    public static string ConfigPath => Path.Combine(Root, "host-config.json");
    public static string ServerJar => Path.Combine(ServerDir, ServerJarName);

    public static void Apply(HostConfig? config)
    {
        ServerDir = string.IsNullOrWhiteSpace(config?.ServerDir) ? DefaultServerDir : config!.ServerDir!;
        ServerJarName = string.IsNullOrWhiteSpace(config?.ServerJar) ? "server.jar" : config!.ServerJar!;
        ServerName = !string.IsNullOrWhiteSpace(config?.Name) ? config!.Name!.Trim() : Path.GetFileName(ServerDir.TrimEnd('\\'));
    }

    /// <summary>A server name made usable as a file or folder name.</summary>
    public static string SafeName(string? name)
    {
        var safe = string.Join("_", (name ?? "").Split(Path.GetInvalidFileNameChars())).Trim().TrimEnd('.').Trim();
        return safe.Length > 0 ? safe : "server";
    }

    /// <summary>servers\&lt;name&gt;, or "&lt;name&gt; 2", "&lt;name&gt; 3"... if that's already taken by a folder with files in it.</summary>
    public static string NewServerDir(string? name)
    {
        var safe = SafeName(name);
        var dir = Path.Combine(ServersDir, safe);
        for (var n = 2; Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any(); n++)
            dir = Path.Combine(ServersDir, $"{safe} {n}");
        return dir;
    }
    public static string PlayitExe => Path.Combine(ToolsDir, "playit.exe");
    public static string CloudflaredExe => Path.Combine(ToolsDir, "cloudflared.exe");
    public static string MapLinkFile => Path.Combine(Root, "map-link.txt");
    public static string PlayitSecretDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "playit_gg");
    public static string PlayitSecret => Path.Combine(PlayitSecretDir, "playit.toml");
}

/// <summary>Thread-safe log that the window drains on a timer.</summary>
public static class Log
{
    public static readonly ConcurrentQueue<string> Lines = new();
    static readonly Regex Ansi = new(@"\x1B\[[0-9;]*[A-Za-z]", RegexOptions.Compiled);
    public static void Write(string text) => Lines.Enqueue(Ansi.Replace(text, ""));
    public static void Host(string text) => Write("[host] " + text);
}

/// <summary>
/// One server the app manages. The current one's values are also the top-level HostConfig fields (which the rest of the
/// app uses); HostConfig.Save keeps the two in step. Property names must match HostConfig's.
/// </summary>
public sealed class ServerProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string? Edition { get; set; }
    public string? Version { get; set; }
    public int RamGB { get; set; } = 4;
    public int RequiredJava { get; set; } = 21;
    public string? PlayitAddress { get; set; } = "";
    public bool ShareMap { get; set; }
    /// <summary>Put BlueMap's link in the description of EssentialsX Discord's channel when it changes.</summary>
    public bool DiscordMapTopic { get; set; }
    public bool UseTunnel { get; set; } = true;
    /// <summary>Friends join through a port forwarded on the router instead of a playit tunnel.</summary>
    public bool PortForward { get; set; }
    /// <summary>With port forwarding: the address friends use when it isn't the network's public IP (a domain name).</summary>
    public string? ForwardAddress { get; set; }
    public string? ServerDir { get; set; }
    public string? ServerJar { get; set; }
    public string? Name { get; set; }
    public string? Modpack { get; set; }
    public string? JavaArgs { get; set; }
    public string? ServerArgs { get; set; }
    public bool RestartEnabled { get; set; }
    public int RestartEveryHours { get; set; } = 24;
    public string RestartAt { get; set; } = "04:00";
    public bool RestartWarn { get; set; } = true;
    /// <summary>Start the server again when it crashes (at most 3 times in 10 minutes).</summary>
    public bool CrashRestart { get; set; } = true;
    /// <summary>Automatic world backups: on/off, every N hours while it runs, also at scheduled restarts, and how many to keep.</summary>
    public bool BackupAuto { get; set; }
    public int BackupEveryHours { get; set; } = 6;
    public bool BackupOnRestart { get; set; } = true;
    public int BackupKeep { get; set; } = 7;
    public List<string> SharedWeb { get; set; } = new();
    public Dictionary<string, string> ServiceAddresses { get; set; } = new();
    /// <summary>How each plugin connection is reached, by service id: "playit", "forward" or "cloudflare" (web pages). Unset follows the Minecraft choice.</summary>
    public Dictionary<string, string> ServiceMethods { get; set; } = new();
    /// <summary>Messages posted in chat on a schedule (Server → Announcements).</summary>
    public List<Announcement> Announcements { get; set; } = new();
    public List<string> SeenServices { get; set; } = new();

    /// <summary>The folder it runs from (null ServerDir is the "server" folder next to the app).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Folder => string.IsNullOrWhiteSpace(ServerDir) ? Paths.DefaultServerDir : ServerDir!;
}

public sealed class HostConfig
{
    public string? Edition { get; set; }
    public string? Version { get; set; }
    public int RamGB { get; set; } = 4;
    public int RequiredJava { get; set; } = 21;
    public string? PlayitAddress { get; set; } = "";
    public bool ShareMap { get; set; }
    /// <summary>Put BlueMap's link in the description of EssentialsX Discord's channel when it changes.</summary>
    public bool DiscordMapTopic { get; set; }
    public bool UseTunnel { get; set; } = true;
    /// <summary>Friends join through a port forwarded on the router instead of a playit tunnel.</summary>
    public bool PortForward { get; set; }
    /// <summary>With port forwarding: the address friends use when it isn't the network's public IP (a domain name).</summary>
    public string? ForwardAddress { get; set; }
    public bool ShowConsole { get; set; } = true;
    /// <summary>Server folder; null means the "server" folder next to the exe.</summary>
    public string? ServerDir { get; set; }
    /// <summary>Jar file inside the server folder; null means server.jar.</summary>
    public string? ServerJar { get; set; }
    /// <summary>Name shown in the header and title bar; null means "Minecraft Self-Hoster".</summary>
    public string? Name { get; set; }
    /// <summary>The modpack this server was set up from ("name version"), or null.</summary>
    public string? Modpack { get; set; }
    /// <summary>Personal CurseForge API key (console.curseforge.com); only needed to look up and update CurseForge files.</summary>
    public string? CurseForgeKey { get; set; }
    /// <summary>Extra Java arguments, placed after the app's -Xms/-Xmx so they can override them.</summary>
    public string? JavaArgs { get; set; }
    /// <summary>Extra server arguments, placed after "nogui".</summary>
    public string? ServerArgs { get; set; }
    /// <summary>Scheduled restarts (Server tab): on/off, every N hours (24 = once a day) counted from RestartAt, and chat warnings first.</summary>
    public bool RestartEnabled { get; set; }
    public int RestartEveryHours { get; set; } = 24;
    public string RestartAt { get; set; } = "04:00";
    public bool RestartWarn { get; set; } = true;
    /// <summary>Start the server again when it crashes (at most 3 times in 10 minutes).</summary>
    public bool CrashRestart { get; set; } = true;
    /// <summary>Automatic world backups: on/off, every N hours while it runs, also at scheduled restarts, and how many to keep.</summary>
    public bool BackupAuto { get; set; }
    public int BackupEveryHours { get; set; } = 6;
    public bool BackupOnRestart { get; set; } = true;
    public int BackupKeep { get; set; } = 7;
    /// <summary>Start the server as soon as the app opens.</summary>
    public bool AutoStartServer { get; set; }
    /// <summary>Jingle when the server is online or an export/import is done; error tone when something fails.</summary>
    public bool PlaySounds { get; set; } = true;
    /// <summary>Web pages besides BlueMap (ShareMap) to share with a Cloudflare link, by service id (see Services).</summary>
    public List<string> SharedWeb { get; set; } = new();
    /// <summary>Colour theme (Themes.All: the menu colours), its highlight colour (Themes.Highlights; null: the theme's own),
    /// and whether the app animates (Settings → Appearance).</summary>
    public string Theme { get; set; } = "Default";
    public string? Highlight { get; set; }
    public bool Animations { get; set; } = true;
    /// <summary>Minimizing hides the window to an icon in the system tray (next to the clock).</summary>
    public bool TrayOnMinimize { get; set; } = true;
    /// <summary>A tray notification when a player joins or leaves while the window is hidden.</summary>
    public bool NotifyPlayers { get; set; } = true;
    /// <summary>The Monday of the week whose summary was last written to the log (yyyy-MM-dd).</summary>
    public string? LastWeekSummary { get; set; }
    /// <summary>Connections tab cards the user folded up, by id ("minecraft" or a service id).</summary>
    public List<string> CollapsedCards { get; set; } = new();
    /// <summary>Public (playit) addresses entered for services with their own port, by service id.</summary>
    public Dictionary<string, string> ServiceAddresses { get; set; } = new();
    /// <summary>How each plugin connection is reached, by service id: "playit", "forward" or "cloudflare" (web pages). Unset follows the Minecraft choice.</summary>
    public Dictionary<string, string> ServiceMethods { get; set; } = new();
    /// <summary>Messages posted in chat on a schedule (Server → Announcements).</summary>
    public List<Announcement> Announcements { get; set; } = new();
    /// <summary>Service ids already shown on the Connections tab (newer ones put a dot on the tab).</summary>
    public List<string> SeenServices { get; set; } = new();
    /// <summary>Folder the last export was saved to; the next export starts there.</summary>
    public string? LastExportDir { get; set; }
    /// <summary>Every server the app manages (the server menu next to the name). The current one is <see cref="CurrentServerId"/>.</summary>
    public List<ServerProfile> Servers { get; set; } = new();
    public string? CurrentServerId { get; set; }

    // the per-server values: every ServerProfile property except Id, which HostConfig has under the same name
    static readonly (System.Reflection.PropertyInfo Profile, System.Reflection.PropertyInfo Config)[] PerServer =
        typeof(ServerProfile).GetProperties()
            .Where(p => p.Name != nameof(ServerProfile.Id) && p.CanWrite)
            .Select(p => (p, typeof(HostConfig).GetProperty(p.Name)!))
            .ToArray();

    static object? Copy(object? value) => value switch
    {
        List<string> l => new List<string>(l),
        Dictionary<string, string> d => new Dictionary<string, string>(d),
        List<Announcement> a => a.Select(x => x with { }).ToList(),
        _ => value,
    };

    /// <summary>Puts the current server's values into a profile.</summary>
    public void CopyTo(ServerProfile profile)
    {
        foreach (var (p, c) in PerServer) p.SetValue(profile, Copy(c.GetValue(this)));
    }

    /// <summary>Makes a profile's values the current ones.</summary>
    public void CopyFrom(ServerProfile profile)
    {
        foreach (var (p, c) in PerServer) c.SetValue(this, Copy(p.GetValue(profile)));
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public ServerProfile? Current => Servers.FirstOrDefault(s => s.Id == CurrentServerId);

    /// <summary>Saves, then makes another server the current one.</summary>
    public void SwitchTo(string id)
    {
        var target = Servers.First(s => s.Id == id);
        Save();                 // the current server's latest values go into its profile first
        CopyFrom(target);
        CurrentServerId = id;
        Save();
    }

    /// <summary>The single-server config from before profiles existed becomes the first profile.</summary>
    bool EnsureProfiles()
    {
        if (Servers.Count > 0 && Current != null) return false;
        if (Servers.Count == 0)
        {
            var first = new ServerProfile();
            CopyTo(first);
            Servers.Add(first);
        }
        CurrentServerId = Servers[0].Id;
        CopyFrom(Servers[0]);
        return true;
    }

    static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    /// <summary>Loads the config and points <see cref="Paths"/> at its server folder.</summary>
    public static HostConfig? Load()
    {
        HostConfig? config = null;
        if (File.Exists(Paths.ConfigPath))
            try { config = JsonSerializer.Deserialize<HostConfig>(File.ReadAllText(Paths.ConfigPath), Options); }
            catch { }
        if (config != null && config.EnsureProfiles()) config.Save();
        Paths.Apply(config);
        return config;
    }

    public static HostConfig LoadOrNew() => Load() ?? new HostConfig();

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsSetUp => Edition != null && File.Exists(Paths.ServerJar);

    public void Save()
    {
        EnsureProfiles();
        CopyTo(Current!);   // keep the current server's profile in step with the values the app changed
        File.WriteAllText(Paths.ConfigPath, JsonSerializer.Serialize(this, Options));
        Paths.Apply(this);
    }
}

/// <summary>What was found in a folder picked as the server folder.</summary>
public sealed record ServerFolderInfo(string? JarName, string? Edition, string? Version, int? JavaMajor);

public static class Net
{
    public static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("minecraft-self-hoster/1.0");
        return http;
    }

    /// <summary>This PC's address on the home network, which a router's port forward points to; null if there's none.</summary>
    public static string? LocalIPv4()
    {
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up
                    || nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                var props = nic.GetIPProperties();
                if (!props.GatewayAddresses.Any(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)) continue;
                var ip = props.UnicastAddresses.FirstOrDefault(u => u.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                if (ip != null) return ip.Address.ToString();
            }
        }
        catch { }
        return null;
    }

    /// <summary>The home network's public IP, once found (<see cref="FindPublicIPv4Async"/>).</summary>
    public static string? PublicIPv4 { get; private set; }

    /// <summary>Asks api.ipify.org for the home network's public IP (what friends connect to with port forwarding).</summary>
    public static async Task<string?> FindPublicIPv4Async()
    {
        if (PublicIPv4 != null) return PublicIPv4;
        try
        {
            var text = (await Http.GetStringAsync("https://api.ipify.org")).Trim();
            if (System.Net.IPAddress.TryParse(text, out _)) PublicIPv4 = text;
        }
        catch { }   // offline: try again next time
        return PublicIPv4;
    }

    public static async Task<JsonNode> GetJsonAsync(string url)
    {
        var text = await Http.GetStringAsync(url);
        return JsonNode.Parse(text)!;
    }

    /// <summary>POST JSON and parse the reply even when the status code is an error.</summary>
    public static async Task<JsonNode?> PostJsonAsync(string url, object body)
    {
        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var resp = await Http.PostAsync(url, content);
        var text = await resp.Content.ReadAsStringAsync();
        try { return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text); }
        catch { return null; }
    }

    /// <summary>Download to a temp file, check the hash (if given), then move into place.</summary>
    public static async Task DownloadAsync(string url, string destination, string? algorithm = null, string? expectedHex = null,
        CancellationToken ct = default, bool quiet = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var part = destination + ".part";
        using (var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(part);
            await src.CopyToAsync(dst, ct);
        }
        if (algorithm != null && !string.IsNullOrEmpty(expectedHex))
        {
            var actual = HashFile(part, algorithm);
            if (!actual.Equals(expectedHex, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(part);
                throw new InvalidOperationException($"Checksum mismatch for {Path.GetFileName(destination)} - the download was corrupted, please try again.");
            }
            if (!quiet) Log.Host($"{algorithm} checksum verified for {Path.GetFileName(destination)}.");
        }
        File.Move(part, destination, overwrite: true);
    }

    public static string HashFile(string path, string algorithm)
    {
        using var stream = File.OpenRead(path);
        byte[] hash = algorithm.ToUpperInvariant() switch
        {
            "SHA1" => SHA1.HashData(stream),
            "SHA256" => SHA256.HashData(stream),
            "SHA512" => SHA512.HashData(stream),
            _ => throw new ArgumentException(algorithm),
        };
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

public static class ServerFiles
{
    public static int GetServerPort()
    {
        var props = Path.Combine(Paths.ServerDir, "server.properties");
        if (File.Exists(props))
            foreach (var line in File.ReadLines(props))
            {
                var m = Regex.Match(line, @"^server-port=(\d+)");
                if (m.Success) return int.Parse(m.Groups[1].Value);
            }
        return 25565;
    }

    public static int GetBlueMapPort()
    {
        var conf = Path.Combine(Paths.ServerDir, "plugins", "BlueMap", "webserver.conf");
        if (File.Exists(conf))
            foreach (var line in File.ReadLines(conf))
            {
                var m = Regex.Match(line, @"^\s*port:\s*(\d+)");
                if (m.Success) return int.Parse(m.Groups[1].Value);
            }
        return 8100;
    }

    public static Task<JsonNode> GetMojangManifestAsync() =>
        Net.GetJsonAsync("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json");

    public static async Task<JsonNode> GetMojangVersionAsync(string version)
    {
        var manifest = await GetMojangManifestAsync();
        var entry = manifest["versions"]!.AsArray().FirstOrDefault(v => v!["id"]!.GetValue<string>() == version)
            ?? throw new InvalidOperationException($"Minecraft version '{version}' does not exist.");
        return await Net.GetJsonAsync(entry["url"]!.GetValue<string>());
    }

    public static async Task<int> GetRequiredJavaAsync(string version)
    {
        try
        {
            var info = await GetMojangVersionAsync(version);
            var major = info["javaVersion"]?["majorVersion"];
            if (major != null) return major.GetValue<int>();
        }
        catch { }
        return 21;
    }

    /// <summary>Installs the server (see <see cref="Editions"/>) and writes eula.txt. The caller must have the user's EULA acceptance.</summary>
    /// <summary>Installs the server software. <paramref name="loader"/> picks a mod loader build (a modpack's); null means the newest.</summary>
    public static async Task<HostConfig> InstallAsync(string edition, string version, string? java, string? loader = null)
    {
        var target = await Editions.InstallAsync(edition, version, java, loader);
        AcceptEula();

        var config = HostConfig.LoadOrNew();
        if (config.Edition != edition || config.Version != version) config.Modpack = null;   // no longer the modpack's setup
        config.Edition = edition;
        config.Version = version;
        config.ServerJar = target;
        config.RequiredJava = await GetRequiredJavaAsync(version);
        config.Save();
        Log.Host($"Installed {edition} {version}.");
        return config;
    }

    /// <summary>Finds the server jar in a folder and reads its type and version.</summary>
    public static ServerFolderInfo InspectFolder(string dir)
    {
        var jars = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.jar").Select(Path.GetFileName).ToList() : new List<string?>();
        string? jar = jars.FirstOrDefault(j => Regex.IsMatch(j!, @"^forge-.+-shim\.jar$", RegexOptions.IgnoreCase))
            ?? jars.FirstOrDefault(j => j!.Equals("quilt-server-launch.jar", StringComparison.OrdinalIgnoreCase))
            ?? jars.FirstOrDefault(j => j!.Equals("fabric-server-launch.jar", StringComparison.OrdinalIgnoreCase))
            ?? jars.FirstOrDefault(j => Regex.IsMatch(j!, "paper|purpur|folia|spigot|fabric|forge|minecraft_server|server", RegexOptions.IgnoreCase))
            ?? (jars.Count == 1 ? jars[0] : null);
        // NeoForge (and Forge 1.17-1.20.2) start from an argument file in libraries\ instead of a jar
        if (Editions.FindArgsFile(dir) is { } args && (jar == null || !Regex.IsMatch(jar, "shim|quilt", RegexOptions.IgnoreCase)))
            return new ServerFolderInfo(args.Path, args.Edition, args.Version, null);
        if (jar == null) return new ServerFolderInfo(null, null, null, null);

        string edition = "Custom"; string? version = null; int? java = null;
        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(Path.Combine(dir, jar));
            var manifest = zip.GetEntry("META-INF/MANIFEST.MF");
            if (manifest != null)
            {
                using var reader = new StreamReader(manifest.Open());
                var text = reader.ReadToEnd();
                if (text.Contains("io.papermc.paperclip")) edition = jar.Contains("purpur", StringComparison.OrdinalIgnoreCase) ? "Purpur" : "Paper";
                else if (text.Contains("net.minecraft.bundler") || text.Contains("net.minecraft.server")) edition = "Vanilla";
                else if (text.Contains("quiltmc")) edition = "Quilt";
                else if (text.Contains("fabricmc")) edition = "Fabric";
                else if (text.Contains("minecraftforge")) edition = "Forge";
            }
            var versionJson = zip.GetEntry("version.json");
            if (versionJson != null)
            {
                using var reader = new StreamReader(versionJson.Open());
                var node = JsonNode.Parse(reader.ReadToEnd());
                version = node?["id"]?.GetValue<string>();
                java = node?["java_version"]?.GetValue<int>();
            }
        }
        catch { }
        if (version == null && edition is ("Quilt" or "Fabric") && !jar.Equals("server.jar", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(Path.Combine(dir, "server.jar")))
            version = InspectFolderJar(Path.Combine(dir, "server.jar"));   // the launcher sits next to the Minecraft server jar
        if (version == null && edition == "Forge")
        {
            var m = Regex.Match(jar, @"^forge-([\d.]+)-");
            if (m.Success) version = m.Groups[1].Value;
        }
        return new ServerFolderInfo(jar, edition, version, java);
    }

    static string? InspectFolderJar(string jar)
    {
        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(jar);
            if (zip.GetEntry("version.json") is not { } entry) return null;
            using var reader = new StreamReader(entry.Open());
            return JsonNode.Parse(reader.ReadToEnd())?["id"]?.GetValue<string>();
        }
        catch { return null; }
    }

    public static bool EulaAccepted() =>
        File.Exists(Path.Combine(Paths.ServerDir, "eula.txt")) &&
        File.ReadAllText(Path.Combine(Paths.ServerDir, "eula.txt")).Contains("eula=true", StringComparison.OrdinalIgnoreCase);

    public static void AcceptEula() =>
        File.WriteAllText(Path.Combine(Paths.ServerDir, "eula.txt"), $"# Accepted via Minecraft Self-Hoster on {DateTime.Now:s}\neula=true\n");

    /// <summary>Zips every world folder (any folder with a level.dat) into backups\.</summary>
    public static string? BackupWorlds() => BackupWorlds(auto: false);

    /// <summary>Automatic backups are named "... worlds auto (time).zip"; only those are ever cleaned up.</summary>
    public const string AutoTag = " worlds auto ";

    public static string? BackupWorlds(bool auto)
    {
        var worlds = Directory.Exists(Paths.ServerDir)
            ? Directory.GetDirectories(Paths.ServerDir).Where(d => File.Exists(Path.Combine(d, "level.dat"))).ToList()
            : new List<string>();
        if (worlds.Count == 0) { Log.Host("No worlds to back up yet - start the server once first."); return null; }

        Directory.CreateDirectory(Paths.BackupDir);
        var zipPath = Path.Combine(Paths.BackupDir, $"{Paths.SafeName(Paths.ServerName)}{(auto ? AutoTag : " worlds ")}{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.zip");
        Log.Host($"Backing up {string.Join(", ", worlds.Select(Path.GetFileName))} to {zipPath} ...");
        int count = 0;
        using (var zip = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
        {
            foreach (var world in worlds)
                foreach (var file in Directory.EnumerateFiles(world, "*", SearchOption.AllDirectories))
                {
                    if (Path.GetFileName(file) == "session.lock") continue;
                    var entry = Path.GetRelativePath(Paths.ServerDir, file).Replace('\\', '/');
                    // the running server may hold a file open: read it shared rather than fail the whole backup
                    try
                    {
                        using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        var zipEntry = zip.CreateEntry(entry, System.IO.Compression.CompressionLevel.Fastest);
                        zipEntry.LastWriteTime = File.GetLastWriteTime(file);
                        using var target = zipEntry.Open();
                        source.CopyTo(target);
                    }
                    catch (IOException ex) { Log.Host($"  skipped {entry}: {ex.Message}"); }
                    if (++count % 2000 == 0) Log.Host($"  ... {count} files");
                }
        }
        Log.Host($"Backup saved: {zipPath} ({count} files, {new FileInfo(zipPath).Length / 1048576.0:N0} MB)");
        return zipPath;
    }

    /// <summary>This server's automatic backups, newest first.</summary>
    public static List<FileInfo> AutoBackups() =>
        Directory.Exists(Paths.BackupDir)
            ? new DirectoryInfo(Paths.BackupDir).GetFiles("*.zip").Where(f => f.Name.Contains(AutoTag)).OrderByDescending(f => f.LastWriteTimeUtc).ToList()
            : new List<FileInfo>();

    /// <summary>Deletes the oldest automatic backups beyond <paramref name="keep"/>. Backups made with the button are never touched.</summary>
    public static void PruneAutoBackups(int keep)
    {
        foreach (var old in AutoBackups().Skip(Math.Max(1, keep)))
        {
            try { old.Delete(); Log.Host($"Removed an old automatic backup: {old.Name}"); }
            catch (Exception ex) { Log.Host($"Couldn't remove {old.Name}: {ex.Message}"); }
        }
    }
}

public static class SystemInfo
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    /// <summary>Installed RAM in whole GB (8 if Windows won't say).</summary>
    public static int TotalMemoryGB
    {
        get
        {
            var status = new MemoryStatusEx { Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MemoryStatusEx>() };
            return GlobalMemoryStatusEx(ref status) ? (int)Math.Round(status.TotalPhys / (1024.0 * 1024 * 1024)) : 8;
        }
    }
}

public static class Java
{
    public static int GetMajor(string exe)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, "-version")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true, RedirectStandardOutput = true,
            };
            using var p = Process.Start(psi)!;
            var text = p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd();
            p.WaitForExit(10000);
            var m = Regex.Match(text, "version \"(\\d+)(?:\\.(\\d+))?");
            if (!m.Success) return 0;
            var major = int.Parse(m.Groups[1].Value);
            if (major == 1 && m.Groups[2].Success) major = int.Parse(m.Groups[2].Value); // "1.8.0_x" style
            return major;
        }
        catch { return 0; }
    }

    /// <summary>Portable Java that the app downloads itself (no admin rights or installer needed).</summary>
    public static string PortableDir => Path.Combine(Paths.ToolsDir, "java");

    /// <summary>
    /// A Java of at least <paramref name="minMajor"/>. The exact version wins when this PC has it: that's the one Minecraft
    /// ships with, and many mods refuse to start on a newer one.
    /// </summary>
    public static string? Find(int minMajor)
    {
        var candidates = new List<string>();
        if (Directory.Exists(PortableDir))
            foreach (var sub in Directory.GetDirectories(PortableDir))
                candidates.Add(Path.Combine(sub, "bin", "java.exe"));
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrEmpty(javaHome)) candidates.Add(Path.Combine(javaHome, "bin", "java.exe"));
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            candidates.Add(Path.Combine(dir.Trim(), "java.exe"));

        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] searchDirs =
        {
            Path.Combine(pf, "Eclipse Adoptium"), Path.Combine(pf, "Java"), Path.Combine(pf, "Microsoft"),
            Path.Combine(pf, "Zulu"), Path.Combine(pf, "BellSoft"), Path.Combine(pf, "Amazon Corretto"),
            Path.Combine(local, "Programs", "Eclipse Adoptium"),
        };
        foreach (var dir in searchDirs.Where(Directory.Exists))
            foreach (var sub in Directory.GetDirectories(dir))
                candidates.Add(Path.Combine(sub, "bin", "java.exe"));

        string? best = null; int bestMajor = 0;
        foreach (var exe in candidates.Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists))
        {
            var major = GetMajor(exe);
            if (major == minMajor) return exe;
            if (major > minMajor && major > bestMajor) { best = exe; bestMajor = major; }
        }
        return best;
    }

    /// <summary>
    /// Downloads Eclipse Temurin (Adoptium) as a zip and unpacks it into tools\java.
    /// Uses the JRE when one is published for that version, otherwise the JDK.
    /// </summary>
    public static async Task<string> InstallPortableAsync(int major)
    {
        JsonNode? package = null;
        foreach (var image in new[] { "jre", "jdk" })
        {
            var assets = await Net.GetJsonAsync(
                $"https://api.adoptium.net/v3/assets/latest/{major}/hotspot?os=windows&architecture=x64&image_type={image}&vendor=eclipse");
            package = assets.AsArray().FirstOrDefault()?["binary"]?["package"];
            if (package != null) break;
        }
        if (package == null) throw new InvalidOperationException($"No Java {major} download is available for Windows x64.");

        var name = package["name"]!.GetValue<string>();
        var sizeMb = (package["size"]?.GetValue<long>() ?? 0) / 1048576;
        Log.Host($"Downloading Java {major} ({name}, {sizeMb} MB)...");
        var zip = Path.Combine(Paths.ToolsDir, name);
        await Net.DownloadAsync(package["link"]!.GetValue<string>(), zip, "SHA256", package["checksum"]?.GetValue<string>());

        Log.Host("Unpacking Java...");
        Directory.CreateDirectory(PortableDir);
        await Task.Run(() => System.IO.Compression.ZipFile.ExtractToDirectory(zip, PortableDir, overwriteFiles: true));
        File.Delete(zip);

        var java = Find(major) ?? throw new InvalidOperationException("Java was unpacked but could not be started.");
        Log.Host($"Java {GetMajor(java)} ready: {java}");
        return java;
    }
}
