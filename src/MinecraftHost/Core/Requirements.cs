namespace MinecraftHost.Core;

public enum RequirementKind { Java, PlayitAgent, PlayitAccount, Cloudflared, Server }

public sealed record RequirementStatus(RequirementKind Kind, string Title, bool Ok, string Detail, bool Optional);

/// <summary>Checks and installs everything this PC needs to host the server.</summary>
public static class Requirements
{
    /// <summary>Java version the server needs (or the latest Minecraft release's, before a server is installed).</summary>
    public static async Task<int> JavaNeededAsync()
    {
        var config = HostConfig.Load();
        if (config?.IsSetUp == true) return config.RequiredJava;
        return await ServerFiles.GetRequiredJavaAsync("latest");
    }

    public static async Task<List<RequirementStatus>> CheckAsync()
    {
        var config = HostConfig.Load();
        var javaMajor = await JavaNeededAsync();
        var java = await Task.Run(() => Java.Find(javaMajor));
        var useTunnel = (config?.UseTunnel ?? true) && config?.PortForward != true;   // port forwarding needs no playit
        var shareMap = config?.ShareMap ?? false;

        return new List<RequirementStatus>
        {
            new(RequirementKind.Java, $"Java {javaMajor} or newer", java != null,
                java != null ? $"Found: {java}" : $"Will download Eclipse Temurin {javaMajor} into tools\\java (about 50 MB, no admin rights needed).",
                Optional: false),
            new(RequirementKind.PlayitAgent, "playit.gg tunnel agent", File.Exists(Paths.PlayitExe),
                File.Exists(Paths.PlayitExe) ? "Installed in tools\\playit.exe" : "Lets friends join without port forwarding. Will download the official agent (5 MB).",
                Optional: !useTunnel),
            new(RequirementKind.PlayitAccount, "playit.gg account linked", Playit.HasSecret,
                Playit.HasSecret ? "This PC is linked to a playit.gg account." : "Opens your browser to sign in or create a free playit.gg account, then approve this PC.",
                Optional: !useTunnel),
            new(RequirementKind.Cloudflared, "Cloudflare tunnel (web map link)", File.Exists(Paths.CloudflaredExe),
                File.Exists(Paths.CloudflaredExe) ? "Installed in tools\\cloudflared.exe" : "Shares the BlueMap web map with a link. Will download the official cloudflared (55 MB).",
                Optional: !shareMap),
            new(RequirementKind.Server, "Minecraft server", config?.IsSetUp == true,
                config?.IsSetUp == true ? $"{config.Edition} {config.Version} in {Paths.ServerDir}" : "Choose an existing server folder, or install Paper, Fabric, Forge or Vanilla.",
                Optional: false),
        };
    }

    /// <summary>Installs one missing item. The server is not handled here (it needs the user's choices and EULA).</summary>
    public static async Task InstallAsync(RequirementKind kind)
    {
        switch (kind)
        {
            case RequirementKind.Java: await Java.InstallPortableAsync(await JavaNeededAsync()); break;
            case RequirementKind.PlayitAgent: await Playit.EnsureInstalledAsync(); break;
            case RequirementKind.PlayitAccount: await Playit.ClaimAsync(); break;
            case RequirementKind.Cloudflared: await Cloudflared.EnsureInstalledAsync(); break;
        }
    }
}
