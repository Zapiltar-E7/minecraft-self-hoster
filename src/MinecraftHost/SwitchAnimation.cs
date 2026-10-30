using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Animation;

namespace MinecraftHost;

/// <summary>
/// Slides the knob and fades the colours of a Switch-style toggle when the user flips it.
/// The template's IsChecked trigger holds the resting look; this only plays the move between the two,
/// so switches that load "on", or rows a list reuses while scrolling, just appear in place.
/// </summary>
public static class SwitchAnimation
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(SwitchAnimation), new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject d, bool value) => d.SetValue(EnabledProperty, value);

    static readonly Duration Length = new(TimeSpan.FromMilliseconds(160));
    static readonly Thickness Off = new(3, 0, 0, 0), On = new(21, 0, 0, 0);   // track 38 - knob 14 - 2 x 3

    static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ToggleButton toggle || e.NewValue is not true) return;
        toggle.Checked += (_, _) => Play(toggle, true);
        toggle.Unchecked += (_, _) => Play(toggle, false);
    }

    static void Play(ToggleButton toggle, bool on)
    {
        // only a flip the user made: by mouse, or by keyboard on the focused switch
        if (!Motion.Enabled || !toggle.IsLoaded || !toggle.IsVisible || !(toggle.IsMouseOver || toggle.IsKeyboardFocusWithin)) return;
        if (toggle.Template?.FindName("Knob", toggle) is FrameworkElement knob)
            knob.BeginAnimation(FrameworkElement.MarginProperty, new ThicknessAnimation(on ? Off : On, on ? On : Off, Length)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop,   // then the trigger's resting value takes over
            });
        foreach (var name in new[] { "TrackOn", "KnobOn" })
            if (toggle.Template?.FindName(name, toggle) is UIElement layer)
                layer.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(on ? 0 : 1, on ? 1 : 0, Length) { FillBehavior = FillBehavior.Stop });
    }
}
