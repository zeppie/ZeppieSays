using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using ZeppieSays.Core;
using ZeppieSays.Notes;

namespace ZeppieSays.UI;

/// <summary>The Notetaker page: where audio files go, how they're transcribed, and the list of transcripts.</summary>
public sealed class NotetakerViewModel : ObservableObject
{
    readonly AppSettings _settings;
    readonly Notetaker _notetaker;
    readonly VoiceLibrary _voices;
    readonly Action _save;

    public NotetakerViewModel(AppSettings settings, Notetaker notetaker, Summarizer summarizer, VoiceLibrary voices,
        MeetingController meetings, Action save)
    {
        _settings = settings;
        _notetaker = notetaker;
        _voices = voices;
        _save = save;
        Meetings = meetings;
        SummaryAvailable = summarizer.IsAvailable;
        ToggleRecording = new RelayCommand(_ =>
        {
            if (Meetings.IsRecording) Meetings.Stop();
            else Meetings.Start(""); // started by hand: no meeting app to name it after
        });
        meetings.PropertyChanged += (_, _) => Raise(nameof(RecordLabel));
        ChooseFolder = new RelayCommand(_ => PickFolder());
        OpenFolder = new RelayCommand(_ => Open(Options.Folder));
        OpenTranscript = new RelayCommand(p => { if (p is NoteJob { TranscriptPath: { } path }) Open(path); });
        ShowInFolder = new RelayCommand(p =>
        {
            if (p is NoteJob job)
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{job.TranscriptPath ?? job.AudioPath}\""));
        });
        Retry = new RelayCommand(p => { if (p is NoteJob job) _notetaker.Retry(job); });
        StartJob = new RelayCommand(p => { if (p is NoteJob job) _notetaker.Start(job); });
        StartAll = new RelayCommand(_ => _notetaker.StartAll());
        DiscardJob = new RelayCommand(p =>
        {
            if (p is not NoteJob job) return;
            if (job == Selected) Selected = null;
            _notetaker.Discard(job);
        });

        // Show a transcript as soon as the selected file finishes.
        Jobs.CollectionChanged += (_, e) =>
        {
            foreach (NoteJob job in e.NewItems ?? Array.Empty<NoteJob>())
            {
                job.PropertyChanged += (s, args) =>
                {
                    if (args.PropertyName == nameof(NoteJob.TranscriptPath) && s == Selected) LoadPreview();
                    if (args.PropertyName == nameof(NoteJob.IsReady)) Raise(nameof(HasSeveralReady));
                };
                if (job.IsReady) Selected = job; // a dropped file opens its settings straight away
            }
            Raise(nameof(HasSeveralReady));
        };
    }

    NotetakerSettings Options => _settings.Notetaker;

    public MeetingController Meetings { get; }
    public RelayCommand ToggleRecording { get; }
    public string RecordLabel => Meetings.IsRecording ? "Stop recording" : "Record now";

    public bool AskToRecordMeetings
    {
        get => Options.AskToRecordMeetings;
        set { Options.AskToRecordMeetings = value; _save(); Raise(); }
    }

    /// <summary>The speakers in the selected transcript, to name them (and teach the voice library).</summary>
    public ObservableCollection<SpeakerNameViewModel> SpeakerNames { get; } = [];

    public ObservableCollection<NoteJob> Jobs => _notetaker.Jobs;
    public bool SummaryAvailable { get; }

    public RelayCommand ChooseFolder { get; }
    public RelayCommand OpenFolder { get; }
    public RelayCommand OpenTranscript { get; }
    public RelayCommand ShowInFolder { get; }
    public RelayCommand Retry { get; }
    public RelayCommand StartJob { get; }
    public RelayCommand StartAll { get; }
    public RelayCommand DiscardJob { get; }

    /// <summary>More than one dropped file is waiting, so "Start all" makes sense.</summary>
    public bool HasSeveralReady => Jobs.Count(j => j.IsReady) > 1;

    public string Folder => Options.Folder;

    public string ModelId
    {
        get => Options.ModelId;
        set
        {
            if (value is null) return; // the picker briefly clears while its list refreshes
            Options.ModelId = value;
            _save();
            Raise();
        }
    }

    public string Language
    {
        get => Options.Language;
        set { if (value is null) return; Options.Language = value; _save(); Raise(); }
    }

    public bool LabelSpeakers
    {
        get => Options.LabelSpeakers;
        set { Options.LabelSpeakers = value; _save(); Raise(); }
    }

    public static IReadOnlyList<Choice> SpeakerCounts { get; } =
        [new("0", "Auto"), .. Enumerable.Range(2, 14).Select(n => new Choice(n.ToString(), $"{n} people"))];

    public string Speakers
    {
        get => Options.Speakers.ToString();
        set { if (value is null) return; Options.Speakers = int.TryParse(value, out var n) ? n : 0; _save(); Raise(); }
    }

    public bool Summarize
    {
        get => Options.Summarize;
        set { Options.Summarize = value; _save(); Raise(); }
    }

    public bool UseDictionary
    {
        get => Options.UseDictionary;
        set { Options.UseDictionary = value; _save(); Raise(); }
    }

    public NoteJob? Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value)) LoadPreview();
        }
    }
    NoteJob? _selected;

    void LoadPreview()
    {
        var path = Selected?.TranscriptPath;
        Preview = path is not null && File.Exists(path) ? File.ReadAllText(path) : "";
        SpeakerNames.Clear();
        if (path is not null)
        {
            var me = UserName.First;
            foreach (var label in Transcript.LoadVoiceprints(path).Keys.Where(l => l != me).Order())
                SpeakerNames.Add(new SpeakerNameViewModel(label, name => NameSpeaker(path, label, name)));
        }
        Raise(nameof(HasSpeakers));
    }

    public bool HasSpeakers => SpeakerNames.Count > 0;

    /// <summary>Renames a speaker in the transcript and remembers the voice, so the next recording knows them.</summary>
    void NameSpeaker(string markdown, string label, string name)
    {
        name = name.Trim();
        if (name.Length == 0 || name == label) return;
        try
        {
            if (Transcript.LoadVoiceprints(markdown).TryGetValue(label, out var voice))
                _voices.Remember(name, voice);
            Transcript.RenameSpeaker(markdown, label, name);
            Log.Info($"Named a speaker in {Path.GetFileName(markdown)}");
        }
        catch (Exception e)
        {
            Log.Error($"Could not rename a speaker in {markdown}", e);
        }
        LoadPreview();
    }

    /// <summary>The selected transcript's Markdown.</summary>
    public string Preview
    {
        get => _preview;
        private set => Set(ref _preview, value);
    }
    string _preview = "";

    public void Start() => _notetaker.Watch();

    public void AddFiles(IEnumerable<string> paths) => _notetaker.AddFiles(paths);

    void PickFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Folder to transcribe audio files from", InitialDirectory = Options.Folder };
        if (dialog.ShowDialog() != true) return;
        Options.Folder = dialog.FolderName;
        _save();
        Raise(nameof(Folder));
        Selected = null;
        _notetaker.Watch();
    }

    static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception e) { Log.Error($"Could not open {path}", e); }
    }
}

/// <summary>One speaker of a transcript, with a box to type their name.</summary>
public sealed class SpeakerNameViewModel : ObservableObject
{
    public SpeakerNameViewModel(string label, Action<string> rename)
    {
        Label = label;
        _name = label;
        Save = new RelayCommand(_ => rename(Name));
    }

    public string Label { get; }

    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }
    string _name;

    public RelayCommand Save { get; }
}
