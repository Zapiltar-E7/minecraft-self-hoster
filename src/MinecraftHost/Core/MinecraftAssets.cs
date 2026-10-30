using System.IO.Compression;
using System.Text.Json.Nodes;

namespace MinecraftHost.Core;

/// <summary>
/// The creeper easter egg's real Minecraft sounds and explosion particles, read from a Minecraft installation already on
/// this PC (official launcher, Modrinth App, Prism, CurseForge). Nothing of Mojang's ships inside this app; with no game
/// installed, the easter egg uses its own generated sound and smoke instead.
/// </summary>
public sealed class MinecraftAssets
{
    /// <summary>random/fuse.ogg as a WAV, at the creeper's pitch (0.5).</summary>
    public byte[]? Fuse { get; private init; }
    /// <summary>random/explode1-4.ogg as WAVs, each at the explosion's usual pitch (about 0.7).</summary>
    public List<byte[]> Explosions { get; private init; } = new();
    /// <summary>textures/particle/explosion_0..15.png: one explosion puff's animation, frame by frame.</summary>
    public List<byte[]> ExplosionFrames { get; private init; } = new();

    static Task<MinecraftAssets>? _loading;

    /// <summary>Looks once (in the background) and remembers the result.</summary>
    public static Task<MinecraftAssets> LoadAsync() => _loading ??= Task.Run(Load);

    static IEnumerable<string> GameFolders()
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new[]
        {
            Path.Combine(roaming, ".minecraft"),
            Path.Combine(roaming, "ModrinthApp", "meta"),
            Path.Combine(roaming, "com.modrinth.theseus", "meta"),
            Path.Combine(roaming, "PrismLauncher"),
            Path.Combine(home, "curseforge", "minecraft", "Install"),
        }.Where(Directory.Exists);
    }

    static MinecraftAssets Load()
    {
        byte[]? fuse = null;
        var explosions = new List<byte[]>();
        var frames = new List<byte[]>();
        try
        {
            // sounds: assets\indexes\<n>.json maps a name to a hash; the file is assets\objects\<first 2>\<hash>
            foreach (var game in GameFolders())
            {
                var indexes = Path.Combine(game, "assets", "indexes");
                if (!Directory.Exists(indexes)) continue;
                foreach (var index in new DirectoryInfo(indexes).GetFiles("*.json").OrderByDescending(f => f.LastWriteTime))
                {
                    var objects = JsonNode.Parse(File.ReadAllText(index.FullName))?["objects"];
                    string? Find(string name) => objects?[name]?["hash"]?.GetValue<string>() is { Length: > 2 } hash
                        && Path.Combine(game, "assets", "objects", hash[..2], hash) is var file && File.Exists(file) ? file : null;
                    if (Find("minecraft/sounds/random/fuse.ogg") is not { } fuseFile) continue;
                    fuse = OggToWav(File.ReadAllBytes(fuseFile), pitch: 0.5, gain: 0.55);
                    for (var i = 1; i <= 4; i++)
                        if (Find($"minecraft/sounds/random/explode{i}.ogg") is { } boom)
                            explosions.Add(OggToWav(File.ReadAllBytes(boom), pitch: 0.7, gain: 0.4));
                    break;
                }
                if (fuse != null) break;
            }

            // particles: inside a game jar (versions\<v>\<v>.jar, or Prism's libraries\...\minecraft-<v>-client.jar), newest first
            var jars = GameFolders().SelectMany(game => new[] { Path.Combine(game, "versions"), Path.Combine(game, "libraries", "com", "mojang", "minecraft") })
                .Where(Directory.Exists)
                .SelectMany(dir => Directory.EnumerateFiles(dir, "*.jar", SearchOption.AllDirectories))
                .Select(f => new FileInfo(f)).Where(f => f.Length > 5_000_000)   // the game itself, not a small library
                .OrderByDescending(f => f.LastWriteTime);
            foreach (var jar in jars)
            {
                try
                {
                    using var zip = ZipFile.OpenRead(jar.FullName);
                    var entries = Enumerable.Range(0, 16).Select(i => zip.GetEntry($"assets/minecraft/textures/particle/explosion_{i}.png")).ToList();
                    if (entries.Any(e => e == null)) continue;
                    foreach (var e in entries)
                    {
                        using var s = e!.Open();
                        using var ms = new MemoryStream();
                        s.CopyTo(ms);
                        frames.Add(ms.ToArray());
                    }
                    break;
                }
                catch { }
            }
        }
        catch (Exception ex) { Log.Host("Couldn't read Minecraft's sounds for the easter egg: " + ex.Message); }
        return new MinecraftAssets { Fuse = fuse, Explosions = explosions, ExplosionFrames = frames };
    }

    /// <summary>
    /// Decodes an OGG Vorbis file to a 16-bit WAV. Pitch works like Minecraft's: playing at a lower rate makes it
    /// both deeper and longer (0.5 = an octave down, twice as long).
    /// </summary>
    static byte[] OggToWav(byte[] ogg, double pitch, double gain)
    {
        using var reader = new NVorbis.VorbisReader(new MemoryStream(ogg), closeOnDispose: true);
        var channels = reader.Channels;
        var samples = new List<float>();
        var buffer = new float[channels * 4096];
        int read;
        while ((read = reader.ReadSamples(buffer, 0, buffer.Length)) > 0)
            for (var i = 0; i + channels <= read; i += channels)
            {
                float mono = 0;
                for (var c = 0; c < channels; c++) mono += buffer[i + c];
                samples.Add(mono / channels);
            }
        var rate = (int)(reader.SampleRate * pitch);
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        var dataBytes = samples.Count * 2;
        w.Write("RIFF"u8); w.Write(36 + dataBytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(dataBytes);
        foreach (var s in samples) w.Write((short)(Math.Clamp(s * gain, -1, 1) * short.MaxValue));
        w.Flush();
        return ms.ToArray();
    }
}
