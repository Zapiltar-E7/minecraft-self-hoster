using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MinecraftHost.Core;

namespace MinecraftHost;

/// <summary>Export the whole server to one .mchost file, and import it on another PC.</summary>
public partial class MainWindow
{
    static string GB(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):N1} GB" : $"{bytes / 1048576.0:N0} MB";

    bool StoppedOrSay(string what)
    {
        if (_state == ServerState.Stopped && _busy == 0) return true;
        Ask($"Stop the server first, so the worlds are saved and nothing changes while {what}.", "Server is running",
            MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    // ------------------------------------------------------------ export

    async void BtnExport_Click(object sender, RoutedEventArgs e)
    {
        if (!StoppedOrSay("they're packed")) return;
        var config = HostConfig.Load();
        if (config?.IsSetUp != true)
        {
            Ask("There's no server set up here to export.", "Export server", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var (source, estimate, worstCase) = await Task.Run(() => Transfer.Estimate(Paths.ServerDir));
        var name = Paths.SafeName(Paths.ServerName);   // the file is named after the server
        var folder = Directory.Exists(config.LastExportDir) ? config.LastExportDir! : Paths.Root;
        var path = ExportPanel(Path.Combine(folder, $"{name} {DateTime.Now:yyyy-MM-dd}{Transfer.Extension}"), source, estimate, worstCase);
        if (path == null) return;
        config = HostConfig.LoadOrNew();
        config.LastExportDir = Path.GetDirectoryName(path);
        config.Save();

        PackageManifest? manifest = null;
        Log.Host($"Exporting the server to {path} ...");
        var (ok, cancelled, error) = await RunTaskAsync(async (p, ct) => manifest = await Transfer.ExportAsync(Paths.ServerDir, config, path, p, ct));
        if (cancelled) { Log.Host("Export cancelled."); return; }
        if (!ok || manifest == null)
        {
            Log.Host("Export failed: " + error?.Message);
            Sounds.Error();
            Ask("The export failed:\n" + error?.Message, "Export server", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        if (_closeAfterTask) return;
        var size = new FileInfo(path).Length;
        Log.Host($"Exported {manifest.RegionFiles} region files, {manifest.Files} other files and {manifest.Downloads.Count} plugin links: {GB(size)}.");
        Sounds.Success();
        if (Ask($"Done: {Path.GetFileName(path)} ({GB(size)}).\n\n" +
                $"On the new PC, put the Minecraft Self-Hoster app in a folder, start it and use Tools > Import server with this file. " +
                $"{manifest.Downloads.Count} plugins are downloaded from Modrinth there, so it needs internet.\n\nShow the file?",
                "Export server", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            Process.Start("explorer.exe", $"/select,\"{path}\"");
    }

    /// <summary>
    /// The export confirmation: where the file goes (with Change...), whether it fits on that drive, and what's in it.
    /// Returns the chosen path, or null if cancelled.
    /// </summary>
    string? ExportPanel(string initialPath, long source, long estimate, long worstCase) =>
        ShowOverlay(close =>
        {
            var path = initialPath;
            var muted = (Brush)FindResource("Muted");
            var panel = new StackPanel();
            panel.Children.Add(Heading("Export server", MessageBoxImage.None));
            panel.Children.Add(new TextBlock
            {
                Text = $"Packs {GB(source)} into one file of about {GB(estimate)}. This takes a few minutes, and your server folder isn't changed.",
                TextWrapping = TextWrapping.Wrap, Foreground = muted, Margin = new Thickness(0, 6, 0, 0),
            });

            panel.Children.Add(new TextBlock { Text = "Save to", Style = (Style)FindResource("FieldLabel"), Margin = new Thickness(0, 16, 0, 4) });
            var where = new DockPanel();
            var change = new Button { Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 6, 12, 6), FontWeight = FontWeights.Normal };
            change.Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new TextBlock { Text = "\uE8DA", FontFamily = (FontFamily)FindResource("Icons"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 8, 0) },
                    new TextBlock { Text = "Change..." },
                },
            };
            DockPanel.SetDock(change, Dock.Right);
            var box = new TextBox { Style = (Style)FindResource("Value") };
            where.Children.Add(change);
            where.Children.Add(box);
            panel.Children.Add(where);
            var space = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(2, 6, 0, 0) };
            panel.Children.Add(space);

            panel.Children.Add(new TextBlock
            {
                Text = "Included: all worlds (packed smaller without losing anything), plugins, mods, config files and plugin data (CoreProtect history too), and this app's settings.\n" +
                       "Left out: BlueMap's map data (it re-renders on the new PC), logs and caches.",
                TextWrapping = TextWrapping.Wrap, Foreground = muted, FontSize = 12, Margin = new Thickness(0, 14, 0, 0), LineHeight = 18,
            });
            panel.Children.Add(new TextBlock
            {
                Text = "The file holds everything in your configs, including passwords and tokens (like a Discord bot token), so keep it private.",
                TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("WarnText"), FontSize = 12, Margin = new Thickness(0, 8, 0, 0),
            });

            var buttons = ButtonRow(new (string, Func<object?>, bool)[] { ("Cancel", () => null, true), ("Export", () => path, false) }, close);
            var export = ((Panel)buttons).Children.OfType<Button>().Last();
            panel.Children.Add(buttons);

            void Refresh()
            {
                box.Text = path;
                box.ToolTip = path;
                // long paths: show the end, where the folder and file name are
                Dispatcher.BeginInvoke(() => box.ScrollToHorizontalOffset(double.MaxValue), System.Windows.Threading.DispatcherPriority.Loaded);
                DriveInfo? drive = null;
                try { drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!); } catch { }
                var free = drive?.IsReady == true ? drive.AvailableFreeSpace : -1;
                var fits = free < 0 || free >= worstCase;
                space.Foreground = fits ? muted : (Brush)FindResource("Bad");
                space.Text = free < 0 ? ""
                    : !fits ? $"Drive {drive!.Name} only has {GB(free)} free, and the file could be up to {GB(worstCase)}. Pick another drive (an external drive works well for moving it)."
                    : $"Drive {drive!.Name} has {GB(free)} free." + (File.Exists(path) ? " A file with this name is already there and will be replaced." : "");
                export.IsEnabled = fits;
            }

            change.Click += (_, _) =>
            {
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Save the server file",
                    FileName = Path.GetFileName(path),
                    Filter = $"Server file (*{Transfer.Extension})|*{Transfer.Extension}",
                    DefaultExt = Transfer.Extension,
                    InitialDirectory = Directory.Exists(Path.GetDirectoryName(path)) ? Path.GetDirectoryName(path) : Paths.Root,
                };
                if (dlg.ShowDialog(this) != true) return;
                path = dlg.FileName;
                Refresh();
            };
            Refresh();
            return panel;
        }, 540) as string;

    // ------------------------------------------------------------ import

    async void BtnImport_Click(object sender, RoutedEventArgs e)
    {
        if (!StoppedOrSay("a server is imported")) return;
        var open = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose the server file",
            Filter = $"Server file (*{Transfer.Extension})|*{Transfer.Extension}",
        };
        if (open.ShowDialog(this) != true) return;
        await ImportPackageAsync(open.FileName);
    }

    /// <summary>Imports a .mchost file (from Tools > Import server, or dropped on the window).</summary>
    async Task ImportPackageAsync(string package)
    {
        if (!StoppedOrSay("a server is imported")) return;
        PackageManifest manifest;
        try { manifest = await Task.Run(() => Transfer.ReadManifest(package)); }
        catch (Exception ex)
        {
            Ask("Can't read that file:\n" + ex.Message, "Import server", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // the server gets its own folder named after it: servers\<name> (made unique)
        var dest = Paths.NewServerDir(string.IsNullOrWhiteSpace(manifest.Config.Name) ? "Imported server" : manifest.Config.Name);

        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dest))!);
        if (drive.AvailableFreeSpace < manifest.TotalBytes * 21 / 20)
        {
            Ask($"The server needs about {GB(manifest.TotalBytes)}, but drive {drive.Name} only has {GB(drive.AvailableFreeSpace)} free.",
                "Not enough space", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var c = manifest.Config;
        var replacing = HostConfig.Load()?.IsSetUp == true
            ? "\n\nIt's added to your servers (the menu next to the name) and becomes the one shown. Your other servers stay as they are."
            : "";
        if (Ask($"Import the server exported on {manifest.FromPc} ({manifest.Created:g})?\n\n" +
                $"{c.Edition} {c.Version}  ·  {c.RamGB} GB RAM  ·  Java {c.RequiredJava}+\n" +
                $"{manifest.RegionFiles} world region files, {manifest.Files} other files, {manifest.Downloads.Count} plugins to download\n\n" +
                $"Into: {dest} ({GB(manifest.TotalBytes)}){replacing}",
                "Import server", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;

        List<string> problems = new();
        Log.Host($"Importing {package} into {dest} ...");
        var (ok, cancelled, error) = await RunTaskAsync(async (p, ct) => problems = await Transfer.ImportAsync(package, dest, p, ct));
        if (!ok)
        {
            Log.Host(cancelled ? "Import cancelled." : "Import failed: " + error?.Message);
            if (!cancelled) Sounds.Error();
            if (_closeAfterTask) return;
            var question = (cancelled ? "Import cancelled." : "The import failed:\n" + error?.Message) +
                           $"\n\nRemove the partly imported files from {dest}? (The folder was empty before.)";
            if (Ask(question, "Import server", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                try { Directory.Delete(dest, recursive: true); } catch (Exception ex) { Log.Host("Could not remove it: " + ex.Message); }
            return;
        }

        // add it to the list of servers, with the old PC's settings for it, and switch to it
        var profile = new ServerProfile();
        c.CopyTo(profile);
        profile.ServerDir = dest;
        AddServerProfile(profile);
        // app-wide settings from the old PC only fill in what this PC doesn't have yet
        var config = HostConfig.LoadOrNew();
        if (string.IsNullOrWhiteSpace(config.CurseForgeKey) && !string.IsNullOrWhiteSpace(c.CurseForgeKey)) { config.CurseForgeKey = c.CurseForgeKey; config.Save(); }
        await RefreshSetupBannerAsync();
        Log.Host($"Imported the server into {dest}." + (problems.Count > 0 ? $" {problems.Count} problem(s):" : ""));
        foreach (var p in problems) Log.Host("  " + p);
        if (problems.Count == 0) Sounds.Success(); else Sounds.Error();
        if (_closeAfterTask) return;

        Ask("The server is imported" + (problems.Count > 0 ? ", with problems:\n\n" + string.Join("\n", problems) + "\n\n" : ".\n\n") +
            "Before starting it on this PC:\n" +
            "1. Set up this PC (Java, playit, Cloudflare) if the yellow banner asks.\n" +
            "2. Link playit.gg account, using the same playit account. Then check at playit.gg that your tunnel uses this PC's agent, so friends keep the same address.\n" +
            "3. BlueMap's map re-renders the first time the server runs.",
            "Import server", MessageBoxButton.OK, problems.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }
}
