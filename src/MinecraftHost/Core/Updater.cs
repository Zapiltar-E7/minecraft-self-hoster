using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace MinecraftHost.Core;

/// <summary>
/// Updates from the app's GitHub releases, only when the user asks: <see cref="CheckAsync"/> finds a newer release,
/// <see cref="InstallAsync"/> downloads it, checks its signature and swaps it in.
/// A release is the exe plus "&lt;exe&gt;.sig", an ECDSA P-256 signature (made with src\ReleaseSigner) over the app's
/// name, the version and the exe's SHA-256. Only the holder of the private key can make one that matches
/// <see cref="PublicKey"/>, so a release someone else uploaded is refused. Windows lets a running exe be renamed but
/// not overwritten, so the old one becomes "&lt;name&gt;.old.exe" and is deleted the next time the app starts.
/// </summary>
public static class Updater
{
    public const string Repo = "Zapiltar-E7/minecraft-self-hoster";
    public const string RepoUrl = "https://github.com/" + Repo;

    /// <summary>The release signing key's public half (SubjectPublicKeyInfo, base64). The private half never leaves the maintainer's PC.</summary>
    const string PublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEC2nWDMKvczYZvjNNovHznakc78B6QAxt+srUtM8zLafwhSqeJ2+9E05BNYNrvdydOvlrQPpRYlfvPPdg3YyETA==";

    /// <summary>This build's version (the csproj's Version).</summary>
    public static Version Current { get; } = Normalize(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0));

    /// <summary>The day this build was made (the csproj's BuildDate metadata).</summary>
    public static string? BuildDate { get; } = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "BuildDate")?.Value;

    public static string CurrentText => Text(Current);

    /// <summary>"1.0.6", or "1.0.6.1" when there's a fourth number: also the version text a release's signature covers.</summary>
    public static string Text(Version v) => v.Revision > 0 ? v.ToString(4) : v.ToString(3);

    /// <summary>A newer version already installed, waiting for the app to restart.</summary>
    public static Version? Installed { get; private set; }

    static string ExePath => Environment.ProcessPath!;
    static string OldPath => Path.Combine(Path.GetDirectoryName(ExePath)!, Path.GetFileNameWithoutExtension(ExePath) + ".old.exe");

    static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

    /// <summary>At startup: removes the exes earlier updates replaced ("&lt;name&gt;.old.exe", or "&lt;name&gt;.old-&lt;time&gt;.exe").</summary>
    public static void CleanUp()
    {
        var dir = Path.GetDirectoryName(ExePath)!;
        foreach (var old in Directory.GetFiles(dir, Path.GetFileNameWithoutExtension(ExePath) + ".old*.exe"))
        {
            try { File.Delete(old); }
            catch { }   // another copy of the app is still running from it: next time
        }
    }

    /// <summary>
    /// Where the running exe goes aside: "&lt;name&gt;.old.exe", or a dated name when that one can't be removed
    /// (another copy of the app, perhaps hidden in the tray, still runs from it).
    /// </summary>
    static string FreeOldPath()
    {
        try
        {
            if (File.Exists(OldPath)) File.Delete(OldPath);
            return OldPath;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return Path.Combine(Path.GetDirectoryName(ExePath)!, $"{Path.GetFileNameWithoutExtension(ExePath)}.old-{DateTime.Now:yyyyMMdd-HHmmss}.exe");
        }
    }

    public sealed record Release(Version Version, string Tag, string ExeUrl, string SigUrl, string PageUrl);

    /// <summary>The newest release, if it's newer than this build and signed; otherwise null.</summary>
    public static async Task<Release?> CheckAsync()
    {
        var json = await Net.GetJsonAsync($"https://api.github.com/repos/{Repo}/releases/latest");
        var tag = json["tag_name"]?.GetValue<string>();
        if (tag == null || !Version.TryParse(tag.TrimStart('v', 'V'), out var version) || Normalize(version) <= Current) return null;
        var assets = json["assets"]?.AsArray().Select(a => (Name: a?["name"]?.GetValue<string>() ?? "", Url: a?["browser_download_url"]?.GetValue<string>() ?? "")).ToList() ?? new();
        var exe = assets.FirstOrDefault(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        var sig = assets.FirstOrDefault(a => a.Name.Equals(exe.Name + ".sig", StringComparison.OrdinalIgnoreCase));
        if (exe.Url.Length == 0 || sig.Url.Length == 0) return null;   // unsigned releases are never offered
        return new Release(Normalize(version), tag, exe.Url, sig.Url, json["html_url"]?.GetValue<string>() ?? RepoUrl + "/releases");
    }

    /// <summary>
    /// Downloads a release, checks its signature, and swaps it in for the running exe (it runs from the next start).
    /// Throws, leaving the app as it was, if the signature doesn't match.
    /// </summary>
    public static async Task InstallAsync(Release release, CancellationToken ct = default)
    {
        var download = ExePath + ".new";
        try
        {
            await Net.DownloadAsync(release.ExeUrl, download, ct: ct, quiet: true);
            var signature = Convert.FromBase64String((await Net.Http.GetStringAsync(release.SigUrl, ct)).Trim());
            if (!Verify(download, release.Version, signature))
                throw new InvalidOperationException($"The download of version {Text(release.Version)} isn't signed by this app's developer, so it wasn't installed.");
            var aside = FreeOldPath();
            File.Move(ExePath, aside);
            try { File.Move(download, ExePath); }
            catch { File.Move(aside, ExePath); throw; }   // put the running one back
            Installed = release.Version;
        }
        finally { try { if (File.Exists(download)) File.Delete(download); } catch { } }
    }

    static bool Verify(string exe, Version version, byte[] signature)
    {
        using var stream = File.OpenRead(exe);
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        var message = Encoding.UTF8.GetBytes($"minecraft-self-hoster\n{Text(version)}\n{hash}");
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(PublicKey), out _);
        return key.VerifyData(message, signature, HashAlgorithmName.SHA256);
    }

    /// <summary>Starts the (updated) exe; the caller closes this one.</summary>
    public static void StartNew() => Process.Start(new ProcessStartInfo(ExePath) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(ExePath)! });
}
