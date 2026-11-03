using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using Microsoft.Win32;
using ZeppieSays.Core;
using ZeppieSays.Engines;
using ZeppieSays.Notes;
using ZeppieSays.Services;

namespace ZeppieSays.UI;

/// <summary>
/// The Notetaker page: the strip of recordings, and for the selected one the player, who spoke when, and the
/// transcript and summary. Also the settings that apply to every file.
/// </summary>
public sealed partial class NotetakerViewModel : ObservableObject
{
    static readonly Brush NoSpeaker = new SolidColorBrush(Color.FromArgb(0x59, 0x80, 0x80, 0x80));

    readonly AppSettings _settings;
    readonly Notetaker _notetaker;
    readonly VoiceLibrary _voices;
    readonly Summarizer _summarizer;
    readonly DictionaryFiles _dictionaryFiles;
    readonly Action _save;
    Timeline? _timeline;
    int _currentLine = -1;
    List<int> _matches = [];
    int _matchAt = -1;

    public NotetakerViewModel(AppSettings settings, Notetaker notetaker, Summarizer summarizer, VoiceLibrary voices,
        MeetingController meetings, DictionaryFiles dictionaryFiles, Action save)
    {
        _settings = settings;
        _notetaker = notetaker;
        _voices = voices;
        _summarizer = summarizer;
        _dictionaryFiles = dictionaryFiles;
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
        BrowseFiles = new RelayCommand(_ => PickFiles());
        SeekCommand = new RelayCommand(p => { if (p is double seconds) Playback.Seek(seconds); });
        SeekLine = new RelayCommand(p =>
        {
            if (p is not TranscriptLineViewModel line) return;
            FollowPlayback = true;
            Playback.Seek(line.Seconds);
        });
        NextMatch = new RelayCommand(_ => StepMatch(1));
        PreviousMatch = new RelayCommand(_ => StepMatch(-1));
        SyncNow = new RelayCommand(_ =>
        {
            FollowPlayback = true;
            if (_currentLine >= 0) ScrollToLine?.Invoke(Lines[_currentLine]);
        });

        SetUpEditing();

        SpeakerDetection = new SpeakerSettingsViewModel(Options.SpeakerDetection, () => VoiceModel.Get(VoiceModelId));
        Options.SpeakerDetection.PropertyChanged += (_, _) => _save();
        RedoJob = new RelayCommand(p =>
        {
            if (p is not NoteJob { IsDone: true } job) return;
            Playback.Close(); // the recording is read again
            _notetaker.Redo(job, _title is { Length: > 0 } title ? title : job.Name);
            Raise(nameof(SelectedSpeakerDetection));
        });
        RecycleJob = new RelayCommand(p => { if (p is NoteJob job) Recycle(job); });

        Playback.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Playback.Position)) UpdateCurrentLine();
            if (e.PropertyName == nameof(Playback.Duration)) Raise(nameof(Duration));
        };

        // Show a transcript as soon as the selected file finishes, and open the settings of a file just dropped.
        Jobs.CollectionChanged += (_, e) =>
        {
            foreach (NoteJob job in e.NewItems ?? Array.Empty<NoteJob>())
            {
                job.PropertyChanged += (s, args) =>
                {
                    if (args.PropertyName == nameof(NoteJob.TranscriptPath) && s == Selected) LoadSelected();
                    if (args.PropertyName == nameof(NoteJob.IsReady)) Raise(nameof(HasSeveralReady));
                    if (s == Selected && args.PropertyName is nameof(NoteJob.Status) or nameof(NoteJob.StatusText)) RaisePlaceholder();
                };
                if (job.IsReady) Selected = job;
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
    public RelayCommand BrowseFiles { get; }

    /// <summary>More than one dropped file is waiting, so "Start all" makes sense.</summary>
    public bool HasSeveralReady => Jobs.Count(j => j.IsReady) > 1;

    public string Folder => Options.Folder;

    // ---- The general settings, edited on the Settings page.

    public string ModelId
    {
        get => Options.ModelId;
        set
        {
            if (value is null) return; // the picker briefly clears while its list refreshes
            Options.ModelId = value;
            _save();
            Raise();
            Raise(nameof(CanPickSpeakerSource));
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

    /// <summary>Who tells the speakers apart: this PC, or ElevenLabs while it is the speech model.</summary>
    public string SpeakerSourceChoice
    {
        get => Options.CloudSpeakers ? SpeakerSources.Cloud : SpeakerSources.Local;
        set { if (value is null) return; Options.CloudSpeakers = value == SpeakerSources.Cloud; _save(); Raise(); }
    }

    public bool CanPickSpeakerSource => SpeakerSources.IsElevenLabs(Options.ModelId);

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

    /// <summary>The advanced speaker detection settings every new file starts with.</summary>
    public SpeakerSettingsViewModel SpeakerDetection { get; }

    public static IReadOnlyList<Choice> VoiceModels { get; } = VoiceModel.All.Select(m => new Choice(m.Id, m.Name)).ToList();

    string VoiceModelId => VoiceModel.Get(Options.VoiceModel).Id;

    /// <summary>The voice model; switching learns the named voices again with the new one, from earlier recordings.</summary>
    public string VoiceModelChoice
    {
        get => VoiceModelId;
        set
        {
            if (value is null || value == VoiceModelId) return;
            Options.VoiceModel = value;
            _save();
            Raise();
            Raise(nameof(VoiceModelNote));
            SpeakerDetection.RaiseThreshold();
            Log.Info($"Voice model is now {value}");
            _ = _notetaker.RelearnVoicesAsync();
        }
    }

    public string VoiceModelNote => VoiceModel.Get(VoiceModelId).Note;

    /// <summary>The advanced speaker settings of the selected file, while it waits to start.</summary>
    public SpeakerSettingsViewModel? SelectedSpeakerDetection
    {
        get
        {
            if (_selectedSpeakerDetection?.Settings == Selected?.Options?.Speaker) return _selectedSpeakerDetection;
            if (Selected?.Options is not { } options) return _selectedSpeakerDetection = null;
            // The threshold's default follows the voice model picked for this file.
            var panel = new SpeakerSettingsViewModel(options.Speaker, () => VoiceModel.Get(options.VoiceModel));
            options.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(NoteOptions.VoiceModel)) panel.RaiseThreshold(); };
            return _selectedSpeakerDetection = panel;
        }
    }
    SpeakerSettingsViewModel? _selectedSpeakerDetection;

    /// <summary>Puts the selected transcript back as Ready, to do it again with other speaker settings.</summary>
    public RelayCommand RedoJob { get; private set; } = null!;

    /// <summary>Moves a recording, its transcript and everything with it to the Recycle Bin, after asking.</summary>
    public RelayCommand RecycleJob { get; private set; } = null!;

    void Recycle(NoteJob job)
    {
        var count = _notetaker.FilesOf(job).Count;
        var answer = System.Windows.MessageBox.Show(
            $"Move “{job.Name}” to the Recycle Bin?\n\nThe recording, the transcript and everything kept with it ({count} files and folders) go together. " +
            "You can restore them from the Recycle Bin.",
            "Zeppie Says", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Question);
        if (answer != System.Windows.MessageBoxResult.OK) return;
        if (job == Selected)
        {
            Playback.Close(); // the player holds the audio open
            Selected = null;
        }
        if (!_notetaker.Recycle(job))
            System.Windows.MessageBox.Show("Could not move it to the Recycle Bin. Is the file open somewhere else?", "Zeppie Says",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
    }

    /// <summary>
    /// The selected recording's name, without its date; typing another one renames the recording, its transcript
    /// and everything kept with them.
    /// </summary>
    public string SelectedTitle
    {
        get => Selected?.Title ?? "";
        set
        {
            if (Selected is not { IsDone: true } job || value.Trim().Length == 0 || value.Trim() == job.Title)
            {
                Raise(); // an empty name puts the current one back
                return;
            }
            Playback.Close(); // the player holds the audio open; the page loads it again once renamed
            if (!_notetaker.Rename(job, value))
            {
                LoadSelected();
                System.Windows.MessageBox.Show("Could not rename this recording. Is a file of it open somewhere else?", "Zeppie Says",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            }
            Raise();
        }
    }

    // ---- The selected recording.

    public NoteJob? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            LoadSelected();
            RaisePlaceholder();
            Raise(nameof(SelectedSpeakerDetection));
            Raise(nameof(SelectedTitle));
        }
    }
    NoteJob? _selected;

    /// <summary>Shown instead of the recording while nothing is selected, or the file is still being transcribed or failed.</summary>
    public bool ShowPlaceholder => Selected is null || (!Selected.IsDone && !Selected.IsReady);

    public string Placeholder => Selected is null
        ? "Pick a recording above, press the red record button, or drop audio files on this page."
        : Selected.StatusText;

    void RaisePlaceholder()
    {
        Raise(nameof(ShowPlaceholder));
        Raise(nameof(Placeholder));
    }

    public Playback Playback { get; } = new();

    /// <summary>The length the waveform and speaker tracks are drawn for: the player's, or the saved one before it has loaded.</summary>
    public double Duration => Playback.Duration > 0 ? Playback.Duration : _timeline?.Duration ?? 0;

    public float[]? Peaks
    {
        get => _peaks;
        private set => Set(ref _peaks, value);
    }
    float[]? _peaks;

    public ObservableCollection<TranscriptLineViewModel> Lines { get; } = [];
    public ObservableCollection<SpeakerRowViewModel> SpeakerRows { get; } = [];

    /// <summary>Who talks when, in each speaker's colour, for colouring the waveform.</summary>
    public IReadOnlyList<ColoredSegment> WaveSegments
    {
        get => _waveSegments;
        private set => Set(ref _waveSegments, value);
    }
    IReadOnlyList<ColoredSegment> _waveSegments = [];

    /// <summary>Whether the selected transcript has saved speaker segments. Older ones don't.</summary>
    public bool HasTimeline => _timeline is { Segments.Count: > 0 };
    public bool HasSpeakers => SpeakerRows.Count > 0;
    public bool ShowTimelineHint => HasSpeakers && !HasTimeline;

    public RelayCommand SeekCommand { get; }
    public RelayCommand SeekLine { get; }

    /// <summary>Asked of the page: scroll the transcript to this paragraph.</summary>
    public event Action<TranscriptLineViewModel>? ScrollToLine;

    // ---- Tabs: transcript or summary.

    public bool ShowSummary
    {
        get => _showSummary;
        set { if (Set(ref _showSummary, value)) Raise(nameof(ShowTranscript)); }
    }
    bool _showSummary;

    public bool ShowTranscript
    {
        get => !_showSummary;
        set { if (value) ShowSummary = false; }
    }

    public string Summary
    {
        get => _summary;
        private set
        {
            if (!Set(ref _summary, value)) return;
            Raise(nameof(HasSummary));
            Raise(nameof(CanOpenSummary));
            Raise(nameof(RegenerateLabel));
        }
    }
    string _summary = "";

    public bool HasSummary => Summary.Length > 0;

    // ---- Following the playback, and searching.

    /// <summary>Keeps the paragraph being played in view. Scrolling by hand turns it off until "Sync with playback".</summary>
    public bool FollowPlayback
    {
        get => _follow;
        set { if (Set(ref _follow, value)) Raise(nameof(ShowSync)); }
    }
    bool _follow = true;

    public bool ShowSync => !FollowPlayback && Playback.IsOpen;
    public RelayCommand SyncNow { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value ?? "")) UpdateMatches();
        }
    }
    string _searchText = "";

    public string MatchText => SearchText.Length == 0 ? ""
        : _matches.Count == 0 ? "No matches"
        : _matchAt < 0 ? $"{_matches.Count} matches"
        : $"{_matchAt + 1} of {_matches.Count}";

    public RelayCommand NextMatch { get; }
    public RelayCommand PreviousMatch { get; }

    void UpdateMatches()
    {
        foreach (var line in Lines) line.IsMatch = false;
        _matches = SearchText.Length == 0
            ? []
            : Enumerable.Range(0, Lines.Count)
                .Where(i => Lines[i].Text.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase)).ToList();
        foreach (var i in _matches) Lines[i].IsMatch = true;
        _matchAt = -1;
        Raise(nameof(MatchText));
    }

    void StepMatch(int step)
    {
        if (_matches.Count == 0) return;
        _matchAt = _matchAt < 0 ? (step > 0 ? 0 : _matches.Count - 1) : (_matchAt + step + _matches.Count) % _matches.Count;
        FollowPlayback = false;
        ScrollToLine?.Invoke(Lines[_matches[_matchAt]]);
        Raise(nameof(MatchText));
    }

    // ---- Loading the selected recording.

    /// <summary>Opens the player and the transcript for the selected, finished recording (or clears them).</summary>
    void LoadSelected()
    {
        Playback.Close();
        _timeline = null;
        Peaks = null;
        FollowPlayback = true;
        SearchText = "";
        ResetEditing();
        LoadTranscript();
        if (Selected is not { IsDone: true } job) return;
        if (AudioFile.IsAudio(job.AudioPath) && File.Exists(job.AudioPath))
            Playback.Open(job.AudioPath);
        Raise(nameof(ShowSync));
        _ = LoadPeaksAsync(job);
    }

    /// <summary>Fills the transcript paragraphs, the summary and the speaker rows from the selected transcript's file.</summary>
    void LoadTranscript()
    {
        Lines.Clear();
        SpeakerRows.Clear();
        _currentLine = -1;
        _matches = [];
        Summary = "";
        WaveSegments = [];
        if (Selected is not { IsDone: true, TranscriptPath: { } path } || !File.Exists(path))
        {
            RaiseTimeline();
            return;
        }

        var parsed = TranscriptFile.Parse(File.ReadAllText(path));
        _timeline = Timeline.Load(path);
        Summary = parsed.Notes ?? "";
        _title = parsed.Title;

        _brushes.Clear();
        foreach (var paragraph in parsed.Paragraphs)
            Lines.Add(new TranscriptLineViewModel(paragraph, BrushFor(paragraph.Speaker)));
        RebuildSpeakers();
        UpdateMatches();
        UpdateCurrentLine();
    }

    void RaiseTimeline()
    {
        Raise(nameof(HasSpeakers));
        Raise(nameof(HasTimeline));
        Raise(nameof(ShowTimelineHint));
        Raise(nameof(Duration));
    }

    /// <summary>The waveform: from the saved timeline, or worked out from the audio (and saved) for older transcripts.</summary>
    async Task LoadPeaksAsync(NoteJob job)
    {
        if (_timeline is { Peaks.Length: > 0 } saved)
        {
            Peaks = saved.Peaks;
            return;
        }
        if (job.TranscriptPath is not { } markdown || !AudioFile.IsAudio(job.AudioPath) || !File.Exists(job.AudioPath)) return;
        try
        {
            var (peaks, seconds) = await Task.Run(() =>
            {
                var samples = AudioFile.Load(job.AudioPath);
                return (Timeline.ComputePeaks([samples]), samples.Length / (double)Audio.SampleRate);
            });
            if (Selected != job) return; // another recording was picked meanwhile
            _timeline ??= new Timeline();
            _timeline.Peaks = peaks;
            if (_timeline.Duration <= 0) _timeline.Duration = seconds;
            Peaks = peaks;
            Raise(nameof(Duration));
            await Task.Run(() => _timeline.Save(markdown));
        }
        catch (Exception e)
        {
            Log.Warn($"Could not draw the waveform of {job.AudioPath}: {e.Message}");
        }
    }

    /// <summary>Marks the paragraph being played and, when following, scrolls it into view.</summary>
    void UpdateCurrentLine()
    {
        var position = Playback.Position + 0.25; // a paragraph lights up just before its first word
        var index = -1;
        for (int low = 0, high = Lines.Count - 1; low <= high;)
        {
            var mid = (low + high) / 2;
            if (Lines[mid].Seconds <= position) { index = mid; low = mid + 1; }
            else high = mid - 1;
        }
        if (index == _currentLine) return;
        if (_currentLine >= 0 && _currentLine < Lines.Count) Lines[_currentLine].IsCurrent = false;
        _currentLine = index;
        if (index < 0) return;
        Lines[index].IsCurrent = true;
        if (FollowPlayback) ScrollToLine?.Invoke(Lines[index]);
    }

    /// <summary>Renames a speaker in the transcript and remembers the voice, so the next recording knows them.</summary>
    void NameSpeaker(string markdown, string label, string name)
    {
        name = name.Trim();
        if (name.Length == 0 || name == label) return;
        try
        {
            var model = VoiceModelId;
            var (madeBy, voiceprints) = Transcript.ReadVoiceprints(markdown);
            if (madeBy == model && voiceprints.TryGetValue(label, out var voice))
                _voices.Remember(model, name, voice);
            else
                LearnFromAudio(model, name, Selected?.AudioPath, _timeline?.Segments
                    .Where(s => s.Speaker == label).Select(s => (s.Start, s.End)).ToList() ?? []);
            Transcript.RenameSpeaker(markdown, label, name);
            Log.Info($"Named a speaker in {Path.GetFileName(markdown)}");
        }
        catch (Exception e)
        {
            Log.Error($"Could not rename a speaker in {markdown}", e);
        }
        // Rebuilt after the box that asked for the rename has let go of focus. The player keeps playing.
        System.Windows.Application.Current.Dispatcher.BeginInvoke(LoadTranscript);
    }

    public void Start()
    {
        _notetaker.Watch();
        _ = _notetaker.RelearnVoicesAsync(); // voices named while another voice model was chosen
    }

    public void AddFiles(IEnumerable<string> paths) => _notetaker.AddFiles(paths);

    void PickFiles()
    {
        var types = string.Join(";", AudioFile.Extensions.Select(e => "*" + e));
        var dialog = new OpenFileDialog { Title = "Audio or video files to transcribe", Multiselect = true, Filter = $"Audio and video|{types}|All files|*.*" };
        if (dialog.ShowDialog() == true) AddFiles(dialog.FileNames);
    }

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

/// <summary>
/// One paragraph of the transcript list: who said it, when, whether it is the one playing or a search hit, and,
/// while the user edits it, the text and speaker they are typing.
/// </summary>
public sealed class TranscriptLineViewModel : ObservableObject
{
    double _seconds;
    string? _speaker;
    string _text;
    Brush _brush;

    public TranscriptLineViewModel(TranscriptParagraph paragraph, Brush brush)
    {
        _seconds = paragraph.Seconds;
        _speaker = paragraph.Speaker;
        _text = paragraph.Text;
        _brush = brush;
    }

    public TranscriptParagraph ToParagraph() => new(Seconds, Speaker, Text);

    public double Seconds
    {
        get => _seconds;
        set { if (Set(ref _seconds, value)) Raise(nameof(Stamp)); }
    }

    public string Stamp => Transcript.Stamp(Seconds);

    public string? Speaker
    {
        get => _speaker;
        set { if (Set(ref _speaker, value)) Raise(nameof(HasSpeaker)); }
    }

    public bool HasSpeaker => Speaker is not null;

    public string Text
    {
        get => _text;
        set => Set(ref _text, value);
    }

    public Brush Brush
    {
        get => _brush;
        set => Set(ref _brush, value);
    }

    public bool IsCurrent
    {
        get => _isCurrent;
        set => Set(ref _isCurrent, value);
    }
    bool _isCurrent;

    public bool IsMatch
    {
        get => _isMatch;
        set => Set(ref _isMatch, value);
    }
    bool _isMatch;

    // ---- Editing

    public bool IsEditing
    {
        get => _isEditing;
        set => Set(ref _isEditing, value);
    }
    bool _isEditing;

    public string EditText
    {
        get => _editText;
        set => Set(ref _editText, value ?? "");
    }
    string _editText = "";

    /// <summary>The speaker box: a name from the list, or a new one typed in; empty means unknown.</summary>
    public string EditSpeaker
    {
        get => _editSpeaker;
        set => Set(ref _editSpeaker, value ?? "");
    }
    string _editSpeaker = "";

    public bool CanMergeUp
    {
        get => _canMergeUp;
        set => Set(ref _canMergeUp, value);
    }
    bool _canMergeUp;

    public bool CanMergeDown
    {
        get => _canMergeDown;
        set => Set(ref _canMergeDown, value);
    }
    bool _canMergeDown;

    /// <summary>The start time box: "1:05" or "1:02:05".</summary>
    public string EditTime
    {
        get => _editTime;
        set => Set(ref _editTime, value ?? "");
    }
    string _editTime = "";

    /// <summary>Why the paragraph can't be saved yet, shown under its box; null when there is nothing to say.</summary>
    public string? EditError
    {
        get => _editError;
        set => Set(ref _editError, value);
    }
    string? _editError;
}

/// <summary>One speaker under the player: their colour, a name that can be edited, and when they talk.</summary>
public sealed class SpeakerRowViewModel : ObservableObject
{
    readonly Action<string> _rename;
    string _name;

    public SpeakerRowViewModel(string label, Brush brush, IReadOnlyList<SpeakerSegment> segments, bool canRename, Action<string> rename)
    {
        Label = label;
        Brush = brush;
        Segments = segments;
        CanRename = canRename;
        _rename = rename;
        _name = label;
    }

    public string Label { get; }
    public Brush Brush { get; }
    public IReadOnlyList<SpeakerSegment> Segments { get; }
    public bool CanRename { get; }
    public bool IsReadOnly => !CanRename;

    /// <summary>Typing a new name and leaving the box renames the speaker everywhere in the transcript.</summary>
    public string Name
    {
        get => _name;
        set
        {
            if (value is null) return;
            var name = value.Trim();
            if (!Set(ref _name, name) || name.Length == 0 || name == Label || !CanRename) return;
            _rename(name);
        }
    }
}
