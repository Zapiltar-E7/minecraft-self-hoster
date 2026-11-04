using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace MinecraftHost.Core;

/// <summary>playit.gg tunnel for the Minecraft port (players join through it).</summary>
public static class Playit
{
    const string DownloadUrl = "https://github.com/playit-cloud/playit-agent/releases/latest/download/playit-windows-x86_64-signed.exe";

    /// <summary>Whether the current server's playit.gg account is linked.</summary>
    public static bool HasSecret => File.Exists(SecretFor(HostConfig.Load()?.PlayitAccount));

    /// <summary>
    /// More playit.gg accounts, so each server can have its own: one key file each, named after the account.
    /// This PC's own (first) account keeps its key where the agent looks by default (<see cref="Paths.PlayitSecret"/>).
    /// </summary>
    public static string AccountsDir => Path.Combine(Paths.PlayitSecretDir, "accounts");

    /// <summary>The key file of an account; null or empty is this PC's own.</summary>
    public static string SecretFor(string? account) =>
        string.IsNullOrWhiteSpace(account) ? Paths.PlayitSecret : Path.Combine(AccountsDir, Paths.SafeName(account) + ".toml");

    /// <summary>The extra accounts linked so far, by name.</summary>
    public static List<string> Accounts()
    {
        try
        {
            return Directory.GetFiles(AccountsDir, "*.toml").Select(f => Path.GetFileNameWithoutExtension(f))
                .Order(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch { return new(); }
    }

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
    /// <param name="account">The account to link (see <see cref="Accounts"/>); by default the current server's.</param>
    public static async Task ClaimAsync(CancellationToken cancel = default, string? account = null)
    {
        account ??= HostConfig.Load()?.PlayitAccount;
        var secretFile = SecretFor(account);
        await EnsureInstalledAsync();
        // an agent keeps the key it started with; a newly added account isn't in use by one yet
        if (string.IsNullOrWhiteSpace(account) || File.Exists(secretFile)) StopAll();

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

        Directory.CreateDirectory(Path.GetDirectoryName(secretFile)!);
        if (File.Exists(secretFile))
            File.Move(secretFile, secretFile + $".bak-{DateTime.Now:yyyyMMdd-HHmmss}");
        File.WriteAllText(secretFile, $"secret_key = \"{secret}\"\n");
        Log.Host(string.IsNullOrWhiteSpace(account)
            ? "This PC is now linked to your playit.gg account."
            : $"The playit.gg account \"{Paths.SafeName(account)}\" is now linked.");
    }

    /// <summary>
    /// Starts the agent hidden, on a playit.gg account (null: this PC's own). Our own agent left running is restarted,
    /// since it may be on another server's account. Returns null if someone else's agent is running (reused, not ours
    /// to stop).
    /// </summary>
    public static Process? Start(string? account = null)
    {
        foreach (var running in Process.GetProcessesByName("playit"))
            try
            {
                if (!string.Equals(ForceStop.ExePath(running.Id), Paths.PlayitExe, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Host(string.IsNullOrWhiteSpace(account)
                        ? "playit agent already running - reusing it."
                        : "Another playit agent is already running on its own account, so this server's account isn't used.");
                    return null;
                }
                running.Kill();
                running.WaitForExit(3000);
            }
            catch { }
        var start = new ProcessStartInfo(Paths.PlayitExe)
        {
            WorkingDirectory = Paths.ToolsDir, UseShellExecute = false, CreateNoWindow = true,
        };
        // this PC's own account is where the agent looks anyway (and older agents don't know --secret-path)
        if (!string.IsNullOrWhiteSpace(account))
        {
            start.ArgumentList.Add("--secret-path");
            start.ArgumentList.Add(SecretFor(account));
        }
        var p = Process.Start(start);
        Log.Host(string.IsNullOrWhiteSpace(account) ? "playit tunnel started." : $"playit tunnel started (account \"{Paths.SafeName(account)}\").");
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
