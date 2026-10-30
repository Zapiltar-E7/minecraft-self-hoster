using System.Text.Json;

namespace MinecraftHost.Core;

/// <summary>
/// Day-by-day numbers for the weekly summary, per server: play time, who played, the most online at once, average TPS
/// and crashes. Kept in data\(server name) stats.json next to the app, 120 days at most.
/// </summary>
public static class Stats
{
    public sealed class Day
    {
        public DateTime Date { get; set; }
        public double PlaySeconds { get; set; }
        public List<string> Players { get; set; } = new();
        public int Peak { get; set; }
        public double TpsSum { get; set; }
        public int TpsCount { get; set; }
        public int Crashes { get; set; }
    }

    public sealed record Summary(int Players, double Hours, int Peak, double? Tps, int Crashes, List<string> Top);

    static string File_ => Path.Combine(Paths.Root, "data", Paths.SafeName(Paths.ServerName) + " stats.json");
    static List<Day>? _days;
    static string? _loadedFor;
    static int _unsaved;

    static List<Day> Days
    {
        get
        {
            if (_days == null || _loadedFor != File_)
            {
                _loadedFor = File_;
                try { _days = File.Exists(File_) ? JsonSerializer.Deserialize<List<Day>>(File.ReadAllText(File_)) ?? new() : new(); }
                catch { _days = new(); }
            }
            return _days;
        }
    }

    static Day Today
    {
        get
        {
            var d = Days.FirstOrDefault(x => x.Date == DateTime.Today);
            if (d == null)
            {
                Days.Add(d = new Day { Date = DateTime.Today });
                Days.RemoveAll(x => x.Date < DateTime.Today.AddDays(-120));
            }
            return d;
        }
    }

    public static void Seen(string name, int online)
    {
        var d = Today;
        if (!d.Players.Contains(name, StringComparer.OrdinalIgnoreCase)) d.Players.Add(name);
        d.Peak = Math.Max(d.Peak, online);
        Save();
    }

    public static void Played(TimeSpan time) { Today.PlaySeconds += time.TotalSeconds; Save(); }

    public static void Tps(double tps)
    {
        var d = Today;
        d.TpsSum += tps;
        d.TpsCount++;
        if (++_unsaved >= 12) Save();   // a sample every 5 s: write about once a minute
    }

    public static void Crash() { Today.Crashes++; Save(); }

    /// <summary>The 7 days ending with <paramref name="lastDay"/>.</summary>
    public static Summary Week(DateTime lastDay)
    {
        var from = lastDay.Date.AddDays(-6);
        var days = Days.Where(d => d.Date >= from && d.Date <= lastDay.Date).ToList();
        var players = days.SelectMany(d => d.Players).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var top = days.SelectMany(d => d.Players).GroupBy(p => p, StringComparer.OrdinalIgnoreCase)
                      .OrderByDescending(g => g.Count()).Take(3).Select(g => g.Key).ToList();
        var count = days.Sum(d => d.TpsCount);
        return new Summary(players.Count, days.Sum(d => d.PlaySeconds) / 3600, days.Select(d => d.Peak).DefaultIfEmpty(0).Max(),
            count > 0 ? days.Sum(d => d.TpsSum) / count : null, days.Sum(d => d.Crashes), top);
    }

    public static void Reset() { Save(); _days = null; }

    public static void Save()
    {
        _unsaved = 0;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
            File.WriteAllText(File_, JsonSerializer.Serialize(Days, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
