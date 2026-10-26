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

/// <summary>A stretch of the recording in one speaker's colour.</summary>
public sealed record ColoredSegment(double Start, double End, Brush Brush);

/// <summary>
/// The loudness of the whole recording as bars. Where speakers are known each bar has the colour of whoever talks
/// there, bright in the part already played and faded in the rest; otherwise the played part is purple.
/// </summary>
public sealed class WaveformView : SeekableControl
{
    const double BarWidth = 2, BarGap = 1;

    static readonly Brush PlayedSilence = Frozen(new SolidColorBrush(Color.FromArgb(0xA0, 0x80, 0x80, 0x80)));

    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments), typeof(IReadOnlyList<ColoredSegment>), typeof(WaveformView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<ColoredSegment>? Segments { get => (IReadOnlyList<ColoredSegment>?)GetValue(SegmentsProperty); set => SetValue(SegmentsProperty, value); }

    // Which speaker's colour each bar gets only changes with the width, the length or the segments, not with the playhead.
    Brush?[]? _barColors;
    (IReadOnlyList<ColoredSegment>? Segments, int Bars, double Duration) _barKey;

    Brush?[]? BarColors(int bars)
    {
        if (Segments is not { Count: > 0 } segments || Duration <= 0) return null;
        if (_barColors is not null && _barKey == (segments, bars, Duration)) return _barColors;
        var colors = new Brush?[bars];
        for (var bar = 0; bar < bars; bar++)
        {
            var at = (bar + 0.5) / bars * Duration;
            foreach (var segment in segments)
                if (segment.Start <= at && at < segment.End)
                {
                    colors[bar] = segment.Brush;
                    break;
                }
        }
        _barKey = (segments, bars, Duration);
        return _barColors = colors;
    }

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
        var colors = BarColors(bars);
        for (var bar = 0; bar < bars; bar++)
        {
            var from = bar * peaks.Length / bars;
            var to = Math.Max(from + 1, (bar + 1) * peaks.Length / bars);
            var peak = 0f;
            for (var i = from; i < Math.Min(to, peaks.Length); i++) peak = Math.Max(peak, peaks[i]);
            var height = Math.Max(2, peak * (ActualHeight - 4));
            var x = bar * (BarWidth + BarGap);
            var played = x < playedX;
            Brush brush = colors is null ? (played ? Accent : Muted)
                : colors[bar] is { } speaker ? (played ? speaker : SpeakerColors.Faded(speaker))
                : (played ? PlayedSilence : Muted);
            dc.DrawRoundedRectangle(brush, null, new Rect(x, middle - height / 2, BarWidth, height), 1, 1);
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

    static readonly Dictionary<Brush, Brush> FadedBrushes = [];

    /// <summary>The same colour, see-through, for the part of the recording that hasn't been played yet.</summary>
    public static Brush Faded(Brush brush)
    {
        lock (FadedBrushes)
        {
            if (FadedBrushes.TryGetValue(brush, out var faded)) return faded;
            faded = brush is SolidColorBrush solid
                ? new SolidColorBrush(Color.FromArgb(0x66, solid.Color.R, solid.Color.G, solid.Color.B))
                : brush;
            faded.Freeze();
            return FadedBrushes[brush] = faded;
        }
    }
}
