using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using ZeppieSays.Core;
using ZeppieSays.Engines;
using ZeppieSays.Services;
using ZeppieSays.UI;

namespace ZeppieSays.Notes;

public enum NoteStatus { Waiting, Working, Done, Failed, Ready }

/// <summary>One audio file in the Notetaker list, from waiting in the folder to a finished transcript.</summary>
public sealed class NoteJob(string audioPath) : ObservableObject
{
    public string AudioPath { get; set; } = audioPath;
    public string Name => Path.GetFileNameWithoutExtension(AudioPath);

    /// <summary>Settings for this file alone; null means the general Notetaker settings at the time it runs.</summary>
    public NoteOptions? Options { get; set; }

    public NoteStatus Status { get => _status; set { if (Set(ref _status, value)) { Raise(nameof(IsWorking)); Raise(nameof(IsDone)); Raise(nameof(IsFailed)); Raise(nameof(IsReady)); } } }
    NoteStatus _status;

    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }
    string _statusText = "Waiting";

    /// <summary>0..1, or below zero for a step without a known length.</summary>
    public double Progress { get => _progress; set { if (Set(ref _progress, value)) Raise(nameof(IsIndeterminate)); } }
    double _progress;
    public bool IsIndeterminate => _progress < 0;

    public string? TranscriptPath { get => _transcriptPath; set => Set(ref _transcriptPath, value); }
    string? _transcriptPath;

    public DateTime When { get; set; } = DateTime.Now;
    public bool IsWorking => Status == NoteStatus.Working;
    public bool IsDone => Status == NoteStatus.Done;
    public bool IsFailed => Status == NoteStatus.Failed;
    /// <summary>Dropped by the user and waiting for them to adjust its settings and press Start.</summary>
    public bool IsReady => Status == NoteStatus.Ready;
}

/// <summary>
/// Watches the Notetaker folder and transcribes every audio file that lands in it, one at a time: speakers
/// (optional), text with timestamps, a summary (optional). The transcript is written as Markdown next to the
/// audio, and both move to the Done subfolder. Work pauses while the user dictates, so the keys stay fast.
/// </summary>
public sealed class Notetaker : IDisposable
{
    const string DoneFolder = "Done";
    /// <summary>Whisper only reads about 220 tokens of prompt; a short description of the recording is enough.</summary>
    const int MaxContextChars = 300;

    readonly AppSettings _settings;
    readonly EngineCache _engines;
    readonly ModelManager _models;
    readonly Summarizer _summarizer;
    readonly VoiceLibrary _voices;
    readonly Func<bool> _isDictating;
    readonly Dispatcher _ui;
    readonly SemaphoreSlim _work = new(0);
    readonly CancellationTokenSource _stop = new();
    FileSystemWatcher? _watcher;
    /// <summary>Dropped files that wait for the user's go-ahead once they show up in the folder, and the ones we copied there.</summary>
    readonly HashSet<string> _review = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _copied = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<NoteJob> Jobs { get; } = [];

    NotetakerSettings Options => _settings.Notetaker;

    public Notetaker(AppSettings settings, EngineCache engines, ModelManager models, Summarizer summarizer,
        VoiceLibrary voices, Func<bool> isDictating, Dispatcher ui)
    {
        _settings = settings;
        _engines = engines;
        _models = models;
        _summarizer = summarizer;
        _voices = voices;
        _isDictating = isDictating;
        _ui = ui;
        _ = Task.Run(WorkAsync);
    }

    public string DoneDirectory => Path.Combine(Options.Folder, DoneFolder);

    /// <summary>(Re)starts watching the folder: lists finished transcripts and queues every audio file waiting there.</summary>
    public void Watch()
    {
        _watcher?.Dispose();
        _watcher = null;
        Jobs.Clear();
        try
        {
            Directory.CreateDirectory(Options.Folder);
            Directory.CreateDirectory(DoneDirectory);
        }
        catch (Exception e)
        {
            Log.Error($"Notetaker folder {Options.Folder} is not usable", e);
            return;
        }

        foreach (var md in Directory.GetFiles(DoneDirectory, "*.md").OrderByDescending(File.GetLastWriteTime).Take(200))
            Jobs.Add(new NoteJob(md) { Status = NoteStatus.Done, StatusText = "Done", TranscriptPath = md, When = File.GetLastWriteTime(md) });

        _watcher = new FileSystemWatcher(Options.Folder) { IncludeSubdirectories = false, EnableRaisingEvents = true };
        _watcher.Created += (_, e) => _ui.BeginInvoke(() => Enqueue(e.FullPath));
        _watcher.Renamed += (_, e) => _ui.BeginInvoke(() => Enqueue(e.FullPath));
        foreach (var file in Directory.GetFiles(Options.Folder).OrderBy(File.GetLastWriteTime))
            Enqueue(file);
    }

    /// <summary>
    /// Copies files the user dropped onto the page into the folder; the folder watch picks them up. They wait as
    /// Ready until the user has looked at their settings and pressed Start.
    /// </summary>
    public void AddFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths.Where(AudioFile.IsAudio))
        {
            if (Path.GetDirectoryName(Path.GetFullPath(path))!.Equals(Path.GetFullPath(Options.Folder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                _review.Add(Path.GetFullPath(path));
                Enqueue(path);
                continue;
            }
            var target = UniquePath(Options.Folder, Path.GetFileName(path));
            _review.Add(Path.GetFullPath(target));
            _copied.Add(Path.GetFullPath(target));
            _ = Task.Run(() =>
            {
                try { File.Copy(path, target); }
                catch (Exception e) { Log.Error($"Could not copy {path} into the Notetaker folder", e); }
            });
        }
    }

    public void Retry(NoteJob job)
    {
        if (job.Status != NoteStatus.Failed) return;
        job.Status = NoteStatus.Waiting;
        job.StatusText = "Waiting";
        _work.Release();
    }

    /// <summary>Starts a dropped file with the settings the user chose for it.</summary>
    public void Start(NoteJob job)
    {
        if (!job.IsReady) return;
        job.Status = NoteStatus.Waiting;
        job.StatusText = "Waiting";
        _work.Release();
    }

    public void StartAll()
    {
        foreach (var job in Jobs.Where(j => j.IsReady).Reverse().ToList()) Start(job);
    }

    /// <summary>Takes a file that hasn't started off the list. A copy we made in the folder goes too; the original is never touched.</summary>
    public void Discard(NoteJob job)
    {
        if (!job.IsReady) return;
        Jobs.Remove(job);
        var path = Path.GetFullPath(job.AudioPath);
        if (!_copied.Remove(path)) return;
        try { File.Delete(path); }
        catch (Exception e) { Log.Warn($"Could not delete the copy {path}: {e.Message}"); }
    }

    void Enqueue(string path)
    {
        if (!AudioFile.IsAudio(path) || Jobs.Any(j => j.AudioPath.Equals(path, StringComparison.OrdinalIgnoreCase)))
            return;
        if (_review.Remove(Path.GetFullPath(path)))
        {
            Jobs.Insert(0, new NoteJob(path)
            {
                Status = NoteStatus.Ready,
                StatusText = "Ready · adjust its settings, then start",
                Options = NoteOptions.From(Options),
            });
            return;
        }
        Jobs.Insert(0, new NoteJob(path));
        _work.Release();
    }

    async Task WorkAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            await _work.WaitAsync(_stop.Token);
            while (await _ui.InvokeAsync(() => Jobs.LastOrDefault(j => j.Status == NoteStatus.Waiting)) is { } job)
                await ProcessAsync(job);
        }
    }

    async Task ProcessAsync(NoteJob job)
    {
        void Report(string text, double progress = -1) => _ui.BeginInvoke(() =>
        {
            job.Status = NoteStatus.Working;
            job.StatusText = text;
            job.Progress = progress;
        });

        var ct = _stop.Token;
        try
        {
            Report("Waiting for the file to finish copying");
            await WaitUntilReadyAsync(job.AudioPath, ct);

            var options = job.Options ?? NoteOptions.From(Options);
            var model = _models.Find(options.ModelId)
                ?? throw new InvalidOperationException("Pick an installed model on the Notetaker page.");

            Report("Reading audio");
            var recording = RecordingInfo.For(job.AudioPath);
            var tracks = await Task.Run(() => AudioFile.LoadChannels(job.AudioPath, recording is null ? 1 : 2), ct);
            var duration = TimeSpan.FromSeconds(tracks[0].Length / (double)Audio.SampleRate);
            Log.Info($"Notetaker: {Path.GetFileName(job.AudioPath)}, {duration.TotalMinutes:F1} min{(recording is null ? "" : ", meeting recording")}");

            var transcript = new Transcript { Title = options.Title.Trim() is { Length: > 0 } title ? title : job.Name, Duration = duration, ModelName = model.Info.Name };
            var turns = recording is null
                ? await FileTurnsAsync(tracks[0], options, transcript, Report, ct)
                : await MeetingTurnsAsync(tracks[0], tracks[1], options, transcript, Report, ct);

            using (_engines.Hold(model.Info.Id))
                await TranscribeAsync(turns, model, options, transcript, Report, ct);

            if (options.Summarize && transcript.Paragraphs.Sum(p => p.Text.Length) > 200)
            {
                Report("Writing the summary");
                try { transcript.Notes = await _summarizer.SummarizeAsync(transcript, ct, options.Context); }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    Log.Warn($"Notetaker summary failed: {e.Message}");
                }
            }

            var (audio, markdown) = await Task.Run(() => Save(job.AudioPath, transcript), ct);
            _ui.Invoke(() =>
            {
                job.AudioPath = audio;
                job.TranscriptPath = markdown;
                job.When = DateTime.Now;
                job.Status = NoteStatus.Done;
                job.StatusText = $"Done · {Transcript.Length(duration)}";
            });
            Log.Info($"Notetaker: wrote {Path.GetFileName(markdown)}, {transcript.Paragraphs.Count} paragraphs, {transcript.Speakers} speakers");
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Log.Error($"Notetaker failed on {job.AudioPath}", e);
            _ui.Invoke(() =>
            {
                job.Status = NoteStatus.Failed;
                job.StatusText = e is InvalidOperationException ? e.Message : "Could not transcribe this file. See the log for details.";
            });
        }
    }

    /// <summary>A stretch of one track to transcribe, and who speaks in it (null: unknown, one paragraph per piece).</summary>
    sealed record Turn(float[] Track, int Start, int End, string? Speaker);

    /// <summary>An ordinary audio file: speaker detection on the whole file when asked for, otherwise one long turn.</summary>
    async Task<List<Turn>> FileTurnsAsync(float[] samples, NoteOptions options, Transcript transcript, Action<string, double> report,
        CancellationToken ct)
    {
        if (options.LabelSpeakers && await FindSpeakersAsync(samples, options.SpeakerCount, report, ct) is { } speakers)
            return await NameSpeakersAsync(samples, speakers, transcript, report, ct);
        return [new Turn(samples, 0, samples.Length, null)];
    }

    /// <summary>
    /// A two-track meeting recording: everything on the microphone track is the user (minus echo of the call),
    /// and only the call track needs speaker detection.
    /// </summary>
    async Task<List<Turn>> MeetingTurnsAsync(float[] mic, float[] call, NoteOptions options, Transcript transcript,
        Action<string, double> report, CancellationToken ct)
    {
        var me = UserName.First is { Length: > 0 } first ? first : "You";
        var mine = AudioFile.SpeechRegions(mic, call);
        var turns = mine.Select(r => new Turn(mic, r.Start, r.End, me)).ToList();

        // The people-count setting includes the user, who is on the other track.
        var others = options.SpeakerCount > 1 ? options.SpeakerCount - 1 : 0;
        if (options.LabelSpeakers && await FindSpeakersAsync(call, others, report, ct) is { } speakers)
            turns.AddRange(await NameSpeakersAsync(call, speakers, transcript, report, ct));
        else
            turns.AddRange(AudioFile.SpeechRegions(call).Select(r => new Turn(call, r.Start, r.End, "Others")));

        // The user's own voice, learned from every meeting with enough of it, so they are recognised in ordinary
        // recordings too. A few words make a poor voiceprint that would weigh on the average for a long time.
        var spoken = mine.Sum(r => r.End - r.Start) / (double)Audio.SampleRate;
        if (me != "You" && spoken >= 20 && await Task.Run(() => VoiceLibrary.Voiceprint(mic, mine), ct) is { } voice)
        {
            _voices.Remember(me, voice);
            transcript.Voiceprints[me] = voice;
        }
        return [.. turns.OrderBy(t => t.Start)];
    }

    async Task<IReadOnlyList<SpeakerTurn>?> FindSpeakersAsync(float[] samples, int count, Action<string, double> report, CancellationToken ct)
    {
        try
        {
            if (!SpeakerDiarizer.IsInstalled)
            {
                report("Downloading speaker models", 0);
                await SpeakerDiarizer.EnsureInstalledAsync(new Progress<double>(p => report("Downloading speaker models", p)), ct);
            }
            report("Finding speakers", 0);
            var turns = await Task.Run(() =>
                SpeakerDiarizer.Run(samples, new Progress<double>(p => report("Finding speakers", p)), count), ct);
            return turns.Count > 0 ? turns : null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Warn($"Speaker labels failed, transcribing without them: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Puts names on detected speakers whose voices are in the library; the rest become "Speaker 1", "Speaker 2", …
    /// Every speaker's voiceprint is kept with the transcript, so the user can name them afterwards.
    /// </summary>
    async Task<List<Turn>> NameSpeakersAsync(float[] samples, IReadOnlyList<SpeakerTurn> speakers, Transcript transcript,
        Action<string, double> report, CancellationToken ct)
    {
        report("Recognising voices", -1);
        var voiceprints = await Task.Run(() => speakers.GroupBy(t => t.Speaker)
            .Select(g => (Speaker: g.Key, Voice: VoiceLibrary.Voiceprint(samples, g.Select(t => (t.Start, t.End)))))
            .Where(v => v.Voice is not null)
            .ToDictionary(v => v.Speaker, v => v.Voice!), ct);
        var known = _voices.Recognize(voiceprints);

        var labels = new Dictionary<int, string>();
        var unknown = 0;
        foreach (var speaker in speakers.Select(t => t.Speaker).Distinct())
        {
            labels[speaker] = known.TryGetValue(speaker, out var name) ? name : $"Speaker {++unknown}";
            if (voiceprints.TryGetValue(speaker, out var voice)) transcript.Voiceprints[labels[speaker]] = voice;
        }
        return speakers.Select(t => new Turn(samples, t.Start, t.End, labels[t.Speaker])).ToList();
    }

    async Task TranscribeAsync(IReadOnlyList<Turn> turns, InstalledModel model, NoteOptions options, Transcript transcript,
        Action<string, double> report, CancellationToken ct)
    {
        var engine = await _engines.GetAsync(model.Info.Id);
        var prompt = "";
        if (model.Info.SupportsPrompt)
        {
            // What the user says the recording is about steers Whisper's spelling, like a line of preceding text.
            var about = options.Context.Trim();
            if (about.Length > MaxContextChars) about = about[..MaxContextChars];
            prompt = (about.Length > 0 ? about + " " : "") +
                     (options.UseDictionary ? TextShaping.BuildPrompt(_settings.AllWords, ScreenContext.Empty) : "");
            prompt = prompt.Trim();
        }
        var language = model.Info.AutoLanguageOnly ? "auto" : options.Language;
        var pieces = turns.Select(t => (Turn: t, Pieces: AudioFile.Split(t.Track, t.Start, t.End))).ToList();
        var total = Math.Max(1, pieces.Sum(p => p.Pieces.Count));
        var done = 0;

        foreach (var (turn, ranges) in pieces)
        {
            var texts = new List<(int Start, string Text)>();
            foreach (var (start, end) in ranges)
            {
                report("Transcribing", done++ / (double)total);
                if (AudioFile.IsSilent(turn.Track, start, end)) continue;
                while (_isDictating()) await Task.Delay(250, ct); // the keys go first
                var raw = await engine.TranscribeAsync(new(AudioFile.Slice(turn.Track, start, end), language, prompt), ct);
                var text = TextShaping.Clean(raw, _settings, ScreenContext.Empty);
                if (text.Length > 0) texts.Add((start, text));
            }
            if (texts.Count == 0) continue;
            // With speakers: one paragraph per turn. Without: one per piece, so timestamps stay close together.
            if (turn.Speaker is not null)
                transcript.Paragraphs.Add(new(turn.Start / (double)Audio.SampleRate, turn.Speaker, string.Join(" ", texts.Select(t => t.Text))));
            else
                transcript.Paragraphs.AddRange(texts.Select(t => new TranscriptParagraph(t.Start / (double)Audio.SampleRate, null, t.Text)));
        }
    }

    /// <summary>
    /// Moves the audio (and its recording description) into Done and writes the transcript and its speakers'
    /// voiceprints beside it; returns the new audio and transcript paths.
    /// </summary>
    (string Audio, string Markdown) Save(string audioPath, Transcript transcript)
    {
        Directory.CreateDirectory(DoneDirectory);
        var audio = UniquePath(DoneDirectory, Path.GetFileName(audioPath));
        var markdown = Path.ChangeExtension(audio, ".md");
        File.WriteAllText(markdown, transcript.ToMarkdown());
        if (transcript.Voiceprints.Count > 0) transcript.SaveVoiceprints(markdown);
        File.Move(audioPath, audio);
        if (File.Exists(RecordingInfo.PathFor(audioPath)))
            File.Move(RecordingInfo.PathFor(audioPath), RecordingInfo.PathFor(audio));
        return (audio, markdown);
    }

    /// <summary>A file that is still being copied or recorded can't be opened exclusively; wait until it can.</summary>
    static async Task WaitUntilReadyAsync(string path, CancellationToken ct)
    {
        long lastSize = -1;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var size = new FileInfo(path).Length;
                using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None)) { }
                if (size == lastSize && size > 0) return;
                lastSize = size;
            }
            catch (FileNotFoundException) { throw new InvalidOperationException("The file was moved or deleted."); }
            catch (IOException) { lastSize = -1; }
            await Task.Delay(1000, ct);
        }
    }

    static string UniquePath(string dir, string fileName)
    {
        var path = Path.Combine(dir, fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 2; File.Exists(path) || File.Exists(Path.ChangeExtension(path, ".md")); i++)
            path = Path.Combine(dir, $"{stem} ({i}){ext}");
        return path;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _watcher?.Dispose();
    }
}
