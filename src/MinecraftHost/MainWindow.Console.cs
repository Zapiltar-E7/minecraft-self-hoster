using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using MinecraftHost.Core;

namespace MinecraftHost;

/// <summary>One line of the server log, coloured by what it is.</summary>
public sealed class LogLine
{
    public LogLine(string text, string kind) { Text = text; Kind = kind; }
    public string Text { get; }
    /// <summary>error, warn, chat, join, host, cmd or info.</summary>
    public string Kind { get; }
    // the theme's brushes, so the log follows a theme change
    public Brush Brush => (Brush)Application.Current.Resources[Kind switch
    {
        "error" => "Bad",
        "warn" => "Warn",
        "chat" => "Text",
        "join" or "host" or "cmd" => "Accent",
        _ => "ConsoleText",
    }];
    public override string ToString() => Text;
}

/// <summary>
/// The Logs tab console: coloured lines, filters (all / chat / problems / app), search, copy, and a command box with
/// history (Up / Down) and Tab completion of commands and player names.
/// </summary>
public partial class MainWindow
{
    const int LogLimit = 6000;
    readonly ObservableCollection<LogLine> _log = new();
    ICollectionView? _logView;
    ScrollViewer? _logScroll;
    readonly List<string> _commandHistory = new();
    int _historyAt = -1;
    string? _typedBeforeHistory;

    static readonly Regex ChatRx = new(@"\]: (\[Not Secure\] )?<[^>]+> ", RegexOptions.Compiled);
    // Paper prints "[12:00:00 WARN]:", vanilla and mod loaders "[12:00:00] [Server thread/WARN]:"
    static readonly Regex ErrorRx = new(@"[/ ](ERROR|FATAL|SEVERE)\]|^\s*at [\w.$]+\(|^\S*Exception\b|ERROR:", RegexOptions.Compiled);
    static readonly Regex WarnRx = new(@"[/ ]WARN(ING)?\]", RegexOptions.Compiled);

    static readonly string[] KnownCommands =
    {
        "advancement", "attribute", "ban", "ban-ip", "banlist", "bossbar", "clear", "clone", "data", "datapack", "defaultgamemode",
        "deop", "difficulty", "effect", "enchant", "execute", "experience", "fill", "forceload", "function", "gamemode", "gamerule",
        "give", "help", "item", "kick", "kill", "list", "locate", "loot", "me", "msg", "op", "pardon", "pardon-ip", "particle",
        "place", "playsound", "plugins", "recipe", "reload", "save-all", "save-off", "save-on", "say", "schedule", "scoreboard",
        "seed", "setblock", "setworldspawn", "spawnpoint", "spectate", "spreadplayers", "stop", "stopsound", "summon", "tag", "team",
        "teleport", "tell", "tellraw", "tick", "time", "title", "tp", "tps", "mspt", "spark", "trigger", "version", "weather",
        "whitelist", "worldborder", "xp", "chunky", "bluemap", "lp", "mv", "co",
    };

    static string KindOf(string line)
    {
        if (line.StartsWith("[host]")) return line.Contains("ERROR") ? "error" : "host";
        if (line.StartsWith("> ")) return "cmd";
        if (ErrorRx.IsMatch(line)) return "error";
        if (WarnRx.IsMatch(line)) return "warn";
        if (ChatRx.IsMatch(line)) return "chat";
        if (JoinedRx.IsMatch(line) || LeftRx.IsMatch(line)) return "join";
        return "info";
    }

    void EnsureLogView()
    {
        if (_logView != null) return;
        _logView = CollectionViewSource.GetDefaultView(_log);
        LogList.ItemsSource = _log;
        LogList.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, (_, _) => CopyLogSelection()));
    }

    void AddLogLine(string text)
    {
        EnsureLogView();
        _log.Add(new LogLine(text, KindOf(text)));
    }

    void TrimLog()
    {
        if (_log.Count <= LogLimit) return;
        // drop the oldest chunk in one go: one reset instead of thousands of removals
        var keep = _log.Skip(_log.Count - LogLimit + 1000).ToList();
        var selected = LogList.SelectedItems.Count;
        _log.Clear();
        foreach (var l in keep) _log.Add(l);
        if (selected > 0) LogList.UnselectAll();
    }

    void ClearLog()
    {
        _log.Clear();
        UpdateLogEmpty();
    }

    ScrollViewer? LogScroll => _logScroll ??= FindChild<ScrollViewer>(LogList);

    static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) return t;
            if (FindChild<T>(c) is { } found) return found;
        }
        return null;
    }

    /// <summary>The log is scrolled to the bottom (or isn't showing), so new lines should keep it there.</summary>
    bool LogAtEnd() => LogScroll is not { } sv || !LogList.IsVisible || sv.VerticalOffset >= sv.ScrollableHeight - 4;

    void ScrollLogToEnd()
    {
        if (LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[^1]);
        LogScroll?.ScrollToEnd();
    }

    // ------------------------------------------------------------ filters and search

    void LogFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (LogList == null) return;
        EnsureLogView();
        if (sender == TxtLogSearch) TxtLogSearchHint.Visibility = TxtLogSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var search = TxtLogSearch.Text.Trim();
        var kinds = LogChat.IsChecked == true ? new[] { "chat", "join" }
            : LogProblems.IsChecked == true ? new[] { "warn", "error" }
            : LogApp.IsChecked == true ? new[] { "host", "cmd" }
            : null;
        _logView!.Filter = kinds == null && search.Length == 0 ? null
            : o => o is LogLine l && (kinds == null || kinds.Contains(l.Kind)) && (search.Length == 0 || l.Text.Contains(search, StringComparison.OrdinalIgnoreCase));
        UpdateLogEmpty();
        ScrollLogToEnd();
    }

    void UpdateLogEmpty() => LogEmpty.Visibility = _logView?.Filter != null && LogList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    // ------------------------------------------------------------ copy

    void CopyLogSelection()
    {
        var lines = LogList.SelectedItems.Cast<LogLine>().OrderBy(l => _log.IndexOf(l)).Select(l => l.Text).ToList();
        if (lines.Count > 0) Clipboard.SetText(string.Join(Environment.NewLine, lines));
    }

    void LogCopy_Click(object sender, RoutedEventArgs e) => CopyLogSelection();
    void LogSelectAll_Click(object sender, RoutedEventArgs e) => LogList.SelectAll();

    // ------------------------------------------------------------ command history and completion

    void RememberCommand(string cmd)
    {
        _commandHistory.Remove(cmd);
        _commandHistory.Add(cmd);
        if (_commandHistory.Count > 100) _commandHistory.RemoveAt(0);
        _historyAt = -1;
        _completion = null;
    }

    (string Prefix, List<string> Options, int Index)? _completion;

    void TxtCommand_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Up or Key.Down && _commandHistory.Count > 0)
        {
            if (_historyAt == -1) { _typedBeforeHistory = TxtCommand.Text; _historyAt = _commandHistory.Count; }
            _historyAt = Math.Clamp(_historyAt + (e.Key == Key.Up ? -1 : 1), 0, _commandHistory.Count);
            TxtCommand.Text = _historyAt == _commandHistory.Count ? _typedBeforeHistory ?? "" : _commandHistory[_historyAt];
            TxtCommand.CaretIndex = TxtCommand.Text.Length;
            e.Handled = true;
        }
        else if (e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;   // stay in the box
            Complete();
        }
        else if (e.Key is not (Key.LeftShift or Key.RightShift)) _completion = null;
    }

    /// <summary>Completes the word before the caret: a command first, then player names. Tab again cycles the matches.</summary>
    void Complete()
    {
        var text = TxtCommand.Text;
        if (_completion is not { } c)
        {
            var start = text.LastIndexOf(' ') + 1;
            var word = text[start..].TrimStart('/');
            var first = start == 0;
            var pool = first
                ? KnownCommands.AsEnumerable()
                : _players.Concat(PlayerHistory.Recent(50).Select(p => p.Name)).Concat(new[] { "@a", "@p", "@s", "@r", "@e" });
            var options = pool.Where(o => o.StartsWith(word, StringComparison.OrdinalIgnoreCase))
                              .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(o => o, StringComparer.OrdinalIgnoreCase).ToList();
            if (options.Count == 0) return;
            c = (text[..start], options, -1);
        }
        var next = (c.Index + 1) % c.Options.Count;
        _completion = (c.Prefix, c.Options, next);
        TxtCommand.Text = c.Prefix + c.Options[next] + (c.Options.Count == 1 ? " " : "");
        TxtCommand.CaretIndex = TxtCommand.Text.Length;
        TxtCommandHint.Visibility = Visibility.Collapsed;
    }
}
