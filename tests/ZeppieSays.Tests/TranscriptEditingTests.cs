using System.IO;
using ZeppieSays.Notes;

namespace ZeppieSays.Tests;

public class TranscriptEditingTests
{
    static Transcript Sample()
    {
        var transcript = new Transcript { Title = "Planning", Duration = TimeSpan.FromMinutes(2), ModelName = "Whisper", Notes = "## Summary\nThey planned." };
        transcript.Paragraphs.Add(new(3, "Speaker 1", "Hello, everyone."));
        transcript.Paragraphs.Add(new(65, "Speaker 2", "Thanks for coming."));
        return transcript;
    }

    [Fact]
    public void Rewrites_the_paragraphs_and_keeps_the_rest_of_the_file()
    {
        var markdown = Sample().ToMarkdown();
        var edited = TranscriptFile.WithParagraphs(markdown,
        [
            new(3, "Anna", "Hello, everybody."),
            new(30, "Anna", "Welcome."),
            new(65, "Speaker 2", "Thanks for coming."),
        ]);

        var parsed = TranscriptFile.Parse(edited);
        Assert.Equal("Planning", parsed.Title);
        Assert.StartsWith("## Summary", parsed.Notes);
        Assert.Equal(["Hello, everybody.", "Welcome.", "Thanks for coming."], parsed.Paragraphs.Select(p => p.Text));
        Assert.Equal(["Anna", "Anna", "Speaker 2"], parsed.Paragraphs.Select(p => p.Speaker));
        Assert.Contains("· 2 speakers ·", edited);
    }

    [Fact]
    public void Updates_the_speaker_count_when_speakers_disappear()
    {
        var edited = TranscriptFile.WithParagraphs(Sample().ToMarkdown(), [new(3, "Speaker 1", "Hello.")]);
        Assert.Contains("· 1 speaker ·", edited);
        var none = TranscriptFile.WithParagraphs(Sample().ToMarkdown(), [new(3, null, "Hello.")]);
        Assert.DoesNotContain("speaker", none.Split('\n')[2]);
    }

    [Fact]
    public void Replaces_and_removes_the_summary()
    {
        var markdown = Sample().ToMarkdown();
        var changed = TranscriptFile.WithNotes(markdown, "## Summary\nA new one.");
        Assert.Contains("A new one.", changed);
        Assert.DoesNotContain("They planned.", changed);
        Assert.Equal(2, TranscriptFile.Parse(changed).Paragraphs.Count);

        var removed = TranscriptFile.WithNotes(changed, null);
        Assert.Null(TranscriptFile.Parse(removed).Notes);
        Assert.Equal(2, TranscriptFile.Parse(removed).Paragraphs.Count);
    }

    [Fact]
    public void Gives_a_stretch_of_one_speakers_segment_to_another()
    {
        var timeline = new Timeline { Segments = [new("A", 0, 10), new("B", 10, 20)] };
        timeline.Reassign(4, 8, "A", "C");
        Assert.Equal([new SpeakerSegment("A", 0, 4), new SpeakerSegment("C", 4, 8), new SpeakerSegment("A", 8, 10), new SpeakerSegment("B", 10, 20)],
            timeline.Segments);
    }

    [Fact]
    public void Reassigning_leaves_other_speakers_and_missing_speakers_alone()
    {
        var timeline = new Timeline { Segments = [new("A", 0, 5), new("B", 3, 8)] };
        timeline.Reassign(0, 8, "B", "A"); // B's part is joined to A's
        Assert.Equal([new SpeakerSegment("A", 0, 8)], timeline.Segments);
        timeline.Reassign(0, 8, null, "Z");
        timeline.Reassign(0, 8, "Nobody", "Z");
        Assert.Equal([new SpeakerSegment("A", 0, 8)], timeline.Segments);
    }

    [Fact]
    public void Finds_a_misheard_phrase_that_was_fixed()
    {
        var changes = WordDiff.Changes("We use beta bid for this.", "We use Betabit for this.");
        Assert.Equal([new WordChange("beta bid", "Betabit")], changes);
    }

    [Fact]
    public void Finds_added_words_and_capital_fixes()
    {
        Assert.Equal([new WordChange("", "Aurora")], WordDiff.Changes("The project is late.", "The project Aurora is late."));
        Assert.Equal([new WordChange("aurora", "Aurora")], WordDiff.Changes("Talk about aurora now.", "Talk about Aurora now."));
        Assert.Empty(WordDiff.Changes("Same words.", "Same words."));
    }

    [Theory]
    [InlineData("65", 65)]
    [InlineData("1:05", 65)]
    [InlineData(" 01:05 ", 65)]
    [InlineData("1:02:05", 3725)]
    [InlineData("0:00", 0)]
    public void Reads_a_typed_time(string typed, double seconds)
    {
        Assert.True(TranscriptFile.TryParseStamp(typed, out var parsed));
        Assert.Equal(seconds, parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1:75")]
    [InlineData("1:2:3:4")]
    [InlineData("-5")]
    [InlineData("1.5")]
    public void Refuses_a_time_that_is_not_one(string typed) => Assert.False(TranscriptFile.TryParseStamp(typed, out _));

    [Fact]
    public void Moving_a_start_earlier_takes_time_from_the_speaker_before()
    {
        var timeline = new Timeline { Segments = [new("A", 0, 10), new("B", 10, 20)] };
        timeline.Reassign(8, 10, "A", "B"); // B's paragraph now starts at 8 instead of 10
        Assert.Equal([new SpeakerSegment("A", 0, 8), new SpeakerSegment("B", 8, 20)], timeline.Segments);
    }

    [Fact]
    public void Moving_a_start_later_gives_time_back_to_the_speaker_before()
    {
        var timeline = new Timeline { Segments = [new("A", 0, 10), new("B", 10, 20)] };
        timeline.Reassign(10, 13, "B", "A"); // B's paragraph now starts at 13
        Assert.Equal([new SpeakerSegment("A", 0, 13), new SpeakerSegment("B", 13, 20)], timeline.Segments);
    }

    [Fact]
    public void Ignores_a_rewrite_and_filler_in_the_dictionary_candidates()
    {
        Assert.Empty(WordDiff.Changes("one two three", "alpha beta gamma delta epsilon"));
        Assert.Empty(WordDiff.DictionaryCandidates("It is fine.", "It is really fine."));
        Assert.Equal([new WordChange("", "Aurora")], WordDiff.DictionaryCandidates("It is late.", "It is late Aurora."));
        Assert.Equal([new WordChange("beta bid", "Betabit")], WordDiff.DictionaryCandidates("at beta bid today", "at Betabit today"));
    }

    [Fact]
    public void The_log_keeps_the_original_once_and_every_edit()
    {
        var folder = Path.Combine(Path.GetTempPath(), "zeppie-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var markdown = Path.Combine(folder, "meeting.md");
            var log = CorrectionLog.Load(markdown);
            log.EnsureOriginal([new(3, "Speaker 1", "Helo")]);
            log.EnsureOriginal([new(3, "Speaker 1", "Changed already")]); // ignored: the first one is the original
            log.Add("text", 3, "Speaker 1", "Speaker 1", "Helo", "Hello");
            log.Save(markdown);

            var loaded = CorrectionLog.Load(markdown);
            Assert.Equal("Helo", loaded.Original.Single().Text);
            Assert.Equal("Hello", loaded.Edits.Single().TextAfter);
            Assert.Equal("text", loaded.Edits.Single().Kind);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
