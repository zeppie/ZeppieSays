using System.IO;
using ZeppieSays.Notes;

namespace ZeppieSays.Tests;

public class NotetakerPageTests
{
    static Transcript Sample(bool speakers = true)
    {
        var transcript = new Transcript { Title = "Planning", Duration = TimeSpan.FromMinutes(75), ModelName = "Whisper" };
        transcript.Paragraphs.Add(new(3, speakers ? "Speaker 1" : null, "Hello, everyone."));
        transcript.Paragraphs.Add(new(65, speakers ? "Anna" : null, "Thanks: let's start."));
        transcript.Paragraphs.Add(new(3725, speakers ? "Speaker 1" : null, "Last point."));
        return transcript;
    }

    [Fact]
    public void Reads_back_the_paragraphs_a_transcript_wrote()
    {
        var parsed = TranscriptFile.Parse(Sample().ToMarkdown());
        Assert.Equal(3, parsed.Paragraphs.Count);
        Assert.Equal(new TranscriptParagraph(3, "Speaker 1", "Hello, everyone."), parsed.Paragraphs[0]);
        Assert.Equal(65, parsed.Paragraphs[1].Seconds);
        Assert.Equal("Anna", parsed.Paragraphs[1].Speaker);
        Assert.Equal("Thanks: let's start.", parsed.Paragraphs[1].Text);
        Assert.Equal(3725, parsed.Paragraphs[2].Seconds); // 1:02:05
    }

    [Fact]
    public void Reads_paragraphs_without_speakers()
    {
        var parsed = TranscriptFile.Parse(Sample(speakers: false).ToMarkdown());
        Assert.All(parsed.Paragraphs, p => Assert.Null(p.Speaker));
        Assert.Equal("Thanks: let's start.", parsed.Paragraphs[1].Text);
    }

    [Fact]
    public void Separates_the_summary_from_the_transcript()
    {
        var transcript = Sample();
        transcript.Notes = "## Summary\nThey planned.\n## Decisions\n- None";
        var parsed = TranscriptFile.Parse(transcript.ToMarkdown());
        Assert.StartsWith("## Summary", parsed.Notes);
        Assert.DoesNotContain("Hello, everyone.", parsed.Notes);
        Assert.Equal(3, parsed.Paragraphs.Count);
        Assert.Null(TranscriptFile.Parse(Sample().ToMarkdown()).Notes);
    }

    [Theory]
    [InlineData("00:03", 3)]
    [InlineData("12:30", 750)]
    [InlineData("1:02:05", 3725)]
    public void Reads_timestamps(string stamp, double seconds) => Assert.Equal(seconds, TranscriptFile.ParseStamp(stamp));

    [Fact]
    public void Peaks_are_scaled_to_the_loudest_slice()
    {
        var samples = new float[1000];
        samples[10] = 0.2f;
        samples[990] = 0.4f; // twice as loud as the first
        var peaks = Timeline.ComputePeaks([samples], count: 10);
        Assert.Equal(10, peaks.Length);
        Assert.Equal(1f, peaks[9]);
        Assert.Equal(MathF.Sqrt(0.5f), peaks[0], 3);
        Assert.Equal(0f, peaks[5]);
    }

    [Fact]
    public void Peaks_of_silence_or_nothing_are_flat()
    {
        Assert.All(Timeline.ComputePeaks([new float[100]], count: 4), p => Assert.Equal(0f, p));
        Assert.All(Timeline.ComputePeaks([], count: 4), p => Assert.Equal(0f, p));
    }

    [Fact]
    public void Joins_a_speakers_segments_that_nearly_touch()
    {
        var merged = Timeline.Merge(
        [
            new SpeakerSegment("A", 0, 2), new SpeakerSegment("A", 2.3, 4),
            new SpeakerSegment("B", 4, 5), new SpeakerSegment("A", 9, 10),
        ]);
        Assert.Equal([new SpeakerSegment("A", 0, 4), new SpeakerSegment("B", 4, 5), new SpeakerSegment("A", 9, 10)], merged);
    }

    [Fact]
    public void Renaming_a_speaker_changes_their_segments_only()
    {
        var timeline = new Timeline { Segments = [new("Speaker 1", 0, 1), new("Speaker 2", 1, 2)] };
        timeline.Rename("Speaker 1", "Anna");
        Assert.Equal(["Anna", "Speaker 2"], timeline.Segments.Select(s => s.Speaker));
    }

    [Fact]
    public void A_timeline_survives_saving_and_loading()
    {
        var folder = Path.Combine(Path.GetTempPath(), "zeppie-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var markdown = Path.Combine(folder, "meeting.md");
            new Timeline { Duration = 90, Segments = [new("Anna", 1, 2.5)], Peaks = [0.1f, 0.9f] }.Save(markdown);
            var loaded = Timeline.Load(markdown);
            Assert.Equal(90, loaded?.Duration);
            Assert.Equal(new SpeakerSegment("Anna", 1, 2.5), loaded?.Segments.Single());
            Assert.Equal([0.1f, 0.9f], loaded!.Peaks);
            Assert.Null(Timeline.Load(Path.Combine(folder, "other.md")));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
