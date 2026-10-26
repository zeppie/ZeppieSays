using System.IO;
using System.Text.Json;

namespace ZeppieSays.Notes;

/// <summary>One stretch of a recording in which a speaker talks, in seconds.</summary>
public sealed record SpeakerSegment(string Speaker, double Start, double End);

/// <summary>
/// What the Notetaker page draws under the player: when each speaker talks and the loudness of the recording.
/// Kept next to the transcript (name.timeline.json). Transcripts made before this existed have none; the page
/// then works out the waveform itself and shows no speaker segments.
/// </summary>
public sealed class Timeline
{
    /// <summary>Bars in the waveform, however long the recording is.</summary>
    public const int PeakCount = 1200;

    public double Duration { get; set; }
    public List<SpeakerSegment> Segments { get; set; } = [];
    /// <summary>0 to 1 per slice of the recording; see <see cref="ComputePeaks"/>.</summary>
    public float[] Peaks { get; set; } = [];

    public static string PathFor(string markdownPath) => Path.ChangeExtension(markdownPath, ".timeline.json");

    public static Timeline? Load(string markdownPath)
    {
        try
        {
            var path = PathFor(markdownPath);
            return File.Exists(path) ? JsonSerializer.Deserialize<Timeline>(File.ReadAllText(path)) : null;
        }
        catch
        {
            return null; // damaged: treated as missing
        }
    }

    public void Save(string markdownPath) => File.WriteAllText(PathFor(markdownPath), JsonSerializer.Serialize(this));

    /// <summary>Renames a speaker, after the user named them in the transcript.</summary>
    public void Rename(string from, string to) =>
        Segments = Segments.Select(s => s.Speaker == from ? s with { Speaker = to } : s).ToList();

    /// <summary>
    /// The loudest sample in each of <paramref name="count"/> equal slices, over all tracks, scaled so the loudest
    /// slice is 1. A square root lifts quiet speech so it isn't a flat line next to a loud peak.
    /// </summary>
    public static float[] ComputePeaks(IReadOnlyList<float[]> tracks, int count = PeakCount)
    {
        var peaks = new float[count];
        var length = tracks.Count == 0 ? 0 : tracks.Max(t => t.Length);
        if (length == 0) return peaks;
        for (var bar = 0; bar < count; bar++)
        {
            var from = (int)((long)bar * length / count);
            var to = (int)((long)(bar + 1) * length / count);
            var max = 0f;
            foreach (var track in tracks)
                for (var i = from; i < Math.Min(to, track.Length); i++)
                    max = Math.Max(max, Math.Abs(track[i]));
            peaks[bar] = max;
        }
        var loudest = peaks.Max();
        if (loudest <= 0) return peaks;
        for (var i = 0; i < count; i++) peaks[i] = MathF.Sqrt(peaks[i] / loudest);
        return peaks;
    }

    /// <summary>Joins a speaker's segments that are less than <paramref name="gap"/> seconds apart, so the track draws a few long bars.</summary>
    public static List<SpeakerSegment> Merge(IEnumerable<SpeakerSegment> segments, double gap = 0.6)
    {
        var merged = new List<SpeakerSegment>();
        foreach (var segment in segments.OrderBy(s => s.Start))
        {
            var last = merged.FindLastIndex(m => m.Speaker == segment.Speaker);
            if (last >= 0 && segment.Start - merged[last].End <= gap)
                merged[last] = merged[last] with { End = Math.Max(merged[last].End, segment.End) };
            else
                merged.Add(segment);
        }
        return merged;
    }
}
