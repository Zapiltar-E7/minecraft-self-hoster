using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace MinecraftHost.Core;

/// <summary>playit.gg tunnel for the Minecraft port (players join through it).</summary>
public static class Playit
{
    const string DownloadUrl = "https://github.com/playit-cloud/playit-agent/releases/latest/download/playit-windows-x86_64-signed.exe";

    public static bool HasSecret => File.Exists(Paths.PlayitSecret);

    public static async Task EnsureInstalledAsync()
    {
        if (File.Exists(Paths.PlayitExe)) return;
        Log.Host("Downloading the playit.gg tunnel agent...");
        await Net.DownloadAsync(DownloadUrl, Paths.PlayitExe);
        try
        {
#pragma warning disable SYSLIB0057
            var cert = X509Certificate.CreateFromSignedFile(Paths.PlayitExe);
#pragma warning restore SYSLIB0057
            Log.Host($"playit agent signed by: {cert.Subject}");
        }
        catch { Log.Host("Warning: the playit agent download is not signed."); }
    }

    public static void StopAll()
    {
        foreach (var p in Process.GetProcessesByName("playit"))
            try { p.Kill(); p.WaitForExit(3000); } catch { }
    }

    /// <summary>
    /// The playit 1.x exe is only a background service that waits for a separate app to give it
    /// a secret key. This does that app's job: claim the agent in the browser and save the key.
    /// </summary>
    public static async Task ClaimAsync(CancellationToken cancel = default)
    {
        await EnsureInstalledAsync();
        StopAll();

        var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(5)).ToLowerInvariant();
        var url = $"https://playit.gg/claim/{code}";
        Log.Host("Linking this PC to your playit.gg account.");
        Log.Host($"Open this link (it should open in your browser): {url}");
        Log.Host("Sign in or create a free account, then approve the agent. Waiting...");

        var opened = false;
        var deadline = DateTime.Now.AddMinutes(15);
        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            if (DateTime.Now > deadline) throw new TimeoutException("Timed out waiting for the claim to be approved on playit.gg.");
            var res = await Net.PostJsonAsync("https://api.playit.gg/claim/setup",
                new { code, agent_type = "assignable", version = "playit 1.0.10" });
            var status = res?["status"]?.GetValue<string>();
            var data = res?["data"]?.ToString();
            if (status == "success" && data == "UserAccepted") break;
            if (status == "success" && data == "UserRejected") throw new InvalidOperationException("The agent was rejected on playit.gg.");
            if (status != "success") Log.Host($"playit.gg: {data}");
            if (!opened) { OpenUrl(url); opened = true; }
            await Task.Delay(2000, cancel);
        }

        string? secret = null;
        for (var i = 0; i < 30 && secret == null; i++)
        {
            var res = await Net.PostJsonAsync("https://api.playit.gg/claim/exchange", new { code });
            if (res?["status"]?.GetValue<string>() == "success") secret = res["data"]?["secret_key"]?.GetValue<string>();
            else await Task.Delay(2000, cancel);
        }
        if (secret == null) throw new InvalidOperationException("playit.gg approved the agent but did not return a key. Try again.");

        Directory.CreateDirectory(Paths.PlayitSecretDir);
        if (File.Exists(Paths.PlayitSecret))
            File.Move(Paths.PlayitSecret, Paths.PlayitSecret + $".bak-{DateTime.Now:yyyyMMdd-HHmmss}");
        File.WriteAllText(Paths.PlayitSecret, $"secret_key = \"{secret}\"\n");
        Log.Host("This PC is now linked to your playit.gg account.");
    }

    /// <summary>Starts the agent hidden. Returns null if one was already running (reused, not ours to stop).</summary>
    public static Process? Start()
    {
        if (Process.GetProcessesByName("playit").Length > 0)
        {
            Log.Host("playit agent already running - reusing it.");
            return null;
        }
        var p = Process.Start(new ProcessStartInfo(Paths.PlayitExe)
        {
            WorkingDirectory = Paths.ToolsDir, UseShellExecute = false, CreateNoWindow = true,
        });
        Log.Host("playit tunnel started.");
        return p;
    }

    /// <summary>
    /// Opens a web page in the browser. Only http and https addresses are opened: links come from plugin files and
    /// online listings, and the shell would happily run a program or open a file given anything else.
    /// </summary>
    public static void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
        {
            Log.Host($"Didn't open \"{url}\": it isn't a web address.");
            return;
        }
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }
}

/// <summary>Cloudflare quick tunnels for web pages like the BlueMap map (no account needed). One process per page.</summary>
public static class Cloudflared
{
    static readonly Regex UrlPattern = new(@"https://[a-z0-9-]+\.trycloudflare\.com", RegexOptions.Compiled);

    public static async Task EnsureInstalledAsync()
    {
        if (File.Exists(Paths.CloudflaredExe)) return;
        Log.Host("Downloading Cloudflare Tunnel (cloudflared)...");
        var release = await Net.GetJsonAsync("https://api.github.com/repos/cloudflare/cloudflared/releases/latest");
        var asset = release["assets"]!.AsArray().FirstOrDefault(a => a!["name"]!.GetValue<string>() == "cloudflared-windows-amd64.exe")
            ?? throw new InvalidOperationException("Could not find the Windows cloudflared download.");
        // the release notes list the SHA-256 of every file
        var m = Regex.Match(release["body"]?.GetValue<string>() ?? "", @"cloudflared-windows-amd64\.exe:\s*([0-9a-fA-F]{64})");
        await Net.DownloadAsync(asset["browser_download_url"]!.GetValue<string>(), Paths.CloudflaredExe,
            m.Success ? "SHA256" : null, m.Success ? m.Groups[1].Value : null);
    }

    /// <summary>Stops a tunnel left over from a crashed session (only our copy of cloudflared).</summary>
    public static void StopStale()
    {
        foreach (var p in Process.GetProcessesByName("cloudflared"))
            try
            {
                if (string.Equals(ForceStop.ExePath(p.Id), Paths.CloudflaredExe, StringComparison.OrdinalIgnoreCase)) p.Kill();
            }
            catch { }
    }

    /// <summary>Opens a tunnel to a local web page. Call <see cref="StopStale"/> once before starting a batch.</summary>
    public static async Task<(Process Process, string Url)?> StartAsync(int port, string what, bool isMap)
    {
        await EnsureInstalledAsync();
        Log.Host($"Sharing {what} (local port {port}) through Cloudflare...");

        var found = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var p = new Process
        {
            StartInfo = new ProcessStartInfo(Paths.CloudflaredExe)
            {
                ArgumentList = { "tunnel", "--no-autoupdate", "--url", $"http://127.0.0.1:{port}" },
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true, RedirectStandardOutput = true,
            },
        };
        DataReceivedEventHandler watch = (_, e) =>
        {
            if (e.Data == null) return;
            var m = UrlPattern.Match(e.Data);
            if (m.Success) found.TrySetResult(m.Value);
        };
        p.ErrorDataReceived += watch;
        p.OutputDataReceived += watch;
        p.Start(); p.BeginErrorReadLine(); p.BeginOutputReadLine();

        var winner = await Task.WhenAny(found.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        if (winner != found.Task)
        {
            try { p.Kill(); } catch { }
            Log.Host($"Could not share {what} (Cloudflare did not answer in 30 seconds).");
            return null;
        }
        var url = found.Task.Result;
        if (isMap) File.WriteAllText(Paths.MapLinkFile, url + Environment.NewLine);
        Log.Host($"{what} link: {url}  (changes each time sharing restarts)");
        return (p, url);
    }
}
