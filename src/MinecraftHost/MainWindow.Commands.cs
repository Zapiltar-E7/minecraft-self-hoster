using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MinecraftHost.Core;

namespace MinecraftHost;

/// <summary>
/// Server → Commands: the Custom Commands plugin's commands, with an editor for each. Saving changes only that command's
/// lines in its config.yml (after a backup) and, while the server runs, reloads the plugin.
/// </summary>
public partial class MainWindow
{
    void RefreshCommands()
    {
        if (CommandRows == null) return;
        CommandRows.Children.Clear();
        if (!CustomCommands.Installed)
        {
            CommandRows.Children.Add(new SettingRow { Icon = "", Title = "Custom Commands isn't installed", Hint = "This section edits that plugin's commands." });
            return;
        }
        List<CustomCommand> commands;
        try { commands = CustomCommands.Read(); }
        catch (Exception ex) { CommandRows.Children.Add(new SettingRow { Icon = "", Title = "Couldn't read its config.yml", Hint = ex.Message }); return; }
        foreach (var c in commands)
        {
            var does = c.Commands.Count > 0 ? $"runs {c.Commands.Count} command{(c.Commands.Count == 1 ? "" : "s")} as {(c.Type == "console" ? "the console" : "the player")}"
                : c.Message.Count > 0 ? "shows a message" : "does nothing yet";
            var hint = (c.Description.Length > 0 ? c.Description + " · " : "") + does
                       + (c.Subcommands.Count > 0 ? $" · {c.Subcommands.Count} subcommand{(c.Subcommands.Count == 1 ? "" : "s")}" : "")
                       + (c.Cooldown > 0 ? $" · {c.Cooldown} s cooldown" : "");
            var toggle = new CheckBox { Style = (Style)FindResource("RowSwitch"), IsChecked = c.Enabled, ToolTip = c.Enabled ? "On" : "Off" };
            System.Windows.Automation.AutomationProperties.SetName(toggle, $"/{c.Name} on");
            var cmd = c;
            RoutedEventHandler flip = (_, _) => { cmd.Enabled = toggle.IsChecked == true; SaveCommand(cmd.Name, cmd); };
            toggle.Checked += flip;
            toggle.Unchecked += flip;
            var edit = SmallIcon("", $"Edit /{c.Name}", true, () => EditCommand(cmd));
            var delete = SmallIcon("", $"Delete /{c.Name}", true, () =>
            {
                if (Ask($"Delete /{cmd.Name}?\n\nIt's taken out of the Custom Commands config (a backup is kept).", "Commands", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                SaveCommand(cmd.Name, null);
            });
            delete.SetResourceReference(Control.ForegroundProperty, "Bad");
            delete.Margin = new Thickness(8, 0, 16, 0);
            CommandRows.Children.Add(new SettingRow
            {
                Icon = "",
                Title = "/" + c.Name + (c.Aliases.Count > 0 ? "   (" + string.Join(", ", c.Aliases.Select(a => "/" + a)) + ")" : ""),
                Hint = hint,
                Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { edit, delete, toggle } },
            });
        }
        var create = new Button { Padding = new Thickness(12, 6, 12, 6), FontWeight = FontWeights.Normal, Content = IconText("", "Create...") };
        System.Windows.Automation.AutomationProperties.SetName(create, "Create a command");
        create.Click += (_, _) => EditCommand(null);
        CommandRows.Children.Add(new SettingRow { Icon = "", Title = "Create a command", Hint = "A message, a link, or commands to run", Content = create });
    }

    /// <summary>Writes one command (null deletes it), then reloads the plugin if the server runs.</summary>
    void SaveCommand(string? oldName, CustomCommand? command)
    {
        try
        {
            CustomCommands.Save(oldName, command);
            Log.Host(command == null ? $"Custom Commands: /{oldName} deleted." : $"Custom Commands: /{command.Name} saved.");
            if (_server?.IsRunning == true && _state == ServerState.Running) _server.Send("customcommands reload");
        }
        catch (Exception ex) { Ask("Couldn't save: " + ex.Message, "Commands", MessageBoxButton.OK, MessageBoxImage.Warning); }
        Dispatcher.BeginInvoke(RefreshCommands);
    }

    // ------------------------------------------------------------ the editor

    /// <summary>A several-lines box: one message line or one command per line.</summary>
    TextBox LinesBox(IEnumerable<string> lines, string name, double height = 96)
    {
        var box = new TextBox
        {
            Style = (Style)FindResource("Input"), Text = string.Join(Environment.NewLine, lines), AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
            Height = height, Padding = new Thickness(0, 6, 0, 6), VerticalContentAlignment = VerticalAlignment.Top,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = (FontFamily)FindResource("Mono"), FontSize = 12,
        };
        System.Windows.Automation.AutomationProperties.SetName(box, name);
        return box;
    }

    static List<string> LinesOf(TextBox box) => box.Text.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).ToList();

    TextBox OneLine(string text, string name, double width = double.NaN)
    {
        var box = new TextBox { Style = (Style)FindResource("Input"), Text = text, Width = width, HorizontalAlignment = HorizontalAlignment.Left };
        System.Windows.Automation.AutomationProperties.SetName(box, name);
        return box;
    }

    /// <summary>Colour and style buttons for a message box (Custom Commands' &amp; codes), and its link syntax.</summary>
    WrapPanel MessageTools(TextBox box)
    {
        void Put(string before, string after = "")
        {
            var start = box.SelectionStart;
            var picked = box.SelectedText;
            box.SelectedText = before + picked + after;
            box.CaretIndex = start + before.Length + picked.Length + after.Length;
            box.Focus();
        }
        var tools = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        foreach (var (code, name, hex) in Motd.Colors)
        {
            var chip = new Button { Width = 18, Height = 18, Padding = new Thickness(0), Margin = new Thickness(0, 0, 3, 3), ToolTip = name, Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)) };
            chip.SetResourceReference(Control.BorderBrushProperty, "FieldBorder");
            System.Windows.Automation.AutomationProperties.SetName(chip, name);
            chip.Click += (_, _) => Put("&" + code, box.SelectionLength > 0 ? "&r" : "");
            tools.Children.Add(chip);
        }
        foreach (var (code, label, tip) in new[] { ('l', "B", "Bold"), ('o', "I", "Italic"), ('n', "U", "Underline"), ('m', "S", "Strikethrough"), ('r', "×", "Reset") })
        {
            var b = new Button { Content = label, Width = 22, Height = 18, Padding = new Thickness(0), Margin = new Thickness(code == 'l' ? 6 : 0, 0, 3, 3), FontSize = 11, ToolTip = tip,
                FontWeight = code == 'l' ? FontWeights.Bold : FontWeights.Normal, FontStyle = code == 'o' ? FontStyles.Italic : FontStyles.Normal };
            System.Windows.Automation.AutomationProperties.SetName(b, tip);
            b.Click += (_, _) => Put("&" + code, box.SelectionLength > 0 && code != 'r' ? "&r" : "");
            tools.Children.Add(b);
        }
        var link = new Button { Content = IconText("", "Link"), Height = 20, Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(6, 0, 0, 3), FontSize = 11.5, FontWeight = FontWeights.Normal,
            ToolTip = "A clickable link: (Text)[<https://...>]" };
        System.Windows.Automation.AutomationProperties.SetName(link, "Insert a link");
        link.Click += (_, _) =>
        {
            var url = Prompt("Link", "The address it opens (https://...). The selected text becomes what players click.", "https://");
            if (string.IsNullOrWhiteSpace(url) || url.Trim() == "https://") return;
            var text = box.SelectedText.Length > 0 ? box.SelectedText : "Click here";
            box.SelectedText = $"({text})[<{url.Trim()}>]";
            box.Focus();
        };
        tools.Children.Add(link);
        return tools;
    }

    /// <summary>What the message looks like in chat: colours, styles, and its links as players see them.</summary>
    Border MessagePreview(TextBox box)
    {
        var preview = new TextBlock { TextWrapping = TextWrapping.Wrap, FontFamily = (FontFamily)FindResource("Mono"), FontSize = 12, Foreground = Brushes.White };
        void Draw()
        {
            preview.Inlines.Clear();
            var lines = LinesOf(box);
            if (lines.Count == 0) { preview.Inlines.Add(new System.Windows.Documents.Run("(no message)") { Foreground = Brushes.Gray }); return; }
            for (var n = 0; n < lines.Count; n++)
            {
                if (n > 0) preview.Inlines.Add(new System.Windows.Documents.LineBreak());
                // (Text)[<url>] shows as the text, aqua and underlined
                var line = Regex.Replace(lines[n], @"\((.*?)\)\[<(.*?)>\]", "&b&n$1&r");
                foreach (var p in Announcements.Parse(line))
                {
                    var run = new System.Windows.Documents.Run(p.Text) { Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(Announcements.Hex(p.Color))) };
                    if (p.Bold) run.FontWeight = FontWeights.Bold;
                    if (p.Italic) run.FontStyle = FontStyles.Italic;
                    var deco = new TextDecorationCollection();
                    if (p.Underline) deco.Add(TextDecorations.Underline);
                    if (p.Strike) deco.Add(TextDecorations.Strikethrough);
                    if (deco.Count > 0) run.TextDecorations = deco;
                    preview.Inlines.Add(run);
                }
            }
        }
        box.TextChanged += (_, _) => Draw();
        Draw();
        return new Border { Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1F, 0x22)), CornerRadius = new CornerRadius(7), Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 6, 0, 0), Child = preview };
    }

    void EditCommand(CustomCommand? existing)
    {
        var others = CustomCommands.Read().Where(c => c.Name != existing?.Name).ToList();
        var name = OneLine(existing?.Name ?? "", "Command name", 220);
        var error = new TextBlock { FontSize = 12, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
        error.SetResourceReference(TextBlock.ForegroundProperty, "Bad");
        var description = OneLine(existing?.Description ?? "", "Description");
        var aliases = OneLine(string.Join(", ", existing?.Aliases ?? new()), "Aliases");
        var permission = OneLine(existing?.Permission ?? "", "Permission");
        var cooldown = OneLine((existing?.Cooldown ?? 0).ToString(), "Cooldown in seconds", 90);
        var enabled = Toggle("On", existing?.Enabled ?? true);
        var message = LinesBox(existing?.Message ?? new(), "Message lines");
        var run = LinesBox(existing?.Commands ?? new(), "Commands to run", 80);
        var runAs = new ComboBox { Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
        runAs.Items.Add(new ComboBoxItem { Content = "The player", Tag = "player" });
        runAs.Items.Add(new ComboBoxItem { Content = "The console", Tag = "console" });
        runAs.SelectedIndex = existing?.Type == "console" ? 1 : 0;
        System.Windows.Automation.AutomationProperties.SetName(runAs, "Run them as");
        // a new command's permission follows its name until it's changed by hand
        var permissionTouched = existing != null;
        permission.TextChanged += (_, _) => { if (permission.IsKeyboardFocused) permissionTouched = true; };
        name.TextChanged += (_, _) => { if (!permissionTouched) permission.Text = name.Text.Trim().Length > 0 ? "customcommands." + name.Text.Trim().ToLowerInvariant() : ""; };

        var subs = (existing?.Subcommands ?? new()).Select(s => new CustomSubcommand { Name = s.Name, OriginalName = s.OriginalName, Permission = s.Permission, Message = s.Message.ToList(), Commands = s.Commands.ToList() }).ToList();
        var subRows = new StackPanel();
        void DrawSubs()
        {
            subRows.Children.Clear();
            foreach (var s in subs)
            {
                var sub = s;
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
                var remove = SmallIcon("", $"Remove {sub.Name}", true, () => { subs.Remove(sub); DrawSubs(); });
                remove.SetResourceReference(Control.ForegroundProperty, "Bad");
                DockPanel.SetDock(remove, Dock.Right);
                var edit = SmallIcon("", $"Edit {sub.Name}", true, () => { EditSubcommand(sub, (existing?.Name ?? name.Text.Trim()), subs); DrawSubs(); });
                DockPanel.SetDock(edit, Dock.Right);
                row.Children.Add(remove);
                row.Children.Add(edit);
                row.Children.Add(new TextBlock { Text = $"/{name.Text.Trim()} {sub.Name}", VerticalAlignment = VerticalAlignment.Center, FontFamily = (FontFamily)FindResource("Mono"), FontSize = 12 });
                subRows.Children.Add(row);
            }
            var add = new Button { Content = IconText("", "Add a subcommand"), Padding = new Thickness(10, 4, 10, 4), FontWeight = FontWeights.Normal, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 0) };
            add.Click += (_, _) =>
            {
                var s = new CustomSubcommand();
                if (EditSubcommand(s, name.Text.Trim(), subs)) { subs.Add(s); DrawSubs(); }
            };
            subRows.Children.Add(add);
        }
        DrawSubs();

        string? Problem()
        {
            var n = name.Text.Trim().TrimStart('/').ToLowerInvariant();
            if (!CustomCommands.ValidName(n)) return "A name of lowercase letters, numbers, - and _ (no spaces).";
            if (others.Any(c => c.Name == n || c.Aliases.Contains(n))) return "Another command already uses that name.";
            if (!int.TryParse(cooldown.Text.Trim(), out var cd) || cd < 0) return "The cooldown is a number of seconds (0 for none).";
            return null;
        }
        var result = ShowOverlay(close =>
        {
            var panel = new StackPanel();
            panel.Children.Add(Heading(existing == null ? "Create a command" : "/" + existing.Name, MessageBoxImage.None));
            panel.Children.Add(Label("Name (what players type after the /)", 14));
            panel.Children.Add(name);
            panel.Children.Add(error);
            panel.Children.Add(Label("Description (shown in /help)", 8));
            panel.Children.Add(description);
            panel.Children.Add(Label("Other names (comma separated)"));
            panel.Children.Add(aliases);
            panel.Children.Add(Label("Permission (players need it, e.g. from LuckPerms)"));
            panel.Children.Add(permission);
            panel.Children.Add(Label("Cooldown (seconds)"));
            panel.Children.Add(cooldown);
            panel.Children.Add(enabled);
            panel.Children.Add(Label("Chat message (one line each)", 16));
            panel.Children.Add(MessageTools(message));
            panel.Children.Add(message);
            panel.Children.Add(MessagePreview(message));
            panel.Children.Add(Label("Commands to run (one per line, without the /)", 14));
            panel.Children.Add(run);
            panel.Children.Add(new TextBlock { Text = "{player} is the player's name, {world} their world.", FontSize = 11.5, Foreground = Res("Muted"), Margin = new Thickness(0, 4, 0, 0) });
            panel.Children.Add(Label("Run them as"));
            panel.Children.Add(runAs);
            panel.Children.Add(Label("Subcommands", 16));
            panel.Children.Add(subRows);
            panel.Children.Add(ButtonRow(new (string, Func<object?>, bool)[]
            {
                ("Cancel", () => null, true),
                (existing == null ? "Create" : "Save", () => Problem() is { } p ? (object)("!" + p) : "save", false),
            }, r => { if (r is string s && s.StartsWith('!')) error.Text = s[1..]; else close(r); }));
            return panel;
        }, 560) as string;
        if (result != "save") return;

        var command = new CustomCommand
        {
            Name = name.Text.Trim().TrimStart('/').ToLowerInvariant(),
            Description = description.Text.Trim(),
            Aliases = aliases.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(a => a.TrimStart('/').ToLowerInvariant()).Where(a => a.Length > 0).Distinct().ToList(),
            Permission = permission.Text.Trim(),
            Cooldown = int.Parse(cooldown.Text.Trim()),
            Enabled = enabled.IsChecked == true,
            Message = LinesOf(message),
            Commands = LinesOf(run).Select(c => c.TrimStart('/')).ToList(),
            Type = (string)((ComboBoxItem)runAs.SelectedItem).Tag,
            Subcommands = subs,
        };
        SaveCommand(existing?.Name, command);
    }

    /// <summary>A panel for one subcommand; true when saved (the object is changed in place).</summary>
    bool EditSubcommand(CustomSubcommand sub, string parent, List<CustomSubcommand> all)
    {
        var name = OneLine(sub.Name, "Subcommand name", 200);
        var error = new TextBlock { FontSize = 12, Margin = new Thickness(0, 4, 0, 0) };
        error.SetResourceReference(TextBlock.ForegroundProperty, "Bad");
        var permission = OneLine(sub.Permission.Length > 0 ? sub.Permission : $"customcommands.{parent}.", "Permission");
        var message = LinesBox(sub.Message, "Message lines", 80);
        var run = LinesBox(sub.Commands, "Commands to run", 70);
        var result = ShowOverlay(close =>
        {
            var panel = new StackPanel();
            panel.Children.Add(Heading($"/{parent} {(sub.Name.Length > 0 ? sub.Name : "...")}", MessageBoxImage.None));
            panel.Children.Add(Label("Name (typed after the command)", 14));
            panel.Children.Add(name);
            panel.Children.Add(error);
            panel.Children.Add(Label("Permission", 8));
            panel.Children.Add(permission);
            panel.Children.Add(Label("Chat message", 14));
            panel.Children.Add(MessageTools(message));
            panel.Children.Add(message);
            panel.Children.Add(MessagePreview(message));
            panel.Children.Add(Label("Commands to run (one per line)", 14));
            panel.Children.Add(run);
            panel.Children.Add(ButtonRow(new (string, Func<object?>, bool)[]
            {
                ("Cancel", () => null, true),
                ("Done", () =>
                {
                    var n = name.Text.Trim().ToLowerInvariant();
                    if (!CustomCommands.ValidName(n)) return "!Lowercase letters, numbers, - and _ only.";
                    if (all.Any(s => s != sub && s.Name == n)) return "!There's already a subcommand with that name.";
                    return "ok";
                }, false),
            }, r => { if (r is string s && s.StartsWith('!')) error.Text = s[1..]; else close(r); }));
            return panel;
        }, 520) as string;
        if (result != "ok") return false;
        sub.Name = name.Text.Trim().ToLowerInvariant();
        sub.Permission = permission.Text.Trim().EndsWith('.') ? "" : permission.Text.Trim();
        sub.Message = LinesOf(message);
        sub.Commands = LinesOf(run).Select(c => c.TrimStart('/')).ToList();
        return true;
    }
}
