using System.Diagnostics;
using System.Reflection;

namespace MinecraftHost.Core;

/// <summary>
/// Updates the app from its GitHub releases: the newest release's .exe is downloaded (checked against the SHA-256
/// GitHub lists for it), then swapped in for the running exe. Windows lets a running exe be renamed but not
/// overwritten, so the old one becomes "&lt;name&gt;.old.exe" and is deleted the next time the app starts.
/// </summary>
public static class Updater
{
    public const string Repo = "Zapiltar-E7/minecraft-self-hoster";
    public const string RepoUrl = "https://github.com/" + Repo;

    /// <summary>This build's version (the csproj's Version).</summary>
    public static Version Current { get; } = Normalize(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0));

    /// <summary>The day this build was made (the csproj's BuildDate metadata).</summary>
    public static string? BuildDate { get; } = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "BuildDate")?.Value;

    public static string CurrentText => Current.ToString(3);

    /// <summary>A newer version already downloaded and swapped in, waiting for the app to restart.</summary>
    public static Version? Staged { get; private set; }

    static string ExePath => Environment.ProcessPath!;
    static string OldPath => Path.Combine(Path.GetDirectoryName(ExePath)!, Path.GetFileNameWithoutExtension(ExePath) + ".old.exe");

    static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    /// <summary>At startup: removes the exe an earlier update replaced.</summary>
    public static void CleanUp()
    {
        try { if (File.Exists(OldPath)) File.Delete(OldPath); }
        catch { }   // another copy of the app is still running from it
    }

    public sealed record Release(Version Version, string Tag, string ExeUrl, string? Sha256);

    /// <summary>The newest release on GitHub with an .exe attached, or null if there's none.</summary>
    public static async Task<Release?> LatestAsync(CancellationToken ct = default)
    {
        var json = await Net.GetJsonAsync($"https://api.github.com/repos/{Repo}/releases/latest");
        var tag = json["tag_name"]?.GetValue<string>();
        if (tag == null || !Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;
        var asset = json["assets"]?.AsArray().FirstOrDefault(a => a?["name"]?.GetValue<string>().EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true);
        var url = asset?["browser_download_url"]?.GetValue<string>();
        if (url == null) return null;
        var digest = asset!["digest"]?.GetValue<string>();   // "sha256:<hex>"
        var sha = digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true ? digest[7..] : null;
        return new Release(Normalize(version), tag, url, sha);
    }

    /// <summary>
    /// Checks GitHub and, when there's a newer build, downloads it and swaps it in. Returns the version swapped in
    /// (it runs from the next start), or null when this one is the newest.
    /// </summary>
    public static async Task<Version?> UpdateAsync(CancellationToken ct = default)
    {
        if (Staged != null) return Staged;
        var latest = await LatestAsync(ct);
        if (latest == null || latest.Version <= Current) return null;
        if (latest.Sha256 == null) throw new InvalidOperationException($"Release {latest.Tag} has no checksum to verify the download with.");

        var download = ExePath + ".new";
        await Net.DownloadAsync(latest.ExeUrl, download, "SHA256", latest.Sha256, ct, quiet: true);
        if (File.Exists(OldPath)) File.Delete(OldPath);
        File.Move(ExePath, OldPath);
        try { File.Move(download, ExePath); }
        catch { File.Move(OldPath, ExePath); throw; }   // put the running one back
        Staged = latest.Version;
        return Staged;
    }

    /// <summary>Starts the (updated) exe; the caller closes this one.</summary>
    public static void StartNew() => Process.Start(new ProcessStartInfo(ExePath) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(ExePath)! });
}
