using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using MinecraftHost.Core;

namespace MinecraftHost;

/// <summary>Section heading in the settings grid (e.g. "settings › spawn-limits").</summary>
public sealed class SettingSection
{
    public required string Title { get; init; }
    public string? Comment { get; init; }
}

/// <summary>One row in the settings grid: a setting's name, its description and an editable value.</summary>
public sealed class SettingItem : INotifyPropertyChanged
{
    public SettingItem(ConfigEntry entry)
    {
        Entry = entry;
        _text = entry.Value;
    }

    public ConfigEntry Entry { get; }
    public string Key => Entry.Key;
    public string? Comment => Entry.Comment;
    public bool IsBool => Entry.IsBool;
    public string ToolTip => Entry.Comment is { } c ? $"{Entry.Path}\n\n{c}" : Entry.Path;
    public bool IsChanged => _text != Entry.Value;

    /// <summary>Raised when the user edits the value.</summary>
    public event Action<SettingItem>? Edited;
    public event PropertyChangedEventHandler? PropertyChanged;

    string _text;
    public string Text
    {
        get => _text;
        set
        {
            if (_text == value) return;
            _text = value;
            Raise(nameof(Text)); Raise(nameof(BoolValue)); Raise(nameof(IsChanged));
            Edited?.Invoke(this);
        }
    }

    public bool BoolValue
    {
        get => _text == "true";
        set => Text = value ? "true" : "false";
    }

    void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Status dot colour: light blue = works / up to date, light red = broken or out of date, grey = unknown.</summary>
public sealed class HealthToBrushConverter : IValueConverter
{
    // the theme's brushes (Themes recolours them in place)
    public static System.Windows.Media.Brush Good => (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["Accent"];
    public static System.Windows.Media.Brush Bad => (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["Bad"];
    public static System.Windows.Media.Brush Unknown => (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["Unknown"];

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        Health.Good => Good,
        Health.Bad => Bad,
        _ => Unknown,
    };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Visible when the value isn't null.</summary>
public sealed class NotNullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value == null ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
