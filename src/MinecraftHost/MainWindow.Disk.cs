using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MinecraftHost.Core;

namespace MinecraftHost;

/// <summary>Tools → Disk space: what's using room for the current server, largest first, with a bar each.</summary>
public partial class MainWindow
{
    void BtnDisk_Click(object sender, RoutedEventArgs e)
    {
        ShowOverlay(close =>
        {
            var panel = new StackPanel();
            panel.Children.Add(Heading("Disk space", MessageBoxImage.None));
            var status = new TextBlock { Text = "Measuring... (big worlds take a little while)", Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
            status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            panel.Children.Add(status);
            var rows = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            panel.Children.Add(rows);
            panel.Children.Add(ButtonRow(new (string, Func<object?>, bool)[] { ("Close", () => null, false) }, close));

            var cts = new CancellationTokenSource();
            panel.Unloaded += (_, _) => cts.Cancel();
            _ = Task.Run(() => DiskUsage.Measure(cts.Token), cts.Token).ContinueWith(t => Dispatcher.BeginInvoke(() =>
            {
                if (t.IsCanceled || t.IsFaulted) { status.Text = t.IsFaulted ? "Couldn't measure: " + t.Exception?.InnerException?.Message : ""; return; }
                var report = t.Result;
                status.Text = report.DriveFree >= 0
                    ? $"{Paths.ServerName} uses {DiskUsage.Format(report.Total)}. Drive {report.Drive} has {DiskUsage.Format(report.DriveFree)} free of {DiskUsage.Format(report.DriveSize)}."
                    : $"{Paths.ServerName} uses {DiskUsage.Format(report.Total)}.";
                if (report.DriveSize > 0) rows.Children.Add(Bar(1 - report.DriveFree / (double)report.DriveSize, "Track", 8, new Thickness(0, 0, 0, 14)));
                var biggest = report.Items.Count > 0 ? report.Items[0].Bytes : 1;
                foreach (var item in report.Items) rows.Children.Add(DiskRow(item, biggest));
                Motion.FadeIn(rows);
            }));
            return panel;
        }, 620);
    }

    FrameworkElement DiskRow(DiskUsage.Item item, long biggest)
    {
        var open = IconButton("", "Show in File Explorer", (_, _) =>
        {
            var arg = File.Exists(item.Path) ? $"/select,\"{item.Path}\"" : $"\"{item.Path}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", arg) { UseShellExecute = true });
        });
        var size = new TextBlock { Text = DiskUsage.Format(item.Bytes), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12, 0, 0, 0) };
        DockPanel.SetDock(size, Dock.Right);
        var title = new DockPanel();
        title.Children.Add(size);
        var name = new StackPanel { Orientation = Orientation.Horizontal };
        name.Children.Add(new TextBlock { Text = item.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        if (item.Removable)
        {
            var tag = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(7, 0, 7, 1), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            tag.Background = new SolidColorBrush(Themes.WithAlpha(Themes.Get("Accent"), 0x33));
            var t = new TextBlock { Text = "Safe to clear", FontSize = 10.5, FontWeight = FontWeights.SemiBold };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
            tag.Child = t;
            name.Children.Add(tag);
        }
        title.Children.Add(name);
        var text = new StackPanel { Children = { title } };
        if (item.Detail.Length > 0)
        {
            var d = new TextBlock { Text = item.Detail, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 0) };
            d.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            text.Children.Add(d);
        }
        text.Children.Add(Bar(item.Bytes / (double)Math.Max(1, biggest), "Accent", 5, new Thickness(0, 6, 0, 0)));
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        open.VerticalAlignment = VerticalAlignment.Top;
        row.Children.Add(open);
        row.Children.Add(text);
        return row;
    }

    static FrameworkElement Bar(double fraction, string brush, double height, Thickness margin)
    {
        var track = new Grid { Height = height, Margin = margin };
        var bg = new Border { CornerRadius = new CornerRadius(height / 2) };
        bg.SetResourceReference(Border.BackgroundProperty, "Field");
        var fill = new Border { CornerRadius = new CornerRadius(height / 2), HorizontalAlignment = HorizontalAlignment.Left };
        fill.SetResourceReference(Border.BackgroundProperty, brush);
        track.Children.Add(bg);
        track.Children.Add(fill);
        track.SizeChanged += (_, _) => fill.Width = Math.Max(height, track.ActualWidth * Math.Clamp(fraction, 0, 1));
        return track;
    }
}
