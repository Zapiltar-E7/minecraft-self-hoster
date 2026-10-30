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
        if (index > 0) PerfRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
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
            Style = (Style)FindResource("PaneStyle"), ToolTip = tip, Padding = new Thickness(0, 0, 0, 14),
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
            double? tps = null, mspt = null;
            var edition = HostConfig.Load()?.Edition;
            if (edition is "Paper" or "Purpur" or "Folia")
            {
                var t = Regex.Match(await _rcon.CommandAsync("tps"), @":\s*\*?([\d.]+)");
                if (t.Success) tps = Math.Min(20, double.Parse(t.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
                var m = Regex.Match(await _rcon.CommandAsync("mspt"), @"([\d.]+)/([\d.]+)/([\d.]+)");
                if (m.Success) mspt = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            }
            else
            {
                // vanilla, Fabric, Forge (1.20.3+): /tick query
                var m = Regex.Match(await _rcon.CommandAsync("tick query"), @"Average time per tick: ([\d.]+)\s*ms");
                if (m.Success)
                {
                    mspt = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                    tps = Math.Min(20, 1000 / Math.Max(mspt.Value, 0.001));
                }
            }
            if (tps is { } v) RecordTps(v);
            else ShowNoTps("This server type doesn't report its TPS.");
            if (mspt is { } ms) Push(_mspt!, ms, Math.Max(50, _mspt!.Points.DefaultIfEmpty(0).Max() * 1.1), $"{ms:0.0} ms",
                ms <= 50 ? "Fast enough for 20 TPS" : "Over 50 ms: the server is falling behind");
        }
        catch
        {
            _rcon?.Dispose();
            _rcon = null;
        }
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
