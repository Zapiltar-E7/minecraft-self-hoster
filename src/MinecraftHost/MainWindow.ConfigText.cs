using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MinecraftHost.Core;

namespace MinecraftHost;

/// <summary>
/// The config editor's Text view: AvalonEdit with code colours (ConfigHighlighting), line numbers and two-space tabs
/// (YAML doesn't allow tab characters), plus the find bar above it.
/// </summary>
public partial class MainWindow
{
    readonly FindMarker _findMarker = new();
    readonly List<int> _findHits = new();
    int _findAt = -1;

    void InitConfigText()
    {
        ConfigEditor.Options.ConvertTabsToSpaces = true;
        ConfigEditor.Options.IndentationSize = 2;
        ConfigEditor.Options.EnableHyperlinks = false;
        ConfigEditor.Options.EnableEmailHyperlinks = false;
        ConfigEditor.TextArea.TextView.LineTransformers.Add(_findMarker);
        StyleConfigText();
        Themes.Changed += () =>
        {
            StyleConfigText();
            if (_docFile != null) ConfigEditor.SyntaxHighlighting = ConfigHighlighting.For(_docFile.ZipEntry ?? _docFile.FullPath);
        };
    }

    /// <summary>The editor's selection, caret and match colours, from the theme.</summary>
    void StyleConfigText()
    {
        Brush B(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
        var accent = Themes.Get("Accent");
        ConfigEditor.TextArea.SelectionBrush = B(Themes.WithAlpha(accent, 0x59));
        ConfigEditor.TextArea.SelectionBorder = null;
        ConfigEditor.TextArea.SelectionForeground = null;
        ConfigEditor.TextArea.Caret.CaretBrush = B(Themes.Get("Text"));
        _findMarker.Brush = B(Themes.WithAlpha(Themes.Get("Warn"), 0x55));
        ConfigEditor.TextArea.TextView.Redraw();
    }

    void TxtFind_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        TxtFindHint.Visibility = TxtFind.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateFind(jump: true);
    }

    void TxtFind_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { GoFind(Keyboard.Modifiers == ModifierKeys.Shift ? -1 : 1); e.Handled = true; }
        else if (e.Key == Key.Escape) { TxtFind.Text = ""; ConfigEditor.Focus(); e.Handled = true; }
    }

    void BtnFindNext_Click(object sender, RoutedEventArgs e) => GoFind(1);
    void BtnFindPrev_Click(object sender, RoutedEventArgs e) => GoFind(-1);

    /// <summary>Finds every match (ignoring case); <paramref name="jump"/>: go to the first one from the caret on.</summary>
    void UpdateFind(bool jump)
    {
        var q = TxtFind.Text;
        _findMarker.Text = q;
        _findHits.Clear();
        var text = ConfigEditor.Text;
        if (q.Length > 0)
            for (var i = text.IndexOf(q, StringComparison.OrdinalIgnoreCase); i >= 0; i = text.IndexOf(q, i + q.Length, StringComparison.OrdinalIgnoreCase))
                _findHits.Add(i);
        ConfigEditor.TextArea.TextView.Redraw();
        if (jump)
        {
            _findAt = -1;
            if (_findHits.Count > 0)
            {
                var from = ConfigEditor.SelectionLength > 0 ? ConfigEditor.SelectionStart : ConfigEditor.CaretOffset;
                var first = _findHits.FindIndex(h => h >= from);
                Show(first < 0 ? 0 : first);
            }
        }
        else if (_findAt >= _findHits.Count) _findAt = _findHits.Count - 1;
        FindCount.Text = q.Length == 0 ? "" : _findHits.Count == 0 ? "No matches" : _findAt >= 0 ? $"{_findAt + 1} of {_findHits.Count}" : $"{_findHits.Count} found";
    }

    void GoFind(int step)
    {
        if (_findHits.Count == 0) return;
        Show(((_findAt < 0 ? (step > 0 ? -1 : 0) : _findAt) + step + _findHits.Count) % _findHits.Count);
        FindCount.Text = $"{_findAt + 1} of {_findHits.Count}";
    }

    /// <summary>Selects a match and scrolls it into view (the focus stays in the find box).</summary>
    void Show(int index)
    {
        _findAt = index;
        var offset = _findHits[index];
        ConfigEditor.Select(offset, TxtFind.Text.Length);
        var at = ConfigEditor.Document.GetLocation(offset);
        ConfigEditor.ScrollTo(at.Line, at.Column);
    }
}
