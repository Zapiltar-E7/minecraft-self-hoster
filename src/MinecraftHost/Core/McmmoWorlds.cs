namespace MinecraftHost.Core;

/// <summary>
/// mcMMO's world blacklist (plugins/mcMMO/world_blacklist.txt): one world name per line, and mcMMO is off in those
/// worlds (no skills, no XP). mcMMO reads it only when the server starts.
/// </summary>
public static class McmmoWorlds
{
    static string Folder => Path.Combine(Paths.ServerDir, "plugins", "mcMMO");
    static string File_ => Path.Combine(Folder, "world_blacklist.txt");

    public static bool Installed =>
        Directory.Exists(Path.Combine(Paths.ServerDir, "plugins"))
        && Directory.GetFiles(Path.Combine(Paths.ServerDir, "plugins"), "*.jar").Any(f => Path.GetFileName(f).StartsWith("mcmmo", StringComparison.OrdinalIgnoreCase)
                                                                                           && !Path.GetFileName(f).StartsWith("mcmmo-groups", StringComparison.OrdinalIgnoreCase));

    public static HashSet<string> Off()
    {
        if (!File.Exists(File_)) return new(StringComparer.OrdinalIgnoreCase);
        return File.ReadAllLines(File_).Select(l => l.Trim()).Where(l => l.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Turns mcMMO off (or back on) in a world. Other names in the file are kept as they are.</summary>
    public static void Set(string world, bool off)
    {
        var lines = File.Exists(File_) ? File.ReadAllLines(File_).ToList() : new List<string>();
        lines.RemoveAll(l => l.Trim().Equals(world, StringComparison.OrdinalIgnoreCase));
        if (off) lines.Add(world);
        Directory.CreateDirectory(Folder);
        File.WriteAllText(File_, lines.Count == 0 ? "" : string.Join("\n", lines) + "\n");
    }
}
