using System.Windows.Threading;
using ZeppieSays.Core;
using ZeppieSays.Notes;

namespace ZeppieSays.UI;

/// <summary>
/// Ties meeting detection, the corner pop-up and the recorder together: a meeting app takes the microphone →
/// "Record this meeting?" → recording until Stop, or until the app lets go of the microphone.
/// </summary>
public sealed class MeetingController : ObservableObject, IDisposable
{
    readonly AppSettings _settings;
    readonly Dispatcher _ui;
    readonly MeetingDetector _detector = new();
    readonly MeetingRecorder _recorder = new();
    readonly MeetingPopup _popup = new();
    string? _askedFor;

    public MeetingController(AppSettings settings, Dispatcher ui)
    {
        _settings = settings;
        _ui = ui;
        _popup.RecordClicked += () => Start(_askedFor ?? "");
        _popup.StopClicked += Stop;
        _detector.Started += app => _ui.BeginInvoke(() => OnMeetingStarted(app));
        _detector.Ended += app => _ui.BeginInvoke(() => OnMeetingEnded(app));
        _recorder.Saved += path => _ui.BeginInvoke(() => _popup.ShowSaved(path is not null));
    }

    public bool IsRecording => _recorder.IsRecording;

    void OnMeetingStarted(string app)
    {
        if (!_settings.Notetaker.AskToRecordMeetings || _recorder.IsRecording) return;
        Log.Info($"{app} started using the microphone");
        _askedFor = app;
        _popup.Ask(app);
    }

    void OnMeetingEnded(string app)
    {
        Log.Info($"{app} stopped using the microphone");
        if (_recorder.IsRecording && _recorder.App == app) Stop();
        else if (_askedFor == app && _popup.IsVisible && !_recorder.IsRecording) _popup.Hide();
    }

    public void Start(string app)
    {
        try
        {
            _recorder.Start(app);
            _popup.ShowRecording(app, () => _recorder.Elapsed);
        }
        catch (Exception e)
        {
            Log.Error("Could not start recording", e);
            _popup.ShowSaved(false);
        }
        Raise(nameof(IsRecording));
    }

    public void Stop()
    {
        if (!_recorder.IsRecording) return;
        _recorder.Stop(_settings.Notetaker.Folder);
        Raise(nameof(IsRecording));
    }

    public void Dispose()
    {
        _detector.Dispose();
        // Quitting mid-meeting still saves the recording; the Notetaker picks it up on the next start.
        if (_recorder.IsRecording) _recorder.Stop(_settings.Notetaker.Folder, wait: true);
        _recorder.Dispose();
        _popup.Close();
    }
}
