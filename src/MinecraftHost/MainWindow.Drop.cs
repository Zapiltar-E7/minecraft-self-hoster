using System.Windows;
using MinecraftHost.Core;

namespace MinecraftHost;

/// <summary>
/// Dropping files on the window: a .mchost server file is imported; plugin and mod jars and datapacks are installed
/// into the server (replacing an older copy of the same one, which goes to backups).
/// </summary>
public partial class MainWindow
{
    static string[]? DroppedPaths(DragEventArgs e) => e.Data.GetDataPresent(DataFormats.FileDrop) ? e.Data.GetData(DataFormats.FileDrop) as string[] : null;

    // dropping only works when nothing else is waiting for an answer or running
    bool CanTakeDrop => _overlays.Count == 0 && _taskCts == null && TourLayer.Visibility != Visibility.Visible;

    void Window_DragEnter(object sender, DragEventArgs e)
    {
        var paths = DroppedPaths(e);
        var ok = paths != null && CanTakeDrop;
        if (ok) ShowDropHint(paths!);
        DropHint.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>Says what dropping these files will do: a modpack sets up a server, anything else installs into it.</summary>
    void ShowDropHint(string[] paths)
    {
        if (paths.Length == 1 && Modpacks.LooksLikeModpack(paths[0]))
        {
            DropTitle.Text = "Drop to install the modpack";
            DropText.Text = $"{Path.GetFileName(paths[0])}\nNext you pick a new server or this one. The right Minecraft version, mod loader and mods are installed for you.";
        }
        else if (paths.Length == 1 && paths[0].EndsWith(Transfer.Extension, StringComparison.OrdinalIgnoreCase))
        {
            DropTitle.Text = "Drop to import the server";
            DropText.Text = Path.GetFileName(paths[0]);
        }
        else
        {
            DropTitle.Text = "Drop to install";
            DropText.Text = "Plugins and mods (.jar) and datapacks (.zip) go into the server. A modpack (.mrpack, or a CurseForge .zip) sets up a server, and a server file (.mchost) is imported.";
        }
    }

    void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DroppedPaths(e) != null && CanTakeDrop ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void Window_DragLeave(object sender, DragEventArgs e)
    {
        // DragLeave also fires when moving between child elements; only hide when the pointer really left the window
        var p = e.GetPosition(this);
        if (p.X <= 0 || p.Y <= 0 || p.X >= ActualWidth || p.Y >= ActualHeight) DropHint.Visibility = Visibility.Collapsed;
    }

    async void Window_Drop(object sender, DragEventArgs e)
    {
        DropHint.Visibility = Visibility.Collapsed;
        if (DroppedPaths(e) is not { Length: > 0 } paths || !CanTakeDrop) return;
        e.Handled = true;
        Activate();

        var packages = paths.Where(p => p.EndsWith(Transfer.Extension, StringComparison.OrdinalIgnoreCase)).ToList();
        if (packages.Count > 0)
        {
            if (paths.Length > 1)
            {
                Ask("Drop a server file (.mchost) on its own. It replaces the whole server, so it isn't installed together with other files.",
                    "Drop files", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            await ImportPackageAsync(packages[0]);
            return;
        }
        var modpacks = paths.Where(Modpacks.LooksLikeModpack).ToList();
        if (modpacks.Count > 0)
        {
            if (paths.Length > 1)
            {
                Ask("Drop a modpack on its own. It sets up the server's Minecraft version and mod loader, so it isn't installed together with other files.",
                    "Drop files", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            await InstallModpackAsync(modpacks[0]);
            return;
        }
        await InstallDroppedAsync(paths);
    }

    async Task InstallDroppedAsync(string[] paths)
    {
        var items = await Task.Run(() => paths.Select(p => (Path: p, Addon: Addons.Identify(p))).ToList());
        var unknown = items.Where(i => i.Addon == null).Select(i => Path.GetFileName(i.Path)).ToList();
        var found = items.Where(i => i.Addon != null).Select(i => i.Addon!).ToList();
        if (found.Count == 0)
        {
            Ask("None of these are plugins, mods or datapacks the app can install:\n\n" + string.Join("\n", unknown.Select(n => "   •  " + n)) +
                "\n\nPlugins and mods are .jar files; datapacks are .zip files (or folders) with a pack.mcmeta inside.",
                "Drop files", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_addons == null) await ScanAddonsAsync();   // to spot older copies of the same thing

        var edition = HostConfig.Load()?.Edition;
        var running = _state != ServerState.Stopped || _busy > 0;
        var installed = new List<string>();
        var skipped = new List<string>();
        var notes = new HashSet<string>();
        foreach (var a in found)
        {
            var kind = a.Kind.ToString().ToLowerInvariant();
            var label = a.Version == null ? a.Name : $"{a.Name} {a.Version}";
            // the same plugin/mod (by name), or a file of the same name, already in the server
            var old = _addons?.FirstOrDefault(o => o.Kind == a.Kind &&
                (o.FileName.Equals(a.FileName, StringComparison.OrdinalIgnoreCase) ||
                 (a.Kind != AddonKind.Datapack && o.Name.Equals(a.Name, StringComparison.OrdinalIgnoreCase))));
            if (old != null)
            {
                if (old.Sha1.Length > 0 && old.Sha1 == a.Sha1) { skipped.Add($"{label}: already installed"); continue; }
                if (running) { skipped.Add($"{label}: stop the server to replace {old.FileName}"); continue; }
                var oldLabel = old.Version == null ? old.FileName : $"{old.Name} {old.Version}";
                if (Ask($"{oldLabel} is already installed{(old.Enabled ? "" : " (switched off)")}.\n\nReplace it with {label}? The old file goes to backups.",
                        "Replace " + kind, MessageBoxButton.YesNo) != MessageBoxResult.Yes)
                {
                    skipped.Add($"{label}: kept {oldLabel}");
                    continue;
                }
            }

            // installs anyway, but say when this server type won't load it
            if (a.Kind == AddonKind.Plugin && edition != null && !Editions.RunsPlugins(edition))
                notes.Add($"This server runs {edition}, which can't load plugins.");
            if (a.Kind == AddonKind.Mod && edition != null && !Editions.RunsMods(edition))
                notes.Add($"This server runs {edition}, which can't load mods. Switch to a mod loader (Fabric, Quilt, Forge or NeoForge) on the Server tab to use them.");
            if (a.Kind == AddonKind.Mod && a.Loader != null && Editions.RunsMods(edition) && !Editions.LoadsMod(edition, a.Loader, HostConfig.Load()?.Version))
                notes.Add($"{a.Name} is made for {a.Loader}, but this server runs {edition}.");

            try
            {
                var to = await Task.Run(() => Addons.Install(a, old));
                installed.Add($"{label} ({kind})");
                Log.Host($"Installed {kind} {label} into {Path.GetRelativePath(Paths.ServerDir, to)}" + (old != null ? $", replacing {old.FileName} (moved to backups)." : "."));
            }
            catch (Exception ex)
            {
                skipped.Add($"{label}: {ex.Message}");
            }
        }

        await ScanAddonsAsync();   // the list, dots and Connections cards follow
        var message = new List<string>();
        if (installed.Count > 0) message.Add("Installed:\n" + string.Join("\n", installed.Select(i => "   •  " + i)));
        if (skipped.Count > 0) message.Add("Not installed:\n" + string.Join("\n", skipped.Select(i => "   •  " + i)));
        if (unknown.Count > 0) message.Add("Not a plugin, mod or datapack:\n" + string.Join("\n", unknown.Select(i => "   •  " + i)));
        message.AddRange(notes);
        if (installed.Count > 0) message.Add(running ? "Restart the server to load them." : "They load the next time the server starts.");
        Ask(string.Join("\n\n", message), "Drop files", MessageBoxButton.OK,
            installed.Count > 0 && skipped.Count == 0 && unknown.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);

        // show where they went
        if (installed.Count > 0)
        {
            if (found.All(a => a.Kind == AddonKind.Mod)) TabMods.IsChecked = true;
            else if (found.All(a => a.Kind != AddonKind.Mod)) TabAddons.IsChecked = true;
        }
    }
}
