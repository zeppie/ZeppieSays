using System.Windows;
using System.Windows.Media;

namespace ZeppieSays.UI;

/// <summary>A thick, round-capped arc (open at the bottom) that fills from left to right with <see cref="Value"/>.</summary>
public sealed class ArcGauge : FrameworkElement
{
    const double StartAngle = 150, Sweep = 240; // degrees, clockwise from the positive x-axis

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(ArcGauge), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track), typeof(Brush), typeof(ArcGauge), new FrameworkPropertyMetadata(Brushes.LightGray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(ArcGauge), new FrameworkPropertyMetadata(Brushes.MediumPurple, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(ArcGauge), new FrameworkPropertyMetadata(14.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public Brush Track { get => (Brush)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var radius = (Math.Min(ActualWidth, ActualHeight) - Thickness) / 2;
        if (radius <= 0) return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        Draw(dc, center, radius, Sweep, Track);
        var value = Math.Clamp(Value, 0, 1);
        if (value > 0) Draw(dc, center, radius, Math.Max(Sweep * value, 0.5), Fill);
    }

    void Draw(DrawingContext dc, Point center, double radius, double sweep, Brush brush)
    {
        var pen = new Pen(brush, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(PointAt(center, radius, StartAngle), false, false);
            ctx.ArcTo(PointAt(center, radius, StartAngle + sweep), new Size(radius, radius), 0, sweep > 180,
                SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    static Point PointAt(Point center, double radius, double degrees)
    {
        var r = degrees * Math.PI / 180;
        return new Point(center.X + radius * Math.Cos(r), center.Y + radius * Math.Sin(r));
    }
}
