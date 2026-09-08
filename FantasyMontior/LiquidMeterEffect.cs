using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace FantasyMontior;

/// <summary>Small, tiled vector currents; animation never changes layout or the sensor value.</summary>
public sealed class LiquidMeterEffect : FrameworkElement
{
    public static readonly DependencyProperty IsLiveProperty = DependencyProperty.Register(
        nameof(IsLive), typeof(bool), typeof(LiquidMeterEffect),
        new PropertyMetadata(false, (d, _) => ((LiquidMeterEffect)d).UpdateAnimation()));

    private readonly TranslateTransform _current = new(), _sparkles = new();
    private readonly DrawingBrush _mist, _stars;
    internal bool IsAnimating => _current.HasAnimatedProperties;
    public bool IsLive { get => (bool)GetValue(IsLiveProperty); set => SetValue(IsLiveProperty, value); }

    public LiquidMeterEffect()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
        _mist = CreateLayer(false, _current);
        _stars = CreateLayer(true, _sparkles);
        Loaded += (_, _) => UpdateAnimation();
        Unloaded += (_, _) => StopAnimation();
        IsVisibleChanged += (_, _) => UpdateAnimation();
        SizeChanged += (_, _) => UpdateAnimation();
    }

    private void UpdateAnimation()
    {
        if (!IsLoaded || !IsVisible || !IsLive || ActualWidth <= 0 || ActualHeight <= 0)
        {
            StopAnimation();
            return;
        }
        if (IsAnimating) return;
        Start(_current, 128, 7);
        Start(_sparkles, -128, 11);
    }

    private static void Start(TranslateTransform transform, double distance, double seconds)
    {
        var animation = new DoubleAnimation(0, distance, TimeSpan.FromSeconds(seconds))
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        Timeline.SetDesiredFrameRate(animation, 24);
        transform.BeginAnimation(TranslateTransform.XProperty, animation);
    }

    private void StopAnimation()
    {
        _current.BeginAnimation(TranslateTransform.XProperty, null);
        _sparkles.BeginAnimation(TranslateTransform.XProperty, null);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var bounds = new Rect(RenderSize);
        dc.DrawRectangle(_mist, null, bounds);
        dc.DrawRectangle(_stars, null, bounds);
    }

    private static DrawingBrush CreateLayer(bool stars, Transform transform)
    {
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            // An explicit tile rectangle keeps both currents seamless at the wrap point.
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 128, 6));
            if (stars)
            {
                var light = new SolidColorBrush(Color.FromArgb(175, 240, 255, 213));
                foreach (var point in new[] { new Point(8, 2), new Point(23, 4.4), new Point(48, 2.8),
                             new Point(73, 1.5), new Point(89, 4.5), new Point(115, 3.2) })
                    dc.DrawEllipse(light, null, point, .65, .4);
            }
            else
            {
                var glow = new RadialGradientBrush(Color.FromArgb(145, 235, 255, 201), Colors.Transparent);
                dc.DrawEllipse(glow, null, new Point(34, 3), 30, 3);
                dc.DrawEllipse(glow, null, new Point(99, 4), 24, 2);
                var ribbon = new SolidColorBrush(Color.FromArgb(90, 225, 255, 192));
                dc.DrawGeometry(ribbon, null, Geometry.Parse(
                    "M0,3 C16,0 32,0 48,3 S80,6 96,3 S112,0 128,3 L128,3.7 C112,0.7 112,0.7 96,3.7 S64,6.7 48,3.7 S16,0.7 0,3.7 Z"));
            }
        }
        drawing.Freeze();
        return new DrawingBrush(drawing)
        {
            TileMode = TileMode.Tile,
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, 128, 6),
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, 128, 6),
            Transform = transform
        };
    }
}
