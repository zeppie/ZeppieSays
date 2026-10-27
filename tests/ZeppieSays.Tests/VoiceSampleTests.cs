using System.IO;
using ZeppieSays.Notes;

namespace ZeppieSays.Tests;

public class VoiceSampleTests
{
    [Fact]
    public void Picks_the_longest_stretch_nobody_talks_over()
    {
        List<SpeakerSegment> segments =
        [
            new("Anna", 0, 10),   // Bram talks over 4–6, leaving 0–4 and 6–10
            new("Bram", 4, 6),
            new("Anna", 20, 25),  // five clean seconds: the longest
        ];
        Assert.Equal((20.0, 25.0), VoiceSample.Longest(segments, "anna"));
    }

    [Fact]
    public void Leaves_out_someone_who_only_says_a_word()
    {
        List<SpeakerSegment> segments = [new("Anna", 0, 1), new("Bram", 1, 30)];
        Assert.Null(VoiceSample.Longest(segments, "Anna"));
    }

    [Fact]
    public void Takes_a_few_seconds_from_the_recording_with_the_most_of_someone()
    {
        var folder = Directory.CreateTempSubdirectory().FullName;
        try
        {
            (string, string) Recording(string name, params SpeakerSegment[] segments)
            {
                var audio = Path.Combine(folder, name + ".m4a");
                var markdown = Path.Combine(folder, name + ".md");
                File.WriteAllText(audio, "");
                new Timeline { Duration = 600, Segments = [.. segments] }.Save(markdown);
                return (audio, markdown);
            }
            var shortTalk = Recording("standup", new SpeakerSegment("Anna", 0, 3));
            var longTalk = Recording("planning", new SpeakerSegment("Anna", 100, 160));
            var gone = (Path.Combine(folder, "deleted.m4a"), Path.Combine(folder, "deleted.md"));

            var found = VoiceSample.Find([shortTalk, longTalk, gone], ["Anna", "Bram"]);

            Assert.Equal(new VoiceSample(longTalk.Item1, 100, 100 + VoiceSample.MaxSeconds), found["Anna"]);
            Assert.False(found.ContainsKey("Bram"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
