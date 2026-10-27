using System.IO;
using System.Text.Json;

namespace ZeppieSays.Notes;

/// <summary>A word of a transcript as the speech model heard it: when (seconds into the recording) and on which track.</summary>
/// <param name="Track">0 for an ordinary file and a meeting's microphone, 1 for a meeting's call.</param>
public sealed record SpokenWord(string Text, double Start, double End, int Track = 0);

/// <summary>
/// Every word of a transcript with its time, before cleanup (name.words.json beside it). With these the speakers
/// can be worked out again and the words handed out to them, without transcribing the recording again.
/// </summary>
public sealed class TranscriptWords
{
    /// <summary>The speech model that heard the words, for the transcript's header.</summary>
    public string SpeechModel { get; set; } = "";
    public List<SpokenWord> Words { get; set; } = [];

    public static string PathFor(string markdownPath) => Path.ChangeExtension(markdownPath, ".words.json");

    public static TranscriptWords? Load(string markdownPath)
    {
        try
        {
            var path = PathFor(markdownPath);
            return File.Exists(path) ? JsonSerializer.Deserialize<TranscriptWords>(File.ReadAllText(path)) : null;
        }
        catch
        {
            return null; // damaged: as if there were none, so the recording is transcribed again
        }
    }

    public void Save(string markdownPath) => File.WriteAllText(PathFor(markdownPath), JsonSerializer.Serialize(this));

    /// <summary>
    /// One paragraph per turn, from the words said in it: a word goes to the turn on its own track that its middle
    /// falls in, or else the nearest one. <paramref name="clean"/> tidies a turn's words into text, as when it
    /// was first transcribed. Turns nobody said a word in are left out.
    /// </summary>
    public static List<TranscriptParagraph> ToParagraphs(IReadOnlyList<(int Track, double Start, double End, string? Speaker)> turns,
        IEnumerable<SpokenWord> words, Func<string, string> clean)
    {
        var said = turns.Select(_ => new List<SpokenWord>()).ToList();
        foreach (var word in words)
        {
            var middle = (word.Start + word.End) / 2;
            var best = -1;
            var distance = double.MaxValue;
            for (var i = 0; i < turns.Count; i++)
            {
                if (turns[i].Track != word.Track) continue;
                var d = middle < turns[i].Start ? turns[i].Start - middle : middle >= turns[i].End ? middle - turns[i].End : 0;
                if (d < distance) (best, distance) = (i, d);
            }
            if (best >= 0) said[best].Add(word);
        }

        var paragraphs = new List<TranscriptParagraph>();
        foreach (var i in Enumerable.Range(0, turns.Count).OrderBy(i => turns[i].Start))
        {
            if (said[i].Count == 0) continue;
            var text = clean(string.Join(" ", said[i].OrderBy(w => w.Start).Select(w => w.Text)));
            if (text.Length > 0) paragraphs.Add(new TranscriptParagraph(turns[i].Start, turns[i].Speaker, text));
        }
        return paragraphs;
    }
}
