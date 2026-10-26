using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using ZeppieSays.Core;
using ZeppieSays.Notes;

namespace ZeppieSays.UI;

/// <summary>
/// Plays the recording of the selected transcript: audio, or video with its picture. The page paints
/// <see cref="VideoBrush"/> for video and follows <see cref="Position"/> for the waveform, the speaker tracks and
/// the transcript.
/// </summary>
public sealed class Playback : ObservableObject
{
    readonly MediaPlayer _player = new() { ScrubbingEnabled = true }; // scrubbing shows the frame you seek to while paused
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };

    public Playback()
    {
        _player.MediaOpened += (_, _) =>
        {
            Duration = _player.NaturalDuration.HasTimeSpan ? _player.NaturalDuration.TimeSpan.TotalSeconds : 0;
            if (_player.HasVideo && _player.NaturalVideoWidth > 0)
            {
                // Uniform: the whole picture, with bars where the window is a different shape.
                var drawing = new VideoDrawing { Player = _player, Rect = new Rect(0, 0, _player.NaturalVideoWidth, _player.NaturalVideoHeight) };
                VideoBrush = new DrawingBrush(drawing) { Stretch = Stretch.Uniform };
                HasVideo = true;
            }
            if (!IsPlaying) _player.Pause(); // the first frame of a video
        };
        _player.MediaEnded += (_, _) =>
        {
            Pause();
            Seek(0);
        };
        _player.MediaFailed += (_, e) =>
        {
            Log.Warn($"Playback failed: {e.ErrorException.Message}");
            IsOpen = false;
        };
        _timer.Tick += (_, _) =>
        {
            if (IsPlaying) Position = _player.Position.TotalSeconds;
        };
        PlayPauseCommand = new RelayCommand(_ => PlayPause());
    }

    public RelayCommand PlayPauseCommand { get; }

    /// <summary>What the page paints for a video; null for audio.</summary>
    public Brush? VideoBrush
    {
        get => _videoBrush;
        private set => Set(ref _videoBrush, value);
    }
    Brush? _videoBrush;

    /// <summary>A recording is loaded; false when the file is gone or can't be played.</summary>
    public bool IsOpen
    {
        get => _isOpen;
        private set { if (Set(ref _isOpen, value)) RaiseLayout(); }
    }
    bool _isOpen;

    public bool HasVideo
    {
        get => _hasVideo;
        private set { if (Set(ref _hasVideo, value)) RaiseLayout(); }
    }
    bool _hasVideo;

    public bool ShowVideo => IsOpen && HasVideo;
    public bool ShowWaveform => IsOpen && !HasVideo;
    public bool IsMissing => !IsOpen;

    public bool IsPlaying
    {
        get => _isPlaying;
        private set { if (Set(ref _isPlaying, value)) Raise(nameof(PlayGlyph)); }
    }
    bool _isPlaying;

    /// <summary>Segoe Fluent Icons: pause while playing, play otherwise.</summary>
    public string PlayGlyph => IsPlaying ? "" : "";

    /// <summary>Seconds into the recording.</summary>
    public double Position
    {
        get => _position;
        private set { if (Set(ref _position, value)) Raise(nameof(TimeText)); }
    }
    double _position;

    public double Duration
    {
        get => _duration;
        private set { if (Set(ref _duration, value)) Raise(nameof(TimeText)); }
    }
    double _duration;

    public string TimeText => $"{Transcript.Stamp(Position)} / {Transcript.Stamp(Duration)}";

    public void Open(string path)
    {
        Close();
        if (!File.Exists(path)) return;
        _player.Open(new Uri(path));
        IsOpen = true;
        _timer.Start();
    }

    /// <summary>Stops and lets go of the file, so it can be moved or deleted.</summary>
    public void Close()
    {
        _timer.Stop();
        _player.Stop();
        _player.Close();
        IsPlaying = false;
        IsOpen = false;
        HasVideo = false;
        VideoBrush = null;
        Position = 0;
        Duration = 0;
    }

    public void PlayPause()
    {
        if (!IsOpen) return;
        if (IsPlaying) Pause();
        else
        {
            _player.Play();
            IsPlaying = true;
        }
    }

    void Pause()
    {
        _player.Pause();
        IsPlaying = false;
    }

    public void Seek(double seconds)
    {
        if (!IsOpen) return;
        seconds = Math.Clamp(seconds, 0, Duration > 0 ? Duration : seconds);
        _player.Position = TimeSpan.FromSeconds(seconds);
        Position = seconds;
    }

    void RaiseLayout()
    {
        Raise(nameof(ShowVideo));
        Raise(nameof(ShowWaveform));
        Raise(nameof(IsMissing));
    }
}
