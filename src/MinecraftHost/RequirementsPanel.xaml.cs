using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MinecraftHost.Core;

namespace MinecraftHost;

/// <summary>Checklist of what this PC needs, with one button to install whatever is missing. Shown as a panel in the main window.</summary>
public partial class RequirementsPanel : UserControl
{
    /// <summary>What the main window should do after this closes.</summary>
    public enum NextStep { None, InstallServer, ChooseFolder }
    public NextStep Next { get; private set; }

    /// <summary>Set by the main window: closes the panel.</summary>
    public Action<object?>? Close { get; set; }

    List<RequirementStatus> _items = new();
    CancellationTokenSource? _cancel;
    bool Installing => _cancel != null;

    public RequirementsPanel()
    {
        InitializeComponent();
        Loaded += async (_, _) => await RefreshAsync();
    }

    async Task RefreshAsync()
    {
        BtnInstall.IsEnabled = BtnRecheck.IsEnabled = false;
        LblStatus.Text = "Checking...";
        try { _items = await Requirements.CheckAsync(); }
        catch (Exception ex) { LblStatus.Text = "Could not check: " + ex.Message; return; }
        Render();
        var missing = _items.Count(i => !i.Ok && i.Kind != RequirementKind.Server);
        LblStatus.Text = missing == 0 ? "Everything is installed." : $"{missing} item(s) to install.";
        BtnInstall.IsEnabled = missing > 0;
        BtnRecheck.IsEnabled = true;
    }

    void Render(RequirementKind? working = null)
    {
        Rows.Children.Clear();
        foreach (var item in _items)
        {
            var busy = working == item.Kind;
            var (glyph, color) = busy ? ("", "Accent")             // sync
                : item.Ok ? ("", "Accent")                          // check mark
                : item.Optional ? ("", "Muted")                    // info
                : ("", "Warn");                                   // warning

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var icon = new TextBlock
            {
                Text = glyph, FontFamily = (FontFamily)FindResource("Icons"), FontSize = 16,
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 12, 0),
            };
            icon.SetResourceReference(TextBlock.ForegroundProperty, color);
            grid.Children.Add(icon);

            var text = new StackPanel();
            var title = item.Title + (item.Optional && !item.Ok ? "  (optional with your current settings)" : "");
            text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
            text.Children.Add(new TextBlock
            {
                Text = busy ? "Installing..." : item.Detail, TextWrapping = TextWrapping.Wrap, FontSize = 12,
                Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(0, 2, 0, 0),
            });
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);

            if (item.Kind == RequirementKind.Server && !item.Ok)
            {
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
                var choose = new Button { Content = "Choose folder", FontWeight = FontWeights.Normal, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 6, 0), IsEnabled = !Installing };
                choose.Click += (_, _) => { Next = NextStep.ChooseFolder; Close?.Invoke(null); };
                var install = new Button { Content = "Install", Padding = new Thickness(10, 6, 10, 6), IsEnabled = !Installing };
                install.Click += (_, _) => { Next = NextStep.InstallServer; Close?.Invoke(null); };
                buttons.Children.Add(choose);
                buttons.Children.Add(install);
                Grid.SetColumn(buttons, 2);
                grid.Children.Add(buttons);
            }

            Rows.Children.Add(new Border
            {
                Background = (Brush)FindResource("Card"), BorderBrush = (Brush)FindResource("CardBorder"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 11, 14, 11), Margin = new Thickness(0, 0, 0, 8),
                Child = grid,
            });
        }
    }

    async void Install_Click(object sender, RoutedEventArgs e)
    {
        _cancel = new CancellationTokenSource();
        BtnInstall.IsEnabled = BtnRecheck.IsEnabled = false;
        BtnClose.Content = "Cancel";
        var failed = 0;
        // the account link comes last because it waits for the user in the browser
        var order = new[] { RequirementKind.Java, RequirementKind.PlayitAgent, RequirementKind.Cloudflared, RequirementKind.PlayitAccount };
        foreach (var kind in order)
        {
            var item = _items.FirstOrDefault(i => i.Kind == kind);
            if (item == null || item.Ok) continue;
            if (_cancel.IsCancellationRequested) break;
            Render(kind);
            LblStatus.Text = kind == RequirementKind.PlayitAccount
                ? "Waiting for you to sign in and approve this PC in your browser..."
                : $"Installing {item.Title}...";
            try
            {
                if (kind == RequirementKind.PlayitAccount) await Playit.ClaimAsync(_cancel.Token);
                else await Requirements.InstallAsync(kind);
                _items[_items.IndexOf(item)] = item with { Ok = true, Detail = "Installed." };
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                failed++;
                Log.Host($"ERROR installing {item.Title}: {ex.Message}");
                _items[_items.IndexOf(item)] = item with { Detail = "Failed: " + ex.Message };
            }
            Render();
        }
        var cancelled = _cancel.IsCancellationRequested;
        _cancel = null;
        BtnClose.Content = "Close";
        await RefreshAsync();
        if (cancelled) LblStatus.Text = "Cancelled. " + LblStatus.Text;
        else if (failed > 0) LblStatus.Text = $"{failed} item(s) failed - see the server log for details. " + LblStatus.Text;
    }

    async void Recheck_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    void Close_Click(object sender, RoutedEventArgs e)
    {
        // downloads can't be interrupted halfway; cancel stops before the next step
        if (Installing) { _cancel!.Cancel(); LblStatus.Text = "Cancelling after the current step..."; return; }
        Close?.Invoke(null);
    }
}
