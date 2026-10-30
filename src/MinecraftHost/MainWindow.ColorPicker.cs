using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MinecraftHost;

/// <summary>
/// The custom highlight colour (Settings → Appearance, the last swatch): a panel with a shade square, a hue bar
/// and a hex box. The colour is tried on the window when a drag ends or the hex is entered; Cancel puts the old one back.
/// </summary>
public partial class MainWindow
{
    /// <summary>Returns the picked colour as "#RRGGBB", or null if cancelled.</summary>
    string? PickColor(Color start)
    {
        var (hue, sat, val) = ToHsv(start);
        var before = Themes.CurrentHighlight?.Name;   // put back on Cancel

        var hueColor = new SolidColorBrush();
        var square = new Grid { Height = 190, Cursor = Cursors.Cross, ClipToBounds = true };
        square.Children.Add(new Rectangle { RadiusX = 8, RadiusY = 8, Fill = new LinearGradientBrush(Colors.White, Colors.White, 0) { GradientStops = { new(Colors.White, 0), new(Colors.White, 1) } } });
        var tint = new Rectangle { RadiusX = 8, RadiusY = 8 };
        square.Children.Add(tint);
        square.Children.Add(new Rectangle { RadiusX = 8, RadiusY = 8, Fill = new LinearGradientBrush(Colors.Transparent, Colors.Black, 90) });
        var squareKnob = new Ellipse { Width = 16, Height = 16, Stroke = Brushes.White, StrokeThickness = 2.5, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
        square.Children.Add(squareKnob);

        var rainbow = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        for (var i = 0; i <= 6; i++) rainbow.GradientStops.Add(new GradientStop(FromHsv(i * 60 % 360, 1, 1), i / 6.0));
        var hueBar = new Grid { Height = 16, Margin = new Thickness(0, 14, 0, 0), Cursor = Cursors.Hand };
        hueBar.Children.Add(new Border { CornerRadius = new CornerRadius(8), Background = rainbow });
        var hueKnob = new Ellipse { Width = 18, Height = 18, Stroke = Brushes.White, StrokeThickness = 3, Margin = new Thickness(0, -1, 0, -1), HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false };
        hueBar.Children.Add(hueKnob);

        var preview = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(16), Margin = new Thickness(0, 0, 10, 0) };
        var hex = new TextBox { Style = (Style)FindResource("Input"), FontFamily = (FontFamily)FindResource("Mono"), Width = 120, VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(hex, "Hex colour");
        var hexRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0), Children = { preview, hex } };

        string Hex() { var c = FromHsv(hue, sat, val); return $"#{c.R:X2}{c.G:X2}{c.B:X2}"; }
        void Show(bool fromHex = false)
        {
            var c = FromHsv(hue, sat, val);
            tint.Fill = new LinearGradientBrush(Color.FromArgb(0, 255, 255, 255), FromHsv(hue, 1, 1), 0);
            preview.Background = new SolidColorBrush(c);
            if (!fromHex) hex.Text = Hex();
            squareKnob.Margin = new Thickness(sat * Math.Max(0, square.ActualWidth) - 8, (1 - val) * square.Height - 8, 0, 0);
            hueKnob.Margin = new Thickness(hue / 360 * Math.Max(0, hueBar.ActualWidth - 18), -1, 0, -1);
        }
        void TryOn() => Themes.Apply(Themes.Current.Name, Hex(), animate: false);

        void Drag(FrameworkElement area, Action<Point> at)
        {
            area.MouseLeftButtonDown += (_, e) => { area.CaptureMouse(); at(e.GetPosition(area)); Show(); };
            area.MouseMove += (_, e) => { if (area.IsMouseCaptured) { at(e.GetPosition(area)); Show(); } };
            area.MouseLeftButtonUp += (_, _) => { if (area.IsMouseCaptured) { area.ReleaseMouseCapture(); TryOn(); } };
        }
        Drag(square, p => { sat = Math.Clamp(p.X / square.ActualWidth, 0, 1); val = 1 - Math.Clamp(p.Y / square.Height, 0, 1); });
        Drag(hueBar, p => hue = Math.Clamp(p.X / hueBar.ActualWidth, 0, 1) * 359.9);
        void FromBox()
        {
            var text = hex.Text.Trim();
            if (!text.StartsWith('#')) text = "#" + text;
            if (text.Length != 7) return;
            try { (hue, sat, val) = ToHsv(Themes.Parse(text)); Show(fromHex: true); TryOn(); } catch (FormatException) { }
        }
        hex.TextChanged += (_, _) => { if (hex.IsKeyboardFocused) FromBox(); };
        square.SizeChanged += (_, _) => Show();
        hueBar.SizeChanged += (_, _) => Show();

        var result = ShowOverlay(close =>
        {
            var panel = new StackPanel();
            panel.Children.Add(Heading("Highlight colour", MessageBoxImage.None));
            square.Margin = new Thickness(0, 14, 0, 0);
            panel.Children.Add(square);
            panel.Children.Add(hueBar);
            panel.Children.Add(hexRow);
            panel.Children.Add(ButtonRow(new (string, Func<object?>, bool)[] { ("Cancel", () => null, true), ("Use this colour", () => Hex(), false) }, close));
            Show();
            return panel;
        }, 420) as string;

        if (result == null) Themes.Apply(Themes.Current.Name, before, animate: false);
        return result;
    }

    static Color FromHsv(double h, double s, double v)
    {
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;
        var (r, g, b) = (h % 360) switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        byte B(double f) => (byte)Math.Round((f + m) * 255);
        return Color.FromRgb(B(r), B(g), B(b));
    }

    static (double H, double S, double V) ToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        var h = d == 0 ? 0 : max == r ? 60 * ((g - b) / d % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
        return (h, max == 0 ? 0 : d / max, max);
    }
}
