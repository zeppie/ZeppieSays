namespace ZeppieSays.Engines;

public sealed record TranscriptionRequest(
    float[] Samples,
    /// <summary>ISO code or "auto".</summary>
    string Language,
    /// <summary>Vocabulary and preceding text to steer models that accept a prompt. May be empty.</summary>
    string Prompt,
    /// <summary>Words to listen for, for models that take a list instead of a prompt (ElevenLabs). Null when there are none.</summary>
    IReadOnlyList<string>? KeyTerms = null);

/// <summary>A word as the speech model heard it, with when it was said (seconds into the samples it was given).</summary>
public sealed record TimedWord(string Text, double Start, double End);

public interface ITranscriptionEngine : IDisposable
{
    string Name { get; }
    /// <summary>Which backend actually runs the model, e.g. "CUDA" or "CPU".</summary>
    string Backend { get; }
    Task<string> TranscribeAsync(TranscriptionRequest request, CancellationToken ct);

    /// <summary>
    /// The text and each word with its time, for the Notetaker: the words let the speakers be worked out again
    /// later without transcribing again. Dictation only needs the text.
    /// </summary>
    Task<(string Text, IReadOnlyList<TimedWord> Words)> TranscribeWordsAsync(TranscriptionRequest request, CancellationToken ct);
}

public static class TimedWords
{
    /// <summary>
    /// Joins sub-word tokens into words: a token that starts with a space (or SentencePiece's ▁) begins a new
    /// word; anything else is glued onto the word before it.
    /// </summary>
    public static List<TimedWord> FromTokens(IEnumerable<(string Text, double Start, double End)> tokens)
    {
        var words = new List<TimedWord>();
        foreach (var (raw, start, end) in tokens)
        {
            var text = raw.Replace('▁', ' ');
            if (text.Length == 0) continue;
            if (words.Count == 0 || char.IsWhiteSpace(text[0]))
            {
                if (text.Trim().Length > 0) words.Add(new TimedWord(text.Trim(), start, Math.Max(start, end)));
            }
            else
                words[^1] = words[^1] with { Text = words[^1].Text + text.TrimEnd(), End = Math.Max(words[^1].End, end) };
        }
        return words;
    }
}

public static class Audio
{
    public const int SampleRate = 16_000;
}
