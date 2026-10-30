using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MinecraftHost.Core;

namespace MinecraftHost;

/// <summary>
/// The help tour (the ? button): steps through the fields of the open tab, dimming everything except the one being
/// explained. Steps whose field isn't showing right now (e.g. the editor before a file is open) are skipped.
/// </summary>
public partial class MainWindow
{
    sealed record TourStep(Func<FrameworkElement?> Target, string Title, string Text);

    List<TourStep> _tour = new();
    int _tourIndex;

    static FrameworkElement? Up(FrameworkElement? e, int levels = 1)
    {
        for (var i = 0; i < levels && e != null; i++) e = (e.Parent ?? VisualTreeHelper.GetParent(e)) as FrameworkElement;
        return e;
    }

    List<TourStep> StepsForCurrentTab()
    {
        var helpStep = new TourStep(() => BtnHelp, "Help is here any time",
            "Every page has its own tour: open a page and press Help. Tools (set up this PC, backups, moving the server), Settings and Info are at the bottom of the sidebar.");

        if (TabConnections.IsChecked == true) return new()
        {
            new(() => JoinPlayit, "How friends join",
                "playit.gg needs no router setup. Port forwarding is for when you can forward the port on your router yourself; then no playit.gg or Cloudflare is needed."),
            new(() => ChkTunnel, "Let friends join",
                "With playit.gg, opens the tunnel when the server starts. Off keeps it to your own network."),
            new(() => Up(TxtFriend), "The address friends use",
                "Give this to your friends; they add it under Multiplayer > Add Server. Copy it, or use the pencil to change it."),
            new(() => ConnList, "Your connections",
                "Minecraft, then each plugin that needs a connection of its own (voice chat, Bedrock players, web maps). The dot shows its state: blue is live, grey is ready or off, amber needs setup. Pick one to see its settings."),
            new(() => ConnDetail, "Its settings",
                "Web maps get a free Cloudflare link while the server runs (it changes every start). Voice chat and Bedrock need a playit tunnel of their own: add one at playit.gg pointing to the port shown, then paste its address here."),
            helpStep,
        };
        if (TabLog.IsChecked == true) return new()
        {
            new(() => LogList, "Server log",
                "Everything the server and this app report: who joins, errors, and what installs or backups did. If something goes wrong, the answer is usually here."),
            new(() => Up(TxtCommand), "Server commands",
                "Type a command and press Enter, without the slash. For example: op YourName, or say Hello. Works while the server is running."),
            helpStep,
        };
        if (TabAddons.IsChecked == true || TabMods.IsChecked == true)
        {
            var what = TabMods.IsChecked == true ? "mods" : "plugins and datapacks";
            return new()
            {
                new(() => Up(TxtAddonSearch), "Search", $"Type part of a name to find it in the list of {what}."),
                new(() => Up(AddonStatus), "What the dots mean",
                    "Blue works and is up to date. Red is broken, made for another version, or has an update. Grey means there's nothing to go on yet."),
                new(() => BtnCheckUpdates, "Check for updates",
                    "Looks every file up on Modrinth (and CurseForge, if you add a key). It also runs by itself when you open this tab."),
                new(() => BtnUpdateAll, "Update all", "Downloads every update found. The old files go to backups. The server must be stopped."),
                new(() => BtnCurseForge, "CurseForge key",
                    "Optional. CurseForge needs a free personal key to look files up. Modrinth needs nothing."),
                new(() => AddonList, $"Your {what}",
                    "Each row: the dot, the name (hover the i for details), Update when there is one, show the file, open its web page, delete it (it goes to backups), and the switch to turn it on or off. Tip: drag files onto the window to install them."),
                helpStep,
            };
        }
        if (TabConfigs.IsChecked == true) return new()
        {
            new(() => Up(CfgServer, 2), "Kinds of files",
                "Server settings, each plugin's settings, the files inside your datapacks, and mod settings. The number is how many files each has."),
            new(() => Up(TxtConfigSearch, 2), "Search", "Type part of a file name to find it."),
            new(() => ConfigList, "Files", "Click one to open it on the right. The dot shows how the plugin or datapack it belongs to is doing."),
            new(() => Up(ViewForm, 2), "Settings or Text",
                "Settings shows one row per setting with its explanation; switches for on/off. Text shows the raw file, for lists and anything else."),
            new(() => BtnConfigSave, "Save",
                "Saves your changes (or press Ctrl+S). The old file is backed up first, and only the values you changed are rewritten."),
            new(() => BtnConfigRevert, "Revert", "Throws away changes you haven't saved."),
            helpStep,
        };
        if (TabTools.IsChecked == true) return new()
        {
            new(() => ToolsPcCard, "This PC",
                "Set up this PC installs Java and the tunnel programs. Link playit.gg account connects this PC to your playit account. Set public address is the address friends join with."),
            new(() => ToolsServerCard, "Server", "Back up worlds saves a zip of every world into backups\\<server name>. Install a modpack sets the server up from a Modrinth .mrpack or CurseForge .zip, with the right Minecraft version and mod loader (or drop the file onto the window)."),
            new(() => ToolsMoveCard, "Move to another PC",
                "Export packs the whole server into one .mchost file. On the new PC, use Import, or just drop the file onto the window."),
            helpStep,
        };
        if (TabSettings.IsChecked == true) return new()
        {
            new(() => ChkOpenAtLogin, "Open when Windows starts", "The app opens minimized on the taskbar when you sign in."),
            new(() => ChkAutoStart, "Start the server when the app opens",
                "Together with the switch above, the server comes up by itself after the PC starts."),
            new(() => ChkSounds, "Sounds", "A short jingle when the server is online or an export/import finishes, and a soft tone when something fails."),
            new(() => HighlightList, "Highlight colour", "The colour of the open page, buttons, switches and links. Auto uses the menu colour's own; the others go with any menu colour."),
            new(() => ChkAnimations, "Animations", "Smooth fades and slides when you switch pages or sections, open panels, fold cards or change colours. Switch off for instant changes."),
            new(() => ThemeList, "Menu colour", "The background, sidebar and text colours: dark ones, light ones, and some tinted ones. Each tile shows a little window in those colours. It changes straight away."),
            helpStep,
        };

        // Server tab, which also covers the parts at the top that every tab shares
        return new()
        {
            new(() => TitleText, "Server name", "Click the name to change it. It's also the window's title."),
            new(() => BtnServers, "Your servers",
                "The app can manage several servers. Open this to switch to another one, add one (a folder you already have, or a new one), or remove one from the list. Its files are never deleted. Switching needs the server stopped."),
            new(() => StatusPill, "Status", "Stopped, Starting, Running or Stopping. Amber means it's busy."),
            new(() => ControlBar, "Start and stop",
                "Start the server (it becomes Restart while running) and Stop it safely. Safe mode starts without plugins to find what's breaking it. Force stop is for when it's stuck."),
            new(() => Up(TabServer), "Tabs",
                "Connections is how people reach the server; Logs is what it says; Plugins, Mods and Configs manage what's in it. A blue dot means something there needs a look."),
            new(() => Up(SecGeneral), "Sections",
                "The Server tab has four parts: General (software, memory, folder), Players, World (the server list and gameplay) and Backups & restarts. This tour covers the one that's open."),
            new(() => PlayersCard, "Players", "Who's online and who was on recently. Click a name to message, teleport, change game mode, make operator, kick or ban."),
            new(() => WhitelistCard, "Whitelist",
                "With the switch on, only the players listed can join. Type a name and press Add, or click Ã— to remove someone."),
            new(() => OpsCard, "Operators", "Players who can use admin commands in game."),
            new(() => RestartCard, "Restarts",
                "Switch this on to restart the server by itself, once a day or every few hours from the time you choose. Players get a warning in chat first, and worlds are saved like with the Restart button."),
            new(() => Up(CmbEdition, 2), "Server type and version",
                "Paper runs plugins; Fabric and Forge run mods; Vanilla is the plain game. The version must match what players use."),
            new(() => BtnInstall, "Install or switch",
                "Installs what you picked, or gets the latest build. It warns before anything that could break worlds, plugins or mods, and offers a backup first."),
            new(() => Up(BtnRamDown), "Memory", "How much of this PC's memory the server may use. Used from the next start."),
            new(() => BtnLaunchArgs, "Launch arguments",
                "For advanced users: extra options for Java or the server. You can leave this closed."),
            new(() => Up(TxtFolder), "Server folder",
                "Where the worlds, plugins and settings are. Choose server folder points the app at another server."),
            new(() => BackupCard, "Backups", "Automatic world backups while the server runs, and how many to keep. Back up now makes one straight away."),
            new(() => ListingCard, "Server list", "The message of the day and icon players see in their multiplayer list, with a preview."),
            new(() => GameplayCard, "Gameplay", "Difficulty, game mode, player limit, view distance, PVP and flight."),
            helpStep,
        };
    }


    void BtnHelp_Click(object sender, RoutedEventArgs e)
    {
        _tour = StepsForCurrentTab();
        _tourIndex = -1;
        TourLayer.Visibility = Visibility.Visible;
        Move(+1);
    }

    static bool Showing(FrameworkElement? e) => e != null && e.IsVisible && e.ActualWidth > 0 && e.ActualHeight > 0;

    /// <summary>Goes to the next (or previous) step whose field is showing; ends after the last.</summary>
    void Move(int direction)
    {
        var i = _tourIndex + direction;
        while (i >= 0 && i < _tour.Count && !Showing(_tour[i].Target())) i += direction;
        if (i >= _tour.Count) { EndTour(); return; }
        if (i < 0) return;
        _tourIndex = i;
        var target = _tour[i].Target()!;
        target.BringIntoView();
        // wait for any scrolling to settle before measuring where the field is
        Dispatcher.BeginInvoke(ShowStep, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    void ShowStep()
    {
        if (TourLayer.Visibility != Visibility.Visible || _tourIndex < 0 || _tourIndex >= _tour.Count) return;
        var step = _tour[_tourIndex];
        var target = step.Target();
        if (!Showing(target)) { Move(+1); return; }

        var shown = _tour.Where(s => Showing(s.Target())).ToList();
        var number = shown.IndexOf(step) + 1;
        TourCount.Text = $"STEP {number} OF {shown.Count}";
        TourTitle.Text = step.Title;
        TourText.Text = step.Text;
        BtnTourBack.IsEnabled = _tour.Take(_tourIndex).Any(s => Showing(s.Target()));
        BtnTourNext.Content = number == shown.Count ? "Done" : "Next";

        // the hole around the field, a little larger than it
        var layer = new Rect(0, 0, TourLayer.ActualWidth, TourLayer.ActualHeight);
        // TourLayer sits beside the content (not above it in the tree), so measure relative to it rather than as an ancestor
        var bounds = target!.TransformToVisual(TourLayer).TransformBounds(new Rect(0, 0, target.ActualWidth, target.ActualHeight));
        bounds.Inflate(6, 6);
        bounds.Intersect(layer);
        TourShade.Data = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(layer), new RectangleGeometry(bounds, 10, 10));
        TourRing.Margin = new Thickness(bounds.X, bounds.Y, 0, 0);
        TourRing.Width = bounds.Width;
        TourRing.Height = bounds.Height;

        // the card goes below the field, else above, else beside it; always inside the window
        TourCard.Measure(new Size(TourCard.Width, double.PositiveInfinity));
        var w = TourCard.Width;
        var h = TourCard.DesiredSize.Height;
        const double gap = 12, edge = 12;
        double x = Math.Clamp(bounds.X, edge, Math.Max(edge, layer.Width - w - edge)), y;
        if (bounds.Bottom + gap + h <= layer.Height - edge) y = bounds.Bottom + gap;
        else if (bounds.Top - gap - h >= edge) y = bounds.Top - gap - h;
        else
        {
            y = Math.Clamp(bounds.Y, edge, Math.Max(edge, layer.Height - h - edge));
            x = bounds.Right + gap + w <= layer.Width - edge ? bounds.Right + gap : Math.Max(edge, bounds.X - gap - w);
        }
        TourCard.Margin = new Thickness(x, y, 0, 0);
        BtnTourNext.Focus();
    }

    void EndTour()
    {
        TourLayer.Visibility = Visibility.Collapsed;
        _tour.Clear();
    }

    void BtnTourNext_Click(object sender, RoutedEventArgs e) => Move(+1);
    void BtnTourBack_Click(object sender, RoutedEventArgs e) => Move(-1);
    void BtnTourEnd_Click(object sender, RoutedEventArgs e) => EndTour();
    void TourLayer_SizeChanged(object sender, SizeChangedEventArgs e) => ShowStep();

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (TourLayer.Visibility == Visibility.Visible && _overlays.Count == 0)
        {
            switch (e.Key)
            {
                case Key.Escape: EndTour(); e.Handled = true; return;
                case Key.Right: Move(+1); e.Handled = true; return;
                case Key.Left: Move(-1); e.Handled = true; return;
            }
        }
        base.OnPreviewKeyDown(e);
    }
}
