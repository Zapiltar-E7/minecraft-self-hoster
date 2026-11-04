namespace MinecraftHost.Core;

/// <summary>
/// The world folders on disk, for the Worlds page when Multiverse isn't installed (worlds an old Multiverse or another
/// plugin left behind). Older servers keep each world in its own folder with a level.dat; Paper 26 keeps the extra ones
/// as dimensions inside the main world (world\dimensions\&lt;namespace&gt;\&lt;name&gt;).
/// </summary>
public static class WorldFolders
{
    /// <param name="Main">The server's own world, nether or end: never deleted from the app.</param>
    public sealed record Folder(string Name, string Dir, bool IsDimension, bool Main);

    public static List<Folder> Find()
    {
        var list = new List<Folder>();
        var level = PlayerLists.GetProperty("level-name") ?? "world";
        try
        {
            if (!Directory.Exists(Paths.ServerDir)) return list;
            foreach (var world in Directory.GetDirectories(Paths.ServerDir).Where(d => File.Exists(Path.Combine(d, "level.dat")))
                         .OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(world);
                var isLevel = name == level;
                list.Add(new Folder(name, world, false, isLevel || name == level + "_nether" || name == level + "_the_end"));

                var dimensions = Path.Combine(world, "dimensions");
                if (!Directory.Exists(dimensions)) continue;
                foreach (var space in Directory.GetDirectories(dimensions).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                    foreach (var dimension in Directory.GetDirectories(space).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                    {
                        var ns = Path.GetFileName(space);
                        var id = Path.GetFileName(dimension);
                        var vanilla = ns == "minecraft" && id is "overworld" or "the_nether" or "the_end";
                        list.Add(new Folder(ns == "minecraft" ? id : $"{ns}:{id}", dimension, true, isLevel && vanilla));
                    }
            }
        }
        catch { }
        return list;
    }
}
