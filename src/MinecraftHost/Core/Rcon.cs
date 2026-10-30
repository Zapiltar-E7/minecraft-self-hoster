using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MinecraftHost.Core;

/// <summary>
/// A small RCON client (the Source RCON protocol Minecraft speaks). The Log tab's performance card asks the server for
/// its TPS this way: the replies come back here instead of being printed to the console and latest.log every few seconds.
/// </summary>
public sealed class Rcon : IDisposable
{
    readonly TcpClient _client = new();
    NetworkStream? _stream;
    int _nextId = 1;
    readonly SemaphoreSlim _lock = new(1, 1);

    public bool Connected => _client.Connected;

    public static async Task<Rcon?> ConnectAsync(int port, string password)
    {
        var rcon = new Rcon();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await rcon._client.ConnectAsync("127.0.0.1", port, timeout.Token);
            rcon._stream = rcon._client.GetStream();
            var (id, _) = await rcon.SendAsync(3, password);   // 3 = log in
            if (id == -1) { rcon.Dispose(); return null; }      // wrong password
            return rcon;
        }
        catch { rcon.Dispose(); return null; }
    }

    /// <summary>Runs a command and returns its reply, without Minecraft's § colour codes.</summary>
    public async Task<string> CommandAsync(string command)
    {
        var (_, body) = await SendAsync(2, command);
        return Regex.Replace(body, "§.", "");
    }

    async Task<(int Id, string Body)> SendAsync(int type, string body)
    {
        await _lock.WaitAsync();
        try
        {
            var stream = _stream ?? throw new IOException("Not connected");
            var id = _nextId++;
            var payload = Encoding.UTF8.GetBytes(body);
            var packet = new byte[14 + payload.Length];
            BitConverter.GetBytes(10 + payload.Length).CopyTo(packet, 0);
            BitConverter.GetBytes(id).CopyTo(packet, 4);
            BitConverter.GetBytes(type).CopyTo(packet, 8);
            payload.CopyTo(packet, 12);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await stream.WriteAsync(packet, timeout.Token);

            var head = await ReadExactly(stream, 4, timeout.Token);
            var length = BitConverter.ToInt32(head, 0);
            if (length < 10 || length > 1 << 20) throw new IOException("Bad RCON reply");
            var rest = await ReadExactly(stream, length, timeout.Token);
            var replyId = BitConverter.ToInt32(rest, 0);
            return (replyId, Encoding.UTF8.GetString(rest, 8, length - 10));
        }
        finally { _lock.Release(); }
    }

    static async Task<byte[]> ReadExactly(NetworkStream stream, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, count - read), ct);
            if (n == 0) throw new IOException("RCON connection closed");
            read += n;
        }
        return buffer;
    }

    public void Dispose()
    {
        try { _client.Dispose(); } catch { }
        _lock.Dispose();
    }

    /// <summary>
    /// Makes sure server.properties has RCON on, with a random password and no echo to operators in game, before a start.
    /// Returns (port, password), or null when it can't be set up.
    /// </summary>
    public static (int Port, string Password)? EnsureEnabled()
    {
        try
        {
            if (!File.Exists(Path.Combine(Paths.ServerDir, "server.properties"))) return null;   // a first start writes it
            var on = PlayerLists.GetProperty("enable-rcon") == "true";
            var password = PlayerLists.GetProperty("rcon.password") ?? "";
            var port = int.TryParse(PlayerLists.GetProperty("rcon.port"), out var p) && p > 0 ? p : 25575;
            if (!on || password.Length < 12)
            {
                password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
                PlayerLists.SetProperty("enable-rcon", "true");
                PlayerLists.SetProperty("rcon.port", port.ToString());
                PlayerLists.SetProperty("rcon.password", password);
                Log.Host($"Turned on RCON (port {port}, random password) in server.properties, for the live performance graphs on the Logs tab.");
            }
            // otherwise operators in game see every stats request in chat
            if (PlayerLists.GetProperty("broadcast-rcon-to-ops") != "false") PlayerLists.SetProperty("broadcast-rcon-to-ops", "false");
            return (port, password);
        }
        catch (Exception ex)
        {
            Log.Host("Couldn't set up RCON for the performance graphs: " + ex.Message);
            return null;
        }
    }
}
