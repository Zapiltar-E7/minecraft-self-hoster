using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using MinecraftHost.Core;

namespace MinecraftHost;

/// <summary>
/// Several servers in one app: the ▾ next to the name lists them (switch to one), adds one (an existing folder, or a new
/// empty one to install into) and removes one from the list (its files stay). One server runs at a time, so switching
/// needs the current one stopped.
/// </summary>
public partial class MainWindow
{
    // the name in the menu: its own name, else (like the title) the app's name for the usual folder, else its folder's name
    static string DisplayName(ServerProfile p) =>
        !string.IsNullOrWhiteSpace(p.Name) ? p.Name!
        : string.IsNullOrWhiteSpace(p.ServerDir) ? DefaultName
        : Path.GetFileName(p.Folder.TrimEnd('\\'));

    void BtnServers_Click(object sender, RoutedEventArgs e)
    {
        var config = HostConfig.LoadOrNew();
        var canSwitch = _state == ServerState.Stopped && _busy == 0;
        var menu = new ContextMenu { PlacementTarget = BtnServers, Placement = PlacementMode.Bottom, MinWidth = 280 };

        foreach (var p in config.Servers)
        {
            var current = p.Id == config.CurrentServerId;
            var what = p.Edition == null ? "Nothing installed yet" : $"{p.Edition} {p.Version}" + (p.Modpack is { } pack ? $" · {pack}" : "");
            var item = new MenuItem
            {
                Header = new DockPanel
                {
                    Children =
                    {
                        Docked(new TextBlock
                        {
                            Text = current ? "" : "", FontFamily = (FontFamily)FindResource("Icons"), Width = 22,
                            Foreground = (Brush)FindResource("Accent"), VerticalAlignment = VerticalAlignment.Center,
                        }, Dock.Left),
                        new StackPanel
                        {
                            Children =
                            {
                                new TextBlock { Text = DisplayName(p), FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal },
                                new TextBlock { Text = $"{what}  ·  {p.Folder}", Foreground = (Brush)FindResource("Muted"), FontSize = 11, MaxWidth = 360, TextTrimming = TextTrimming.CharacterEllipsis },
                            },
                        },
                    },
                },
                IsEnabled = current || canSwitch,
                ToolTip = current || canSwitch ? null : "Stop the server first: one server runs at a time.",
            };
            if (!current) item.Click += (_, _) => SwitchServer(p.Id);
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry("", "Add a server...", canSwitch, AddServer));
        menu.Items.Add(MenuEntry("", "Duplicate this server...", canSwitch && Directory.Exists(Paths.ServerDir), () => _ = DuplicateServerAsync(),
            canSwitch ? "Makes a copy with its own folder, to try things out or start a new world with the same setup." : "Stop the server first."));
        menu.Items.Add(MenuEntry("", "Remove this server from the list...", canSwitch && config.Servers.Count > 1, RemoveCurrentServer,
            config.Servers.Count > 1 ? "Its files stay where they are." : "This is the only server."));
        BtnServers.ContextMenu = menu;
        menu.IsOpen = true;
    }

    static T Docked<T>(T element, Dock dock) where T : UIElement
    {
        DockPanel.SetDock(element, dock);
        return element;
    }

    MenuItem MenuEntry(string glyph, string text, bool enabled, Action click, string? tip = null)
    {
        var item = new MenuItem
        {
            Header = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("Icons"), Width = 22, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = text },
                },
            },
            IsEnabled = enabled,
            ToolTip = tip,
        };
        item.Click += (_, _) => click();
        return item;
    }

    /// <summary>Makes another server the one the whole window manages.</summary>
    void SwitchServer(string id)
    {
        if (_state != ServerState.Stopped || _busy > 0) return;
        if (!ConfirmDiscardConfig()) return;   // unsaved edits in the Configs tab belong to the old server
        var config = HostConfig.LoadOrNew();
        config.SwitchTo(id);
        AfterServerChanged();
        Log.Host($"Now managing {DisplayName(config.Current!)} ({Paths.ServerDir}).");
    }

    /// <summary>Everything that shows the current server reloads for the new one.</summary>
    void AfterServerChanged()
    {
        var config = HostConfig.LoadOrNew();
        PlayerHistory.Reset();
        Stats.Reset();
        _ = NamePlayersAsync();
        _recentNote = null;
        _announceDue.Clear();   // each server has its own announcements
        RebuildAnnouncements();
        CheckDiskSpace(force: true);
        _joinRequests.Clear();
        UpdateJoinRequests();
        UpdateServerIcon();
        SyncWorldCard();
        _players.Clear();
        _services = new();
        _webTunnels.Clear();
        ResetConfigs();          // config files, plugins, mods
        _playerListsShown = "";  // whitelist / operators
        _loading = true;
        ChkTunnel.IsChecked = config.UseTunnel;
        _loading = false;
        SyncSoftwareCard();
        SyncRestartCard();
        UpdateView();
        if (ConnectionsPage.Visibility == Visibility.Visible) RebuildConnections();
        _ = RefreshSetupBannerAsync();
        _ = CheckForUpdatesAsync(quiet: true);   // the dots on the tabs
    }

    async void AddServer()
    {
        var name = Prompt("Add a server", "What should this server be called?", "", "Next");
        if (name == null) return;
        name = name.Trim();
        if (name.Length == 0) name = "New server";

        var choice = ShowOverlay(close =>
        {
            var panel = new StackPanel();
            panel.Children.Add(Heading("Add a server", MessageBoxImage.None));
            panel.Children.Add(new TextBlock
            {
                Text = "Is there already a server in a folder on this PC, or should this be a new one?\n\n" +
                       "From a modpack sets it up from a Modrinth (.mrpack) or CurseForge (.zip) modpack file.\n\n" +
                       "A new one gets its own empty folder named after it, in the servers folder next to the app. You then pick the type and version under Server and click Install.",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), Foreground = (Brush)FindResource("Text"), LineHeight = 19,
            });
            panel.Children.Add(ButtonRow(new (string, Func<object?>, bool)[]
            {
                ("Cancel", () => null, true),
                ("From a modpack", () => "modpack", false),
                ("Make a new one", () => "new", false),
                ("Choose its folder", () => "existing", false),
            }, close));
            return panel;
        }, 520) as string;
        if (choice == null) return;
        if (choice == "modpack")
        {
            await InstallModpackAsync(newServerName: name);
            return;
        }

        var profile = new ServerProfile { Name = name };
        if (choice == "existing")
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = $"Choose the folder of \"{name}\"", InitialDirectory = Paths.Root };
            if (dlg.ShowDialog(this) != true) return;
            var dir = dlg.FolderName;
            if (HostConfig.LoadOrNew().Servers.FirstOrDefault(s => s.Folder.TrimEnd('\\').Equals(dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) is { } already)
            {
                Ask($"That folder is already in your list as \"{DisplayName(already)}\".", "Add a server", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var info = ServerFiles.InspectFolder(dir);
            profile.ServerDir = dir;
            if (info.JarName != null)
            {
                profile.ServerJar = info.JarName;
                profile.Edition = info.Edition;
                profile.Version = info.Version ?? "unknown";
                profile.RequiredJava = info.JavaMajor ?? (info.Version != null ? await ServerFiles.GetRequiredJavaAsync(info.Version) : 21);
            }
        }
        else
        {
            var dir = Paths.NewServerDir(name);   // servers\<name>, made unique
            Directory.CreateDirectory(dir);
            profile.ServerDir = dir;
            profile.RamGB = HostConfig.LoadOrNew().RamGB;
        }

        AddServerProfile(profile);
        Log.Host($"Added the server \"{name}\" ({profile.Folder}).");
        if (profile.Edition == null)
        {
            ShowSoftwareCard();
            Ask($"\"{name}\" is ready for a server. Pick the type and version under Server, then click Install.",
                "Add a server", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    /// <summary>Adds a server to the list and switches to it. An untouched, empty first entry is replaced instead of kept.</summary>
    void AddServerProfile(ServerProfile profile)
    {
        var config = HostConfig.LoadOrNew();
        config.Save();   // the current server's latest values into its profile
        if (config.Servers.Count == 1 && config.Current is { Edition: null } empty && !Directory.Exists(empty.Folder))
            config.Servers.Remove(empty);
        config.Servers.Add(profile);
        if (config.Current == null) { config.CurrentServerId = profile.Id; config.CopyFrom(profile); config.Save(); }
        else config.SwitchTo(profile.Id);
        AfterServerChanged();
    }

    /// <summary>
    /// After a rename the server's folders follow its name: backups\&lt;old&gt; becomes backups\&lt;new&gt;, and a folder the
    /// app made in servers\ is renamed too (only while the server is stopped, so otherwise when it next stops).
    /// Folders the user chose themselves, and the usual "server" folder, keep their names.
    /// </summary>
    void RenameServerFolders(string oldName)
    {
        var oldBackups = Path.Combine(Paths.BackupsRoot, Paths.SafeName(oldName));
        var newBackups = Paths.BackupDir;
        if (!oldBackups.Equals(newBackups, StringComparison.OrdinalIgnoreCase) && Directory.Exists(oldBackups) && _busy == 0)
            try
            {
                if (!Directory.Exists(newBackups)) Directory.Move(oldBackups, newBackups);
                else
                {
                    // both exist (e.g. renamed back): move over what doesn't clash, keep the rest where it is
                    foreach (var entry in Directory.EnumerateFileSystemEntries(oldBackups))
                    {
                        var to = Path.Combine(newBackups, Path.GetFileName(entry));
                        if (Directory.Exists(to) || File.Exists(to)) continue;
                        if (Directory.Exists(entry)) Directory.Move(entry, to); else File.Move(entry, to);
                    }
                    if (!Directory.EnumerateFileSystemEntries(oldBackups).Any()) Directory.Delete(oldBackups);
                }
                Log.Host($"Backups are now in {newBackups}.");
            }
            catch (Exception ex) { Log.Host($"Could not rename the backups folder {oldBackups}: {ex.Message}"); }

        if (_state != ServerState.Stopped || _busy > 0)
        {
            if (NeedsFolderRename(out _)) Log.Host("The server's folder gets its new name when the server stops.");
        }
        else SyncServerFolderName();
    }

    /// <summary>Whether the server's folder is one the app made in servers\ and doesn't carry the server's name.</summary>
    static bool NeedsFolderRename(out string folder)
    {
        folder = Paths.ServerDir.TrimEnd('\\');
        if (!string.Equals(Path.GetDirectoryName(folder), Paths.ServersDir, StringComparison.OrdinalIgnoreCase)) return false;
        var leaf = Path.GetFileName(folder);
        var safe = Paths.SafeName(Paths.ServerName);
        return !leaf.Equals(safe, StringComparison.OrdinalIgnoreCase) &&
               !System.Text.RegularExpressions.Regex.IsMatch(leaf, "^" + System.Text.RegularExpressions.Regex.Escape(safe) + @" \d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>Renames the current server's folder in servers\ to match its name, if it's stopped and nothing is open.</summary>
    void SyncServerFolderName()
    {
        if (_state != ServerState.Stopped || _busy > 0 || !NeedsFolderRename(out var folder) || !Directory.Exists(folder)) return;
        if (_dirty && _docFile != null)
        {
            Log.Host("The server's folder keeps its old name for now, because a config file has unsaved changes. It's renamed when the server next stops.");
            return;
        }
        var target = Paths.NewServerDir(Paths.ServerName);
        try
        {
            if (Directory.Exists(target)) Directory.Delete(target);   // NewServerDir only hands back a free or empty folder
            Directory.Move(folder, target);
        }
        catch (Exception ex)
        {
            Log.Host($"Could not rename the server's folder (is a file in it open?): {ex.Message}. It keeps the name {Path.GetFileName(folder)}.");
            return;
        }
        var config = HostConfig.LoadOrNew();
        config.ServerDir = target;
        config.Save();
        Log.Host($"Renamed the server's folder to {target}.");
        AfterServerChanged();
    }

    sealed record DuplicateChoice(string Name, bool Worlds, bool Map);

    /// <summary>Copies the current server into servers\&lt;new name&gt;, with the same settings, and switches to the copy.</summary>
    async Task DuplicateServerAsync()
    {
        if (!StoppedOrSay("the server is copied")) return;
        if (!ConfirmDiscardConfig()) return;
        var config = HostConfig.LoadOrNew();
        config.Save();   // the latest values into the current profile, which the copy starts from
        var source = Paths.ServerDir;
        var original = DisplayName(config.Current!);

        ServerCopySize size = new(0, 0, 0, 0);
        if (!await RunBusyAsync(async () => size = await Task.Run(() => ServerCopy.Measure(source)))) return;
        if (DuplicatePanel(original, source, size) is not { } choice) return;

        var dest = Paths.NewServerDir(choice.Name);
        Log.Host($"Copying \"{original}\" to {dest} ({(choice.Worlds ? "with" : "without")} the worlds{(size.Map > 0 ? choice.Map ? ", with the map" : ", without the map" : "")})...");
        var (ok, cancelled, error) = await RunTaskAsync((p, ct) => ServerCopy.CopyAsync(source, dest, choice.Worlds, choice.Map, p, ct));
        if (!ok)
        {
            Log.Host(cancelled ? "Copy cancelled." : "Copy failed: " + error?.Message);
            try { if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true); } catch (Exception ex) { Log.Host($"Could not remove the partial copy {dest}: {ex.Message}"); }
            if (_closeAfterTask) return;
            if (!cancelled)
            {
                Sounds.Error();
                Ask("The copy failed, so the partial copy was removed:\n" + error?.Message, "Duplicate server", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            return;
        }

        var profile = new ServerProfile();
        HostConfig.LoadOrNew().CopyTo(profile);   // type, version, RAM, launch arguments, playit address, sharing...
        profile.Name = choice.Name;
        profile.ServerDir = dest;
        AddServerProfile(profile);
        Log.Host($"Duplicated \"{original}\" as \"{choice.Name}\" ({dest}). Now managing the copy.");
        Sounds.Success();
        if (_closeAfterTask) return;
        Ask($"\"{choice.Name}\" is ready, and it's the server shown now.\n\n" +
            (choice.Worlds ? "It has its own copy of the worlds, so nothing you do in it changes \"" + original + "\"." : "It makes a new world the first time it starts.") +
            $"\n\nSwitch back to \"{original}\" with the menu next to the name.",
            "Duplicate server", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>The new name, what to copy, and whether it fits on the drive. Null when cancelled.</summary>
    DuplicateChoice? DuplicatePanel(string original, string source, ServerCopySize size) =>
        ShowOverlay(close =>
        {
            var muted = (Brush)FindResource("Muted");
            var panel = new StackPanel();
            panel.Children.Add(Heading("Duplicate server", MessageBoxImage.None));
            panel.Children.Add(new TextBlock
            {
                Text = $"Makes a copy of \"{original}\" with its own folder, plugins, mods and settings. \"{original}\" isn't changed.",
                TextWrapping = TextWrapping.Wrap, Foreground = muted, Margin = new Thickness(0, 6, 0, 0), LineHeight = 19,
            });

            panel.Children.Add(new TextBlock { Text = "Name of the copy", Style = (Style)FindResource("FieldLabel"), Margin = new Thickness(0, 16, 0, 4) });
            var box = new TextBox { Style = (Style)FindResource("Input"), Text = $"{original} copy" };
            box.Loaded += (_, _) => box.SelectAll();
            panel.Children.Add(box);
            var folder = new TextBlock { Foreground = muted, FontSize = 12, Margin = new Thickness(2, 6, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            panel.Children.Add(folder);

            var worlds = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = true, Margin = new Thickness(0, 16, 0, 0), Content = $"Copy the worlds ({GB(size.Worlds)})" };
            var worldsNote = new TextBlock { Foreground = muted, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(46, 2, 0, 0) };
            panel.Children.Add(worlds);
            panel.Children.Add(worldsNote);
            var map = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = false, Margin = new Thickness(0, 12, 0, 0), Content = $"Copy the BlueMap map data ({GB(size.Map)})" };
            if (size.Map > 0)
            {
                panel.Children.Add(map);
                panel.Children.Add(new TextBlock { Text = "Without it, the copy's map draws itself again as players explore.", Foreground = muted, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(46, 2, 0, 0) });
            }

            var space = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 16, 0, 0) };
            panel.Children.Add(space);
            panel.Children.Add(new TextBlock
            {
                Text = "The copy uses the same port and playit address. That's fine, since one server runs at a time.",
                TextWrapping = TextWrapping.Wrap, Foreground = muted, FontSize = 11.5, Margin = new Thickness(0, 8, 0, 0),
            });

            var buttons = ButtonRow(new (string, Func<object?>, bool)[]
            {
                ("Cancel", () => null, true),
                ("Duplicate", () => new DuplicateChoice(box.Text.Trim(), worlds.IsChecked == true, size.Map > 0 && map.IsChecked == true), false),
            }, close);
            var go = ((Panel)buttons).Children.OfType<Button>().Last();
            panel.Children.Add(buttons);

            void Refresh()
            {
                var name = box.Text.Trim();
                var dest = name.Length == 0 ? null : Paths.NewServerDir(name);
                folder.Text = dest == null ? "Type a name for the copy." : $"Goes in {Path.GetRelativePath(Paths.Root, dest)}";
                worldsNote.Text = worlds.IsChecked == true
                    ? "The copy gets its own worlds; playing in one doesn't change the other."
                    : "The copy makes a new world when it first starts (datapacks are still copied).";
                var needed = size.Total - (worlds.IsChecked == true ? 0 : size.Worlds) - (map.IsChecked == true ? 0 : size.Map);
                DriveInfo? drive = null;
                try { drive = new DriveInfo(Path.GetPathRoot(Paths.ServersDir)!); } catch { }
                var free = drive?.IsReady == true ? drive.AvailableFreeSpace : -1;
                var fits = free < 0 || free > needed + (512L << 20);   // leave a little room
                space.Foreground = fits ? muted : (Brush)FindResource("Bad");
                space.Text = free < 0 ? $"Copies {GB(needed)}."
                    : fits ? $"Copies {GB(needed)}. Drive {drive!.Name} has {GB(free)} free."
                    : $"Copies {GB(needed)}, but drive {drive!.Name} only has {GB(free)} free. Leave out the worlds or the map, or free up space.";
                go.IsEnabled = fits && dest != null;
            }
            box.TextChanged += (_, _) => Refresh();
            worlds.Checked += (_, _) => Refresh(); worlds.Unchecked += (_, _) => Refresh();
            map.Checked += (_, _) => Refresh(); map.Unchecked += (_, _) => Refresh();
            Refresh();
            return panel;
        }, 520) as DuplicateChoice;

    void RemoveCurrentServer()
    {
        var config = HostConfig.LoadOrNew();
        if (config.Servers.Count < 2 || config.Current is not { } current) return;
        if (Ask($"Remove \"{DisplayName(current)}\" from your list?\n\nNothing is deleted: its worlds, plugins and settings stay in\n{current.Folder}\n\n" +
                "You can add it back any time with Add a server > Choose its folder.",
                "Remove server", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (!ConfirmDiscardConfig()) return;
        var next = config.Servers.First(s => s.Id != current.Id);
        config.SwitchTo(next.Id);
        config.Servers.RemoveAll(s => s.Id == current.Id);
        config.Save();
        AfterServerChanged();
        Log.Host($"Removed \"{DisplayName(current)}\" from the list (its files are still in {current.Folder}). Now managing {DisplayName(next)}.");
    }
}
