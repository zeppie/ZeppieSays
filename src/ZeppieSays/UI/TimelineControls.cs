using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ZeppieSays.Notes;

namespace ZeppieSays.UI;

/// <summary>
/// Something drawn along the length of a recording with a playhead, where a click or drag moves the playback
/// to that moment. <see cref="SeekCommand"/> gets the time in seconds.
/// </summary>
public abstract class SeekableControl : FrameworkElement
{
    public static readonly DependencyProperty DurationProperty = DependencyProperty.Register(
        nameof(Duration), typeof(double), typeof(SeekableControl),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PositionProperty = DependencyProperty.Register(
        nameof(Position), typeof(double), typeof(SeekableControl),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SeekCommandProperty = DependencyProperty.Register(
        nameof(SeekCommand), typeof(ICommand), typeof(SeekableControl));

    protected static readonly Brush Accent = Frozen(new SolidColorBrush(Color.FromRgb(0x7C, 0x5C, 0xFF)));
    /// <summary>Neutral grey that reads on both the light and the dark theme.</summary>
    protected static readonly Brush Muted = Frozen(new SolidColorBrush(Color.FromArgb(0x59, 0x80, 0x80, 0x80)));

    public double Duration { get => (double)GetValue(DurationProperty); set => SetValue(DurationProperty, value); }
    public double Position { get => (double)GetValue(PositionProperty); set => SetValue(PositionProperty, value); }
    public ICommand? SeekCommand { get => (ICommand?)GetValue(SeekCommandProperty); set => SetValue(SeekCommandProperty, value); }

    protected static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    protected double PlayheadX => Duration > 0 ? Math.Clamp(Position / Duration, 0, 1) * ActualWidth : 0;

    protected double X(double seconds) => Duration > 0 ? Math.Clamp(seconds / Duration, 0, 1) * ActualWidth : 0;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        CaptureMouse();
        SeekTo(e.GetPosition(this).X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (IsMouseCaptured) SeekTo(e.GetPosition(this).X);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        ReleaseMouseCapture();
    }

    void SeekTo(double x)
    {
        if (Duration <= 0 || ActualWidth <= 0) return;
        var seconds = Math.Clamp(x / ActualWidth, 0, 1) * Duration;
        if (SeekCommand?.CanExecute(seconds) == true) SeekCommand.Execute(seconds);
    }

    /// <summary>A clear rectangle over the whole control, so clicks land even where nothing is drawn.</summary>
    protected void DrawHitArea(DrawingContext dc) => dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));

    protected void DrawPlayhead(DrawingContext dc)
    {
        if (Duration <= 0) return;
        var x = Math.Round(PlayheadX) + 0.5;
        dc.DrawLine(new Pen(Accent, 2), new Point(x, 0), new Point(x, ActualHeight));
    }
}

/// <summary>The loudness of the whole recording as bars; the part already played is coloured.</summary>
public sealed class WaveformView : SeekableControl
{
    const double BarWidth = 2, BarGap = 1;

    public static readonly DependencyProperty PeaksProperty = DependencyProperty.Register(
        nameof(Peaks), typeof(float[]), typeof(WaveformView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public float[]? Peaks { get => (float[]?)GetValue(PeaksProperty); set => SetValue(PeaksProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        DrawHitArea(dc);
        if (Peaks is not { Length: > 0 } peaks || ActualWidth <= 0) return;

        var bars = Math.Max(1, (int)(ActualWidth / (BarWidth + BarGap)));
        var middle = ActualHeight / 2;
        var playedX = PlayheadX;
        for (var bar = 0; bar < bars; bar++)
        {
            var from = bar * peaks.Length / bars;
            var to = Math.Max(from + 1, (bar + 1) * peaks.Length / bars);
            var peak = 0f;
            for (var i = from; i < Math.Min(to, peaks.Length); i++) peak = Math.Max(peak, peaks[i]);
            var height = Math.Max(2, peak * (ActualHeight - 4));
            var x = bar * (BarWidth + BarGap);
            dc.DrawRoundedRectangle(x < playedX ? Accent : Muted, null, new Rect(x, middle - height / 2, BarWidth, height), 1, 1);
        }
        DrawPlayhead(dc);
    }
}

/// <summary>One speaker's row: a bar wherever they talk, on a faint track.</summary>
public sealed class SegmentTrack : SeekableControl
{
    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments), typeof(IReadOnlyList<SpeakerSegment>), typeof(SegmentTrack),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BrushProperty = DependencyProperty.Register(
        nameof(Brush), typeof(Brush), typeof(SegmentTrack),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<SpeakerSegment>? Segments { get => (IReadOnlyList<SpeakerSegment>?)GetValue(SegmentsProperty); set => SetValue(SegmentsProperty, value); }
    public Brush Brush { get => (Brush)GetValue(BrushProperty); set => SetValue(BrushProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        DrawHitArea(dc);
        var barTop = ActualHeight / 2 - 3;
        dc.DrawRoundedRectangle(Muted, null, new Rect(0, barTop + 1, ActualWidth, 4), 2, 2);
        foreach (var segment in Segments ?? Array.Empty<SpeakerSegment>())
        {
            var x = X(segment.Start);
            var width = Math.Max(2, X(segment.End) - x);
            dc.DrawRoundedRectangle(Brush, null, new Rect(x, barTop, width, 6), 2, 2);
        }
        DrawPlayhead(dc);
    }
}

/// <summary>Colours that tell the speakers apart, the same in the transcript and in the speaker tracks.</summary>
public static class SpeakerColors
{
    static readonly Brush[] Palette = new[]
    {
        "#3B82F6", "#EF4444", "#22A06B", "#F59E0B", "#8B5CF6", "#EC4899", "#14B8A6", "#8A8F98",
    }.Select(hex => (Brush)new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex))).ToArray();

    static SpeakerColors()
    {
        foreach (var brush in Palette) brush.Freeze();
    }

    public static Brush For(int index) => Palette[index % Palette.Length];
}
