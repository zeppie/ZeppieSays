using System.IO;
using SherpaOnnx;
using ZeppieSays.Core;
using ZeppieSays.Engines;
using ZeppieSays.Services;

namespace ZeppieSays.Notes;

/// <summary>One stretch of a recording where a single person speaks.</summary>
public sealed record SpeakerTurn(int Start, int End, int Speaker);

/// <summary>
/// Works out who speaks when (pyannote segmentation plus WeSpeaker voice embeddings through sherpa-onnx), on the CPU.
/// The two small models (~33 MB) are downloaded the first time speaker labels are used.
/// </summary>
public static class SpeakerDiarizer
{
    const string Release = "https://github.com/k2-fsa/sherpa-onnx/releases/download/";
    const string SegmentationUrl = Release + "speaker-segmentation-models/sherpa-onnx-pyannote-segmentation-3-0.tar.bz2";
    const string EmbeddingUrl = Release + "speaker-recongition-models/wespeaker_en_voxceleb_resnet34_LM.onnx";

    static string Dir => Path.Combine(AppPaths.Models, "speakers");
    static string SegmentationDir => Path.Combine(Dir, "segmentation");
    internal static string EmbeddingPath => Path.Combine(Dir, "embedding.onnx");

    public static bool IsInstalled =>
        File.Exists(EmbeddingPath) && Directory.Exists(SegmentationDir) && Directory.GetFiles(SegmentationDir, "*.onnx").Length > 0;

    public static async Task EnsureInstalledAsync(IProgress<double> progress, CancellationToken ct)
    {
        if (IsInstalled) return;
        Directory.CreateDirectory(Dir);
        Log.Info("Downloading speaker models");
        var archive = Path.Combine(Dir, "segmentation.download");
        var embedding = EmbeddingPath + ".download";
        try
        {
            await ModelManager.DownloadAsync(SegmentationUrl, archive, 7_000_000, new Progress<double>(p => progress.Report(p * 0.2)), ct);
            await Task.Run(() => ModelManager.ExtractFlattened(archive, SegmentationDir), ct);
            await ModelManager.DownloadAsync(EmbeddingUrl, embedding, 26_500_000, new Progress<double>(p => progress.Report(0.2 + p * 0.8)), ct);
            File.Move(embedding, EmbeddingPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(archive)) File.Delete(archive);
            if (File.Exists(embedding)) File.Delete(embedding);
        }
    }

    /// <summary>How alike two voices must sound to count as one speaker; lower splits one voice into several.</summary>
    public const float DefaultThreshold = 0.5f;

    /// <summary>Speaker turns in order, with speakers numbered 1, 2, … by when they first speak.</summary>
    /// <param name="speakers">The number of people when known, which is more reliable; 0 lets the threshold decide.</param>
    public static IReadOnlyList<SpeakerTurn> Run(float[] samples, IProgress<double> progress, int speakers = 0,
        float threshold = DefaultThreshold)
    {
        var segmentation = Directory.GetFiles(SegmentationDir, "*.onnx");
        var config = new OfflineSpeakerDiarizationConfig();
        config.Segmentation.Pyannote.Model = segmentation.FirstOrDefault(f => f.Contains("int8")) ?? segmentation[0];
        config.Segmentation.NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
        config.Embedding.Model = EmbeddingPath;
        config.Embedding.NumThreads = config.Segmentation.NumThreads;
        config.Clustering.NumClusters = speakers > 0 ? speakers : -1;
        config.Clustering.Threshold = threshold;
        config.MinDurationOn = 0.3f;
        config.MinDurationOff = 0.5f;

        using var diarizer = new OfflineSpeakerDiarization(config);
        OfflineSpeakerDiarizationProgressCallback callback = (done, total, _) =>
        {
            if (total > 0) progress.Report((double)done / total);
            return 0;
        };
        var result = diarizer.ProcessWithCallback(samples, callback, IntPtr.Zero);
        GC.KeepAlive(callback); // native code calls it until Process returns

        var segments = result
            .Select(s => new SpeakerTurn(
                Math.Clamp((int)(s.Start * Audio.SampleRate), 0, samples.Length),
                Math.Clamp((int)(s.End * Audio.SampleRate), 0, samples.Length),
                s.Speaker))
            .Where(s => s.End > s.Start)
            .OrderBy(s => s.Start)
            .ToList();
        if (speakers <= 0) segments = MergeSmallClusters(samples, segments);

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

    /// <summary>Speech below which a detected "speaker" is taken to be a fragment of someone else.</summary>
    const int MinSpeakerSeconds = 15;

    /// <summary>
    /// Without a known number of people, clustering on real recordings splits off fragments (a laugh, a "ja",
    /// crosstalk) as extra speakers. Each speaker with under <see cref="MinSpeakerSeconds"/> of speech is folded into
    /// the main speaker whose voice it is closest to, or, when it's too short to compare, the one speaking nearest in time.
    /// </summary>
    static List<SpeakerTurn> MergeSmallClusters(float[] samples, List<SpeakerTurn> segments)
    {
        var seconds = segments.GroupBy(s => s.Speaker)
            .ToDictionary(g => g.Key, g => g.Sum(s => s.End - s.Start) / (double)Audio.SampleRate);
        var main = seconds.Where(s => s.Value >= MinSpeakerSeconds).Select(s => s.Key).ToHashSet();
        var small = seconds.Keys.Where(k => !main.Contains(k)).ToList();
        if (main.Count == 0 || small.Count == 0) return segments;

        float[]? Voice(int speaker) => VoiceLibrary.Voiceprint(samples,
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
