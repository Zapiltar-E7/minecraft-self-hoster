using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MinecraftHost;

/// <summary>
/// The app's animations: tab pages sliding in, panels fading in, cards folding, hover fades and theme fades.
/// All of them check <see cref="Enabled"/> (Settings → Animations); when it's off, things just appear.
/// </summary>
public static class Motion
{
    public static bool Enabled { get; set; } = true;

    static readonly IEasingFunction Out = new CubicEase { EasingMode = EasingMode.EaseOut };
    static Duration Ms(int ms) => new(TimeSpan.FromMilliseconds(ms));

    /// <summary>
    /// A page coming into view: fades in while sliding a few pixels, from the side it was picked towards
    /// (a sidebar page further down rises from below, one further up drops from above; a section tab to the right
    /// comes in from the right). <paramref name="dx"/> and <paramref name="dy"/> are where it starts.
    /// </summary>
    public static void PageIn(UIElement page, double dx = 0, double dy = 10)
    {
        if (!Enabled) return;
        var move = new TranslateTransform(dx, dy);
        page.RenderTransform = move;
        page.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Ms(200)) { EasingFunction = Out, FillBehavior = FillBehavior.Stop });
        if (dx != 0) move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(dx, 0, Ms(260)) { EasingFunction = Out });
        if (dy != 0) move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(dy, 0, Ms(260)) { EasingFunction = Out });
    }

    // ------------------------------------------------------------ sliding marker

    static readonly IEasingFunction InOut = new CubicEase { EasingMode = EasingMode.EaseInOut };

    /// <summary>
    /// Keeps <paramref name="marker"/> (in the Canvas <paramref name="layer"/>, laid over the buttons) on the checked one of
    /// <paramref name="buttons"/>, sliding to the next when another is checked: the sidebar's highlight, a section tab's
    /// underline. <paramref name="underline"/>: a line along the button's bottom instead of its whole box.
    /// </summary>
    public static void Track(FrameworkElement marker, Canvas layer, IReadOnlyList<System.Windows.Controls.Primitives.ToggleButton> buttons, bool underline)
    {
        var move = new TranslateTransform();
        marker.RenderTransform = move;
        Rect? at = null;

        Rect? Target()
        {
            var b = buttons.FirstOrDefault(x => x.IsChecked == true && x.IsVisible);
            if (b == null || !layer.IsVisible || b.ActualWidth == 0) return null;
            var p = b.TranslatePoint(new Point(0, 0), layer);
            return underline ? new Rect(p.X, p.Y + b.ActualHeight - 1, b.ActualWidth, 2) : new Rect(p.X, p.Y, b.ActualWidth, b.ActualHeight);
        }

        void Place(bool slide)
        {
            var to = Target();
            if (to == null) { marker.Visibility = Visibility.Hidden; at = null; return; }
            if (to == at) return;
            var r = to.Value;
            marker.Visibility = Visibility.Visible;
            if (slide && Enabled && at != null)
            {
                DoubleAnimation A(double v) => new(v, Ms(260)) { EasingFunction = InOut };
                move.BeginAnimation(TranslateTransform.XProperty, A(r.X));
                move.BeginAnimation(TranslateTransform.YProperty, A(r.Y));
                marker.BeginAnimation(FrameworkElement.WidthProperty, A(r.Width));
                marker.BeginAnimation(FrameworkElement.HeightProperty, A(r.Height));
            }
            else
            {
                // jump (first show, a resize, a page coming back into view): drop any slide still holding the old spot
                move.BeginAnimation(TranslateTransform.XProperty, null);
                move.BeginAnimation(TranslateTransform.YProperty, null);
                marker.BeginAnimation(FrameworkElement.WidthProperty, null);
                marker.BeginAnimation(FrameworkElement.HeightProperty, null);
                (move.X, move.Y, marker.Width, marker.Height) = (r.X, r.Y, r.Width, r.Height);
            }
            at = r;
        }

        foreach (var b in buttons) b.Checked += (_, _) => Place(slide: true);   // before the layout pass, so LayoutUpdated doesn't jump it there first
        // anything else that moves the buttons (the window resizing, text wrapping above them): follow without a slide
        layer.LayoutUpdated += (_, _) => { var to = Target(); if (to != at) Place(slide: false); };
    }

    /// <summary>An in-window panel opening: the dimmed backdrop fades in and the card grows slightly into place.</summary>
    public static void PanelIn(UIElement backdrop, UIElement card)
    {
        if (!Enabled) return;
        backdrop.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Ms(140)) { FillBehavior = FillBehavior.Stop });
        var scale = new ScaleTransform(0.96, 0.96);
        card.RenderTransformOrigin = new Point(0.5, 0.5);
        card.RenderTransform = scale;
        var grow = new DoubleAnimation(0.96, 1, Ms(200)) { EasingFunction = Out };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        card.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Ms(160)) { FillBehavior = FillBehavior.Stop });
    }

    /// <summary>Something appearing in place (a hint, a banner): a quick fade.</summary>
    public static void FadeIn(UIElement element, int ms = 160)
    {
        if (!Enabled) return;
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Ms(ms)) { FillBehavior = FillBehavior.Stop });
    }

    /// <summary>Opens or folds <paramref name="body"/>: its height slides and it fades; folded, it's Collapsed.</summary>
    public static void Fold(FrameworkElement body, bool open)
    {
        body.BeginAnimation(FrameworkElement.HeightProperty, null);
        body.BeginAnimation(UIElement.OpacityProperty, null);
        if (!Enabled || !body.IsLoaded)
        {
            body.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            return;
        }
        double from, to;
        if (open)
        {
            body.Visibility = Visibility.Visible;
            body.Measure(new Size(body.ActualWidth > 0 ? body.ActualWidth : (body.Parent as FrameworkElement)?.ActualWidth ?? double.PositiveInfinity, double.PositiveInfinity));
            (from, to) = (0, body.DesiredSize.Height);
        }
        else (from, to) = (body.ActualHeight, 0);

        var slide = new DoubleAnimation(from, to, Ms(open ? 220 : 180)) { EasingFunction = Out };
        slide.Completed += (_, _) =>
        {
            body.BeginAnimation(FrameworkElement.HeightProperty, null);
            body.Height = double.NaN;
            if (!open) body.Visibility = Visibility.Collapsed;
        };
        body.BeginAnimation(FrameworkElement.HeightProperty, slide);
        body.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(open ? 0 : 1, open ? 1 : 0, Ms(open ? 220 : 140)) { FillBehavior = FillBehavior.Stop });
        if (body is Panel or Decorator) body.ClipToBounds = true;
    }

    /// <summary>Keeps an icon turning (a refresh in progress); Stop puts it back.</summary>
    public static void Spin(UIElement element, bool on)
    {
        if (element.RenderTransform is not RotateTransform rotate)
        {
            element.RenderTransformOrigin = new Point(0.5, 0.5);
            element.RenderTransform = rotate = new RotateTransform();
        }
        rotate.BeginAnimation(RotateTransform.AngleProperty, on && Enabled
            ? new DoubleAnimation(0, 360, Ms(700)) { RepeatBehavior = RepeatBehavior.Forever }
            : null);
    }

    /// <summary>Turns a chevron (or any element) to an angle, smoothly.</summary>
    public static void Turn(UIElement element, double angle)
    {
        if (element.RenderTransform is not RotateTransform rotate)
        {
            element.RenderTransformOrigin = new Point(0.5, 0.5);
            element.RenderTransform = rotate = new RotateTransform();
        }
        if (Enabled) rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(angle, Ms(200)) { EasingFunction = Out });
        else { rotate.BeginAnimation(RotateTransform.AngleProperty, null); rotate.Angle = angle; }
    }

    // ------------------------------------------------------------ hover fades

    /// <summary>
    /// Set by the button and tab styles. Their template has a "Hover" layer that a trigger shows on mouse over;
    /// this fades it in and out instead of switching it at once.
    /// </summary>
    public static readonly DependencyProperty HoverProperty = DependencyProperty.RegisterAttached(
        "Hover", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnHoverChanged));

    public static bool GetHover(DependencyObject d) => (bool)d.GetValue(HoverProperty);
    public static void SetHover(DependencyObject d, bool value) => d.SetValue(HoverProperty, value);

    static void OnHoverChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Control control || e.NewValue is not true) return;
        control.MouseEnter += (_, _) => FadeHover(control, true);
        control.MouseLeave += (_, _) => FadeHover(control, false);
    }

    static void FadeHover(Control control, bool over)
    {
        if (!Enabled || !control.IsEnabled || control.Template?.FindName("Hover", control) is not UIElement layer) return;
        // FillBehavior.Stop: once done, the trigger's resting value (shown or hidden) takes over
        layer.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(over ? 0 : 1, over ? 1 : 0, Ms(over ? 120 : 200)) { FillBehavior = FillBehavior.Stop });
    }
}
