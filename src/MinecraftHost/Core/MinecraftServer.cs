using System.Diagnostics;
using System.Text;

namespace MinecraftHost.Core;

/// <summary>Runs server.jar with redirected console so the window can show output and send commands.</summary>
public sealed class MinecraftServer
{
    readonly Process _process = new();

    public event Action? Ready;
    public event Action<int>? Exited;
    /// <summary>Every output line, on a background thread (for waiting on a reply such as "Saved the game").</summary>
    public event Action<string>? Line;
    public bool IsRunning { get; private set; }
    public Process Process => _process;

    public void Start(string java, HostConfig config, bool safeMode)
    {
        var psi = new ProcessStartInfo(java)
        {
            WorkingDirectory = Paths.ServerDir,
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in BuildArguments(config)) psi.ArgumentList.Add(arg);
        if (safeMode)
        {
            psi.ArgumentList.Add("--safeMode");
            if (config.Edition == "Paper")
            {
                // point Paper at an empty plugin folder so nothing in plugins\ is loaded
                var empty = Path.Combine(Paths.ServerDir, "safe-mode-plugins");
                Directory.CreateDirectory(empty);
                psi.ArgumentList.Add("--plugins");
                psi.ArgumentList.Add(empty);
            }
        }

        _process.StartInfo = psi;
        _process.EnableRaisingEvents = true;
        _process.OutputDataReceived += OnLine;
        _process.ErrorDataReceived += OnLine;
        _process.Exited += (_, _) =>
        {
            IsRunning = false;
            // let the last output lines arrive before reporting the exit
            _process.WaitForExit();
            Exited?.Invoke(_process.ExitCode);
        };

        Log.Host($"Starting {config.Edition} {config.Version} with {config.RamGB} GB RAM{(safeMode ? " [SAFE MODE]" : "")}...");
        if (!string.IsNullOrWhiteSpace(config.JavaArgs) || !string.IsNullOrWhiteSpace(config.ServerArgs))
            Log.Host("Launch arguments: " + string.Join(" ", psi.ArgumentList.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)));
        _process.Start();
        IsRunning = true;
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    /// <summary>Java options, the launch target and server options; the user's extra arguments go after the app's own.</summary>
    public static List<string> BuildArguments(HostConfig config)
    {
        var args = new List<string> { "-Xms1G", $"-Xmx{config.RamGB}G", "-Dstdout.encoding=UTF-8", "-Dstderr.encoding=UTF-8" };
        args.AddRange(SplitArguments(config.JavaArgs));
        // older Forge (1.17-1.20.2) starts from an argument file instead of a jar
        if (Paths.ServerJarName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) args.Add("@" + Paths.ServerJarName);
        else { args.Add("-jar"); args.Add(Paths.ServerJarName); }
        args.Add("nogui");
        args.AddRange(SplitArguments(config.ServerArgs));
        return args;
    }

    /// <summary>Splits on spaces, keeping "quoted parts" together.</summary>
    public static List<string> SplitArguments(string? text)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return list;
        var current = new StringBuilder();
        bool quoted = false, any = false;
        foreach (var c in text)
        {
            if (c == '"') { quoted = !quoted; any = true; }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (any) list.Add(current.ToString());
                current.Clear(); any = false;
            }
            else { current.Append(c); any = true; }
        }
        if (any) list.Add(current.ToString());
        return list;
    }

    bool _readyRaised;
    void OnLine(object sender, DataReceivedEventArgs e)
    {
        if (e.Data == null) return;
        Log.Write(e.Data);
        Line?.Invoke(e.Data);
        if (!_readyRaised && e.Data.Contains("Done (") && e.Data.Contains(")! For help"))
        {
            _readyRaised = true;
            Ready?.Invoke();
        }
        // Forge and NeoForge can write a crash report while starting and then hang instead of exiting
        else if (!_readyRaised && !_crashed && e.Data.Contains("Crash report saved to"))
        {
            _crashed = true;
            _ = Task.Delay(TimeSpan.FromSeconds(15)).ContinueWith(_ =>
            {
                if (!IsRunning) return;
                Log.Host("The server crashed while starting and didn't close by itself, so the app stopped it.");
                try { _process.Kill(entireProcessTree: true); } catch { }
            });
        }
    }
    bool _crashed;

    public void Send(string command)
    {
        if (!IsRunning) return;
        _process.StandardInput.WriteLine(command);
        _process.StandardInput.Flush();
    }
}
