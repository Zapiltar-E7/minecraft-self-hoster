using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using MinecraftHost.Core;

namespace MinecraftHost;

/// <summary>
/// Easter egg: click the creeper next to the server name five times quickly and it lights up (flashing white and
/// swelling, with a hissing fuse), then explodes and shakes the window. It's back a moment later. With Minecraft installed
/// on this PC it uses the game's own fuse and explosion sounds and explosion particles (<see cref="MinecraftAssets"/>);
/// otherwise its own generated sound and blocky smoke.
/// </summary>
public partial class MainWindow
{
    readonly List<DateTime> _creeperClicks = new();
    bool _creeperLit;

    void CreeperIcon_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_creeperLit) return;
        var now = DateTime.Now;
        _creeperClicks.Add(now);
        _creeperClicks.RemoveAll(t => (now - t).TotalSeconds > 1.5);
        if (_creeperClicks.Count == 1) _ = MinecraftAssets.LoadAsync();   // start looking for the game's files right away
        if (_creeperClicks.Count < 5) return;
        _creeperClicks.Clear();
        _ = CreeperGoesOffAsync();
    }

    async Task CreeperGoesOffAsync()
    {
        _creeperLit = true;
        try
        {
            var game = await MinecraftAssets.LoadAsync();

            // the fuse: flashing white faster and faster while it swells, like a creeper about to blow
            if (game.Fuse != null) Sounds.PlayWav(game.Fuse); else Sounds.CreeperFuse();
            var fuse = TimeSpan.FromSeconds(Sounds.FuseSeconds);
            var flash = new DoubleAnimationUsingKeyFrames { Duration = fuse };
            var at = 0.0;
            for (var step = 0.26; at < Sounds.FuseSeconds - 0.05; step = Math.Max(0.07, step * 0.8))
            {
                flash.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.85, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(at))));
                flash.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(Math.Min(at + step / 2, Sounds.FuseSeconds)))));
                at += step;
            }
            CreeperFlash.BeginAnimation(OpacityProperty, flash);
            var swell = new DoubleAnimation(1, 1.3, fuse) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
            CreeperScale.BeginAnimation(ScaleTransform.ScaleXProperty, swell);
            CreeperScale.BeginAnimation(ScaleTransform.ScaleYProperty, swell);
            await Task.Delay(fuse);

            // boom
            if (game.Explosions.Count > 0) Sounds.PlayWav(game.Explosions[Random.Shared.Next(game.Explosions.Count)]); else Sounds.CreeperBoom();
            CreeperFlash.BeginAnimation(OpacityProperty, null);
            CreeperFlash.Opacity = 0;
            var pop = new DoubleAnimation(1.3, 2.2, TimeSpan.FromMilliseconds(120));
            CreeperScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            CreeperScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
            CreeperIcon.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(120)));
            var center = CreeperIcon.TranslatePoint(new Point(CreeperIcon.ActualWidth / 2, CreeperIcon.ActualHeight / 2), FxLayer);
            if (game.ExplosionFrames.Count == 16) ExplodeLikeMinecraft(center, game.ExplosionFrames); else Explode(center);
            Shake();

            // a moment later it's back, a little sheepish
            await Task.Delay(2600);
            var back = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(380)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 } };
            CreeperScale.BeginAnimation(ScaleTransform.ScaleXProperty, back);
            CreeperScale.BeginAnimation(ScaleTransform.ScaleYProperty, back);
            CreeperIcon.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
            await Task.Delay(400);
        }
        finally { _creeperLit = false; }
    }

    /// <summary>Blocky smoke puffs (Minecraft's explosion particles are grey-white squares) flying out and fading.</summary>
    void Explode(Point center)
    {
        var rng = new Random();
        // a quick bright flash where the creeper was
        var burst = new Ellipse { Width = 140, Height = 140, Fill = new RadialGradientBrush(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF), Color.FromArgb(0, 0xFF, 0xFF, 0xFF)) };
        Canvas.SetLeft(burst, center.X - 70);
        Canvas.SetTop(burst, center.Y - 70);
        FxLayer.Children.Add(burst);
        var burstFade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(350));
        burstFade.Completed += (_, _) => FxLayer.Children.Remove(burst);
        burst.BeginAnimation(OpacityProperty, burstFade);

        for (var i = 0; i < 34; i++)
        {
            var size = rng.Next(6, 17);
            var shade = (byte)rng.Next(150, 245);
            var puff = new Rectangle
            {
                Width = size, Height = size,
                Fill = new SolidColorBrush(Color.FromRgb(shade, shade, shade)),
                RenderTransformOrigin = new Point(0.5, 0.5),
            };
            var scale = new ScaleTransform();
            var move = new TranslateTransform();
            puff.RenderTransform = new TransformGroup { Children = { scale, move } };
            Canvas.SetLeft(puff, center.X - size / 2.0);
            Canvas.SetTop(puff, center.Y - size / 2.0);
            FxLayer.Children.Add(puff);

            var angle = rng.NextDouble() * Math.PI * 2;
            var distance = 40 + rng.NextDouble() * 150;
            var time = TimeSpan.FromMilliseconds(500 + rng.Next(500));
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, Math.Cos(angle) * distance, time) { EasingFunction = ease });
            // the smoke drifts up a little as it slows
            move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, Math.Sin(angle) * distance * 0.8 - 25, time) { EasingFunction = ease });
            var grow = new DoubleAnimation(0.4, 1.4, time);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            var fade = new DoubleAnimation(1, 0, time) { BeginTime = TimeSpan.FromMilliseconds(120) };
            puff.Opacity = 1;
            fade.Completed += (_, _) => FxLayer.Children.Remove(puff);
            puff.BeginAnimation(OpacityProperty, fade);
        }
    }

    List<BitmapSource[]>? _explosionShades;   // the 16 frames, tinted to a few greys

    /// <summary>
    /// Minecraft's creeper explosion: a cloud of explosion puffs popping up over a short moment around the blast, each one
    /// playing the 16-frame animation in its own grey (the game picks 40-100% brightness), drawn with crisp pixels.
    /// </summary>
    void ExplodeLikeMinecraft(Point center, List<byte[]> frames)
    {
        _explosionShades ??= new[] { 0.45, 0.6, 0.75, 0.9, 1.0 }.Select(shade => frames.Select(png => Tint(png, shade)).ToArray()).ToList();
        var rng = new Random();
        for (var i = 0; i < 40; i++)
        {
            var size = rng.Next(46, 86);
            var angle = rng.NextDouble() * Math.PI * 2;
            var distance = Math.Sqrt(rng.NextDouble()) * 85;   // spread evenly over a disc
            var image = new Image { Width = size, Height = size, Opacity = 0 };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
            Canvas.SetLeft(image, center.X + Math.Cos(angle) * distance - size / 2.0);
            Canvas.SetTop(image, center.Y + Math.Sin(angle) * distance * 0.75 - size / 2.0);
            FxLayer.Children.Add(image);

            var shades = _explosionShades[rng.Next(_explosionShades.Count)];
            var start = TimeSpan.FromMilliseconds(rng.Next(0, 400));     // the game spawns them over 8 ticks
            var life = TimeSpan.FromMilliseconds(rng.Next(300, 520));     // 6-10 ticks each
            var anim = new ObjectAnimationUsingKeyFrames { BeginTime = start, Duration = life };
            for (var f = 0; f < 16; f++)
                anim.KeyFrames.Add(new DiscreteObjectKeyFrame(shades[f], KeyTime.FromTimeSpan(TimeSpan.FromTicks(life.Ticks * f / 16))));
            var show = new DoubleAnimationUsingKeyFrames { BeginTime = start, Duration = life };
            show.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            show.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(life)));
            show.Completed += (_, _) => FxLayer.Children.Remove(image);
            image.BeginAnimation(Image.SourceProperty, anim);
            image.BeginAnimation(OpacityProperty, show);
        }
    }

    /// <summary>A particle frame with its colours multiplied by <paramref name="shade"/>, like the game's grey tint.</summary>
    static BitmapSource Tint(byte[] png, double shade)
    {
        var decoder = BitmapDecoder.Create(new MemoryStream(png), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var source = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
        var stride = source.PixelWidth * 4;
        var pixels = new byte[stride * source.PixelHeight];
        source.CopyPixels(pixels, stride, 0);
        for (var i = 0; i < pixels.Length; i += 4)
            for (var c = 0; c < 3; c++) pixels[i + c] = (byte)(pixels[i + c] * shade);
        var tinted = BitmapSource.Create(source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        tinted.Freeze();
        return tinted;
    }

    /// <summary>The whole window content jolts and settles.</summary>
    void Shake()
    {
        var shift = MainContent.RenderTransform as TranslateTransform ?? new TranslateTransform();
        MainContent.RenderTransform = shift;
        var rng = new Random();
        AnimationTimeline Jolt(double strength)
        {
            var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(450) };
            for (var i = 0; i < 9; i++)
            {
                var falloff = 1 - i / 9.0;
                a.KeyFrames.Add(new LinearDoubleKeyFrame((rng.NextDouble() * 2 - 1) * strength * falloff, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(i * 50))));
            }
            a.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(450))));
            return a;
        }
        shift.BeginAnimation(TranslateTransform.XProperty, Jolt(9));
        shift.BeginAnimation(TranslateTransform.YProperty, Jolt(6));
    }
}
