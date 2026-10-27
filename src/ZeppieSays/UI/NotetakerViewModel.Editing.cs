using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Media;
using ZeppieSays.Core;
using ZeppieSays.Engines;
using ZeppieSays.Llm;
using ZeppieSays.Notes;

namespace ZeppieSays.UI;

/// <summary>A word the user corrected, offered for the dictionary so it comes out right next time.</summary>
public sealed record DictionarySuggestion(string Term, string Heard)
{
    public string Label => Heard.Length > 0 ? $"{Term}  (heard “{Heard}”)" : Term;
}

/// <summary>
/// Editing the selected transcript: changing words, changing who says a paragraph, splitting and merging
/// paragraphs, deleting them, and replacing a word everywhere. Every change is written back to the transcript file
/// at once, keeps the speaker timeline in step, is logged beside the transcript, and teaches the dictionary and the
/// voice library where it can.
/// </summary>
public sealed partial class NotetakerViewModel
{
    /// <summary>Speaker names that say nothing about who it is; a voice is not learned under these.</summary>
    [GeneratedRegex(@"^(Speaker \d+|Others|You)$", RegexOptions.IgnoreCase)]
    private static partial Regex GenericSpeaker();

    readonly Dictionary<string, Brush> _brushes = [];
    CorrectionLog? _log;
    string _title = "";
    bool _timelineChanged;

    public RelayCommand EditLine { get; private set; } = null!;
    public RelayCommand UseCurrentTime { get; private set; } = null!;
    public RelayCommand SaveEdit { get; private set; } = null!;
    public RelayCommand CancelEdit { get; private set; } = null!;
    public RelayCommand DeleteLine { get; private set; } = null!;
    public RelayCommand MergeUp { get; private set; } = null!;
    public RelayCommand MergeDown { get; private set; } = null!;
    public RelayCommand ToggleReplace { get; private set; } = null!;
    public RelayCommand ReplaceAll { get; private set; } = null!;
    public RelayCommand AddSuggestion { get; private set; } = null!;
    public RelayCommand DismissSuggestions { get; private set; } = null!;
    public RelayCommand RegenerateSummary { get; private set; } = null!;

    /// <summary>The speakers a paragraph can be given to: those in this transcript and everyone in the voice library.</summary>
    public ObservableCollection<string> SpeakerNames { get; } = [];

    public ObservableCollection<DictionarySuggestion> Suggestions { get; } = [];
    public bool HasSuggestions => Suggestions.Count > 0;

    void SetUpEditing()
    {
        EditLine = new RelayCommand(p => { if (p is TranscriptLineViewModel line) BeginEdit(line); });
        UseCurrentTime = new RelayCommand(p =>
        {
            if (p is TranscriptLineViewModel line) line.EditTime = Transcript.Stamp(Playback.Position);
        });
        SaveEdit = new RelayCommand(p => { if (p is TranscriptLineViewModel line) Save(line); });
        CancelEdit = new RelayCommand(p => { if (p is TranscriptLineViewModel line) line.IsEditing = false; });
        DeleteLine = new RelayCommand(p => { if (p is TranscriptLineViewModel line) Delete(line); });
        MergeUp = new RelayCommand(p => { if (p is TranscriptLineViewModel line) Merge(line, up: true); });
        MergeDown = new RelayCommand(p => { if (p is TranscriptLineViewModel line) Merge(line, up: false); });
        ToggleReplace = new RelayCommand(_ => ShowReplace = !ShowReplace);
        ReplaceAll = new RelayCommand(_ => ReplaceEverywhere());
        AddSuggestion = new RelayCommand(p => { if (p is DictionarySuggestion suggestion) AddToDictionary(suggestion); });
        DismissSuggestions = new RelayCommand(_ => ClearSuggestions());
        RegenerateSummary = new RelayCommand(_ => _ = RegenerateSummaryAsync());
        Suggestions.CollectionChanged += (_, _) => Raise(nameof(HasSuggestions));
    }

    /// <summary>A new recording was picked: nothing of the previous one's editing carries over.</summary>
    void ResetEditing()
    {
        _log = null;
        _timelineChanged = false;
        ClearSuggestions();
        ReplaceMessage = "";
        SummaryOutdated = false;
        SummaryError = null;
    }

    // ---- Colours and speakers

    Brush BrushFor(string? speaker)
    {
        if (speaker is null) return NoSpeaker;
        if (!_brushes.TryGetValue(speaker, out var brush)) _brushes[speaker] = brush = SpeakerColors.For(_brushes.Count);
        return brush;
    }

    /// <summary>The speaker rows, the waveform colours and the speaker list, from the paragraphs and the timeline as they are now.</summary>
    void RebuildSpeakers()
    {
        SpeakerRows.Clear();
        SpeakerNames.Clear();
        WaveSegments = [];
        if (Selected is not { TranscriptPath: { } path })
        {
            RaiseTimeline();
            return;
        }

        var labels = Lines.Select(l => l.Speaker).OfType<string>().Distinct().ToList();
        var me = UserName.First;
        var voiceprints = Transcript.LoadVoiceprints(path);
        foreach (var label in labels)
        {
            var segments = _timeline?.Segments.Where(s => s.Speaker == label).ToList() ?? [];
            SpeakerRows.Add(new SpeakerRowViewModel(label, BrushFor(label), segments,
                canRename: label != me && voiceprints.ContainsKey(label), name => NameSpeaker(path, label, name)));
        }
        WaveSegments = SpeakerRows.SelectMany(r => r.Segments.Select(s => new ColoredSegment(s.Start, s.End, r.Brush))).ToList();
        foreach (var name in labels.Concat(_voices.Names).Distinct(StringComparer.OrdinalIgnoreCase).Order())
            SpeakerNames.Add(name);
        RaiseTimeline();
    }

    // ---- Editing a paragraph

    void BeginEdit(TranscriptLineViewModel line)
    {
        foreach (var other in Lines.Where(l => l.IsEditing)) other.IsEditing = false;
        var index = Lines.IndexOf(line);
        line.EditText = line.Text;
        line.EditSpeaker = line.Speaker ?? "";
        line.EditTime = line.Stamp;
        line.EditError = null;
        line.CanMergeUp = index > 0;
        line.CanMergeDown = index >= 0 && index < Lines.Count - 1;
        line.IsEditing = true;
        FollowPlayback = false; // the list must not scroll away from what is being typed
    }

    void Save(TranscriptLineViewModel line)
    {
        var text = line.EditText.Trim();
        if (text.Length == 0) return; // an empty paragraph is a delete, which has its own button
        if (!TranscriptFile.TryParseStamp(line.EditTime, out var typedTime))
        {
            line.EditError = "A time looks like 1:05 (minutes:seconds), or 1:02:05 with hours.";
            return;
        }
        line.EditError = null;
        var speaker = line.EditSpeaker.Trim() is { Length: > 0 } name ? name : null;
        var index = Lines.IndexOf(line);
        var newTime = ClampTime(index, typedTime);
        var textChanged = text != line.Text;
        var speakerChanged = speaker != line.Speaker;
        var timeChanged = Math.Floor(newTime) != Math.Floor(line.Seconds); // the file keeps whole seconds
        if (!textChanged && !speakerChanged && !timeChanged)
        {
            line.IsEditing = false;
            return;
        }

        BeginChange();
        var (oldText, oldSpeaker) = (line.Text, line.Speaker);
        if (timeChanged)
        {
            var oldTime = line.Seconds;
            MoveStart(index, oldTime, newTime);
            line.Seconds = newTime;
            _log?.Add("time", oldTime, oldSpeaker, oldSpeaker, Transcript.Stamp(oldTime), Transcript.Stamp(newTime));
        }
        if (speakerChanged)
        {
            Reassign(index, oldSpeaker, speaker);
            line.Speaker = speaker;
            line.Brush = BrushFor(speaker);
            _log?.Add("speaker", line.Seconds, oldSpeaker, speaker, oldText, oldText);
        }
        if (textChanged)
        {
            line.Text = text;
            _log?.Add("text", line.Seconds, speaker, speaker, oldText, text);
        }
        line.IsEditing = false;
        Commit();

        if (textChanged) Suggest(oldText, text);
        if (speakerChanged && speaker is not null) TeachVoice(speaker, line.Seconds, EndOf(index));
    }

    /// <summary>
    /// A paragraph starts between the one before it and the one after it, in whole seconds, so the order never
    /// changes. Anything else is brought to the nearest time that fits.
    /// </summary>
    double ClampTime(int index, double time)
    {
        var min = index > 0 ? Math.Floor(Lines[index - 1].Seconds) + 1 : 0;
        var max = index < Lines.Count - 1 ? Math.Floor(Lines[index + 1].Seconds) - 1
            : Duration > 0 ? Math.Floor(Duration) : double.MaxValue;
        return min > max ? Lines[index].Seconds : Math.Clamp(Math.Round(time), min, max);
    }

    /// <summary>
    /// The paragraph now starts at another time, which moves where the speaker before it stops and this one starts:
    /// starting earlier takes time from the previous speaker, starting later gives it back.
    /// </summary>
    void MoveStart(int index, double oldTime, double newTime)
    {
        if (index <= 0 || _timeline is not { Segments.Count: > 0 }) return;
        var previous = Lines[index - 1].Speaker;
        var mine = Lines[index].Speaker;
        if (previous is null || mine is null || previous == mine) return;
        if (newTime < oldTime) _timeline.Reassign(newTime, oldTime, previous, mine);
        else _timeline.Reassign(oldTime, newTime, mine, previous);
        _timelineChanged = true;
    }

    /// <summary>
    /// Splits the paragraph at <paramref name="caret"/> (a position in the text being edited). The second part starts at
    /// a time estimated from where in the text the cut falls, and opens for editing so its speaker can be set.
    /// </summary>
    public void SplitLine(TranscriptLineViewModel line, int caret)
    {
        var text = line.EditText;
        var index = Lines.IndexOf(line);
        if (index < 0 || caret <= 0 || caret >= text.Length) return;
        var first = text[..caret].Trim();
        var second = text[caret..].Trim();
        if (first.Length == 0 || second.Length == 0) return;

        BeginChange();
        var end = EndOf(index);
        var at = line.Seconds + (end - line.Seconds) * caret / text.Length;
        if (end - line.Seconds > 1) at = Math.Clamp(at, line.Seconds + 0.5, end - 0.5);

        var speaker = line.EditSpeaker.Trim() is { Length: > 0 } name ? name : null;
        var (oldText, oldSpeaker) = (line.Text, line.Speaker);
        if (speaker != oldSpeaker)
        {
            Reassign(index, oldSpeaker, speaker);
            line.Speaker = speaker;
            line.Brush = BrushFor(speaker);
        }
        line.Text = first;
        line.IsEditing = false;
        var tail = new TranscriptLineViewModel(new TranscriptParagraph(at, line.Speaker, second), BrushFor(line.Speaker));
        Lines.Insert(index + 1, tail);
        _log?.Add("split", line.Seconds, oldSpeaker, speaker, oldText, $"{first} | {second}");
        Commit();
        BeginEdit(tail);
        ScrollToLine?.Invoke(tail);
    }

    void Merge(TranscriptLineViewModel line, bool up)
    {
        var index = Lines.IndexOf(line);
        var other = index + (up ? -1 : 1);
        if (index < 0 || other < 0 || other >= Lines.Count) return;

        BeginChange();
        var text = line.EditText.Trim() is { Length: > 0 } typed ? typed : line.Text;
        // The merged paragraph belongs to the speaker of the one that stays: the earlier one.
        var (keep, gone) = up ? (Lines[other], line) : (line, Lines[other]);
        var goneIndex = Lines.IndexOf(gone);
        Reassign(goneIndex, gone.Speaker, keep.Speaker);
        var joined = up ? $"{keep.Text} {text}" : $"{text} {gone.Text}";
        _log?.Add("merge", keep.Seconds, gone.Speaker, keep.Speaker, $"{keep.Text} | {gone.Text}", joined);
        keep.Text = joined;
        keep.IsEditing = false;
        Lines.Remove(gone);
        Commit();
    }

    void Delete(TranscriptLineViewModel line)
    {
        BeginChange();
        _log?.Add("delete", line.Seconds, line.Speaker, null, line.Text, "");
        Lines.Remove(line);
        Commit();
    }

    // ---- Replace everywhere

    public bool ShowReplace
    {
        get => _showReplace;
        set => Set(ref _showReplace, value);
    }
    bool _showReplace;

    public string ReplaceText
    {
        get => _replaceText;
        set => Set(ref _replaceText, value ?? "");
    }
    string _replaceText = "";

    public string ReplaceMessage
    {
        get => _replaceMessage;
        private set => Set(ref _replaceMessage, value);
    }
    string _replaceMessage = "";

    /// <summary>Replaces what is in the search box with the replacement text, in every paragraph (any capitals).</summary>
    void ReplaceEverywhere()
    {
        if (SearchText.Length == 0)
        {
            ReplaceMessage = "Type the word to replace in the search box first.";
            return;
        }
        var pattern = new Regex(Regex.Escape(SearchText), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var changed = Lines.Where(l => pattern.IsMatch(l.Text)).ToList();
        if (changed.Count == 0)
        {
            ReplaceMessage = "Nothing to replace.";
            return;
        }

        BeginChange();
        var count = 0;
        foreach (var line in changed)
        {
            var before = line.Text;
            count += pattern.Matches(before).Count;
            line.Text = pattern.Replace(before, _ => ReplaceText);
            _log?.Add("replace", line.Seconds, line.Speaker, line.Speaker, before, line.Text);
            Suggest(before, line.Text);
        }
        Commit();
        ReplaceMessage = $"Replaced {count} time{(count == 1 ? "" : "s")} in {changed.Count} paragraph{(changed.Count == 1 ? "" : "s")}.";
    }

    // ---- Keeping the file, the timeline and the log in step

    /// <summary>Before the first change of a change: the log has the transcript as it was written by the speech model.</summary>
    void BeginChange()
    {
        if (_log is null && Selected is { TranscriptPath: { } path }) _log = CorrectionLog.Load(path);
        _log?.EnsureOriginal(Lines.Select(l => l.ToParagraph()));
    }

    /// <summary>Where the paragraph at <paramref name="index"/> ends: when the next one starts, or the end of the recording.</summary>
    double EndOf(int index) =>
        index + 1 < Lines.Count ? Lines[index + 1].Seconds : Duration > Lines[index].Seconds ? Duration : Lines[index].Seconds + 10;

    /// <summary>The paragraph's stretch of the recording now belongs to another speaker in the timeline too.</summary>
    void Reassign(int index, string? from, string? to)
    {
        if (to is null || from is null || _timeline is not { Segments.Count: > 0 }) return;
        _timeline.Reassign(Lines[index].Seconds, EndOf(index), from, to);
        _timelineChanged = true;
    }

    /// <summary>Writes the paragraphs to the transcript file, the log and the timeline, and refreshes what is drawn from them.</summary>
    void Commit()
    {
        if (Selected is { TranscriptPath: { } path })
        {
            try
            {
                File.WriteAllText(path, TranscriptFile.WithParagraphs(File.ReadAllText(path), Lines.Select(l => l.ToParagraph()).ToList()));
                _log?.Save(path);
                if (_timelineChanged) _timeline?.Save(path);
                _timelineChanged = false;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Error($"Could not save the edited transcript {path}", e);
                System.Windows.MessageBox.Show($"Could not save the changes: {e.Message}", "Zeppie Says",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            }
        }
        RebuildSpeakers();
        _currentLine = -1;
        UpdateMatches();
        UpdateCurrentLine();
        if (HasSummary) SummaryOutdated = true;
    }

    // ---- Learning from a correction

    /// <summary>Offers the words the user changed for the dictionary, as the dictation and Whisper's hints can use them.</summary>
    void Suggest(string before, string after)
    {
        foreach (var change in WordDiff.DictionaryCandidates(before, after))
        {
            var known = _settings.AllWords.Any(w => w.Term.Equals(change.After, StringComparison.OrdinalIgnoreCase) &&
                (change.Before.Length == 0 || w.SoundsLike.Contains(change.Before, StringComparer.OrdinalIgnoreCase)));
            var suggestion = new DictionarySuggestion(change.After, change.Before);
            if (known || Suggestions.Contains(suggestion)) continue;
            Suggestions.Add(suggestion);
        }
        while (Suggestions.Count > 6) Suggestions.RemoveAt(0);
    }

    void ClearSuggestions() => Suggestions.Clear();

    void AddToDictionary(DictionarySuggestion suggestion)
    {
        var file = _dictionaryFiles.Files.FirstOrDefault(f => f.IsDefault) ?? _dictionaryFiles.Files.FirstOrDefault();
        if (file is null) return;
        // Adding a word replaces its line, so what it already sounds like has to come along.
        var sounds = _settings.AllWords
            .FirstOrDefault(w => w.Term.Equals(suggestion.Term, StringComparison.OrdinalIgnoreCase))?.SoundsLike.ToList() ?? [];
        if (suggestion.Heard.Length > 0 && !sounds.Contains(suggestion.Heard, StringComparer.OrdinalIgnoreCase))
            sounds.Add(suggestion.Heard);
        try
        {
            _dictionaryFiles.AddWord(file, new DictionaryEntry { Term = suggestion.Term, SoundsLike = sounds });
            Log.Info($"Added '{suggestion.Term}' to the dictionary from a transcript correction");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Error("Could not add a corrected word to the dictionary", e);
            return;
        }
        Suggestions.Remove(suggestion);
    }

    /// <summary>
    /// The user says this stretch is <paramref name="name"/>: adds that audio to the person's voice in the voice
    /// library, so they are recognised better next time. Not for "Speaker 3" or "Others", which name no one.
    /// </summary>
    void TeachVoice(string name, double start, double end)
    {
        if (GenericSpeaker().IsMatch(name) || Selected is not { } job) return;
        LearnFromAudio(VoiceModelId, name, job.AudioPath, [(start, end)]);
    }

    /// <summary>Learns a person's voice, in the background, from the given stretches (seconds) of a recording.</summary>
    void LearnFromAudio(string model, string name, string? audio, IReadOnlyList<(double Start, double End)> stretches)
    {
        if (audio is null || !File.Exists(audio) || stretches.Count == 0) return;
        _ = Task.Run(() =>
        {
            try
            {
                var samples = AudioFile.Load(audio);
                var ranges = stretches.Select(s => (
                    (int)Math.Clamp(s.Start * Audio.SampleRate, 0, samples.Length),
                    (int)Math.Clamp(s.End * Audio.SampleRate, 0, samples.Length))).Where(r => r.Item2 > r.Item1);
                if (VoiceLibrary.Voiceprint(model, samples, ranges) is not { } voice)
                {
                    Log.Info($"Too little speech to learn {name}'s voice from");
                    return;
                }
                _voices.Remember(model, name, voice);
                Log.Info($"Learned {name}'s voice from the recording");
            }
            catch (Exception e)
            {
                Log.Warn($"Could not learn {name}'s voice: {e.Message}");
            }
        });
    }

    // ---- The summary after editing

    /// <summary>The summary was written before the latest edits.</summary>
    public bool SummaryOutdated
    {
        get => _summaryOutdated;
        private set => Set(ref _summaryOutdated, value);
    }
    bool _summaryOutdated;

    public bool IsWritingSummary
    {
        get => _isWritingSummary;
        private set
        {
            if (Set(ref _isWritingSummary, value)) Raise(nameof(RegenerateLabel));
        }
    }
    bool _isWritingSummary;

    public string? SummaryError
    {
        get => _summaryError;
        private set => Set(ref _summaryError, value);
    }
    string? _summaryError;

    /// <summary>The Summary tab opens when there is a summary, or one can be written.</summary>
    public bool CanOpenSummary => HasSummary || SummaryAvailable;

    public string RegenerateLabel => IsWritingSummary ? "Writing…" : HasSummary ? "Regenerate summary" : "Write summary";

    async Task RegenerateSummaryAsync()
    {
        if (IsWritingSummary || Selected is not { TranscriptPath: { } path } job) return;
        IsWritingSummary = true;
        SummaryError = null;
        try
        {
            var transcript = new Transcript { Title = _title, Duration = TimeSpan.FromSeconds(Duration), ModelName = "" };
            transcript.Paragraphs.AddRange(Lines.Select(l => l.ToParagraph()));
            var notes = await _summarizer.SummarizeAsync(transcript, CancellationToken.None);
            if (notes is null)
            {
                SummaryError = "No summary model is available. Check the default model on the Styles page.";
                return;
            }
            await Task.Run(() => File.WriteAllText(path, TranscriptFile.WithNotes(File.ReadAllText(path), notes)));
            if (Selected != job) return;
            Summary = notes;
            SummaryOutdated = false;
        }
        catch (Exception e)
        {
            Log.Warn($"Writing the summary again failed: {e.Message}");
            SummaryError = e is LlmException ? e.Message : "The summary could not be written. See the log.";
        }
        finally
        {
            IsWritingSummary = false;
        }
    }
}
