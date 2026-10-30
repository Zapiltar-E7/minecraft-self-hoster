using System.Text.RegularExpressions;

namespace MinecraftHost.Core;

/// <summary>Web = a page in the browser (shared with a Cloudflare link); Game = its own port that needs a playit tunnel.</summary>
public enum ServiceKind { Web, Game }

/// <summary>A plugin or mod that listens on a port of its own, besides the Minecraft one.</summary>
public sealed record ServiceDef(
    string Id, string Title, string Purpose, ServiceKind Kind, string Protocol,
    string[] Names,          // plugin.yml / mod names that provide it
    string[] ConfigFiles,    // where its port is set, relative to the server folder (first that exists wins)
    string PortPattern,      // regex with the port in group 1
    int DefaultPort,
    string Icon);

/// <summary>A service found in this server, with the port it's set to.</summary>
public sealed record DetectedService(ServiceDef Def, string ProvidedBy, int Port, string? ConfigFile)
{
    public string Id => Def.Id;
}

public static class Services
{
    public static readonly ServiceDef[] Known =
    {
        new("bluemap", "BlueMap web map", "A live 3D map of your worlds in the browser.", ServiceKind.Web, "HTTP",
            new[] { "BlueMap" }, new[] { "plugins/BlueMap/webserver.conf", "config/bluemap/webserver.conf" },
            @"(?m)^\s*port\s*[:=]\s*(\d+)", 8100, ""),
        new("dynmap", "Dynmap web map", "A live map of your worlds in the browser.", ServiceKind.Web, "HTTP",
            new[] { "dynmap", "Dynmap" }, new[] { "plugins/dynmap/configuration.txt", "dynmap/configuration.txt" },
            @"(?m)^\s*webserver-port:\s*(\d+)", 8123, ""),
        new("squaremap", "squaremap web map", "A live map of your worlds in the browser.", ServiceKind.Web, "HTTP",
            new[] { "squaremap" }, new[] { "plugins/squaremap/config.yml", "config/squaremap/config.yml" },
            @"(?m)^\s*port:\s*(\d+)", 8080, ""),
        new("pl3xmap", "Pl3xMap web map", "A live map of your worlds in the browser.", ServiceKind.Web, "HTTP",
            new[] { "Pl3xMap" }, new[] { "plugins/Pl3xMap/config.yml", "config/pl3xmap/config.yml" },
            @"(?m)^\s*port:\s*(\d+)", 8080, ""),
        new("plan", "Plan player analytics", "Stats about your players and server, in the browser.", ServiceKind.Web, "HTTP",
            new[] { "Plan" }, new[] { "plugins/Plan/config.yml" },
            @"(?mi)^\s*port:\s*(\d+)", 8804, ""),
        new("voicechat", "Simple Voice Chat", "Lets players talk in game. Each player needs the mod in their own game too.", ServiceKind.Game, "UDP",
            new[] { "voicechat", "Simple Voice Chat" }, new[] { "plugins/voicechat/voicechat-server.properties", "config/voicechat/voicechat-server.properties" },
            @"(?m)^port=(-?\d+)", 24454, ""),
        new("geyser", "Geyser (Bedrock players)", "Lets players on Bedrock (phones, consoles, Windows Bedrock) join.", ServiceKind.Game, "UDP",
            new[] { "Geyser-Spigot", "Geyser-Fabric", "Geyser-NeoForge", "Geyser" }, new[] { "plugins/Geyser-Spigot/config.yml", "config/Geyser-Fabric/config.yml", "config/Geyser-NeoForge/config.yml" },
            @"(?ms)^bedrock:.*?^\s+port:\s*(\d+)", 19132, ""),
        new("votifier", "Votifier", "Lets server-list sites tell your server when someone votes.", ServiceKind.Game, "TCP",
            new[] { "Votifier", "NuVotifier" }, new[] { "plugins/Votifier/config.yml", "plugins/NuVotifier/config.yml" },
            @"(?m)^port:\s*(\d+)", 8192, ""),
    };

    /// <summary>The services provided by the plugins and mods that are switched on.</summary>
    public static List<DetectedService> Detect(IEnumerable<Addon> addons)
    {
        var on = addons.Where(a => a.Enabled && a.Kind != AddonKind.Datapack).ToList();
        var found = new List<DetectedService>();
        foreach (var def in Known)
        {
            var by = on.FirstOrDefault(a => def.Names.Any(n => n.Equals(a.Name, StringComparison.OrdinalIgnoreCase)));
            if (by == null) continue;
            var (port, file) = ReadPort(def);
            found.Add(new DetectedService(def, by.Name, port, file));
        }
        return found;
    }

    static (int Port, string? File) ReadPort(ServiceDef def)
    {
        foreach (var rel in def.ConfigFiles)
        {
            var path = Path.Combine(Paths.ServerDir, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) continue;
            try
            {
                var m = Regex.Match(File.ReadAllText(path), def.PortPattern);
                if (m.Success && int.TryParse(m.Groups[1].Value, out var port))
                    return (port == -1 ? ServerFiles.GetServerPort() : port, path);   // -1: "same as Minecraft"
            }
            catch { }
            return (def.DefaultPort, path);
        }
        return (def.DefaultPort, null);   // no config yet: it's made at the first start, with the default port
    }

    /// <summary>
    /// Simple Voice Chat tells players where to connect with voice_host; with a playit tunnel that's the tunnel's address.
    /// Returns false if its settings file isn't there yet.
    /// </summary>
    public static bool SetVoiceHost(DetectedService voice, string address)
    {
        if (voice.ConfigFile == null || !File.Exists(voice.ConfigFile)) return false;
        var backup = Path.Combine(Paths.BackupDir, "configs", DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"), Path.GetRelativePath(Paths.ServerDir, voice.ConfigFile));
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        File.Copy(voice.ConfigFile, backup, overwrite: true);
        var text = File.ReadAllText(voice.ConfigFile);
        var newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.TrimEnd('\r', '\n').Split(newline).ToList();
        var i = lines.FindIndex(l => l.StartsWith("voice_host="));
        if (i >= 0) lines[i] = "voice_host=" + address;
        else lines.Add("voice_host=" + address);
        File.WriteAllText(voice.ConfigFile, string.Join(newline, lines) + newline);
        return true;
    }
}
