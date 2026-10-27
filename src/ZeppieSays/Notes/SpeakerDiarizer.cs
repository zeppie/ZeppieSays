using System.IO;
using SherpaOnnx;
using ZeppieSays.Core;
using ZeppieSays.Engines;
using ZeppieSays.Services;

namespace ZeppieSays.Notes;

/// <summary>One stretch of a recording where a single person speaks.</summary>
public sealed record SpeakerTurn(int Start, int End, int Speaker);

/// <summary>
/// Works out who speaks when (pyannote segmentation plus voiceprints from a <see cref="VoiceModel"/> through
/// sherpa-onnx), on the CPU. The small models (~35 MB) are downloaded the first time speaker labels are used.
/// </summary>
public static class SpeakerDiarizer
{
    const string Release = "https://github.com/k2-fsa/sherpa-onnx/releases/download/";
    const string SegmentationUrl = Release + "speaker-segmentation-models/sherpa-onnx-pyannote-segmentation-3-0.tar.bz2";
    const string EmbeddingRelease = Release + "speaker-recongition-models/";

    static string Dir => Path.Combine(AppPaths.Models, "speakers");
    static string SegmentationDir => Path.Combine(Dir, "segmentation");

    /// <summary>Where a voice model lives; the first model kept its old name, embedding.onnx.</summary>
    internal static string EmbeddingPath(string model) =>
        Path.Combine(Dir, model == VoiceModel.LegacyId ? "embedding.onnx" : VoiceModel.Get(model).FileName);

    public static bool IsInstalled(string model) =>
        File.Exists(EmbeddingPath(model)) && Directory.Exists(SegmentationDir) && Directory.GetFiles(SegmentationDir, "*.onnx").Length > 0;

    public static async Task EnsureInstalledAsync(string model, IProgress<double> progress, CancellationToken ct)
    {
        if (IsInstalled(model)) return;
        Directory.CreateDirectory(Dir);
        var voice = VoiceModel.Get(model);
        Log.Info($"Downloading speaker models ({voice.Name})");
        var archive = Path.Combine(Dir, "segmentation.download");
        var embedding = EmbeddingPath(model) + ".download";
        try
        {
            var segmentationShare = 0.0;
            if (!Directory.Exists(SegmentationDir) || Directory.GetFiles(SegmentationDir, "*.onnx").Length == 0)
            {
                segmentationShare = 0.2;
                await ModelManager.DownloadAsync(SegmentationUrl, archive, 7_000_000, new Progress<double>(p => progress.Report(p * 0.2)), ct);
                await Task.Run(() => ModelManager.ExtractFlattened(archive, SegmentationDir), ct);
            }
            if (!File.Exists(EmbeddingPath(model)))
            {
                await ModelManager.DownloadAsync(EmbeddingRelease + voice.FileName, embedding, voice.Size,
                    new Progress<double>(p => progress.Report(segmentationShare + p * (1 - segmentationShare))), ct);
                File.Move(embedding, EmbeddingPath(model), overwrite: true);
            }
        }
        finally
        {
            if (File.Exists(archive)) File.Delete(archive);
            if (File.Exists(embedding)) File.Delete(embedding);
        }
    }

    /// <summary>Speaker turns in order, with speakers numbered 1, 2, … by when they first speak.</summary>
    /// <param name="speakers">The number of people when known, which is more reliable; 0 lets the threshold decide.</param>
    public static IReadOnlyList<SpeakerTurn> Run(float[] samples, IProgress<double> progress, string model, SpeakerSettings settings,
        int speakers = 0)
    {
        var threshold = settings.Threshold ?? VoiceModel.Get(model).DefaultThreshold;
        List<SpeakerTurn> segments;
        if (speakers > 0)
        {
            // Asked for N groups, the clustering sometimes spends one on a stray bit of noise or crosstalk that then
            // has no speech left, and returns fewer people. Asking for one or two more and folding the smallest
            // extras back in gets the number the user gave.
            segments = [];
            for (var ask = speakers; ask <= speakers + 2; ask++)
            {
                segments = Detect(samples, progress, model, settings, threshold, ask);
                var found = segments.Select(s => s.Speaker).Distinct().Count();
                if (found >= speakers)
                {
                    if (ask > speakers) Log.Info($"Speaker detection: asked for {ask} groups to find {speakers} people");
                    break;
                }
            }
            segments = KeepLargest(samples, model, segments, speakers);
        }
        else
        {
            segments = Detect(samples, progress, model, settings, threshold, -1);
            var minSpeech = settings.MinSpeechFor(samples.Length / (double)Audio.SampleRate);
            if (minSpeech > 0) segments = MergeSmallClusters(samples, model, segments, minSpeech);
        }

        // Number speakers by first appearance and merge a speaker's segments that follow each other closely.
        var numbers = new Dictionary<int, int>();
        var turns = new List<SpeakerTurn>();
        foreach (var s in segments)
        {
            if (!numbers.TryGetValue(s.Speaker, out var speaker))
                numbers[s.Speaker] = speaker = numbers.Count + 1;
            if (turns.Count > 0 && turns[^1].Speaker == speaker && s.Start - turns[^1].End < Audio.SampleRate * 3 / 2)
                turns[^1] = turns[^1] with { End = Math.Max(s.End, turns[^1].End) };
            else
                turns.Add(s with { Speaker = speaker });
        }
        return turns;
    }

    /// <summary>One pass of sherpa-onnx: segments with its own cluster numbers, in order.</summary>
    static List<SpeakerTurn> Detect(float[] samples, IProgress<double> progress, string model, SpeakerSettings settings,
        float threshold, int clusters)
    {
        var segmentation = Directory.GetFiles(SegmentationDir, "*.onnx");
        var config = new OfflineSpeakerDiarizationConfig();
        config.Segmentation.Pyannote.Model = segmentation.FirstOrDefault(f => f.Contains("int8")) ?? segmentation[0];
        config.Segmentation.NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
        config.Embedding.Model = EmbeddingPath(model);
        config.Embedding.NumThreads = config.Segmentation.NumThreads;
        config.Clustering.NumClusters = clusters;
        config.Clustering.Threshold = threshold;
        config.MinDurationOn = (float)settings.ShortestSpeech;
        config.MinDurationOff = (float)settings.ShortestPause;

        using var diarizer = new OfflineSpeakerDiarization(config);
        OfflineSpeakerDiarizationProgressCallback callback = (done, total, _) =>
        {
            if (total > 0) progress.Report((double)done / total);
            return 0;
        };
        var result = diarizer.ProcessWithCallback(samples, callback, IntPtr.Zero);
        GC.KeepAlive(callback); // native code calls it until Process returns

        return result
            .Select(s => new SpeakerTurn(
                Math.Clamp((int)(s.Start * Audio.SampleRate), 0, samples.Length),
                Math.Clamp((int)(s.End * Audio.SampleRate), 0, samples.Length),
                s.Speaker))
            .Where(s => s.End > s.Start)
            .OrderBy(s => s.Start)
            .ToList();
    }

    /// <summary>Seconds of speech per detected speaker.</summary>
    static Dictionary<int, double> SpeechPerSpeaker(IEnumerable<SpeakerTurn> segments) =>
        segments.GroupBy(s => s.Speaker).ToDictionary(g => g.Key, g => g.Sum(s => s.End - s.Start) / (double)Audio.SampleRate);

    /// <summary>The <paramref name="count"/> speakers who talk most; everyone else is folded into them.</summary>
    static List<SpeakerTurn> KeepLargest(float[] samples, string model, List<SpeakerTurn> segments, int count)
    {
        var main = SpeechPerSpeaker(segments).OrderByDescending(s => s.Value).Take(count).Select(s => s.Key).ToHashSet();
        return Fold(samples, model, segments, main);
    }

    /// <summary>
    /// Without a known number of people, clustering on real recordings splits off fragments (a laugh, a "ja",
    /// crosstalk) as extra speakers. Each speaker with under <paramref name="minSeconds"/> of speech is folded into
    /// a main speaker (see <see cref="Fold"/>).
    /// </summary>
    static List<SpeakerTurn> MergeSmallClusters(float[] samples, string model, List<SpeakerTurn> segments, double minSeconds)
    {
        var seconds = SpeechPerSpeaker(segments);
        var main = seconds.Where(s => s.Value >= minSeconds).Select(s => s.Key).ToHashSet();
        if (main.Count == 0 && seconds.Count > 0) main.Add(seconds.MaxBy(s => s.Value).Key);
        return Fold(samples, model, segments, main);
    }

    /// <summary>
    /// Gives every speaker outside <paramref name="main"/> to the main speaker whose voice it is closest to, or, when
    /// it's too short to compare, to the one speaking nearest in time.
    /// </summary>
    static List<SpeakerTurn> Fold(float[] samples, string model, List<SpeakerTurn> segments, HashSet<int> main)
    {
        var small = segments.Select(s => s.Speaker).Distinct().Where(k => !main.Contains(k)).ToList();
        if (main.Count == 0 || small.Count == 0) return segments;

        float[]? Voice(int speaker) => VoiceLibrary.Voiceprint(model, samples,
            segments.Where(s => s.Speaker == speaker).OrderByDescending(s => s.End - s.Start).Select(s => (s.Start, s.End)));
        var mainVoices = main.Select(m => (Speaker: m, Voice: Voice(m))).Where(v => v.Voice is not null).ToList();

        var into = new Dictionary<int, int>();
        foreach (var speaker in small)
        {
            if (Voice(speaker) is { } voice && mainVoices.Count > 0)
                into[speaker] = mainVoices.MaxBy(m => VoiceLibrary.Similarity(voice, m.Voice!)).Speaker;
            else
            {
                var first = segments.First(s => s.Speaker == speaker);
                into[speaker] = segments.Where(s => main.Contains(s.Speaker))
                    .MinBy(s => Math.Abs(s.Start - first.Start))!.Speaker;
            }
        }
        Log.Info($"Speaker detection: {main.Count} speakers, {small.Count} fragments merged into them");
        return segments.Select(s => into.TryGetValue(s.Speaker, out var target) ? s with { Speaker = target } : s).ToList();
    }
}
