using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace ZeppieSays.UI;

/// <summary>The corner card for meetings: asks to record, then shows the running recording with a Stop button.</summary>
public partial class MeetingPopup : Window
{
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer _hide = new();
    Func<TimeSpan>? _elapsed;

    public event Action? RecordClicked;
    public event Action? StopClicked;

    public MeetingPopup()
    {
        InitializeComponent();
        _tick.Tick += (_, _) => UpdateTimer();
        _hide.Tick += (_, _) => { _hide.Stop(); Hide(); };
    }

    /// <summary>Asks whether to record; goes away by itself after a minute.</summary>
    public void Ask(string app)
    {
        AskTitle.Text = $"{app} is using your microphone";
        ShowOnly(AskPanel);
        HideAfter(TimeSpan.FromMinutes(1));
    }

    public void ShowRecording(string app, Func<TimeSpan> elapsed)
    {
        _elapsed = elapsed;
        RecordingTitle.Text = app.Length > 0 ? $"Recording {app} meeting" : "Recording";
        UpdateTimer();
        _tick.Start();
        _hide.Stop();
        ShowOnly(RecordingPanel);
    }

    public void ShowSaved(bool ok)
    {
        _tick.Stop();
        SavedText.Text = ok ? "Recording saved. Transcribing in Notetaker…" : "The recording could not be saved. See the log.";
        ShowOnly(SavedPanel);
        HideAfter(TimeSpan.FromSeconds(5));
    }

    void UpdateTimer()
    {
        var t = _elapsed?.Invoke() ?? TimeSpan.Zero;
        Timer.Text = t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
        Dot.Opacity = Dot.Opacity < 1 ? 1 : 0.35; // a slow blink
    }

    void ShowOnly(FrameworkElement panel)
    {
        foreach (var p in new FrameworkElement[] { AskPanel, RecordingPanel, SavedPanel })
            p.Visibility = p == panel ? Visibility.Visible : Visibility.Collapsed;
        Show();
        UpdateLayout();
        // Bottom right, above the taskbar.
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 8;
        Top = area.Bottom - ActualHeight - 8;
    }

    void HideAfter(TimeSpan delay)
    {
        _hide.Interval = delay;
        _hide.Stop();
        _hide.Start();
    }

    void Record_Click(object sender, RoutedEventArgs e) => RecordClicked?.Invoke();
    void Stop_Click(object sender, RoutedEventArgs e) => StopClicked?.Invoke();

    void NotNow_Click(object sender, RoutedEventArgs e)
    {
        _hide.Stop();
        Hide();
    }

    void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not System.Windows.Controls.Primitives.ButtonBase) DragMove();
    }
}
