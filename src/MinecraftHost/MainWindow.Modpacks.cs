using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MinecraftHost.Core;

namespace MinecraftHost;

/// <summary>
/// Installing a modpack (a Modrinth .mrpack, or a CurseForge zip or manifest.json): the right Minecraft version and mod
/// loader build first, then the pack's mods and settings. It goes into a new server named after the pack, or replaces
/// the current server's software and mods.
/// </summary>
public partial class MainWindow
{
    sealed record ModpackChoice(bool NewServer, bool BackupWorlds);

    const int ModpackMinRam = 6;   // most modpacks need at least this much

    void BtnModpack_Click(object sender, RoutedEventArgs e) => _ = InstallModpackAsync();

    /// <summary>Picks (or takes) a modpack file, asks where it goes, and installs it.</summary>
    /// <param name="newServerName">From Add a server: always a new server, with this name.</param>
    async Task InstallModpackAsync(string? path = null, string? newServerName = null)
    {
        if (!StoppedOrSay("a modpack is installed")) return;
        if (path == null)
        {
            var open = new Microsoft.Win32.OpenFileDialog { Title = "Choose a modpack", Filter = Modpacks.FileFilter };
            if (open.ShowDialog(this) != true) return;
            path = open.FileName;
        }

        Modpack pack;
        try { pack = await Task.Run(() => Modpacks.Read(path)); }
        catch (Exception ex)
        {
            Ask("Can't read that modpack:\n" + ex.Message, "Install a modpack", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var config = HostConfig.LoadOrNew();
        var currentEmpty = !config.IsSetUp && (!Directory.Exists(Paths.ServerDir) || !Directory.EnumerateFileSystemEntries(Paths.ServerDir).Any());
        if (ModpackPanel(pack, config, currentEmpty, newServerName) is not { } choice) return;
        if (!ConfirmDiscardConfig()) return;   // unsaved edits in the Configs tab belong to the old setup

        // the loader installers (Forge, NeoForge, Quilt) need Java, and so does running the server
        var required = await ServerFiles.GetRequiredJavaAsync(pack.Minecraft);
        var java = await Task.Run(() => Java.Find(required));
        if (java == null)
        {
            await OfferJavaInstallAsync(required);
            java = await Task.Run(() => Java.Find(required));
            if (java == null && Editions.InstallerNeedsJava(pack.Edition))
            {
                Ask($"{pack.Edition} needs Java {required} to install. Install Java with Tools > Set up this PC, then try again.",
                    "Install a modpack", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
        }
        else if (Editions.RunsMods(pack.Edition) && await Task.Run(() => Java.GetMajor(java)) is var major && major != required &&
                 Ask($"Minecraft {pack.Minecraft} comes with Java {required}, and this PC has Java {major}. Many mods only start on the exact version.\n\n" +
                     $"Download Java {required} now? It goes into the app's tools folder (about 50 MB) and needs no admin rights.",
                     "Java for this modpack", MessageBoxButton.YesNo) == MessageBoxResult.Yes &&
                 await RunBusyAsync(() => Java.InstallPortableAsync(required)))
            java = await Task.Run(() => Java.Find(required)) ?? java;

        string? newId = null;
        if (choice.NewServer)
        {
            var name = newServerName ?? pack.Name;
            var dir = Paths.NewServerDir(name);
            Directory.CreateDirectory(dir);
            var profile = new ServerProfile { Name = name, ServerDir = dir, RamGB = ModpackRam(config.RamGB) };
            AddServerProfile(profile);
            newId = profile.Id;
            Log.Host($"Added the server \"{name}\" ({dir}) for the modpack.");
        }

        Log.Host($"Installing the modpack {pack.Title} ({pack.Source}): Minecraft {pack.Minecraft}, {pack.LoaderText}, {pack.Files.Count} files, into {Paths.ServerDir} ...");
        ModpackResult? result = null;
        var key = config.CurseForgeKey;
        var (ok, cancelled, error) = await RunTaskAsync(async (p, ct) =>
        {
            if (choice.BackupWorlds)
            {
                p.Report(new TransferProgress("Backing up worlds...", 0.01, ""));
                await Task.Run(ServerFiles.BackupWorlds, ct);
            }
            if (!choice.NewServer) await Task.Run(SetAsideOldMods, ct);
            p.Report(new TransferProgress($"Installing {pack.LoaderText} for Minecraft {pack.Minecraft}...", 0.05,
                Editions.InstallerNeedsJava(pack.Edition) ? "Its installer downloads Minecraft and its libraries; this takes a minute or two" : ""));
            await ServerFiles.InstallAsync(pack.Edition, pack.Minecraft, java, pack.LoaderVersion);
            ct.ThrowIfCancellationRequested();
            result = await Modpacks.InstallFilesAsync(pack, Paths.ServerDir, key, p, ct);
            var c = HostConfig.LoadOrNew();
            c.Modpack = pack.Title;
            c.RamGB = ModpackRam(c.RamGB);
            c.Save();
        });

        AfterServerChanged();   // software card, mods, configs and the header all changed
        if (_closeAfterTask) return;
        if (!ok || result == null)
        {
            Log.Host(cancelled ? "Modpack install cancelled." : "Modpack install failed: " + error?.Message);
            if (!cancelled) Sounds.Error();
            var text = (cancelled ? "The modpack install was cancelled." : "The modpack install failed:\n" + error?.Message);
            if (newId != null && HostConfig.LoadOrNew().Servers.Count > 1)
            {
                if (Ask(text + $"\n\nRemove the half-installed server \"{newServerName ?? pack.Name}\" and delete its folder?\n{Paths.ServerDir}",
                        "Install a modpack", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                    RemoveNewServer(newId);
            }
            else Ask(text, "Install a modpack", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        foreach (var problem in result.Problems) Log.Host("  " + problem);
        if (result.SwitchedOff.Count > 0) Log.Host($"Switched off {result.SwitchedOff.Count} mods only players need: {string.Join(", ", result.SwitchedOff)}");
        if (result.Skipped.Count > 0) Log.Host($"Left out {result.Skipped.Count} resource packs or shaders: {string.Join(", ", result.Skipped)}");
        foreach (var (name, page) in result.ManualDownloads) Log.Host($"  Download yourself: {name} - {page}");
        Log.Host($"Installed the modpack {pack.Title}: {result.Installed} files.");
        var clean = result.Problems.Count == 0 && result.ManualDownloads.Count == 0;
        if (clean) Sounds.Success(); else Sounds.Error();

        var modsDir = Path.Combine(Paths.ServerDir, "mods");
        var active = Directory.Exists(modsDir) ? Directory.GetFiles(modsDir, "*.jar").Length : 0;
        var lines = new List<string> { $"{pack.Title} is installed: {pack.LoaderText} for Minecraft {pack.Minecraft}, with {active} {(active == 1 ? "mod" : "mods")}." };
        if (result.SwitchedOff.Count > 0)
            lines.Add($"{result.SwitchedOff.Count} mods that only players need (like {string.Join(", ", result.SwitchedOff.Take(3))}) are switched off. They're on the Mods tab.");
        if (result.Problems.Count > 0)
            lines.Add($"{result.Problems.Count} files didn't install:\n" + string.Join("\n", result.Problems.Take(6)) + (result.Problems.Count > 6 ? "\n... (the rest are in the log)" : ""));
        if (result.ManualDownloads.Count > 0)
            lines.Add($"{result.ManualDownloads.Count} mods have to be downloaded by hand, because their authors don't allow other apps to download them:\n" +
                      string.Join("\n", result.ManualDownloads.Take(8).Select(m => "• " + m.Name)) +
                      "\nDownload each one for this Minecraft version, then drop the files on this window.");
        lines.Add($"Friends need the same modpack ({pack.Title}) installed in their launcher to join. Click Start when you're ready.");
        var buttons = result.ManualDownloads.Count > 0 ? MessageBoxButton.YesNo : MessageBoxButton.OK;
        if (result.ManualDownloads.Count > 0) lines.Add("Open their download pages now?");
        if (Ask(string.Join("\n\n", lines), "Modpack installed", buttons, clean ? MessageBoxImage.Information : MessageBoxImage.Warning) == MessageBoxResult.Yes)
            foreach (var (_, page) in result.ManualDownloads.Take(15))
                try { Playit.OpenUrl(page); } catch { }
        TabMods.IsChecked = true;
    }

    /// <summary>
    /// A modded server that crashed while starting, with a crash report naming the mods to blame: offer to switch them off
    /// (to disabled\mods, shown on the Mods tab) and start again. Mods made only for players cause this most often.
    /// </summary>
    async void OfferSwitchOffCrashedMods(List<(string Name, string Jar)> culprits)
    {
        if (_state != ServerState.Stopped || _busy > 0 || _overlays.Count > 0) return;
        var list = string.Join("\n", culprits.Select(c => $"• {c.Name} ({Path.GetFileName(c.Jar)})"));
        var one = culprits.Count == 1;
        if (Ask($"The server stopped while starting because of {(one ? "this mod" : "these mods")}:\n{list}\n\n" +
                $"Mods like this are often made only for players (graphics, menus, sounds), so a server can't run {(one ? "it" : "them")}. " +
                $"Switch {(one ? "it" : "them")} off and start again? {(one ? "It moves" : "They move")} to the switched-off mods on the Mods tab, so you can switch {(one ? "it" : "them")} back on.",
                "A mod stopped the server", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var off = Path.Combine(Addons.DisabledDir, "mods");
        Directory.CreateDirectory(off);
        foreach (var (name, jar) in culprits)
            try
            {
                File.Move(jar, Path.Combine(off, Path.GetFileName(jar)), overwrite: true);
                Log.Host($"Switched off {name} ({Path.GetFileName(jar)}).");
            }
            catch (Exception ex) { Log.Host($"Could not switch off {Path.GetFileName(jar)}: {ex.Message}"); return; }
        _addons = null;   // the Mods tab rescans
        UpdateView();
        await StartServerAsync(_lastStartSafe);
    }

    static int ModpackRam(int ram) => Math.Clamp(Math.Max(ram, ModpackMinRam), 1, MaxRam(SystemInfo.TotalMemoryGB));

    /// <summary>The current server's mods go to backups before the modpack's come in; its config folder is copied there too.</summary>
    static void SetAsideOldMods()
    {
        var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + " before modpack";
        var backup = Path.Combine(Paths.BackupDir, "mods", stamp);
        foreach (var rel in new[] { "mods", Path.Combine("disabled", "mods") })
        {
            var from = Path.Combine(Paths.ServerDir, rel);
            if (!Directory.Exists(from) || !Directory.EnumerateFileSystemEntries(from).Any()) continue;
            var to = Path.Combine(backup, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            Directory.Move(from, to);
            Log.Host($"Moved the old {rel} folder to {to}.");
        }
        var config = Path.Combine(Paths.ServerDir, "config");
        if (Directory.Exists(config))
        {
            var to = Path.Combine(Paths.BackupDir, "configs", stamp, "config");
            foreach (var file in Directory.EnumerateFiles(config, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(to, Path.GetRelativePath(config, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
            }
            Log.Host($"Copied the old config folder to {to}.");
        }
    }

    void RemoveNewServer(string id)
    {
        var config = HostConfig.LoadOrNew();
        if (config.Servers.FirstOrDefault(s => s.Id == id) is not { } server || config.Servers.Count < 2) return;
        var folder = server.Folder;
        if (config.CurrentServerId == id) config.SwitchTo(config.Servers.First(s => s.Id != id).Id);
        config.Servers.RemoveAll(s => s.Id == id);
        config.Save();
        try { Directory.Delete(folder, recursive: true); } catch (Exception ex) { Log.Host($"Could not delete {folder}: {ex.Message}"); }
        AfterServerChanged();
        Log.Host($"Removed the half-installed server ({folder}).");
    }

    /// <summary>What the pack is and where it goes. Null when cancelled.</summary>
    ModpackChoice? ModpackPanel(Modpack pack, HostConfig config, bool currentEmpty, string? newServerName) =>
        ShowOverlay(close =>
        {
            var muted = (Brush)FindResource("Muted");
            var warn = (Brush)FindResource("WarnText");
            var panel = new StackPanel();
            panel.Children.Add(Heading("Install a modpack", MessageBoxImage.None));
            panel.Children.Add(new TextBlock { Text = pack.Title, FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) });
            panel.Children.Add(new TextBlock
            {
                Text = (pack.Author is { Length: > 0 } a ? $"by {a}  ·  " : "") + $"{pack.Source} modpack",
                Foreground = muted, FontSize = 12, Margin = new Thickness(0, 2, 0, 0),
            });

            // the facts in three columns
            var facts = new Grid { Margin = new Thickness(0, 14, 0, 0) };
            var items = new[] { ("Minecraft", pack.Minecraft), ("Mod loader", pack.LoaderText), ("Files", pack.Files.Count.ToString()) };
            for (var i = 0; i < items.Length; i++)
            {
                facts.ColumnDefinitions.Add(new ColumnDefinition());
                var cell = new StackPanel();
                cell.Children.Add(new TextBlock { Text = items[i].Item1, Style = (Style)FindResource("FieldLabel"), Margin = new Thickness(0, 0, 0, 2) });
                cell.Children.Add(new TextBlock { Text = items[i].Item2, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                Grid.SetColumn(cell, i);
                facts.Children.Add(cell);
            }
            panel.Children.Add(facts);

            var notes = new List<string>();
            if (pack.LeftOut > 0) notes.Add($"{pack.LeftOut} files the pack marks as optional or only for players are left out.");
            if (pack.Source == "CurseForge" && string.IsNullOrWhiteSpace(config.CurseForgeKey))
                notes.Add("Mods download through curseforge.com. A few authors don't allow that; those are listed at the end for you to download.");
            if (pack.Source == "CurseForge" && !pack.PackPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                notes.Add("Only the manifest was chosen, so the pack's own settings files (its overrides folder) aren't included. Choose the pack's .zip to get them.");
            if (notes.Count > 0)
                panel.Children.Add(new TextBlock { Text = string.Join("\n", notes), TextWrapping = TextWrapping.Wrap, Foreground = muted, FontSize = 12, Margin = new Thickness(0, 12, 0, 0), LineHeight = 18 });

            // where it goes: a new server, or this one
            var newName = newServerName ?? pack.Name;
            var current = string.IsNullOrWhiteSpace(config.Name) ? DefaultName : config.Name!;
            var toNew = new RadioButton { Style = (Style)FindResource("TabButton"), GroupName = "ModpackTarget", Margin = new Thickness(0), Content = "A new server" };
            var toCurrent = new RadioButton { Style = (Style)FindResource("TabButton"), GroupName = "ModpackTarget", Margin = new Thickness(0), Content = currentEmpty ? "This server (empty)" : "Replace this server" };
            (newServerName != null || !currentEmpty ? toNew : toCurrent).IsChecked = true;
            if (newServerName == null)
            {
                panel.Children.Add(new TextBlock { Text = "Install into", Style = (Style)FindResource("FieldLabel"), Margin = new Thickness(0, 16, 0, 4) });
                panel.Children.Add(new Border
                {
                    Background = (Brush)FindResource("Field"), CornerRadius = new CornerRadius(9), Padding = new Thickness(3), HorizontalAlignment = HorizontalAlignment.Left,
                    Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { toNew, toCurrent } },
                });
            }
            var where = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 8, 0, 0), LineHeight = 18 };
            panel.Children.Add(where);
            var hasWorlds = Directory.Exists(Paths.ServerDir) && Directory.GetDirectories(Paths.ServerDir).Any(d => File.Exists(Path.Combine(d, "level.dat")));
            var backup = new CheckBox { Style = (Style)FindResource("Switch"), Content = "Back up worlds first", IsChecked = hasWorlds, Margin = new Thickness(0, 10, 0, 0) };
            panel.Children.Add(backup);
            if (!ServerFiles.EulaAccepted() || toNew.IsChecked == true)
                panel.Children.Add(new TextBlock { Text = "Running a server means you accept the Minecraft EULA (aka.ms/MinecraftEULA).", Foreground = muted, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) });

            void Refresh()
            {
                var isNew = toNew.IsChecked == true;
                backup.Visibility = !isNew && hasWorlds ? Visibility.Visible : Visibility.Collapsed;
                if (isNew)
                {
                    where.Foreground = muted;
                    where.Text = $"Makes the server \"{newName}\" in {Path.Combine("servers", Paths.SafeName(newName))}, with {ModpackRam(config.RamGB)} GB RAM. Your other servers stay as they are.";
                }
                else if (currentEmpty)
                {
                    where.Foreground = muted;
                    where.Text = $"Installs into \"{current}\" ({Paths.ServerDir}).";
                }
                else
                {
                    var warnings = SwitchWarnings(config, pack.Edition, pack.Minecraft, modpack: true);
                    warnings.Insert(0, $"\"{current}\" switches to {pack.LoaderText} for Minecraft {pack.Minecraft}. Its current mods move to backups, and the pack's settings files replace the ones with the same name.");
                    if (hasWorlds) warnings.Add("Worlds made with other mods can lose blocks and items that this pack doesn't have.");
                    where.Foreground = warn;
                    where.Text = string.Join("\n\n", warnings);
                }
            }
            toNew.Checked += (_, _) => Refresh();
            toCurrent.Checked += (_, _) => Refresh();
            Refresh();

            panel.Children.Add(ButtonRow(new (string, Func<object?>, bool)[]
            {
                ("Cancel", () => null, true),
                ("Install", () => new ModpackChoice(toNew.IsChecked == true, toNew.IsChecked != true && backup.IsChecked == true && hasWorlds), false),
            }, close));
            return panel;
        }, 540) as ModpackChoice;
}
