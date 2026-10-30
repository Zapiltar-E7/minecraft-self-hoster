using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MinecraftHost.Core;

/// <summary>
/// Puts the web map's link in the description (topic) of the Discord channel EssentialsX Discord talks in, using that
/// plugin's own bot token. Only the "Live map:" line is replaced; the rest of the description is kept.
/// </summary>
public static class DiscordTopic
{
    const string Api = "https://discord.com/api/v10";
    static readonly Regex MapLine = new(@"^.*Live map: \S+\s*$", RegexOptions.Multiline);

    static string ConfigFile => Path.Combine(Paths.ServerDir, "plugins", "EssentialsDiscord", "config.yml");

    /// <summary>The bot token and the primary channel id from EssentialsX Discord's settings, or null if it isn't set up.</summary>
    public static (string Token, string Channel)? FindBot()
    {
        if (!File.Exists(ConfigFile)) return null;
        string text;
        try { text = File.ReadAllText(ConfigFile); } catch { return null; }
        var token = Regex.Match(text, @"^token:\s*[""']?([^""'\s#]+)", RegexOptions.Multiline).Groups[1].Value;
        var channel = Regex.Match(text, @"^channels:\s*\r?\n(?:[ \t]+.*\r?\n)*?[ \t]+primary:\s*[""']?(\d+)", RegexOptions.Multiline).Groups[1].Value;
        if (token.Length < 20 || token.Contains("INSERT", StringComparison.OrdinalIgnoreCase) || channel.Length == 0) return null;
        return (token, channel);
    }

    public static bool IsAvailable => FindBot() != null;

    /// <summary>Sets the "Live map:" line of the channel description. Logs what happened; never throws.</summary>
    public static async Task SetMapLinkAsync(string url)
    {
        try
        {
            if (FindBot() is not var (token, channel)) { Log.Host("Discord: EssentialsX Discord isn't set up (no bot token or primary channel), so the channel description wasn't changed."); return; }

            using var get = Request(HttpMethod.Get, $"{Api}/channels/{channel}", token);
            using var got = await Net.Http.SendAsync(get);
            if (!got.IsSuccessStatusCode) { Log.Host($"Discord: couldn't read the channel ({Explain(got.StatusCode)})."); return; }
            var old = JsonNode.Parse(await got.Content.ReadAsStringAsync())?["topic"]?.GetValue<string>() ?? "";

            var line = $"🗺️ Live map: {url}";
            var topic = MapLine.IsMatch(old) ? MapLine.Replace(old, line, 1) : (old.TrimEnd().Length > 0 ? old.TrimEnd() + "\n" + line : line);
            if (topic.Length > 1024) topic = line;   // Discord's limit; the map line matters most
            if (topic == old) { Log.Host("Discord: the channel description already has this map link."); return; }

            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var patch = Request(HttpMethod.Patch, $"{Api}/channels/{channel}", token);
                patch.Content = new StringContent(JsonSerializer.Serialize(new { topic }), Encoding.UTF8, "application/json");
                using var resp = await Net.Http.SendAsync(patch);
                if (resp.IsSuccessStatusCode) { Log.Host("Discord: updated the channel description with the new map link."); return; }
                if (resp.StatusCode == (HttpStatusCode)429 && attempt == 0)
                {
                    // Discord allows only 2 description changes per 10 minutes per channel
                    var wait = JsonNode.Parse(await resp.Content.ReadAsStringAsync())?["retry_after"]?.GetValue<double>() ?? 60;
                    if (wait > 600) break;
                    Log.Host($"Discord: changed the description too often; trying again in {Math.Ceiling(wait)} seconds.");
                    await Task.Delay(TimeSpan.FromSeconds(wait + 1));
                    continue;
                }
                Log.Host($"Discord: couldn't change the channel description ({Explain(resp.StatusCode)}).");
                return;
            }
        }
        catch (Exception ex) { Log.Host($"Discord: couldn't change the channel description ({ex.Message})."); }
    }

    static HttpRequestMessage Request(HttpMethod method, string url, string token)
    {
        var r = new HttpRequestMessage(method, url);
        r.Headers.Authorization = new AuthenticationHeaderValue("Bot", token);
        r.Headers.Add("X-Audit-Log-Reason", "Minecraft Host: new web map link");
        return r;
    }

    static string Explain(HttpStatusCode code) => code switch
    {
        HttpStatusCode.Forbidden => "the bot needs the Manage Channels permission on that channel",
        HttpStatusCode.Unauthorized => "the bot token in EssentialsX Discord's settings was refused",
        HttpStatusCode.NotFound => "the primary channel in EssentialsX Discord's settings wasn't found",
        (HttpStatusCode)429 => "Discord only allows 2 description changes per 10 minutes",
        _ => $"Discord answered {(int)code}",
    };
}
