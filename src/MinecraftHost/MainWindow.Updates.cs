using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MinecraftHost.Core;

namespace MinecraftHost;

/// <summary>
/// Settings → Update automatically: checks GitHub at startup and every 6 hours, and swaps a newer build in (Updater);
/// it runs from the next start, or straight away with Restart. Also the Info panel (version, GitHub, credits).
/// </summary>
public partial class MainWindow
{
    readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromHours(6) };
    bool _updateChecked, _updateFailed, _restartAfterClose;

    void InitUpdates()
    {
        Updater.CleanUp();
        ChkAutoUpdate.IsChecked = HostConfig.Load()?.AutoUpdate != false;
        ShowUpdateNote();
        _updateTimer.Tick += (_, _) => _ = CheckForUpdateAsync();
        _updateTimer.Start();
        Closed += (_, _) => { if (_restartAfterClose) Updater.StartNew(); };
        _ = CheckForUpdateAsync();
    }

    async Task CheckForUpdateAsync()
    {
        if (HostConfig.Load()?.AutoUpdate == false || Updater.Staged != null) return;
        UpdateNote.Text = "Checking for updates...";
        try
        {
            var version = await Updater.UpdateAsync();
            if (version != null) Log.Host($"Version {version.ToString(3)} downloaded. It starts the next time the app opens.");
            _updateFailed = false;
        }
        catch { _updateFailed = true; }   // offline, or GitHub unreachable: try again later
        _updateChecked = true;
        ShowUpdateNote();
    }

    void ShowUpdateNote()
    {
        var staged = Updater.Staged;
        UpdateNote.Text = staged != null ? $"Version {staged.ToString(3)} is ready"
            : $"Version {Updater.CurrentText}" + (_updateFailed ? " · couldn't check" : _updateChecked ? " · up to date" : "");
        BtnRestartUpdate.Visibility = staged != null ? Visibility.Visible : Visibility.Collapsed;
    }

    void ChkAutoUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var config = HostConfig.LoadOrNew();
        config.AutoUpdate = ChkAutoUpdate.IsChecked == true;
        config.Save();
        if (config.AutoUpdate) _ = CheckForUpdateAsync();
    }

    /// <summary>Closes (stopping the server safely first, if it runs) and opens the new version.</summary>
    void BtnRestartUpdate_Click(object sender, RoutedEventArgs e)
    {
        _restartAfterClose = true;
        Close();
        if (IsLoaded && !_closeAfterStop) _restartAfterClose = false;   // closing was called off
    }

    // ------------------------------------------------------------ Info

    void BtnInfo_Click(object sender, RoutedEventArgs e) => ShowOverlay(close =>
    {
        TextBlock Line(string text, string brush = "Text", double top = 0, double size = 13)
        {
            var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = size, Margin = new Thickness(0, top, 0, 0) };
            t.SetResourceReference(TextBlock.ForegroundProperty, brush);
            return t;
        }
        var panel = new StackPanel();
        panel.Children.Add(Heading("Minecraft Self-Hoster", MessageBoxImage.None));
        panel.Children.Add(Line($"Version {Updater.CurrentText}" + (Updater.BuildDate != null ? $" · build {Updater.BuildDate}" : ""), "Muted", 2));

        var github = new Button { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 14, 0, 0), Padding = new Thickness(12, 6, 12, 6), FontWeight = FontWeights.Normal };
        github.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                new TextBlock { Text = "\uE8A7", FontFamily = (FontFamily)FindResource("Icons"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 8, 0) },
                new TextBlock { Text = "View on GitHub" },
            },
        };
        github.Click += (_, _) => Playit.OpenUrl(Updater.RepoUrl);
        panel.Children.Add(github);

        panel.Children.Add(new TextBlock { Text = "CREDITS", Style = (Style)FindResource("CardTitle"), Margin = new Thickness(0, 20, 0, 6) });
        foreach (var (name, what) in new[]
        {
            ("NVorbis", "MIT License, © 2020 Andrew Ward. Plays Minecraft's sounds for the creeper."),
            ("playit.gg", "Tunnels so friends can join without port forwarding."),
            ("Cloudflare", "Quick links for web maps."),
            ("Eclipse Temurin", "The Java the server runs on."),
        })
        {
            var row = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
            row.Inlines.Add(new System.Windows.Documents.Run(name) { FontWeight = FontWeights.SemiBold });
            var rest = new System.Windows.Documents.Run("  " + what);
            rest.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Muted");
            row.Inlines.Add(rest);
            panel.Children.Add(row);
        }
        panel.Children.Add(Line("Not an official Minecraft product. Not approved by or associated with Mojang or Microsoft.", "Muted", 16, 11.5));
        panel.Children.Add(ButtonRow(new (string, Func<object?>, bool)[] { ("Close", () => null, true) }, close));
        return panel;
    }, 460);
}
