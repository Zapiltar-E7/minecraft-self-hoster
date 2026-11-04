using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MinecraftHost.Core;

namespace MinecraftHost;

/// <summary>
/// The Logs tab's performance row: TPS, tick time (MSPT), memory and CPU over the last few minutes.
/// TPS and MSPT come over RCON (turned on with a random password at start), so the queries don't fill the log;
/// memory and CPU come from the Java process. A TPS dip below 18 is flagged on the tile and in the log.
/// </summary>
public partial class MainWindow
{
    sealed class Tile
    {
        public required Border Card;
        public required TextBlock Value, Note;
        public required Polyline Line;
        public required Canvas Graph;
        public readonly List<double> Points = new();
        public double Max = 1;
    }

    const int PerfPoints = 72;   // 6 minutes at one sample per 5 s
    readonly DispatcherTimer _perfTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    Tile? _tps, _mspt, _mem, _cpu;
    Rcon? _rcon;
    bool _rconTrying;
    DateTime _rconRetryAfter;
    (int Port, string Password)? _rconInfo;
    TimeSpan _lastCpu;
    DateTime _lastCpuAt;
    int _lowTpsSamples;
    DateTime? _lagSince;

    void InitPerformance()
    {
        PerfRow.ColumnDefinitions.Clear();
        PerfRow.Children.Clear();
        _tps = AddTile(0, "TPS", "Ticks per second. 20 is full speed.");
        _mspt = AddTile(1, "TICK TIME", "Milliseconds per tick (MSPT). Under 50 keeps 20 TPS.");
        _mem = AddTile(2, "MEMORY", "What the Java process uses, out of the RAM you gave the server.");
        _cpu = AddTile(3, "CPU", "The server's share of this PC's processor.");
        _perfTimer.Tick += async (_, _) => await SampleAsync();
        _perfTimer.Start();
        ResetPerformance();
    }

    Tile AddTile(int index, string title, string tip)
    {
        if (index > 0) PerfRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        PerfRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var value = new TextBlock { FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 2, 0, 0) };
        var note = new TextBlock { FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis };
        note.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var line = new Polyline { StrokeThickness = 1.6, StrokeLineJoin = PenLineJoin.Round };
        line.SetResourceReference(Shape.StrokeProperty, "Accent");
        var graph = new Canvas { Height = 34, Margin = new Thickness(0, 8, 0, 0), ClipToBounds = true, Children = { line } };
        var head = new DockPanel();
        var titleText = new TextBlock { Text = title, Style = (Style)FindResource("CardTitle"), Margin = new Thickness(0) };
        if (index == 0) head.Children.Add(ProfileButton());   // "What's using it?" (MainWindow.Health.cs)
        head.Children.Add(titleText);
        var card = new Border
        {
            Style = (Style)FindResource("PaneStyle"), ToolTip = tip, Padding = new Thickness(16, 12, 16, 14),
            Child = new StackPanel { Children = { head, value, note, graph } },
        };
        Grid.SetColumn(card, index * 2);
        PerfRow.Children.Add(card);
        var tile = new Tile { Card = card, Value = value, Note = note, Line = line, Graph = graph };
        graph.SizeChanged += (_, _) => Draw(tile);
        return tile;
    }

    void ResetPerformance()
    {
        foreach (var t in new[] { _tps, _mspt, _mem, _cpu })
        {
            if (t == null) continue;
            t.Points.Clear();
            t.Value.Text = "–";
            t.Note.Text = _state == ServerState.Stopped ? "The server isn't running." : "Waiting for the first reading...";
            t.Value.SetResourceReference(TextBlock.ForegroundProperty, "Text");
            Draw(t);
        }
        _lowTpsSamples = 0;
        _lagSince = null;
    }

    /// <summary>Called before a start: RCON on in server.properties, so TPS can be asked for quietly.</summary>
    void PrepareStats() => _rconInfo = Rcon.EnsureEnabled();

    void StopStats()
    {
        _rcon?.Dispose();
        _rcon = null;
        _rconInfo = null;
        _tpsCommand = null;
        _tpsTried = false;
        _spark = null;
        _sparkAsked = default;
        Interlocked.Exchange(ref _sparkUntil, 0);
        ResetPerformance();
    }

    async Task SampleAsync()
    {
        if (_tps == null) return;
        if (_server?.IsRunning != true || _state != ServerState.Running) return;

        // memory and CPU of the Java process
        try
        {
            var p = _server.Process;
            p.Refresh();
            var now = DateTime.UtcNow;
            var cpu = p.TotalProcessorTime;
            if (_lastCpuAt != default)
            {
                var pct = (cpu - _lastCpu).TotalMilliseconds / ((now - _lastCpuAt).TotalMilliseconds * Environment.ProcessorCount) * 100;
                Push(_cpu!, Math.Clamp(pct, 0, 100), 100, $"{pct:0}%", $"{Environment.ProcessorCount} threads on this PC");
            }
            _lastCpu = cpu;
            _lastCpuAt = now;
            var gb = p.WorkingSet64 / (double)(1L << 30);
            var ram = HostConfig.Load()?.RamGB ?? 4;
            Push(_mem!, gb, Math.Max(ram, gb), $"{gb:0.0} GB", $"of {ram} GB given to the server");
        }
        catch { }

        // TPS and tick time over RCON
        if (_rcon == null || !_rcon.Connected)
        {
            if (_rconTrying || DateTime.Now < _rconRetryAfter) return;
            _rconInfo ??= Rcon.EnsureEnabled();
            if (_rconInfo is not { } info) { ShowNoTps("RCON isn't set up yet. It's turned on at the next start."); return; }
            _rconTrying = true;
            _rcon?.Dispose();
            _rcon = await Rcon.ConnectAsync(info.Port, info.Password);
            _rconTrying = false;
            if (_rcon == null)
            {
                _rconRetryAfter = DateTime.Now.AddSeconds(30);
                ShowNoTps("Couldn't reach the server's RCON. Restart the server once to turn it on.");
                return;
            }
        }
        await SyncPlayersAsync();
        try
        {
            var edition = HostConfig.Load()?.Edition;
            var (tps, mspt) = await QueryTpsAsync(edition);
            if (tps is { } v) RecordTps(v);
            else if (!Editions.RunsMods(edition)) ShowNoTps("This server type doesn't report its TPS.");
            else if (!SparkInstalled()) ShowNoTps("This version doesn't report its TPS. Add the spark mod (Mods tab) to see it.");
            else ShowNoTps("Waiting for spark's first reading...");
            if (mspt is { } ms) Push(_mspt!, ms, Math.Max(50, _mspt!.Points.DefaultIfEmpty(0).Max() * 1.1), $"{ms:0.0} ms",
                ms <= 50 ? "Fast enough for 20 TPS" : "Over 50 ms: the server is falling behind");
        }
        catch
        {
            _rcon?.Dispose();
            _rcon = null;
        }
    }

    // Which query gave TPS this run: "tick query" (vanilla, Fabric, Forge 1.20.3+), "forge tps" / "neoforge tps"
    // (older Forge and NeoForge), or "spark tps" (older Fabric and Quilt, with the spark mod). Null until one answers.
    string? _tpsCommand;
    bool _tpsTried;
    sealed record SparkReading(double Tps, double? Mspt);
    volatile SparkReading? _spark;
    DateTime _sparkAsked;
    long _sparkUntil;   // ticks: lines up to then are spark's reply to the app (hidden from the log)
    bool _sparkDurations;

    static readonly Regex TickQueryRx = new(@"Average time per tick: ([\d.]+)\s*ms", RegexOptions.Compiled);
    static readonly Regex ForgeTpsRx = new(@"Overall\s*:\s*Mean tick time:\s*([\d.,]+)\s*ms\.?\s*Mean TPS:\s*([\d.,]+)", RegexOptions.Compiled);
    static readonly Regex SparkNumberRx = new(@"\*?(\d+(?:\.\d+)?)", RegexOptions.Compiled);
    static readonly Regex SparkDurationRx = new(@"([\d.]+)/([\d.]+)/([\d.]+)/([\d.]+)", RegexOptions.Compiled);

    static double Num(string text) => double.Parse(text.Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture);

    async Task<(double? Tps, double? Mspt)> QueryTpsAsync(string? edition)
    {
        if (edition is "Paper" or "Purpur" or "Folia")
        {
            double? tps = null, mspt = null;
            var t = Regex.Match(await _rcon!.CommandAsync("tps"), @":\s*\*?([\d.]+)");
            if (t.Success) tps = Math.Min(20, Num(t.Groups[1].Value));
            var m = Regex.Match(await _rcon.CommandAsync("mspt"), @"([\d.]+)/([\d.]+)/([\d.]+)");
            if (m.Success) mspt = Num(m.Groups[1].Value);
            return (tps, mspt);
        }

        // over RCON, so nothing shows in the log; each is tried once a run and then only the one that answered
        if (_tpsCommand == "tick query" || _tpsCommand == null && !_tpsTried)
        {
            var m = TickQueryRx.Match(await _rcon!.CommandAsync("tick query"));
            if (m.Success)
            {
                _tpsCommand = "tick query";
                var ms = Num(m.Groups[1].Value);
                return (Math.Min(20, 1000 / Math.Max(ms, 0.001)), ms);
            }
        }
        var forge = edition switch { "Forge" => new[] { "forge tps" }, "NeoForge" => new[] { "neoforge tps", "forge tps" }, _ => Array.Empty<string>() };
        foreach (var command in forge.Where(c => _tpsCommand == c || _tpsCommand == null && !_tpsTried))
        {
            var m = ForgeTpsRx.Match(await _rcon!.CommandAsync(command));
            if (!m.Success) continue;
            _tpsCommand = command;
            return (Math.Min(20, Num(m.Groups[2].Value)), Num(m.Groups[1].Value));
        }
        _tpsTried = true;

        // spark answers its commands a moment later, after RCON has replied, so it's asked on the console instead:
        // every 30 s, with its reply left out of the log. In between, the last reading stands.
        if (_tpsCommand is null or "spark tps" && Editions.RunsMods(edition) && SparkInstalled())
        {
            AskSpark();
            if (_spark is { } s) { _tpsCommand = "spark tps"; return (s.Tps, s.Mspt); }
        }
        return (null, null);
    }

    bool SparkInstalled()
    {
        try { return Directory.EnumerateFiles(System.IO.Path.Combine(Paths.ServerDir, "mods"), "spark*.jar").Any(); }
        catch { return false; }
    }

    void AskSpark()
    {
        if (_server is not { IsRunning: true } server || DateTime.Now - _sparkAsked < TimeSpan.FromSeconds(30)) return;
        _sparkAsked = DateTime.Now;
        _sparkDurations = false;
        Interlocked.Exchange(ref _sparkUntil, DateTime.Now.AddSeconds(10).Ticks);
        server.Hide = SparkLine;
        server.Send("spark tps");
    }

    /// <summary>
    /// On the server's output thread. spark's reply to "spark tps":
    ///   [⚡] TPS from last 5s, 10s, 1m, 5m, 15m:
    ///   [⚡]  *20.0, *20.0, 19.9, 20.0, 20.0
    ///   [⚡] Tick durations (min/med/95%ile/max ms) from last 10s, 1m:
    ///   [⚡]  0.4/0.9/1.5/12.3;  0.4/0.9/1.6/45.1
    ///   [⚡] CPU usage from last 10s, 1m, 15m: ... (system) ... (process)
    /// The 10-second TPS and median tick time are kept.
    /// </summary>
    bool SparkLine(string line)
    {
        if (DateTime.Now.Ticks > Interlocked.Read(ref _sparkUntil)) return false;
        var i = line.IndexOf("[⚡]", StringComparison.Ordinal);
        if (i < 0) return false;
        var text = line[(i + 3)..];
        if (text.Contains("TPS from last")) { }
        else if (text.Contains("Tick durations")) _sparkDurations = true;
        else if (text.Contains("(process)")) Interlocked.Exchange(ref _sparkUntil, 0);
        else if (_sparkDurations)
        {
            _sparkDurations = false;
            var d = SparkDurationRx.Match(text);
            if (d.Success && _spark is { } s) _spark = s with { Mspt = Num(d.Groups[2].Value) };
        }
        else if (!text.Contains("CPU usage") && !text.Contains('%'))
        {
            var values = SparkNumberRx.Matches(text);
            if (values.Count >= 2) _spark = new SparkReading(Math.Min(20, Num(values[1].Groups[1].Value)), _spark?.Mspt);
        }
        return true;
    }

    DateTime _playersSynced;

    /// <summary>"list" over RCON: who's really online, in case a join or leave line was missed (or the app started late).</summary>
    async Task SyncPlayersAsync()
    {
        if (_rcon == null || DateTime.Now - _playersSynced < TimeSpan.FromSeconds(60)) return;
        _playersSynced = DateTime.Now;
        try
        {
            var reply = await _rcon.CommandAsync("list");
            var i = reply.IndexOf(':');
            if (i < 0 || !Regex.IsMatch(reply, @"\d+ of a max", RegexOptions.IgnoreCase)) return;
            var online = reply[(i + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(n => Regex.Replace(n, @"^\[[^\]]*\]\s*", ""))      // "[AFK] name" and similar prefixes
                .Where(n => PlayerLists.NameRx.IsMatch(n)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var changed = false;
            foreach (var name in online.Where(n => !_players.Contains(n)).ToList()) changed |= PlayerJoined(name);
            foreach (var name in _players.Where(n => !online.Contains(n)).ToList()) { PlayerLeft(name); changed = true; }
            if (changed) UpdatePlayers();
        }
        catch { }
    }

    void ShowNoTps(string why)
    {
        if (_tps!.Points.Count > 0) return;
        _tps.Note.Text = why;
        _mspt!.Note.Text = why;
    }

    void RecordTps(double tps)
    {
        var low = tps < 18;
        _lowTpsSamples = low ? _lowTpsSamples + 1 : 0;
        string note;
        if (_lowTpsSamples >= 2)
        {
            if (_lagSince == null)
            {
                _lagSince = DateTime.Now;
                Log.Host($"Lag: TPS dropped to {tps:0.0} (full speed is 20).");
            }
            note = $"Lagging since {_lagSince:HH:mm}";
        }
        else
        {
            if (_lagSince != null && !low)
            {
                Log.Host($"TPS is back to {tps:0.0} after {PlayerHistory.Duration(DateTime.Now - _lagSince.Value)} of lag.");
                _lagSince = null;
            }
            note = tps >= 19.5 ? "Full speed" : "Slightly behind";
        }
        Push(_tps!, tps, 20, $"{tps:0.0}", note);
        Stats.Tps(tps);
        var key = tps < 15 ? "Bad" : tps < 18 ? "Warn" : "Accent";
        _tps!.Value.SetResourceReference(TextBlock.ForegroundProperty, key == "Accent" ? "Text" : key);
        _tps.Line.SetResourceReference(Shape.StrokeProperty, key);
    }

    void Push(Tile t, double value, double max, string text, string note)
    {
        t.Points.Add(value);
        if (t.Points.Count > PerfPoints) t.Points.RemoveAt(0);
        t.Max = max;
        t.Value.Text = text;
        t.Note.Text = note;
        Draw(t);
    }

    static void Draw(Tile t)
    {
        // no readings yet (the server is stopped): no empty graph space under the note
        t.Graph.Visibility = t.Points.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var w = t.Graph.ActualWidth;
        var h = t.Graph.ActualHeight;
        var points = new PointCollection();
        if (w > 0 && t.Points.Count > 1)
        {
            var step = w / (PerfPoints - 1);
            var x0 = w - step * (t.Points.Count - 1);   // newest on the right
            for (var i = 0; i < t.Points.Count; i++)
                points.Add(new Point(x0 + i * step, h - 2 - Math.Clamp(t.Points[i] / t.Max, 0, 1) * (h - 4)));
        }
        t.Line.Points = points;
    }
}
