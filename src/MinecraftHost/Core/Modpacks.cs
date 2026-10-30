using System.IO.Compression;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MinecraftHost.Core;

/// <summary>One file a modpack downloads: from Modrinth (links and hashes) or CurseForge (project and file ids).</summary>
public sealed record ModpackFile(string Path, List<string> Urls, string? Sha1, string? Sha512, long Size, int ProjectId = 0, int FileId = 0);

/// <summary>A modpack read from a Modrinth .mrpack or a CurseForge zip / manifest.json.</summary>
public sealed class Modpack
{
    public required string PackPath { get; init; }
    public required string Source { get; init; }            // "Modrinth" or "CurseForge"
    public required string Name { get; init; }
    public string? PackVersion { get; init; }
    public string? Author { get; init; }
    public required string Minecraft { get; init; }
    /// <summary>The server type that runs it: Fabric, Quilt, Forge, NeoForge, or Vanilla when it names no loader.</summary>
    public required string Edition { get; init; }
    public string? LoaderVersion { get; init; }
    public List<ModpackFile> Files { get; } = new();
    /// <summary>Folders in the zip copied over the server folder, in order (later ones win).</summary>
    public List<string> OverrideFolders { get; } = new();
    /// <summary>Files the pack itself marks as client-only (Modrinth) or optional, left out.</summary>
    public int LeftOut { get; set; }

    public string Title => PackVersion is { Length: > 0 } v && !Name.Contains(v) ? $"{Name} {v}" : Name;
    public string LoaderText => Edition == "Vanilla" ? "no mod loader" : LoaderVersion == null ? Edition : $"{Edition} {LoaderVersion}";
}

/// <summary>What installing a modpack's files did, for the summary.</summary>
public sealed class ModpackResult
{
    public int Installed;
    public List<string> SwitchedOff { get; } = new();             // client-only mods, moved to disabled\mods
    public List<string> Skipped { get; } = new();                 // resource packs and shaders (only players use them)
    public List<string> Problems { get; } = new();
    public List<(string Name, string Page)> ManualDownloads { get; } = new();   // CurseForge authors who block other apps
}

public static class Modpacks
{
    public const string FileFilter = "Modpacks (*.mrpack, CurseForge .zip or manifest.json)|*.mrpack;*.zip;*.json";

    // the Modrinth pack format only allows downloads from these hosts
    static readonly string[] ModrinthHosts = { "cdn.modrinth.com", "github.com", "raw.githubusercontent.com", "gitlab.com" };
    // folders only the game itself (players) uses; a server never reads them
    static readonly string[] ClientFolders = { "resourcepacks/", "shaderpacks/", "screenshots/", "saves/" };

    /// <summary>Whether a dropped file looks like a modpack (without reading all of it).</summary>
    public static bool LooksLikeModpack(string path)
    {
        if (path.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase)) return true;
        if (Path.GetFileName(path).Equals("manifest.json", StringComparison.OrdinalIgnoreCase)) return true;
        if (!path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return false;
        try
        {
            using var zip = ZipFile.OpenRead(path);
            return zip.GetEntry("manifest.json") != null && zip.GetEntry("pack.mcmeta") == null;
        }
        catch { return false; }
    }

    public static Modpack Read(string path)
    {
        if (Path.GetFileName(path).Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
            return ReadCurseForge(path, JsonNode.Parse(File.ReadAllText(path))!, zip: false);
        using var archive = ZipFile.OpenRead(path);
        if (archive.GetEntry("modrinth.index.json") is { } index) return ReadModrinth(path, JsonOf(index));
        if (archive.GetEntry("manifest.json") is { } manifest) return ReadCurseForge(path, JsonOf(manifest), zip: true);
        throw new InvalidOperationException("This isn't a modpack: it has no modrinth.index.json (Modrinth) or manifest.json (CurseForge).");

        static JsonNode JsonOf(ZipArchiveEntry e)
        {
            using var r = new StreamReader(e.Open());
            return JsonNode.Parse(r.ReadToEnd())!;
        }
    }

    static Modpack ReadModrinth(string path, JsonNode index)
    {
        if (index["game"]?.GetValue<string>() is { } game && game != "minecraft")
            throw new InvalidOperationException($"This modpack is for {game}, not Minecraft.");
        var deps = index["dependencies"]?.AsObject() ?? throw new InvalidOperationException("The modpack doesn't say which Minecraft version it's for.");
        string? Dep(string key) => deps[key]?.GetValue<string>();
        var (edition, loader) = Dep("neoforge") is { } n ? ("NeoForge", n)
            : Dep("forge") is { } f ? ("Forge", f)
            : Dep("quilt-loader") is { } q ? ("Quilt", q)
            : Dep("fabric-loader") is { } fa ? ("Fabric", fa)
            : ("Vanilla", (string?)null);
        var pack = new Modpack
        {
            PackPath = path, Source = "Modrinth",
            Name = index["name"]?.GetValue<string>() ?? Path.GetFileNameWithoutExtension(path),
            PackVersion = index["versionId"]?.GetValue<string>(),
            Minecraft = Dep("minecraft") ?? throw new InvalidOperationException("The modpack doesn't say which Minecraft version it's for."),
            Edition = edition, LoaderVersion = loader,
        };
        foreach (var file in index["files"]?.AsArray().OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
        {
            var rel = file["path"]?.GetValue<string>()?.Replace('\\', '/');
            if (rel == null) continue;
            if (file["env"]?["server"]?.GetValue<string>() == "unsupported" || ClientFolders.Any(c => rel.StartsWith(c, StringComparison.OrdinalIgnoreCase)))
            {
                pack.LeftOut++;
                continue;
            }
            var urls = file["downloads"]?.AsArray().Select(u => u?.GetValue<string>()).OfType<string>()
                .Where(u => Uri.TryCreate(u, UriKind.Absolute, out var uri) && uri.Scheme == "https" && ModrinthHosts.Contains(uri.Host)).ToList() ?? new();
            pack.Files.Add(new ModpackFile(rel, urls, file["hashes"]?["sha1"]?.GetValue<string>(), file["hashes"]?["sha512"]?.GetValue<string>(),
                file["fileSize"]?.GetValue<long>() ?? 0));
        }
        pack.OverrideFolders.AddRange(new[] { "overrides/", "server-overrides/" });
        return pack;
    }

    static Modpack ReadCurseForge(string path, JsonNode manifest, bool zip)
    {
        if (manifest["manifestType"]?.GetValue<string>() is { } type && type != "minecraftModpack")
            throw new InvalidOperationException("This manifest.json isn't a CurseForge Minecraft modpack.");
        var mc = manifest["minecraft"]?["version"]?.GetValue<string>() ?? throw new InvalidOperationException("The modpack doesn't say which Minecraft version it's for.");
        var loaders = manifest["minecraft"]?["modLoaders"]?.AsArray().OfType<JsonObject>().ToList() ?? new();
        var id = (loaders.FirstOrDefault(l => l["primary"]?.GetValue<bool>() == true) ?? loaders.FirstOrDefault())?["id"]?.GetValue<string>();
        // ids look like "forge-47.2.0", "neoforge-21.1.77", "fabric-0.16.9", "quilt-0.27.1"
        var (edition, loader) = id == null ? ("Vanilla", (string?)null) : SplitLoaderId(id);
        if (loader != null && loader.StartsWith(mc + "-")) loader = loader[(mc.Length + 1)..];   // "neoforge-1.20.1-47.1.106"
        var pack = new Modpack
        {
            PackPath = path, Source = "CurseForge",
            Name = manifest["name"]?.GetValue<string>() ?? Path.GetFileNameWithoutExtension(path),
            PackVersion = manifest["version"]?.GetValue<string>(),
            Author = manifest["author"]?.GetValue<string>(),
            Minecraft = mc, Edition = edition, LoaderVersion = loader,
        };
        foreach (var file in manifest["files"]?.AsArray().OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
        {
            if (file["required"]?.GetValue<bool>() == false) { pack.LeftOut++; continue; }   // optional files are off unless picked
            var project = file["projectID"]?.GetValue<int>() ?? 0;
            var fileId = file["fileID"]?.GetValue<int>() ?? 0;
            if (project > 0 && fileId > 0) pack.Files.Add(new ModpackFile("", new(), null, null, 0, project, fileId));
        }
        if (zip) pack.OverrideFolders.Add((manifest["overrides"]?.GetValue<string>() ?? "overrides").TrimEnd('/') + "/");
        return pack;

        static (string, string?) SplitLoaderId(string id)
        {
            var dash = id.IndexOf('-');
            var name = (dash < 0 ? id : id[..dash]).ToLowerInvariant();
            var version = dash < 0 ? null : id[(dash + 1)..];
            return name switch
            {
                "neoforge" => ("NeoForge", version),
                "forge" => ("Forge", version),
                "fabric" => ("Fabric", version),
                "quilt" => ("Quilt", version),
                _ => throw new InvalidOperationException($"The modpack needs the mod loader \"{id}\", which this app can't install."),
            };
        }
    }

    /// <summary>A path inside the server folder; anything that would land outside it (.., absolute paths) is refused.</summary>
    static string? Inside(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':')) return null;
        var full = Path.GetFullPath(Path.Combine(root, relative));
        var prefix = Path.GetFullPath(root).TrimEnd('\\') + "\\";
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    /// <summary>
    /// Downloads the pack's files into the server folder, copies its override files over it, then switches off mods that
    /// only work in the game itself (they would crash or do nothing on a server).
    /// </summary>
    public static async Task<ModpackResult> InstallFilesAsync(Modpack pack, string serverDir, string? curseForgeKey,
        IProgress<TransferProgress> progress, CancellationToken ct)
    {
        var result = new ModpackResult();
        var files = pack.Files;
        Dictionary<int, CurseForgeInfo>? cf = null;
        if (pack.Source == "CurseForge" && files.Count > 0)
        {
            progress.Report(new TransferProgress("Looking up the modpack's files on CurseForge...", 0.15, $"{files.Count} files"));
            cf = curseForgeKey is { Length: > 0 } ? await LookUpCurseForgeAsync(files, curseForgeKey, ct) : null;
        }

        var staging = Path.Combine(serverDir, ".modpack-download");
        Directory.CreateDirectory(staging);
        long totalBytes = files.Sum(f => f.Size), doneBytes = 0;
        var done = 0;
        var gate = new SemaphoreSlim(6);
        var lockObj = new object();
        void Report(string name)
        {
            var fraction = totalBytes > 0 ? (double)doneBytes / totalBytes : (double)done / Math.Max(1, files.Count);
            progress.Report(new TransferProgress($"Downloading mods ({done} of {files.Count})...", 0.15 + 0.7 * fraction,
                totalBytes > 0 ? $"{doneBytes / 1048576.0:N0} MB of {totalBytes / 1048576.0:N0} MB  ·  {name}" : name));
        }

        try
        {
            await Task.WhenAll(files.Select(async file =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    var name = await DownloadOneAsync(file, pack, serverDir, staging, cf, result, lockObj, ct);
                    lock (lockObj) { done++; doneBytes += file.Size; }
                    Report(name);
                }
                finally { gate.Release(); }
            }));
            ct.ThrowIfCancellationRequested();

            progress.Report(new TransferProgress("Copying the modpack's settings...", 0.87, ""));
            await Task.Run(() => CopyOverrides(pack, serverDir, result), ct);

            progress.Report(new TransferProgress("Checking for mods only players need...", 0.93, ""));
            await SwitchOffClientModsAsync(serverDir, result, ct);
        }
        finally { try { Directory.Delete(staging, recursive: true); } catch { } }
        return result;
    }

    sealed record CurseForgeInfo(string FileName, string? DownloadUrl, string? Sha1, long Size, int ClassId, string? Page, string ModName);

    /// <summary>With an API key: file names, links, hashes and what kind of project each file is.</summary>
    static async Task<Dictionary<int, CurseForgeInfo>?> LookUpCurseForgeAsync(List<ModpackFile> files, string key, CancellationToken ct)
    {
        try
        {
            var filesReply = await CurseForgePostAsync("https://api.curseforge.com/v1/mods/files", new { fileIds = files.Select(f => f.FileId).ToArray() }, key, ct);
            var modsReply = await CurseForgePostAsync("https://api.curseforge.com/v1/mods", new { modIds = files.Select(f => f.ProjectId).Distinct().ToArray() }, key, ct);
            var mods = (modsReply?["data"] as JsonArray)?.OfType<JsonObject>().ToDictionary(m => m["id"]!.GetValue<int>()) ?? new();
            var map = new Dictionary<int, CurseForgeInfo>();
            foreach (var f in (filesReply?["data"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
            {
                var modId = f["modId"]!.GetValue<int>();
                mods.TryGetValue(modId, out var mod);
                var sha1 = (f["hashes"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(h => h["algo"]?.GetValue<int>() == 1)?["value"]?.GetValue<string>();
                map[f["id"]!.GetValue<int>()] = new CurseForgeInfo(f["fileName"]!.GetValue<string>(), f["downloadUrl"]?.GetValue<string>(), sha1,
                    f["fileLength"]?.GetValue<long>() ?? 0, mod?["classId"]?.GetValue<int>() ?? 6, mod?["links"]?["websiteUrl"]?.GetValue<string>(),
                    mod?["name"]?.GetValue<string>() ?? f["displayName"]?.GetValue<string>() ?? f["fileName"]!.GetValue<string>());
            }
            return map;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Host($"CurseForge lookup didn't work ({ex.Message}); downloading through curseforge.com instead.");
            return null;
        }
    }

    static async Task<JsonNode?> CurseForgePostAsync(string url, object body, string key, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("x-api-key", key);
        using var resp = await Net.Http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
    }

    /// <summary>Downloads one file into place (Modrinth) or into the staging folder and then into mods\ (CurseForge). Returns its name.</summary>
    static async Task<string> DownloadOneAsync(ModpackFile file, Modpack pack, string serverDir, string staging,
        Dictionary<int, CurseForgeInfo>? cf, ModpackResult result, object lockObj, CancellationToken ct)
    {
        void Problem(string text) { lock (lockObj) result.Problems.Add(text); }

        if (pack.Source == "Modrinth")
        {
            var name = Path.GetFileName(file.Path);
            var target = Inside(serverDir, file.Path);
            if (target == null) { Problem($"{file.Path}: refused, it points outside the server folder."); return name; }
            if (file.Urls.Count == 0) { Problem($"{name}: no download link from an allowed site."); return name; }
            if (file.Sha1 == null && file.Sha512 == null) { Problem($"{name}: the pack gives no checksum for it, so it wasn't installed."); return name; }
            Exception? last = null;
            foreach (var link in file.Urls)
                try
                {
                    if (file.Sha512 != null) await Net.DownloadAsync(link, target, "SHA512", file.Sha512, ct, quiet: true);
                    else await Net.DownloadAsync(link, target, "SHA1", file.Sha1, ct, quiet: true);
                    lock (lockObj) result.Installed++;
                    return name;
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { last = ex; }
            Problem($"{name}: {last?.Message}");
            return name;
        }

        // CurseForge
        CurseForgeInfo? info = null;
        cf?.TryGetValue(file.FileId, out info);
        var label = info?.ModName ?? $"project {file.ProjectId}";
        if (info is { ClassId: not 6 })   // 6 = mods; resource packs (12), shaders (6552) and the like are for players
        {
            lock (lockObj) result.Skipped.Add(info.ModName);
            return label;
        }
        if (info != null && info.DownloadUrl == null)
        {
            lock (lockObj) result.ManualDownloads.Add((info.ModName, info.Page ?? $"https://www.curseforge.com/projects/{file.ProjectId}"));
            return label;
        }
        var url = info?.DownloadUrl ?? $"https://www.curseforge.com/api/v1/mods/{file.ProjectId}/files/{file.FileId}/download";
        var temp = Path.Combine(staging, $"{file.ProjectId}-{file.FileId}");
        string fileName;
        try
        {
            using (var resp = await Net.Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"{(int)resp.StatusCode} {resp.ReasonPhrase}");
                fileName = info?.FileName ?? Uri.UnescapeDataString(resp.RequestMessage?.RequestUri?.Segments.LastOrDefault() ?? $"{file.FileId}.jar");
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(temp);
                await src.CopyToAsync(dst, ct);
            }
            if (info?.Sha1 != null && !Net.HashFile(temp, "SHA1").Equals(info.Sha1, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("checksum mismatch, the download was corrupted");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (info == null)   // without a key there's no page link from the API; the project id still finds it
                lock (lockObj) result.ManualDownloads.Add(($"CurseForge project {file.ProjectId} (file {file.FileId})", $"https://www.curseforge.com/projects/{file.ProjectId}"));
            else Problem($"{label}: {ex.Message}");
            return label;
        }

        fileName = Path.GetFileName(fileName);
        if (!fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
        {
            // without a key the project kind is unknown: only jars are mods, zips are resource packs or shaders
            lock (lockObj) result.Skipped.Add(fileName);
            return fileName;
        }
        var mods = Path.Combine(serverDir, "mods");
        Directory.CreateDirectory(mods);
        File.Move(temp, Path.Combine(mods, fileName), overwrite: true);
        lock (lockObj) result.Installed++;
        return fileName;
    }

    static void CopyOverrides(Modpack pack, string serverDir, ModpackResult result)
    {
        if (pack.OverrideFolders.Count == 0 || pack.PackPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return;
        using var zip = ZipFile.OpenRead(pack.PackPath);
        var copied = 0;
        foreach (var folder in pack.OverrideFolders)
            foreach (var entry in zip.Entries.Where(e => e.FullName.Replace('\\', '/').StartsWith(folder, StringComparison.OrdinalIgnoreCase)))
            {
                var rel = entry.FullName.Replace('\\', '/')[folder.Length..];
                if (rel.Length == 0 || rel.EndsWith('/') || ClientFolders.Any(c => rel.StartsWith(c, StringComparison.OrdinalIgnoreCase))) continue;
                var target = Inside(serverDir, rel);
                if (target == null) { result.Problems.Add($"{entry.FullName}: refused, it points outside the server folder."); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
                copied++;
            }
        Log.Host($"Copied {copied} settings and other files from the modpack.");
    }

    /// <summary>
    /// Mods marked client-only in their own files (fabric.mod.json "environment": "client", Forge's clientSideOnly), and those
    /// Modrinth lists as not for servers, move to disabled\mods. They show switched off on the Mods tab.
    /// </summary>
    static async Task SwitchOffClientModsAsync(string serverDir, ModpackResult result, CancellationToken ct)
    {
        var mods = Path.Combine(serverDir, "mods");
        if (!Directory.Exists(mods)) return;
        var clientOnly = new List<string>();
        var jars = Directory.GetFiles(mods, "*.jar");
        foreach (var jar in jars)
            if (IsClientOnlyJar(jar)) clientOnly.Add(jar);

        // Modrinth knows which mods are client-only even when a pack marks them wrongly (or, on CurseForge, not at all)
        try
        {
            var byHash = jars.Except(clientOnly).GroupBy(j => Net.HashFile(j, "SHA1")).ToDictionary(g => g.Key, g => g.First());
            if (byHash.Count > 0 && await Net.PostJsonAsync("https://api.modrinth.com/v2/version_files", new { hashes = byHash.Keys.ToArray(), algorithm = "sha1" }) is JsonObject versions)
            {
                var projectOf = versions.Where(v => v.Value?["project_id"] != null).ToDictionary(v => v.Key, v => v.Value!["project_id"]!.GetValue<string>());
                var ids = System.Text.Json.JsonSerializer.Serialize(projectOf.Values.Distinct());
                var projects = (await Net.GetJsonAsync("https://api.modrinth.com/v2/projects?ids=" + Uri.EscapeDataString(ids))).AsArray().OfType<JsonObject>();
                var serverless = projects.Where(p => p["server_side"]?.GetValue<string>() == "unsupported").Select(p => p["id"]!.GetValue<string>()).ToHashSet();
                clientOnly.AddRange(projectOf.Where(p => serverless.Contains(p.Value)).Select(p => byHash[p.Key]));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Log.Host("Couldn't check Modrinth for client-only mods: " + ex.Message); }

        if (clientOnly.Count == 0) return;
        var off = Path.Combine(serverDir, "disabled", "mods");
        Directory.CreateDirectory(off);
        foreach (var jar in clientOnly.Distinct())
        {
            File.Move(jar, Path.Combine(off, Path.GetFileName(jar)), overwrite: true);
            result.SwitchedOff.Add(Path.GetFileNameWithoutExtension(jar));
        }
    }

    /// <summary>
    /// The mods a Forge or NeoForge crash report (written since <paramref name="since"/>) blames for a failed start: its
    /// "-- MOD id --" sections name the jar in mods\. Often these are mods only made for players.
    /// </summary>
    public static List<(string Name, string Jar)> FindCrashingMods(string serverDir, DateTime since)
    {
        var found = new List<(string, string)>();
        var reports = Path.Combine(serverDir, "crash-reports");
        if (!Directory.Exists(reports)) return found;
        var report = new DirectoryInfo(reports).GetFiles("*.txt").Where(f => f.LastWriteTime >= since.AddSeconds(-5))
            .OrderByDescending(f => f.LastWriteTime).FirstOrDefault();
        if (report == null) return found;
        var text = File.ReadAllText(report.FullName);
        var mods = Path.GetFullPath(Path.Combine(serverDir, "mods")).TrimEnd('\\') + "\\";
        foreach (Match section in Regex.Matches(text, @"(?ms)^-- MOD (\S+) --\s*$(.*?)(?=^-- |\z)"))
        {
            var file = Regex.Match(section.Groups[2].Value, @"Mod File: /?(.+?\.jar)\s*$", RegexOptions.Multiline);
            if (!file.Success) continue;
            string jar;
            try { jar = Path.GetFullPath(Uri.UnescapeDataString(file.Groups[1].Value)); } catch { continue; }
            if (!jar.StartsWith(mods, StringComparison.OrdinalIgnoreCase) || !File.Exists(jar)) continue;
            var name = Regex.Match(section.Groups[2].Value, @"Failure message: (.+?) \(").Groups[1].Value;
            found.Add((name.Length > 0 ? name : section.Groups[1].Value, jar));
        }
        return found.DistinctBy(f => f.Item2).ToList();
    }

    static bool IsClientOnlyJar(string jar)
    {
        try
        {
            using var zip = ZipFile.OpenRead(jar);
            string? Read(string name)
            {
                if (zip.GetEntry(name) is not { } e) return null;
                using var r = new StreamReader(e.Open());
                return r.ReadToEnd();
            }
            if (Read("fabric.mod.json") is { } fabric)
                return JsonNode.Parse(fabric, documentOptions: new() { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true })
                    ?["environment"]?.GetValue<string>() == "client";
            if (Read("quilt.mod.json") is { } quilt)
                return JsonNode.Parse(quilt, documentOptions: new() { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true })
                    ?["minecraft"]?["environment"]?.GetValue<string>() == "client";
            if ((Read("META-INF/neoforge.mods.toml") ?? Read("META-INF/mods.toml")) is { } toml)
                return Regex.IsMatch(toml, @"(?m)^\s*clientSideOnly\s*=\s*true");
        }
        catch { }
        return false;
    }
}
