using System.Diagnostics;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MinecraftHost.Core;

/// <summary>The server types the app can install: their release versions and how to install each one.</summary>
public static class Editions
{
    public static readonly string[] All = { "Paper", "Fabric", "Quilt", "Forge", "NeoForge", "Vanilla" };

    public static bool RunsPlugins(string? edition) => edition is "Paper" or "Purpur";
    public static bool RunsMods(string? edition) => edition is "Fabric" or "Quilt" or "Forge" or "NeoForge";
    /// <summary>These install by running their own installer, which needs Java.</summary>
    public static bool InstallerNeedsJava(string? edition) => edition is "Forge" or "NeoForge" or "Quilt";

    /// <summary>Whether a server of this type loads a mod made for <paramref name="loader"/>.</summary>
    public static bool LoadsMod(string? edition, string loader, string? mc) => edition switch
    {
        "Fabric" => loader is "Fabric",
        "Quilt" => loader is "Fabric" or "Quilt",                         // Quilt runs Fabric mods too
        "Forge" => loader is "Forge",
        "NeoForge" => loader is "NeoForge" || (loader is "Forge" && mc == "1.20.1"),   // NeoForge 1.20.1 is a Forge fork
        _ => false,
    };

    const string PaperApi = "https://fill.papermc.io/v3/projects/paper";
    const string FabricMeta = "https://meta.fabricmc.net/v2/versions";
    const string QuiltMeta = "https://meta.quiltmc.org/v3/versions";
    const string ForgePromos = "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json";
    const string ForgeMaven = "https://maven.minecraftforge.net/net/minecraftforge/forge";
    const string NeoForgeMaven = "https://maven.neoforged.net/releases/net/neoforged";
    const string NeoForgeVersions = "https://maven.neoforged.net/api/maven/versions/releases/net/neoforged/neoforge";

    static readonly Dictionary<string, List<string>> VersionCache = new();
    static List<string>? _neoForgeBuilds;

    /// <summary>Release versions (no snapshots or release candidates), newest first.</summary>
    public static async Task<List<string>> GetVersionsAsync(string edition)
    {
        if (VersionCache.TryGetValue(edition, out var cached)) return cached;
        IEnumerable<string> versions = edition switch
        {
            "Paper" => (await Net.GetJsonAsync(PaperApi))["versions"]!.AsObject()
                .SelectMany(g => g.Value!.AsArray().Select(v => v!.GetValue<string>())),
            "Fabric" => (await Net.GetJsonAsync($"{FabricMeta}/game")).AsArray()
                .Where(v => v!["stable"]?.GetValue<bool>() == true).Select(v => v!["version"]!.GetValue<string>()),
            "Quilt" => (await Net.GetJsonAsync($"{QuiltMeta}/game")).AsArray()
                .Where(v => v!["stable"]?.GetValue<bool>() == true).Select(v => v!["version"]!.GetValue<string>()),
            "Forge" => (await Net.GetJsonAsync(ForgePromos))["promos"]!.AsObject()
                .Select(p => p.Key[..p.Key.LastIndexOf('-')]),
            "NeoForge" => (await GetNeoForgeBuildsAsync()).Select(NeoForgeMinecraft).OfType<string>(),
            _ => (await ServerFiles.GetMojangManifestAsync())["versions"]!.AsArray()
                .Where(v => v!["type"]?.GetValue<string>() == "release").Select(v => v!["id"]!.GetValue<string>()),
        };
        var list = versions.Where(v => !v.Contains('-')).Distinct().OrderByDescending(v => v, VersionComparer.Instance).ToList();
        VersionCache[edition] = list;
        return list;
    }

    static async Task<List<string>> GetNeoForgeBuildsAsync() =>
        _neoForgeBuilds ??= (await Net.GetJsonAsync(NeoForgeVersions))["versions"]!.AsArray().Select(v => v!.GetValue<string>()).ToList();

    /// <summary>
    /// The Minecraft version a NeoForge build is for: 21.1.77 is for 1.21.1 and 21.0.x for 1.21; since Minecraft's
    /// year numbering, 26.1.2.114 is for 26.1.2 and 26.3.0.58 for 26.3.
    /// </summary>
    public static string? NeoForgeMinecraft(string build)
    {
        var parts = build.Split('-')[0].Split('.');
        if (parts.Length < 3 || !int.TryParse(parts[0], out var major) || major == 0) return null;
        if (major < 25) return parts[1] == "0" ? $"1.{parts[0]}" : $"1.{parts[0]}.{parts[1]}";
        return parts.Length >= 4 && parts[2] != "0" ? $"{parts[0]}.{parts[1]}.{parts[2]}" : $"{parts[0]}.{parts[1]}";
    }

    /// <summary>
    /// Installs the server into the server folder and returns the file to launch, relative to that folder.
    /// Forge, NeoForge and Quilt run their installer, so they need <paramref name="java"/>. <paramref name="loader"/> picks the
    /// loader build (from a modpack); null means the recommended or newest one.
    /// </summary>
    public static async Task<string> InstallAsync(string edition, string version, string? java, string? loader = null)
    {
        var dir = Paths.ServerDir;
        Directory.CreateDirectory(dir);
        if (InstallerNeedsJava(edition) && java == null) throw new InvalidOperationException($"{edition} needs Java to install.");
        string target = edition switch
        {
            "Forge" => await InstallForgeAsync(version, java!, loader),
            "NeoForge" => await InstallNeoForgeAsync(version, java!, loader),
            "Quilt" => await InstallQuiltAsync(version, java!, loader),
            _ => "server.jar",
        };
        if (edition is "Paper" or "Fabric" or "Vanilla")
        {
            var jar = Path.Combine(dir, "server.jar");
            if (edition == "Paper") await InstallPaperAsync(version, jar);
            else if (edition == "Fabric") await InstallFabricAsync(version, jar, loader);
            else await InstallVanillaAsync(version, jar);
        }
        // launchers of other types left behind would be picked over server.jar when the folder is chosen again
        if (edition is not "Forge")
            foreach (var shim in Directory.GetFiles(dir, "forge-*-shim.jar")) File.Delete(shim);
        if (edition is not "Quilt" && File.Exists(Path.Combine(dir, "quilt-server-launch.jar"))) File.Delete(Path.Combine(dir, "quilt-server-launch.jar"));
        return target;
    }

    static async Task InstallVanillaAsync(string version, string jar)
    {
        var info = await ServerFiles.GetMojangVersionAsync(version);
        var server = info["downloads"]?["server"] ?? throw new InvalidOperationException($"Mojang does not publish a server jar for {version}.");
        Log.Host($"Downloading Vanilla {version} server...");
        await Net.DownloadAsync(server["url"]!.GetValue<string>(), jar, "SHA1", server["sha1"]?.GetValue<string>());
    }

    static async Task InstallPaperAsync(string version, string jar)
    {
        JsonNode build;
        try { build = await Net.GetJsonAsync($"{PaperApi}/versions/{version}/builds/latest"); }
        catch { throw new InvalidOperationException($"No Paper build found for {version}."); }
        var dl = build["downloads"]!["server:default"]!;
        var channel = build["channel"]?.GetValue<string>();
        Log.Host($"Downloading Paper {version} (build {build["id"]}, {channel})...");
        if (channel != "STABLE") Log.Host($"This Paper build is marked {channel}; back up your world regularly.");
        await Net.DownloadAsync(dl["url"]!.GetValue<string>(), jar, "SHA256", dl["checksums"]?["sha256"]?.GetValue<string>());
    }

    static async Task<string> NewestStable(string url) =>
        (await Net.GetJsonAsync(url)).AsArray().First(v => v!["stable"]?.GetValue<bool>() == true)!["version"]!.GetValue<string>();

    static async Task InstallFabricAsync(string version, string jar, string? loader)
    {
        loader ??= await NewestStable($"{FabricMeta}/loader");
        var installer = await NewestStable($"{FabricMeta}/installer");
        Log.Host($"Downloading the Fabric {version} server launcher (loader {loader})...");
        // Fabric publishes no checksum for the generated launcher; it comes over HTTPS from Fabric's own server
        try { await Net.DownloadAsync($"{FabricMeta}/loader/{version}/{loader}/{installer}/server/jar", jar); }
        catch (HttpRequestException) { throw new InvalidOperationException($"Fabric has no server for {version} with loader {loader}."); }
        Log.Host("Fabric downloads the rest of Minecraft the first time the server starts.");
    }

    static async Task<string> InstallQuiltAsync(string version, string java, string? loader)
    {
        // Quilt lists beta loaders as well; without a modpack's pick, take the newest without a "-beta" tag
        loader ??= (await Net.GetJsonAsync($"{QuiltMeta}/loader")).AsArray()
            .Select(v => v!["version"]!.GetValue<string>()).First(v => !v.Contains('-'));
        var newest = (await Net.GetJsonAsync($"{QuiltMeta}/installer")).AsArray().First()!;
        var dir = Paths.ServerDir;
        var installer = Path.Combine(dir, "quilt-installer.jar");
        Log.Host($"Downloading the Quilt installer {newest["version"]}...");
        // check against the maven's own .sha1: the hashes in Quilt's version list don't match the published jar
        var url = newest["url"]!.GetValue<string>();
        string? sha1 = null;
        try { sha1 = (await Net.Http.GetStringAsync(url + ".sha1")).Trim(); } catch { }
        await Net.DownloadAsync(url, installer, sha1 == null ? null : "SHA1", sha1);
        Log.Host($"Running the Quilt installer (Minecraft {version}, loader {loader})...");
        try { await RunInstallerAsync(java, "Quilt", "-jar", installer, "install", "server", version, loader, "--download-server", "--install-dir=" + dir); }
        finally { try { File.Delete(installer); } catch { } }
        if (!File.Exists(Path.Combine(dir, "quilt-server-launch.jar")))
            throw new InvalidOperationException("Quilt installed, but its launcher wasn't found in the server folder.");
        return "quilt-server-launch.jar";
    }

    static async Task<string> InstallForgeAsync(string version, string java, string? build)
    {
        if (build == null)
        {
            var promos = (await Net.GetJsonAsync(ForgePromos))["promos"]!;
            build = (promos[$"{version}-recommended"] ?? promos[$"{version}-latest"])?.GetValue<string>()
                ?? throw new InvalidOperationException($"Forge has no build for {version}.");
        }
        var full = $"{version}-{build}";
        var url = $"{ForgeMaven}/{full}/forge-{full}-installer.jar";
        await DownloadAndRunInstallerAsync(url, $"forge-{full}-installer.jar", java, "Forge " + full);

        // Forge 1.20.3+ makes a shim jar; 1.17-1.20.2 only an argument file; older versions a runnable jar
        var dir = Paths.ServerDir;
        foreach (var candidate in new[] { $"forge-{full}-shim.jar", $"libraries/net/minecraftforge/forge/{full}/win_args.txt", $"forge-{full}.jar", $"forge-{full}-universal.jar" })
            if (File.Exists(Path.Combine(dir, candidate))) return candidate;
        throw new InvalidOperationException("Forge installed, but its launcher wasn't found in the server folder.");
    }

    static async Task<string> InstallNeoForgeAsync(string version, string java, string? build)
    {
        if (build == null)
        {
            var builds = (await GetNeoForgeBuildsAsync()).Where(b => NeoForgeMinecraft(b) == version).ToList();
            build = builds.Where(b => !b.Contains('-')).OrderByDescending(b => b, VersionComparer.Instance).FirstOrDefault()
                ?? builds.OrderByDescending(b => b, VersionComparer.Instance).FirstOrDefault()
                ?? throw new InvalidOperationException($"NeoForge has no build for {version}.");
            if (build.Contains('-')) Log.Host($"NeoForge only has test builds ({build}) for {version} so far.");
        }
        // NeoForge for 1.20.1 was published under the old Forge name
        var (artifact, full) = version == "1.20.1" ? ("forge", $"1.20.1-{build.Replace("1.20.1-", "")}") : ("neoforge", build);
        var url = $"{NeoForgeMaven}/{artifact}/{full}/{artifact}-{full}-installer.jar";
        await DownloadAndRunInstallerAsync(url, $"neoforge-{full}-installer.jar", java, "NeoForge " + full);

        var args = $"libraries/net/neoforged/{artifact}/{full}/win_args.txt";
        if (File.Exists(Path.Combine(Paths.ServerDir, args))) return args;
        throw new InvalidOperationException("NeoForge installed, but its launcher wasn't found in the server folder.");
    }

    /// <summary>Forge and NeoForge: download the installer (checked against the maven's SHA-1) and run it with --installServer.</summary>
    static async Task DownloadAndRunInstallerAsync(string url, string fileName, string java, string what)
    {
        string? sha1 = null;
        try { sha1 = (await Net.Http.GetStringAsync(url + ".sha1")).Trim(); } catch { }
        var installer = Path.Combine(Paths.ServerDir, fileName);
        Log.Host($"Downloading the {what} installer...");
        try { await Net.DownloadAsync(url, installer, sha1 == null ? null : "SHA1", sha1); }
        catch (HttpRequestException) { throw new InvalidOperationException($"Couldn't download the {what} installer. Is that build real?"); }
        Log.Host($"Running the {what.Split(' ')[0]} installer (downloads Minecraft and its libraries; this takes a minute or two)...");
        try { await RunInstallerAsync(java, what.Split(' ')[0], "-jar", installer, "--installServer", "."); }
        finally
        {
            foreach (var leftover in new[] { installer, installer + ".log" })
                try { File.Delete(leftover); } catch { }
        }
    }

    static async Task RunInstallerAsync(string java, string what, params string[] args)
    {
        var psi = new ProcessStartInfo(java)
        {
            WorkingDirectory = Paths.ServerDir, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var p = Process.Start(psi)!;
        // the installers print one line per patched class or library; keep only the top-level steps
        void OnLine(object _, DataReceivedEventArgs e) { if (!string.IsNullOrWhiteSpace(e.Data) && !e.Data.StartsWith(' ')) Log.Write(e.Data); }
        p.OutputDataReceived += OnLine;
        p.ErrorDataReceived += OnLine;
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0) throw new InvalidOperationException($"The {what} installer failed (exit code {p.ExitCode}). See the log.");
    }

    /// <summary>A NeoForge or older Forge argument file in libraries\ (these servers have no jar to start).</summary>
    public static (string Path, string Edition, string? Version)? FindArgsFile(string dir)
    {
        foreach (var (folder, edition) in new[] { ("libraries/net/neoforged/neoforge", "NeoForge"), ("libraries/net/neoforged/forge", "NeoForge"), ("libraries/net/minecraftforge/forge", "Forge") })
        {
            var root = Path.Combine(dir, folder);
            if (!Directory.Exists(root)) continue;
            var newest = Directory.GetDirectories(root).Where(d => File.Exists(Path.Combine(d, "win_args.txt")))
                .Select(d => Path.GetFileName(d)!).OrderByDescending(n => n, VersionComparer.Instance).FirstOrDefault();
            if (newest == null) continue;
            var version = edition == "NeoForge" && !newest.StartsWith("1.") ? NeoForgeMinecraft(newest) : newest.Split('-')[0];
            return ($"{folder}/{newest}/win_args.txt", edition, version);
        }
        return null;
    }
}

/// <summary>Orders Minecraft versions numerically: 1.9 &lt; 1.21.4 &lt; 26.3.</summary>
public sealed class VersionComparer : IComparer<string>
{
    public static readonly VersionComparer Instance = new();

    public int Compare(string? a, string? b)
    {
        var x = Regex.Matches(a ?? "", @"\d+").Select(m => long.Parse(m.Value)).ToList();
        var y = Regex.Matches(b ?? "", @"\d+").Select(m => long.Parse(m.Value)).ToList();
        for (var i = 0; i < Math.Max(x.Count, y.Count); i++)
        {
            var c = (i < x.Count ? x[i] : 0).CompareTo(i < y.Count ? y[i] : 0);
            if (c != 0) return c;
        }
        return 0;
    }
}
