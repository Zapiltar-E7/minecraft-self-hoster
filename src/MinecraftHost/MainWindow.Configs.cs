using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using MinecraftHost.Core;

namespace MinecraftHost;

/// <summary>
/// The Configs tab, with sub-tabs for server, plugin, datapack and mod files. Files open as a grid of
/// settings (name + value per row); the Text view shows the raw file for things the grid can't show, like lists.
/// </summary>
public partial class MainWindow
{
    ConfigFiles.Document? _doc;          // the file as it is on disk
    ConfigFile? _docFile;
    bool _dirty;
    bool _loadingDoc;
    bool _configsLoaded;
    ListCollectionView? _configView;
    List<ConfigFile>? _configFiles;
    ConfigCategory _configCategory = ConfigCategory.Server;

    // settings grid state
    bool _formMode;
    string _formBase = "";               // text the grid was parsed from
    List<ConfigEntry> _formEntries = new();
    List<SettingItem> _formItems = new();

    void InitConfigTab()
    {
        // Ctrl+S saves from anywhere on the tab
        ConfigPage.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) { SaveConfig(); e.Handled = true; }
        };
        SetDirty(false);
    }

    // ------------------------------------------------------------ file list

    void LoadConfigList()
    {
        var files = ConfigFiles.Find();
        _configFiles = files;
        ApplyConfigHealth();
        _configView = new ListCollectionView(files) { Filter = o => MatchesSearch((ConfigFile)o) };
        _configView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ConfigFile.Group)));
        _loadingDoc = true;
        ConfigList.ItemsSource = _configView;
        if (_docFile != null) ConfigList.SelectedItem = files.FirstOrDefault(f => f.Key == _docFile.Key && f.Category == _configCategory);
        _loadingDoc = false;
        _configsLoaded = true;
        UpdateConfigCount();
    }

    bool MatchesSearch(ConfigFile f)
    {
        var q = TxtConfigSearch.Text.Trim();
        return f.Category == _configCategory && (q.Length == 0 || f.RelativePath.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    void UpdateConfigCount()
    {
        ConfigCount.Text = _configView == null ? "" : $"{_configView.Count} files in {Paths.ServerDir}";
        // the sub-tabs show how many files each has
        foreach (var (box, category) in new[] { (CfgServerCount, ConfigCategory.Server), (CfgPluginsCount, ConfigCategory.Plugin),
                                               (CfgDatapacksCount, ConfigCategory.Datapack), (CfgModsCount, ConfigCategory.Mod) })
            box.Text = _configFiles == null ? "" : _configFiles.Count(f => f.Category == category).ToString();

        var none = _configFiles != null && !_configFiles.Any(f => f.Category == _configCategory);
        var noMatch = !none && _configView is { Count: 0 };
        ConfigListEmpty.Visibility = none || noMatch ? Visibility.Visible : Visibility.Collapsed;
        ConfigListEmpty.Text = noMatch ? "Nothing matches the search." : _configCategory switch
        {
            ConfigCategory.Plugin => "No plugin settings yet. Plugins create theirs the first time the server runs with them.",
            ConfigCategory.Datapack => "No datapacks in this world yet. They go in the world's datapacks folder.",
            ConfigCategory.Mod => "No mod settings yet. Mods create theirs in the config folder the first time the server runs with them.",
            _ => "No server files yet. Start the server once to create them.",
        };
    }

    void ConfigCategory_Checked(object sender, RoutedEventArgs e)
    {
        if (ConfigList == null) return;   // fires during InitializeComponent
        var before = _configCategory;
        _configCategory = sender == CfgPlugins ? ConfigCategory.Plugin : sender == CfgDatapacks ? ConfigCategory.Datapack
            : sender == CfgMods ? ConfigCategory.Mod : ConfigCategory.Server;
        ConfigListTitle.Text = _configCategory switch
        {
            ConfigCategory.Plugin => "PLUGIN FILES",
            ConfigCategory.Datapack => "DATAPACK FILES",
            ConfigCategory.Mod => "MOD FILES",
            _ => "SERVER FILES",
        };
        // the open file stays open; it's only highlighted in its own sub-tab
        _loadingDoc = true;
        _configView?.Refresh();
        ConfigList.SelectedItem = _docFile?.Category == _configCategory ? _docFile : null;
        _loadingDoc = false;
        if (_configCategory != before) Motion.PageIn(ConfigList, _configCategory > before ? 18 : -18, 0);   // from the side of the tab picked
        UpdateConfigCount();
    }

    void TxtConfigSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = TxtConfigSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _configView?.Refresh();
        UpdateConfigCount();
    }

    void BtnConfigRefresh_Click(object sender, RoutedEventArgs e) => LoadConfigList();

    void ConfigList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingDoc || ConfigList.SelectedItem is not ConfigFile file) return;
        if (_docFile?.Key == file.Key) return;
        if (!ConfirmDiscardConfig())
        {
            _loadingDoc = true;                 // put the selection back on the file being edited
            ConfigList.SelectedItem = _docFile == null ? null : _configView?.Cast<ConfigFile>().FirstOrDefault(f => f.Key == _docFile.Key);
            _loadingDoc = false;
            return;
        }
        OpenConfig(file, preferForm: true);
    }

    // ------------------------------------------------------------ open / views

    void OpenConfig(ConfigFile file, bool preferForm)
    {
        try { _doc = ConfigFiles.Load(file); }
        catch (Exception ex)
        {
            Ask($"Could not open {file.RelativePath}:\n{ex.Message}", "Config files", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _docFile = file;
        ConfigTitle.Text = $"{file.Group}  /  {file.Name}";
        ConfigPath.Text = file.ZipEntry == null ? file.FullPath : $"{file.FullPath}  ›  {file.ZipEntry}";
        ConfigToolbar.Visibility = Visibility.Visible;

        _loadingDoc = true;
        ConfigEditor.Text = _doc.Text;
        ConfigEditor.IsEnabled = true;
        ConfigEditor.ScrollToHome();
        ConfigEditor.CaretIndex = 0;
        _loadingDoc = false;

        // the grid is offered when the format is understood and the file has settings in it
        var canForm = TryBuildForm(_doc.Text, out _);
        ViewForm.IsEnabled = canForm;
        ShowView(canForm && preferForm);
        SetDirty(false);

        string? notice = null;
        if (!canForm && ConfigForm.FormatOf(file.FormatPath) == ConfigFormat.None) notice = "This is a plain text file, so it opens as text.";
        else if (!canForm) notice = "This file has no simple settings to show as a grid (only lists or nested data), so it opens as text.";
        if (file.Category == ConfigCategory.Datapack)
            notice = (notice == null ? "" : notice + " ") + "Datapack changes apply after /reload or a restart." +
                     (file.ZipEntry != null ? " The whole pack is backed up before each save." : "");
        if (_server?.IsRunning == true)
            notice = (notice == null ? "" : notice + " ") + "The server is running: most changes take effect after a restart (some plugins have a reload command).";
        ShowConfigNotice(notice);
    }

    /// <summary>Parses text into the settings grid. Returns false if the format isn't supported, has no settings, or doesn't parse.</summary>
    bool TryBuildForm(string text, out string? error)
    {
        error = null;
        if (_docFile == null || ConfigForm.FormatOf(_docFile.FormatPath) == ConfigFormat.None) return false;
        List<ConfigEntry> entries;
        try { entries = ConfigForm.Parse(_docFile.FormatPath, text); }
        catch (Exception ex) { error = ex.Message; return false; }
        entries = entries.Where(e => e.Key != "==").ToList();   // Bukkit's internal type markers
        if (entries.Count == 0) return false;

        _formBase = text;
        _formEntries = entries;
        _formItems = entries.Select(e =>
        {
            var item = new SettingItem(e);
            item.Edited += _ => UpdateDirty();
            return item;
        }).ToList();
        FilterForm();
        return true;
    }

    void FilterForm()
    {
        var q = TxtSettingFilter.Text.Trim();
        var rows = new List<object>();
        var multipleSections = _formItems.Select(i => i.Entry.Section).Distinct().Count() > 1;
        foreach (var group in _formItems
                     .Where(i => q.Length == 0 || i.Entry.Path.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                                 (i.Comment?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                                 i.Text.Contains(q, StringComparison.OrdinalIgnoreCase))
                     .GroupBy(i => i.Entry.Section))
        {
            if (multipleSections || group.Key.Length > 0)
                rows.Add(new SettingSection { Title = group.Key.Length == 0 ? "General" : group.Key.Replace(".", "  ›  ") });
            rows.AddRange(group);
        }
        FormList.ItemsSource = rows;
    }

    void TxtSettingFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        SettingFilterHint.Visibility = TxtSettingFilter.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_formMode) FilterForm();
    }

    void ShowView(bool form)
    {
        _formMode = form;
        _loadingDoc = true;
        ViewForm.IsChecked = form;
        ViewText.IsChecked = !form;
        _loadingDoc = false;
        var open = _docFile != null;   // nothing open: the empty-state hint instead of an empty editor
        ConfigEmpty.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
        FormList.Visibility = open && form ? Visibility.Visible : Visibility.Collapsed;
        TextHost.Visibility = open && !form ? Visibility.Visible : Visibility.Collapsed;
        SettingFilterBox.Visibility = form ? Visibility.Visible : Visibility.Hidden;
    }

    void ConfigView_Checked(object sender, RoutedEventArgs e)
    {
        if (_loadingDoc || _docFile == null || FormList == null) return;
        var wantForm = ViewForm.IsChecked == true;
        if (wantForm == _formMode) return;

        if (wantForm)
        {
            // text -> grid: re-read whatever was typed in the Text view
            if (!TryBuildForm(ConfigEditor.Text, out var error))
            {
                Ask(error != null
                        ? $"The text can't be read as {ConfigForm.FormatOf(_docFile.FormatPath)} right now:\n{error}\n\nFix it in the Text view first."
                        : "This file has no simple settings to show as a grid.",
                    "Settings view", MessageBoxButton.OK, MessageBoxImage.Information);
                ShowView(false);
                return;
            }
            ShowView(true);
        }
        else
        {
            // grid -> text: write the grid's edits into the text first
            var text = CurrentConfigText();
            _loadingDoc = true;
            ConfigEditor.Text = text;
            _loadingDoc = false;
            ShowView(false);
        }
        UpdateDirty();
    }

    /// <summary>The file's text including any unsaved edits, from whichever view is active.</summary>
    string CurrentConfigText()
    {
        if (!_formMode || _docFile == null) return ConfigEditor.Text;
        var edits = _formItems.Where(i => i.IsChanged).ToDictionary(i => i.Entry, i => i.Text);
        return edits.Count == 0 ? _formBase : ConfigForm.Apply(_docFile.FormatPath, _formBase, _formEntries, edits);
    }

    // ------------------------------------------------------------ dirty state

    void ShowConfigNotice(string? text)
    {
        ConfigNotice.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
        ConfigNoticeText.Text = text ?? "";
    }

    static string Normalize(string s) => s.Replace("\r\n", "\n");

    void UpdateDirty()
    {
        if (_doc == null) { SetDirty(false); return; }
        if (_formMode)
            SetDirty(_formItems.Any(i => i.IsChanged) || Normalize(_formBase) != Normalize(_doc.Text));
        else
            SetDirty(Normalize(ConfigEditor.Text) != Normalize(_doc.Text));
    }

    void SetDirty(bool dirty)
    {
        _dirty = dirty;
        ConfigDirtyDot.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;
        BtnConfigSave.IsEnabled = BtnConfigRevert.IsEnabled = dirty;
    }

    void ConfigEditor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingDoc || _doc == null || _formMode) return;
        UpdateDirty();
    }

    void ConfigEditor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.None)
        {
            // YAML doesn't allow tabs, so Tab inserts two spaces
            ConfigEditor.SelectedText = "  ";
            ConfigEditor.Select(ConfigEditor.SelectionStart + 2, 0);
            e.Handled = true;
        }
    }

    // ------------------------------------------------------------ save / revert

    void BtnConfigSave_Click(object sender, RoutedEventArgs e) => SaveConfig();

    bool SaveConfig()
    {
        if (_doc == null || _docFile == null || !_dirty) return true;
        string text;
        try { text = CurrentConfigText(); }
        catch (Exception ex)
        {
            Ask("Could not apply the changes: " + ex.Message, "Config files", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        var problem = ConfigFiles.Validate(_docFile.FormatPath, text);
        if (problem != null &&
            Ask(problem + "\n\nThe plugin may fail to load this file. Save anyway?", "Check this file",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return false;
        try
        {
            var changedCount = _formMode ? _formItems.Count(i => i.IsChanged) : 0;
            var backup = ConfigFiles.Save(_doc, text);
            Log.Host($"Saved {_docFile.RelativePath}{(changedCount > 0 ? $" ({changedCount} setting(s) changed)" : "")}; " +
                     $"previous version in {Path.GetRelativePath(Paths.Root, Path.GetDirectoryName(backup)!)}.");
            var filter = TxtSettingFilter.Text;
            var wasForm = _formMode;
            OpenConfig(_docFile, preferForm: wasForm);
            TxtSettingFilter.Text = filter;
            ShowConfigNotice(_server?.IsRunning == true
                ? "Saved. Restart the server (or use the plugin's reload command) for the change to take effect."
                : "Saved. The previous version is in backups\\configs.");
            return true;
        }
        catch (Exception ex)
        {
            Ask("Could not save: " + ex.Message, "Config files", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    void BtnConfigRevert_Click(object sender, RoutedEventArgs e)
    {
        if (_docFile == null) return;
        if (_dirty && Ask($"Discard your changes to {_docFile.Name}?", "Revert", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        OpenConfig(_docFile, preferForm: _formMode || !ViewForm.IsEnabled);
    }

    void BtnConfigShow_Click(object sender, RoutedEventArgs e)
    {
        if (_docFile != null) Process.Start("explorer.exe", $"/select,\"{_docFile.FullPath}\"");
        else BtnFolder_Click(sender, e);
    }

    /// <summary>True if there are no unsaved changes, or the user chose what to do with them.</summary>
    bool ConfirmDiscardConfig()
    {
        if (!_dirty || _docFile == null) return true;
        var answer = Ask($"Save your changes to {_docFile.Name}?", "Unsaved changes", MessageBoxButton.YesNoCancel);
        if (answer == MessageBoxResult.Cancel) return false;
        if (answer == MessageBoxResult.Yes) return SaveConfig();
        SetDirty(false);
        return true;
    }

    /// <summary>Called when the server folder changes: the list and open file no longer apply.</summary>
    void ResetConfigs()
    {
        _doc = null; _docFile = null; _configsLoaded = false; _configFiles = null;
        // the plugins tab belongs to the old folder too
        _addons = null; _online.Clear(); _lastOnlineCheck = null;
        AddonList.ItemsSource = null;
        if (TabAddons.IsChecked == true) _ = ScanAddonsAsync();
        _formItems = new(); _formEntries = new(); _formBase = "";
        _loadingDoc = true;
        ConfigEditor.Text = ""; ConfigEditor.IsEnabled = false;
        ConfigList.ItemsSource = null;
        FormList.ItemsSource = null;
        _loadingDoc = false;
        ShowView(false);
        ConfigToolbar.Visibility = Visibility.Collapsed;
        SetDirty(false);
        ConfigTitle.Text = "No file open";
        ConfigPath.Text = "";
        ShowConfigNotice(null);
        if (TabConfigs.IsChecked == true) LoadConfigList();
    }
}
