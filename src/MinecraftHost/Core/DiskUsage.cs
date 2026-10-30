namespace MinecraftHost.Core;

/// <summary>What's using space for the current server: its worlds, map, plugins, logs and backups.</summary>
public static class DiskUsage
{
    public sealed record Item(string Name, string Detail, string Path, long Bytes, bool Removable = false);

    public sealed record Report(List<Item> Items, long Total, string Drive, long DriveFree, long DriveSize);

    public static long SizeOf(string path, CancellationToken ct = default)
    {
        if (File.Exists(path)) return new FileInfo(path).Length;
        if (!Directory.Exists(path)) return 0;
        long total = 0;
        try
        {
            foreach (var f in new DirectoryInfo(path).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                ct.ThrowIfCancellationRequested();
                try { total += f.Length; } catch { }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        return total;
    }

    public static Report Measure(CancellationToken ct = default)
    {
        var dir = Paths.ServerDir;
        var items = new List<Item>();
        var counted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string name, string detail, string path, bool removable = false)
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return;
            var size = SizeOf(path, ct);
            counted.Add(Path.GetFullPath(path));
            items.Add(new Item(name, detail, path, size, removable));
        }

        if (Directory.Exists(dir))
        {
            // worlds: any folder with a level.dat (Paper 26 keeps the dimensions inside world\)
            foreach (var world in Directory.GetDirectories(dir).Where(d => File.Exists(Path.Combine(d, "level.dat"))))
                Add($"World: {Path.GetFileName(world)}", "Your builds. Back it up, don't delete it.", world);

            var bluemap = Path.Combine(dir, "bluemap");
            if (Directory.Exists(bluemap))
            {
                // leftovers worth pointing out on their own: a corrupt map database set aside earlier
                foreach (var junk in Directory.GetFiles(bluemap, "*.corrupt*"))
                    Add($"Old BlueMap database: {Path.GetFileName(junk)}", "Set aside as corrupt. Safe to delete once the map looks right.", junk, removable: true);
                items.Add(new Item("BlueMap web map", "The map's tiles. BlueMap can render them again.", bluemap,
                    SizeOf(bluemap, ct) - items.Where(i => i.Path.StartsWith(bluemap, StringComparison.OrdinalIgnoreCase)).Sum(i => i.Bytes)));
                counted.Add(Path.GetFullPath(bluemap));
            }

            var coreProtect = Path.Combine(dir, "plugins", "CoreProtect", "database.db");
            Add("CoreProtect history", "Who placed or broke each block, for rollbacks. It keeps growing.", coreProtect);
            var plugins = Path.Combine(dir, "plugins");
            if (Directory.Exists(plugins))
            {
                var size = SizeOf(plugins, ct) - (File.Exists(coreProtect) ? new FileInfo(coreProtect).Length : 0);
                items.Add(new Item("Plugins and their data", "Plugin jars, settings and data.", plugins, size));
                counted.Add(Path.GetFullPath(plugins));
            }
            Add("Mods", "Mod jars.", Path.Combine(dir, "mods"));
            Add("Logs", "Old server logs. Safe to clear out.", Path.Combine(dir, "logs"), removable: true);
            Add("Crash reports", "Safe to clear out once the crash is fixed.", Path.Combine(dir, "crash-reports"), removable: true);
            Add("Libraries", "Downloaded by the server. It gets them again if they're missing.", Path.Combine(dir, "libraries"));
            Add("Server cache", "The Minecraft server files Paper is built on. Downloaded again if missing.", Path.Combine(dir, "cache"));
            Add("Switched-off plugins", "Plugins and datapacks set aside by the app.", Path.Combine(dir, "disabled-until-26.3"));

            // everything else in the folder, as one row
            long other = 0;
            foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
                if (!counted.Contains(entry.FullName)) other += entry is DirectoryInfo ? SizeOf(entry.FullName, ct) : ((FileInfo)entry).Length;
            items.Add(new Item("Everything else", "Server jar, settings and smaller folders.", dir, other));
        }

        Add("Backups of this server", "World zips and saved settings, in the app's backups folder.", Paths.BackupDir);

        string driveName = "";
        long free = -1, driveSize = -1;
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir))!);
            if (drive.IsReady) { driveName = drive.Name; free = drive.AvailableFreeSpace; driveSize = drive.TotalSize; }
        }
        catch { }
        items = items.Where(i => i.Bytes > 0).OrderByDescending(i => i.Bytes).ToList();
        return new Report(items, items.Sum(i => i.Bytes), driveName, free, driveSize);
    }

    public static string Format(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0} MB" : $"{Math.Max(1, bytes / 1024)} KB";
}
