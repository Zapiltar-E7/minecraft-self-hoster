using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MinecraftHost.Core;

namespace MinecraftHost;

/// <summary>
/// Everything that used to be a pop-up window shows as a panel over the main window instead
/// (only the Windows file and folder pickers are separate windows). Panels wait for an answer the same
/// way a dialog does, by running a nested message loop, so callers stay simple: <c>if (Ask(...) == Yes)</c>.
/// Long jobs (export / import) show their progress in a bar at the bottom of the window.
/// </summary>
public partial class MainWindow
{
    sealed class OverlayEntry
    {
        public FrameworkElement Content = null!;
        public double Width;
        public object? Result;
        public readonly DispatcherFrame Frame = new();
        public IInputElement? FocusBefore;
    }

    readonly Stack<OverlayEntry> _overlays = new();

    /// <summary>
    /// Shows a panel over the window and waits until it calls the close action given to <paramref name="build"/>.
    /// Returns what was passed to that action.
    /// </summary>
    object? ShowOverlay(Func<Action<object?>, FrameworkElement> build, double width = 480)
    {
        var entry = new OverlayEntry { Width = width, FocusBefore = Keyboard.FocusedElement };
        entry.Content = build(result =>
        {
            entry.Result = result;
            entry.Frame.Continue = false;
        });
        _overlays.Push(entry);
        ShowTopOverlay();
        Dispatcher.PushFrame(entry.Frame);   // returns once the panel is closed
        _overlays.Pop();
        ShowTopOverlay();
        if (_overlays.Count == 0) entry.FocusBefore?.Focus();
        return entry.Result;
    }

    void ShowTopOverlay()
    {
        if (_overlays.Count == 0)
        {
            OverlayHost.Content = null;
            Overlay.Visibility = Visibility.Collapsed;
            return;
        }
        var top = _overlays.Peek();
        OverlayHost.Content = top.Content;
        OverlayCard.MaxWidth = top.Width;
        var opening = Overlay.Visibility != Visibility.Visible;
        Overlay.Visibility = Visibility.Visible;
        if (opening) Motion.PanelIn(Overlay, OverlayCard);
        else Motion.FadeIn(OverlayHost, 140);   // one panel replaced by another
        // focus the main button (or the first control) once the panel is laid out
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            var main = FindDefault(top.Content);
            if (main != null) main.Focus();
            else top.Content.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        });
    }

    static Control? FindDefault(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBox box && box.IsEnabled) return box;     // an input box takes focus first
            if (child is Button { IsDefault: true } b) return b;
            if (FindDefault(child) is { } found) return found;
        }
        return null;
    }

    // ------------------------------------------------------------ messages

    /// <summary>A message or question in the window. Esc / Cancel / No give the "no" answer.</summary>
    MessageBoxResult Ask(string text, string title, MessageBoxButton buttons, MessageBoxImage icon = MessageBoxImage.Question)
    {
        (string Label, MessageBoxResult Result)[] options = buttons switch
        {
            MessageBoxButton.OKCancel => new[] { ("Cancel", MessageBoxResult.Cancel), ("OK", MessageBoxResult.OK) },
            MessageBoxButton.YesNo => new[] { ("No", MessageBoxResult.No), ("Yes", MessageBoxResult.Yes) },
            MessageBoxButton.YesNoCancel => new[] { ("Cancel", MessageBoxResult.Cancel), ("No", MessageBoxResult.No), ("Yes", MessageBoxResult.Yes) },
            _ => new[] { ("OK", MessageBoxResult.OK) },
        };
        var escape = buttons switch
        {
            MessageBoxButton.YesNo => MessageBoxResult.No,
            MessageBoxButton.OK => MessageBoxResult.OK,
            _ => MessageBoxResult.Cancel,
        };

        var result = ShowOverlay(close =>
        {
            var panel = new StackPanel();
            panel.Children.Add(Heading(title, icon));
            panel.Children.Add(new TextBlock
            {
                Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0),
                Foreground = (Brush)FindResource("Text"), LineHeight = 19,
            });
            panel.Children.Add(ButtonRow(options.Select(o => (o.Label, (Func<object?>)(() => o.Result), o.Result == escape)).ToArray(), close));
            return panel;
        });
        return result is MessageBoxResult r ? r : escape;
    }

    /// <summary>For unexpected errors (App's handler).</summary>
    public void ShowError(string message) => Ask(message, "Something went wrong", MessageBoxButton.OK, MessageBoxImage.Error);

    /// <summary>A one-line text input in the window. Returns null if cancelled.</summary>
    string? Prompt(string title, string prompt, string initial, string okLabel = "Save") =>
        ShowOverlay(close =>
        {
            var box = new TextBox { Style = (Style)FindResource("Input"), FontFamily = (FontFamily)FindResource("Mono"), Text = initial };
            box.Loaded += (_, _) => box.SelectAll();
            var panel = new StackPanel();
            panel.Children.Add(Heading(title, MessageBoxImage.None));
            panel.Children.Add(new TextBlock
            {
                Text = prompt, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(0, 6, 0, 14),
            });
            panel.Children.Add(box);
            panel.Children.Add(ButtonRow(new (string, Func<object?>, bool)[] { ("Cancel", () => null, true), (okLabel, () => box.Text, false) }, close));
            return panel;
        }) as string;
    FrameworkElement Heading(string title, MessageBoxImage icon)
    {
        var row = new DockPanel();
        var (glyph, color) = icon switch
        {
            MessageBoxImage.Warning => ("", "Warn"),
            MessageBoxImage.Error => ("", "Bad"),
            MessageBoxImage.Information => ("", "Accent"),
            _ => ((string?)null, (string?)null),
        };
        if (glyph != null)
        {
            var g = new TextBlock
            {
                Text = glyph, FontFamily = (FontFamily)FindResource("Icons"), FontSize = 18, Margin = new Thickness(0, 2, 10, 0),
                VerticalAlignment = VerticalAlignment.Top,
            };
            g.SetResourceReference(TextBlock.ForegroundProperty, color!);
            DockPanel.SetDock(g, Dock.Left);
            row.Children.Add(g);
        }
        row.Children.Add(new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        return row;
    }

    /// <summary>Right-aligned buttons; the last one is the main (accent, Enter) button, <c>IsCancel</c> marks the Esc one.</summary>
    FrameworkElement ButtonRow((string Label, Func<object?> Result, bool IsCancel)[] buttons, Action<object?> close)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        for (var i = 0; i < buttons.Length; i++)
        {
            var (label, result, isCancel) = buttons[i];
            var main = i == buttons.Length - 1;
            var button = new Button
            {
                Content = label, MinWidth = 80, IsDefault = main, IsCancel = isCancel,
                Margin = new Thickness(0, 0, main ? 0 : 8, 0),
                FontWeight = main ? FontWeights.SemiBold : FontWeights.Normal,
            };
            if (main) button.Style = (Style)FindResource("AccentButton");
            button.Click += (_, _) => close(result());
            row.Children.Add(button);
        }
        return row;
    }

    // ------------------------------------------------------------ long jobs at the bottom of the window

    CancellationTokenSource? _taskCts;
    double _taskFraction;
    bool _closeAfterTask;

    /// <summary>Runs a job with the progress bar along the bottom. The rest of the window stays usable.</summary>
    async Task<(bool Ok, bool Cancelled, Exception? Error)> RunTaskAsync(Func<IProgress<TransferProgress>, CancellationToken, Task> work)
    {
        _taskCts = new CancellationTokenSource();
        BtnTaskCancel.IsEnabled = true;
        SetTaskProgress(new TransferProgress("Starting...", 0, ""));
        TaskBar.Visibility = Visibility.Visible;
        _busy++;
        UpdateView();
        var progress = new Progress<TransferProgress>(SetTaskProgress);
        (bool, bool, Exception?) outcome;
        try
        {
            await work(progress, _taskCts.Token);
            outcome = (true, false, null);
        }
        catch (OperationCanceledException) when (_taskCts.IsCancellationRequested) { outcome = (false, true, null); }
        catch (Exception ex) { outcome = (false, false, ex); }
        finally
        {
            _taskCts.Dispose();
            _taskCts = null;
            _busy--;
            TaskBar.Visibility = Visibility.Collapsed;
            UpdateView();
        }
        if (_closeAfterTask) _ = Dispatcher.BeginInvoke(Close);
        return outcome;
    }

    void SetTaskProgress(TransferProgress p)
    {
        TaskStage.Text = _taskCts?.IsCancellationRequested == true ? "Cancelling..." : p.Stage;
        TaskDetail.Text = p.Detail;
        _taskFraction = Math.Clamp(p.Fraction, 0, 1);
        TaskFill.Width = _taskFraction * TaskTrack.ActualWidth;
    }

    void TaskTrack_SizeChanged(object sender, SizeChangedEventArgs e) => TaskFill.Width = _taskFraction * TaskTrack.ActualWidth;

    void BtnTaskCancel_Click(object sender, RoutedEventArgs e)
    {
        if (_taskCts == null || _taskCts.IsCancellationRequested) return;
        _taskCts.Cancel();
        BtnTaskCancel.IsEnabled = false;
        TaskStage.Text = "Cancelling...";
    }
}
