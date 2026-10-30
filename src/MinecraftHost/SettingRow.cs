using System.Windows;
using System.Windows.Controls;

namespace MinecraftHost;

/// <summary>
/// One row of a settings panel: an icon, a title and an optional hint on the left, the control (its content) on the
/// right, and optionally more below it (chips, a preview). Its look is the implicit style in App.xaml.
/// </summary>
public class SettingRow : ContentControl
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(nameof(Icon), typeof(string), typeof(SettingRow));
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(SettingRow));
    public static readonly DependencyProperty HintProperty = DependencyProperty.Register(nameof(Hint), typeof(object), typeof(SettingRow));
    public static readonly DependencyProperty BelowProperty = DependencyProperty.Register(nameof(Below), typeof(object), typeof(SettingRow));

    /// <summary>A Segoe Fluent Icons glyph.</summary>
    public string? Icon { get => (string?)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public string? Title { get => (string?)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    /// <summary>A short muted line under the title: text, or a named TextBlock that code fills in.</summary>
    public object? Hint { get => GetValue(HintProperty); set => SetValue(HintProperty, value); }
    /// <summary>Content across the row's width, under the title and control.</summary>
    public object? Below { get => GetValue(BelowProperty); set => SetValue(BelowProperty, value); }
}
