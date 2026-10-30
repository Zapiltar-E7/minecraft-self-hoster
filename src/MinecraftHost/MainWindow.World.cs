using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MinecraftHost.Core;

namespace MinecraftHost;

/// <summary>
/// The World settings card (Server tab): the MOTD with colour buttons and a preview of the multiplayer list,
/// difficulty, game mode, max players, view and simulation distance, PVP and flight, and the server icon.
/// Everything is written straight into server.properties (one line at a time, the rest untouched).
/// </summary>
public partial class MainWindow
{
    static readonly string[] Difficulties = { "peaceful", "easy", "normal", "hard" };
    static readonly string[] Gamemodes = { "survival", "creative", "adventure", "spectator" };
    bool _worldLoading;

    void InitWorldCard()
    {
        _worldLoading = true;
        CmbDifficulty.ItemsSource = Difficulties.Select(Cap).ToList();
        CmbGamemode.ItemsSource = Gamemodes.Select(Cap).ToList();
        _worldLoading = false;

        // colour and style buttons that put a § code in the MOTD at the caret
        foreach (var (code, name, hex) in Motd.Colors)
        {
            var chip = new Button
            {
                Width = 18, Height = 18, Padding = new Thickness(0), Margin = new Thickness(0, 0, 4, 4), ToolTip = name,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)), Tag = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
            };
            chip.SetResourceReference(Control.BorderBrushProperty, "FieldBorder");
            chip.Click += (_, _) => InsertMotdCode(code);
            MotdTools.Children.Add(chip);
        }
        foreach (var (code, label, tip) in new[] { ('l', "B", "Bold"), ('o', "I", "Italic"), ('n', "U", "Underline"), ('r', "×", "Reset to plain") })
        {
            var b = new Button
            {
                Content = label, Width = 22, Height = 18, Padding = new Thickness(0), Margin = new Thickness(code == 'l' ? 6 : 0, 0, 4, 4), FontSize = 11, ToolTip = tip,
                FontWeight = code == 'l' ? FontWeights.Bold : FontWeights.Normal, FontStyle = code == 'o' ? FontStyles.Italic : FontStyles.Normal,
            };
            b.Click += (_, _) => InsertMotdCode(code);
            MotdTools.Children.Add(b);
        }
        SyncWorldCard();
    }

    static string Cap(string s) => char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>Fills the card from server.properties (also after switching servers or installing).</summary>
    void SyncWorldCard()
    {
        if (CmbDifficulty?.ItemsSource == null) return;
        _worldLoading = true;
        TxtMotd.Text = Motd.Decode(PlayerLists.GetProperty("motd") ?? "A Minecraft Server");
        CmbDifficulty.SelectedIndex = Array.IndexOf(Difficulties, PlayerLists.GetProperty("difficulty") ?? "easy");
        CmbGamemode.SelectedIndex = Array.IndexOf(Gamemodes, PlayerLists.GetProperty("gamemode") ?? "survival");
        TxtMaxPlayers.Text = PlayerLists.GetProperty("max-players") ?? "20";
        TxtViewDistance.Text = PlayerLists.GetProperty("view-distance") ?? "10";
        TxtSimDistance.Text = PlayerLists.GetProperty("simulation-distance") ?? "10";
        ChkPvp.IsChecked = PlayerLists.GetProperty("pvp") != "false";
        ChkFlight.IsChecked = PlayerLists.GetProperty("allow-flight") == "true";
        _worldLoading = false;
        ListingCard.IsEnabled = GameplayCard.IsEnabled = File.Exists(Path.Combine(Paths.ServerDir, "server.properties"));
        WorldNote.Text = GameplayCard.IsEnabled
            ? "Most changes apply at the next restart."
            : "Start the server once to create its settings.";
        UpdateMotdPreview();
        UpdateIconPreview();
    }

    void SetProp(string key, string value)
    {
        if (PlayerLists.GetProperty(key) == value) return;
        try { PlayerLists.SetProperty(key, value); }
        catch (Exception ex) { Ask("Couldn't save server.properties: " + ex.Message, "World settings", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    void WorldSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_worldLoading || CmbDifficulty == null) return;
        if (sender == CmbDifficulty && CmbDifficulty.SelectedIndex >= 0)
        {
            var d = Difficulties[CmbDifficulty.SelectedIndex];
            SetProp("difficulty", d);
            if (_server?.IsRunning == true) _server.Send("difficulty " + d);   // takes effect right away
        }
        else if (sender == CmbGamemode && CmbGamemode.SelectedIndex >= 0) SetProp("gamemode", Gamemodes[CmbGamemode.SelectedIndex]);
        else if (sender == ChkPvp) SetProp("pvp", ChkPvp.IsChecked == true ? "true" : "false");
        else if (sender == ChkFlight) SetProp("allow-flight", ChkFlight.IsChecked == true ? "true" : "false");
    }

    void WorldNumber_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SaveWorldNumber((TextBox)sender);
    }

    void WorldNumber_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => SaveWorldNumber((TextBox)sender);

    void SaveWorldNumber(TextBox box)
    {
        if (_worldLoading) return;
        var (key, min, max) = box == TxtMaxPlayers ? ("max-players", 1, 1000) : box == TxtViewDistance ? ("view-distance", 2, 32) : ("simulation-distance", 2, 32);
        if (!int.TryParse(box.Text.Trim(), out var n) || n < min || n > max)
        {
            WorldNote.SetResourceReference(TextBlock.ForegroundProperty, "Bad");
            WorldNote.Text = $"Use a whole number from {min} to {max}.";
            box.Text = PlayerLists.GetProperty(key) ?? "";
            return;
        }
        WorldNote.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        SyncWorldNoteText();
        SetProp(key, n.ToString());
    }

    void SyncWorldNoteText() => WorldNote.Text = "Most changes apply at the next restart.";

    // ------------------------------------------------------------ MOTD

    void InsertMotdCode(char code)
    {
        var at = TxtMotd.CaretIndex;
        TxtMotd.Text = TxtMotd.Text.Insert(at, "§" + code);
        TxtMotd.Focus();
        TxtMotd.CaretIndex = at + 2;
    }

    void TxtMotd_TextChanged(object sender, TextChangedEventArgs e) => UpdateMotdPreview();

    void TxtMotd_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_worldLoading || !ListingCard.IsEnabled) return;
        var lines = TxtMotd.Text.Replace("\r", "").Split('\n').Take(2);
        SetProp("motd", Motd.Encode(string.Join("\n", lines)));
    }

    void UpdateMotdPreview()
    {
        if (MotdPreview == null) return;
        MotdName.Text = TitleText.Text;
        MotdPreview.Inlines.Clear();
        foreach (var part in Motd.Parse(TxtMotd.Text.Replace("\r", "")))
        {
            var run = new Run(part.Text) { Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(part.Hex)) };
            if (part.Bold) run.FontWeight = FontWeights.Bold;
            if (part.Italic) run.FontStyle = FontStyles.Italic;
            if (part.Underline) run.TextDecorations = TextDecorations.Underline;
            if (part.Strike) run.TextDecorations = TextDecorations.Strikethrough;
            MotdPreview.Inlines.Add(run);
        }
    }

    // ------------------------------------------------------------ server icon

    static string IconFile => Path.Combine(Paths.ServerDir, "server-icon.png");

    void UpdateIconPreview()
    {
        IconPreview.Source = MotdIcon.Source = ServerIcon.Source;
        BtnIconRemove.IsEnabled = File.Exists(IconFile);
    }

    void BtnIconChoose_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a picture for the server icon",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp|All files|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var source = new BitmapImage();
            source.BeginInit();
            source.CacheOption = BitmapCacheOption.OnLoad;
            source.UriSource = new Uri(dlg.FileName);
            source.EndInit();

            // the middle square, scaled to 64 x 64
            var side = Math.Min(source.PixelWidth, source.PixelHeight);
            var crop = new CroppedBitmap(source, new Int32Rect((source.PixelWidth - side) / 2, (source.PixelHeight - side) / 2, side, side));
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                RenderOptions.SetBitmapScalingMode(visual, side <= 64 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
                dc.DrawImage(crop, new Rect(0, 0, 64, 64));
            }
            var icon = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
            icon.Render(visual);

            SetAsideIcon();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(icon));
            Directory.CreateDirectory(Paths.ServerDir);
            using (var file = File.Create(IconFile)) encoder.Save(file);
            Log.Host($"Server icon set from {Path.GetFileName(dlg.FileName)}." + (_server?.IsRunning == true ? " Players see it after the next restart." : ""));
        }
        catch (Exception ex)
        {
            Ask("That picture couldn't be used: " + ex.Message, "Server icon", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _iconShown = null;
        UpdateServerIcon();
        UpdateIconPreview();
    }

    void BtnIconRemove_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(IconFile)) return;
        if (Ask("Remove the server icon? A copy is kept in the backups folder.", "Server icon", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        SetAsideIcon();
        _iconShown = null;
        UpdateServerIcon();
        UpdateIconPreview();
    }

    /// <summary>Moves the current icon into backups\(server)\ before it's replaced or removed.</summary>
    static void SetAsideIcon()
    {
        if (!File.Exists(IconFile)) return;
        Directory.CreateDirectory(Paths.BackupDir);
        var copy = Path.Combine(Paths.BackupDir, $"server-icon {DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png");
        File.Move(IconFile, copy);
        Log.Host($"The old server icon was moved to {copy}.");
    }
}
