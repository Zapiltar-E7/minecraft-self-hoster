using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MinecraftHost.Core;

namespace MinecraftHost;

/// <summary>
/// Updates, only when the user says so: with Settings → Check for updates on, GitHub is checked at startup and every
/// 6 hours. A newer signed release shows a notice above the page (and in Settings) with Download and install; nothing is
/// ever installed without that click. Installing checks the release's signature (Updater), and it runs from the next
/// start, or straight away with Restart. Also the Info panel (version, GitHub, credits).
/// </summary>
public partial class MainWindow
{
    readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromHours(6) };
    bool _updateChecked, _updateFailed, _updateInstalling, _restartAfterClose;
    Updater.Release? _updateAvailable;
    Version? _updateDismissed;   // "Later": not shown again for this version until the app restarts

    void InitUpdates()
    {
        Updater.CleanUp();
        ChkAutoUpdate.IsChecked = HostConfig.Load()?.CheckForUpdates != false;
        ShowUpdate();
        _updateTimer.Tick += (_, _) => _ = CheckForUpdateAsync();
        _updateTimer.Start();
        Closed += (_, _) => { if (_restartAfterClose) Updater.StartNew(); };
        _ = CheckForUpdateAsync();
    }

    async Task CheckForUpdateAsync()
    {
        if (HostConfig.Load()?.CheckForUpdates == false || Updater.Installed != null || _updateInstalling) return;
        UpdateNote.Text = "Checking for updates...";
        try
        {
            var release = await Updater.CheckAsync();
            if (release != null && release.Version != _updateAvailable?.Version) Log.Host($"Version {release.Version.ToString(3)} is available (Settings → Check for updates).");
            _updateAvailable = release;
            _updateFailed = false;
        }
        catch { _updateFailed = true; }   // offline, or GitHub unreachable: try again later
        _updateChecked = true;
        ShowUpdate();
    }

    /// <summary>The note in Settings, its buttons, and the notice above the page.</summary>
    void ShowUpdate()
    {
        var installed = Updater.Installed;
        var available = _updateAvailable;
        UpdateNote.Text = installed != null ? $"Version {installed.ToString(3)} is installed. Restart to use it."
            : _updateInstalling ? $"Downloading version {available!.Version.ToString(3)}..."
            : available != null ? $"Version {available.Version.ToString(3)} is available (you have {Updater.CurrentText})"
            : $"Version {Updater.CurrentText}" + (_updateFailed ? " · couldn't check" : _updateChecked ? " · up to date" : "");
        BtnInstallUpdate.Visibility = available != null && installed == null && !_updateInstalling ? Visibility.Visible : Visibility.Collapsed;
        BtnRestartUpdate.Visibility = installed != null ? Visibility.Visible : Visibility.Collapsed;

        var show = installed != null && installed != _updateDismissed || _updateInstalling || installed == null && available != null && available.Version != _updateDismissed;
        UpdateBannerText.Text = installed != null ? $"Version {installed.ToString(3)} is installed. Restart the app to use it."
            : _updateInstalling ? $"Downloading version {available!.Version.ToString(3)} and checking its signature..."
            : available != null ? $"Version {available.Version.ToString(3)} is available. You have {Updater.CurrentText}." : "";
        UpdateBannerLabel.Text = installed != null ? "Restart now" : "Download and install";
        BtnUpdateBanner.IsEnabled = !_updateInstalling;
        var wasShown = UpdateBanner.Visibility == Visibility.Visible;
        UpdateBanner.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show && !wasShown) Motion.FadeIn(UpdateBanner);
    }

    void ChkAutoUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var config = HostConfig.LoadOrNew();
        config.CheckForUpdates = ChkAutoUpdate.IsChecked == true;
        config.Save();
        if (config.CheckForUpdates) _ = CheckForUpdateAsync();
        else { _updateAvailable = null; ShowUpdate(); }
    }

    void BtnUpdateBanner_Click(object sender, RoutedEventArgs e)
    {
        if (Updater.Installed != null) BtnRestartUpdate_Click(sender, e);
        else _ = InstallUpdateAsync();
    }

    void BtnInstallUpdate_Click(object sender, RoutedEventArgs e) => _ = InstallUpdateAsync();

    void BtnUpdateLater_Click(object sender, RoutedEventArgs e)
    {
        _updateDismissed = Updater.Installed ?? _updateAvailable?.Version;
        UpdateBanner.Visibility = Visibility.Collapsed;
    }

    void BtnUpdateNotes_Click(object sender, RoutedEventArgs e) => Playit.OpenUrl(_updateAvailable?.PageUrl ?? Updater.RepoUrl + "/releases");

    async Task InstallUpdateAsync()
    {
        if (_updateAvailable is not { } release || _updateInstalling) return;
        _updateInstalling = true;
        ShowUpdate();
        try
        {
            await Updater.InstallAsync(release);
            Log.Host($"Version {release.Version.ToString(3)} is installed (signature checked). It starts the next time the app opens.");
        }
        catch (Exception ex)
        {
            Log.Host("The update wasn't installed: " + ex.Message);
            Ask("The update wasn't installed.\n\n" + ex.Message, "Update", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _updateInstalling = false;
            ShowUpdate();
        }
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
            ("NVorbis", "MIT License, © 2020 Andrew Ward. Plays Minecraft's sounds for a small easter egg."),
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
