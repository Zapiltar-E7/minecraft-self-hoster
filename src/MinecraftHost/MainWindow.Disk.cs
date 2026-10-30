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
        // cancelled when the panel closes, not when a question (Delete?) covers it for a moment
        var cts = new CancellationTokenSource();
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

            void Measure() => _ = Task.Run(() => DiskUsage.Measure(cts.Token), cts.Token).ContinueWith(t => Dispatcher.BeginInvoke(() =>
            {
                rows.Children.Clear();
                if (t.IsCanceled || t.IsFaulted) { status.Text = t.IsFaulted ? "Couldn't measure: " + t.Exception?.InnerException?.Message : ""; return; }
                var report = t.Result;
                status.Text = report.DriveFree >= 0
                    ? $"{Paths.ServerName} uses {DiskUsage.Format(report.Total)}. Drive {report.Drive} has {DiskUsage.Format(report.DriveFree)} free of {DiskUsage.Format(report.DriveSize)}."
                    : $"{Paths.ServerName} uses {DiskUsage.Format(report.Total)}.";
                if (report.DriveSize > 0) rows.Children.Add(Bar(1 - report.DriveFree / (double)report.DriveSize, "Track", 8, new Thickness(0, 0, 0, 14)));
                var biggest = report.Items.Count > 0 ? report.Items[0].Bytes : 1;
                foreach (var item in report.Items) rows.Children.Add(DiskRow(item, biggest, status, Measure));
                Motion.FadeIn(rows);
            }));
            Measure();
            return panel;
        }, 620);
        cts.Cancel();
    }

    FrameworkElement DiskRow(DiskUsage.Item item, long biggest, TextBlock status, Action remeasure)
    {
        Button? delete = null;
        if (item.Delete != null)
        {
            var stopped = _state == ServerState.Stopped && _busy == 0;
            delete = IconButton("\uE74D", stopped ? "Delete" : "Stop the server first", async (_, _) =>
            {
                var warn = item.Delete.Length > 0;
                if (Ask($"Delete {item.Name} ({DiskUsage.Format(item.Bytes)})?" + (warn ? "\n\n" + item.Delete : ""), "Delete",
                        MessageBoxButton.YesNo, warn ? MessageBoxImage.Warning : MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                status.Text = $"Deleting {item.Name}...";
                try
                {
                    await Task.Run(() => DiskUsage.Delete(item.Path));
                    Log.Host($"Deleted {item.Name} ({DiskUsage.Format(item.Bytes)}): {item.Path}");
                }
                catch (Exception ex)
                {
                    Log.Host($"Couldn't fully delete {item.Name}: {ex.Message}");
                    Ask($"Couldn't delete all of {item.Name}: {ex.Message}", "Delete", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                remeasure();
            });
            delete.IsEnabled = stopped;
            System.Windows.Automation.AutomationProperties.SetName(delete, "Delete " + item.Name);
            ToolTipService.SetShowOnDisabled(delete, true);
            delete.SetResourceReference(Control.ForegroundProperty, "Bad");
            delete.VerticalAlignment = VerticalAlignment.Top;
            delete.Margin = new Thickness(6, 0, 0, 0);
        }
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
        if (delete != null) row.Children.Add(delete);
        else row.Children.Add(new FrameworkElement { Width = 38 });   // keeps the bars lined up with the rows that have one
        DockPanel.SetDock(row.Children[^1], Dock.Right);
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
