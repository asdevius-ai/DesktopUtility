using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DesktopUtility.Host.Controls;

/// <summary>
/// Card surface with the design system's two signature details: a soft accent spotlight that follows
/// the cursor, and a 1px highlight along the top edge. Both are drawn in OnRender, behind the card content.
/// </summary>
public sealed class SpotlightBorder : Border
{
    private static readonly Color SpotlightColor = Color.FromArgb(0x26, 0x5E, 0x6A, 0xD2); // accent @ 15%
    private static readonly Brush TopHighlight = CreateTopHighlight();
    private static readonly IEasingFunction ExpoOut = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 };

    public static readonly DependencyProperty SpotlightOpacityProperty = DependencyProperty.Register(
        nameof(SpotlightOpacity), typeof(double), typeof(SpotlightBorder),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SpotlightRadiusProperty = DependencyProperty.Register(
        nameof(SpotlightRadius), typeof(double), typeof(SpotlightBorder), new PropertyMetadata(170.0));

    private Point _mouse;

    public double SpotlightOpacity
    {
        get => (double)GetValue(SpotlightOpacityProperty);
        set => SetValue(SpotlightOpacityProperty, value);
    }

    public double SpotlightRadius
    {
        get => (double)GetValue(SpotlightRadiusProperty);
        set => SetValue(SpotlightRadiusProperty, value);
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        Animate(1);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        Animate(0);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _mouse = e.GetPosition(this);
        if (SpotlightOpacity > 0) InvalidateVisual();
    }

    private void Animate(double to)
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            SpotlightOpacity = to;
            return;
        }

        BeginAnimation(SpotlightOpacityProperty,
            new DoubleAnimation(to, TimeSpan.FromMilliseconds(250)) { EasingFunction = ExpoOut });
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        double w = ActualWidth, h = ActualHeight;
        if (w < 4 || h < 4) return;

        double radius = CornerRadius.TopLeft;
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h), radius, radius));

        if (SpotlightOpacity > 0.01)
        {
            var spot = new RadialGradientBrush(SpotlightColor, Colors.Transparent)
            {
                MappingMode = BrushMappingMode.Absolute,
                Center = _mouse,
                GradientOrigin = _mouse,
                RadiusX = SpotlightRadius,
                RadiusY = SpotlightRadius,
                Opacity = SpotlightOpacity
            };
            dc.DrawRectangle(spot, null, new Rect(0, 0, w, h));
        }

        dc.DrawRectangle(TopHighlight, null, new Rect(radius, 0, Math.Max(0, w - radius * 2), 1));
        dc.Pop();
    }

    private static Brush CreateTopHighlight()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0),
                new GradientStop(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF), 0.5),
                new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1)
            }
        };
        brush.Freeze();
        return brush;
    }
}
