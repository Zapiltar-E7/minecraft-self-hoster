namespace MinecraftHost.Core;

/// <summary>What's using space for the current server: its worlds, map, plugins, logs and backups.</summary>
public static class DiskUsage
{
    /// <summary>
    /// One row. <paramref name="Delete"/>: what deleting it costs, said when asking first; null means it has no delete
    /// button (the server's own files, and plugins, which the Plugins page removes).
    /// </summary>
    public sealed record Item(string Name, string Detail, string Path, long Bytes, bool Removable = false, string? Delete = null);

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
        void Add(string name, string detail, string path, bool removable = false, string? delete = null)
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return;
            var size = SizeOf(path, ct);
            counted.Add(Path.GetFullPath(path));
            items.Add(new Item(name, detail, path, size, removable, delete));
        }

        if (Directory.Exists(dir))
        {
            // worlds: any folder with a level.dat (Paper 26 keeps the dimensions inside world\)
            foreach (var world in Directory.GetDirectories(dir).Where(d => File.Exists(Path.Combine(d, "level.dat"))))
                Add($"World: {Path.GetFileName(world)}", "Your builds. Back it up, don't delete it.", world,
                    delete: "Everything built in this world is lost for good. The server makes a new world in its place at the next start.");

            var bluemap = Path.Combine(dir, "bluemap");
            if (Directory.Exists(bluemap))
            {
                // leftovers worth pointing out on their own: a corrupt map database set aside earlier
                foreach (var junk in Directory.GetFiles(bluemap, "*.corrupt*"))
                    Add($"Old BlueMap database: {Path.GetFileName(junk)}", "Set aside as corrupt. Safe to delete once the map looks right.", junk, removable: true, delete: "");
                items.Add(new Item("BlueMap web map", "The map's tiles. BlueMap can render them again.", bluemap,
                    SizeOf(bluemap, ct) - items.Where(i => i.Path.StartsWith(bluemap, StringComparison.OrdinalIgnoreCase)).Sum(i => i.Bytes),
                    Delete: "BlueMap renders the whole map again, which can take hours on a big world."));
                counted.Add(Path.GetFullPath(bluemap));
            }

            var coreProtect = Path.Combine(dir, "plugins", "CoreProtect", "database.db");
            Add("CoreProtect history", "Who placed or broke each block, for rollbacks. It keeps growing.", coreProtect,
                delete: "Every logged block change is lost, so nothing before now can be rolled back. CoreProtect starts a new history.");
            var plugins = Path.Combine(dir, "plugins");
            if (Directory.Exists(plugins))
            {
                var size = SizeOf(plugins, ct) - (File.Exists(coreProtect) ? new FileInfo(coreProtect).Length : 0);
                items.Add(new Item("Plugins and their data", "Plugin jars, settings and data.", plugins, size));
                counted.Add(Path.GetFullPath(plugins));
            }
            Add("Mods", "Mod jars.", Path.Combine(dir, "mods"));
            Add("Logs", "Old server logs. Safe to clear out.", Path.Combine(dir, "logs"), removable: true, delete: "");
            Add("Crash reports", "Safe to clear out once the crash is fixed.", Path.Combine(dir, "crash-reports"), removable: true, delete: "");
            Add("Libraries", "Downloaded by the server. It gets them again if they're missing.", Path.Combine(dir, "libraries"),
                delete: "The server downloads them again at the next start.");
            Add("Server cache", "The Minecraft server files Paper is built on. Downloaded again if missing.", Path.Combine(dir, "cache"),
                delete: "The server downloads them again at the next start.");
            Add("Switched-off plugins", "Plugins, mods and datapacks set aside by the app.", Path.Combine(dir, "disabled"),
                delete: "They're gone for good and can't be switched back on.");
            foreach (var old in Directory.GetDirectories(dir, "disabled-until-*"))
                Add($"Set aside: {Path.GetFileName(old)}", "Plugins and datapacks waiting for an update.", old,
                    delete: "They're gone for good and can't be switched back on.");

            // everything else in the folder, as one row
            long other = 0;
            foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
                if (!counted.Contains(entry.FullName)) other += entry is DirectoryInfo ? SizeOf(entry.FullName, ct) : ((FileInfo)entry).Length;
            items.Add(new Item("Everything else", "Server jar, settings and smaller folders.", dir, other));
        }

        Add("Backups of this server", "World zips and saved settings, in the app's backups folder.", Paths.BackupDir,
            delete: "Every backup of this server is lost, including ones you made yourself.");

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

    /// <summary>Deletes a row's file or folder for good (read-only files too).</summary>
    public static void Delete(string path)
    {
        if (File.Exists(path))
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
            return;
        }
        if (!Directory.Exists(path)) return;
        foreach (var f in new DirectoryInfo(path).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            if (f.IsReadOnly) f.IsReadOnly = false;
        Directory.Delete(path, recursive: true);
    }

    public static string Format(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0} MB" : $"{Math.Max(1, bytes / 1024)} KB";
}
