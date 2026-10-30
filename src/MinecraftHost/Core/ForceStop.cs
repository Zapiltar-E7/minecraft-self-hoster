using System.Diagnostics;
using System.Text.RegularExpressions;

namespace MinecraftHost.Core;

/// <summary>
/// Hard-kills the server and tunnels, including ones this app lost track of (for example after a crash).
/// The server is found by the process listening on its port, so another app's Java is never touched.
/// </summary>
public static class ForceStop
{
    public sealed record Target(int Pid, string Description);

    public static List<Target> FindTargets(Process? tracked)
    {
        var targets = new List<Target>();
        void AddPid(int pid, string what)
        {
            if (pid > 0 && targets.All(t => t.Pid != pid)) targets.Add(new Target(pid, what));
        }

        if (tracked is { HasExited: false }) AddPid(tracked.Id, "Minecraft server");
        foreach (var pid in ListeningPids(ServerFiles.GetServerPort()))
            try
            {
                var name = Process.GetProcessById(pid).ProcessName;
                if (name.StartsWith("java", StringComparison.OrdinalIgnoreCase)) AddPid(pid, $"Minecraft server (port {ServerFiles.GetServerPort()})");
            }
            catch { }

        foreach (var p in Process.GetProcessesByName("playit")) AddPid(p.Id, "playit.gg tunnel");
        foreach (var p in Process.GetProcessesByName("cloudflared"))
            if (string.Equals(ExePath(p.Id), Paths.CloudflaredExe, StringComparison.OrdinalIgnoreCase)) AddPid(p.Id, "Cloudflare map tunnel");
        return targets;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern bool QueryFullProcessImageName(IntPtr process, int flags, System.Text.StringBuilder name, ref int size);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    /// <summary>Full exe path of a process. Unlike Process.MainModule this works without debug-level access.</summary>
    public static string? ExePath(int pid)
    {
        var handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var sb = new System.Text.StringBuilder(1024);
            var size = sb.Capacity;
            return QueryFullProcessImageName(handle, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(handle); }
    }

    public static int Kill(IEnumerable<Target> targets)
    {
        var killed = 0;
        foreach (var t in targets)
            try
            {
                var p = Process.GetProcessById(t.Pid);
                p.Kill(entireProcessTree: true);
                p.WaitForExit(5000);
                Log.Host($"Force stopped {t.Description} (PID {t.Pid}).");
                killed++;
            }
            catch (Exception ex) { Log.Host($"Could not stop {t.Description} (PID {t.Pid}): {ex.Message}"); }
        return killed;
    }

    /// <summary>PIDs listening on a TCP port (IPv4 and IPv6), read from netstat.</summary>
    static IEnumerable<int> ListeningPids(int port)
    {
        string output;
        try
        {
            var psi = new ProcessStartInfo("netstat", "-ano -p TCP") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            using var p = Process.Start(psi)!;
            output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            // -p TCP only lists IPv4; ask again for IPv6
            psi.Arguments = "-ano -p TCPv6";
            using var p6 = Process.Start(psi)!;
            output += p6.StandardOutput.ReadToEnd();
            p6.WaitForExit(5000);
        }
        catch { yield break; }

        foreach (Match m in Regex.Matches(output, @"^\s*TCP\s+\S+:(\d+)\s+\S+\s+LISTENING\s+(\d+)", RegexOptions.Multiline))
            if (int.Parse(m.Groups[1].Value) == port) yield return int.Parse(m.Groups[2].Value);
    }
}
