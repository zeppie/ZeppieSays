using System.IO;

namespace ZeppieSays.Notes;

/// <summary>A few seconds of a recording in which one person talks, to hear whose voice a name stands for.</summary>
public sealed record VoiceSample(string AudioPath, double Start, double End)
{
    /// <summary>Long enough to recognise a voice, short enough to be a quick check.</summary>
    public const double MaxSeconds = 8;
    /// <summary>Shorter stretches are mostly "yes" or "hm" and say little about who it is.</summary>
    const double MinSeconds = 1.5;

    public string Recording => Path.GetFileNameWithoutExtension(AudioPath);

    /// <summary>
    /// For each name, the longest stretch in the given transcripts where that person talks and nobody else does.
    /// The voice library keeps no audio, so the sample comes from transcripts that still have their recording.
    /// Names nobody talks in for long enough are left out.
    /// </summary>
    public static Dictionary<string, VoiceSample> Find(IEnumerable<(string Audio, string Markdown)> recordings, IEnumerable<string> names)
    {
        var wanted = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        var best = new Dictionary<string, VoiceSample>(StringComparer.OrdinalIgnoreCase);
        foreach (var (audio, markdown) in recordings)
        {
            if (!File.Exists(audio) || Timeline.Load(markdown) is not { } timeline) continue;
            foreach (var group in timeline.Segments.GroupBy(s => s.Speaker, StringComparer.OrdinalIgnoreCase))
            {
                if (!wanted.Contains(group.Key) || Longest(timeline.Segments, group.Key) is not { } stretch) continue;
                var end = Math.Min(stretch.End, stretch.Start + MaxSeconds);
                if (best.TryGetValue(group.Key, out var known) && known.End - known.Start >= end - stretch.Start) continue;
                best[group.Key] = new VoiceSample(audio, stretch.Start, end);
            }
        }
        return best;
    }

    /// <summary>The longest stretch in which <paramref name="speaker"/> talks alone, or null when none is long enough.</summary>
    public static (double Start, double End)? Longest(IReadOnlyList<SpeakerSegment> segments, string speaker) =>
        Alone(segments, speaker).Where(p => p.End - p.Start >= MinSeconds)
            .Select(p => ((double Start, double End)?)p).MaxBy(p => p!.Value.End - p.Value.Start);

    /// <summary>Every stretch in which <paramref name="speaker"/> talks and nobody else does, longest first.</summary>
    public static List<(double Start, double End)> Alone(IReadOnlyList<SpeakerSegment> segments, string speaker)
    {
        var others = segments.Where(s => !s.Speaker.Equals(speaker, StringComparison.OrdinalIgnoreCase)).ToList();
        var alone = new List<(double Start, double End)>();
        foreach (var segment in segments.Where(s => s.Speaker.Equals(speaker, StringComparison.OrdinalIgnoreCase)))
        {
            // Cut out every moment someone else talks over them; what is left are the clean pieces.
            var pieces = new List<(double Start, double End)> { (segment.Start, segment.End) };
            foreach (var other in others)
                pieces = pieces.SelectMany(p => other.End <= p.Start || other.Start >= p.End
                    ? [p]
                    : new[] { (p.Start, other.Start), (other.End, p.End) }.Where(q => q.Item2 > q.Item1)).ToList();
            alone.AddRange(pieces);
        }
        return [.. alone.OrderByDescending(p => p.End - p.Start)];
    }
}
