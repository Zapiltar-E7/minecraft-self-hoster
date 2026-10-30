using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MinecraftHost.Core;

namespace MinecraftHost;

/// <summary>
/// Port forwarding, for people who can set up their router: friends connect to the network's public IP, so no playit
/// tunnel and no Cloudflare link is needed. Chosen per server on the Connections tab (Minecraft → Port forwarding).
/// </summary>
public partial class MainWindow
{
    /// <summary>The address friends join with: the playit address, or with port forwarding the public IP (or the address set instead).</summary>
    static string? FriendAddress(HostConfig? c)
    {
        if (c == null) return null;
        if (!c.PortForward) return string.IsNullOrEmpty(c.PlayitAddress) ? null : c.PlayitAddress;
        if (!string.IsNullOrEmpty(c.ForwardAddress)) return c.ForwardAddress;
        if (Net.PublicIPv4 == null) return null;
        var port = ServerFiles.GetServerPort();
        return port == 25565 ? Net.PublicIPv4 : $"{Net.PublicIPv4}:{port}";   // 25565 is the game's default: no port needed
    }

    async Task FindPublicAddressAsync()
    {
        if (await Net.FindPublicIPv4Async() == null) return;
        UpdateView();
        if (ConnectionsPage.Visibility == Visibility.Visible) RebuildConnections();
    }

    void JoinMethod_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || _connBuilding || JoinForward == null) return;
        var config = HostConfig.LoadOrNew();
        config.PortForward = JoinForward.IsChecked == true;
        config.Save();
        UpdateView();
        RebuildConnections();
        Motion.FadeIn(ConnDetail);
    }

    void ChangeForwardAddress(HostConfig config)
    {
        var value = Prompt("Public address",
            $"Leave empty to use this network's public IP{(Net.PublicIPv4 != null ? $" ({Net.PublicIPv4})" : "")}, or enter a domain name that points to it.",
            config.ForwardAddress ?? "");
        if (value == null) return;
        var addr = value.Trim();
        if (addr.Length > 0 && !Regex.IsMatch(addr, @"^[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)+(:\d+)?$"))
        {
            Ask($"'{addr}' doesn't look like an address.", "Public address", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        config.ForwardAddress = addr.Length > 0 ? addr : null;
        config.Save();
        UpdateView();
        if (ConnectionsPage.Visibility == Visibility.Visible) RebuildConnections();
    }

    /// <summary>A plugin's connection with port forwarding: the port to forward, and the address (or link) friends use.</summary>
    List<UIElement> ForwardParts(DetectedService s)
    {
        var web = s.Def.Kind == ServiceKind.Web;
        // a domain name set for the game points to the same router; otherwise the public IP
        var set = HostConfig.Load()?.ForwardAddress;
        var host = !string.IsNullOrEmpty(set) ? set.Split(':')[0] : Net.PublicIPv4;
        var address = host == null ? null : web ? $"http://{host}:{s.Port}" : $"{host}:{s.Port}";
        var value = new TextBox { Style = (Style)FindResource("Value"), Text = address ?? "Finding your public address..." };
        var row = new DockPanel();
        if (web) row.Children.Add(IconButton("\uE8A7", "Open in your browser", (_, _) => Playit.OpenUrl(address ?? $"http://localhost:{s.Port}")));
        row.Children.Add(IconButton("\uE8C8", "Copy", (_, _) => { if (address != null) Clipboard.SetText(address); }));
        row.Children.Add(value);
        var protocol = s.Def.Protocol == "UDP" ? "UDP" : "TCP";
        return new List<UIElement>
        {
            FieldLabel(web ? "Link for friends" : "Friends connect to"), row,
            Muted($"Forward {protocol} {s.Port} on your router to this PC ({Net.LocalIPv4() ?? "its local IP"}).", 8),
        };
    }
}
