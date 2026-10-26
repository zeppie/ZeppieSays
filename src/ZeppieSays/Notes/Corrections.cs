using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ZeppieSays.Notes;

/// <summary>One change the user made to a transcript.</summary>
/// <param name="Kind">"text", "speaker", "split", "merge", "delete" or "replace".</param>
public sealed record Correction(DateTime When, string Kind, double Seconds, string? SpeakerBefore, string? SpeakerAfter,
    string TextBefore, string TextAfter);

/// <summary>
/// Everything the user corrected in a transcript, kept beside it (name.corrections.json): the transcript as the
/// speech model wrote it, and each change after that. Nothing reads this yet; it is the material to learn from.
/// </summary>
public sealed class CorrectionLog
{
    /// <summary>The paragraphs as first transcribed, saved just before the first change.</summary>
    public List<TranscriptParagraph> Original { get; set; } = [];
    public List<Correction> Edits { get; set; } = [];

    public static string PathFor(string markdownPath) => Path.ChangeExtension(markdownPath, ".corrections.json");

    public static CorrectionLog Load(string markdownPath)
    {
        try
        {
            var path = PathFor(markdownPath);
            return File.Exists(path) ? JsonSerializer.Deserialize<CorrectionLog>(File.ReadAllText(path)) ?? new() : new();
        }
        catch
        {
            return new(); // damaged: start a new log rather than lose the edit
        }
    }

    public void Save(string markdownPath) =>
        File.WriteAllText(PathFor(markdownPath), JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));

    /// <summary>Remembers the machine-made transcript, once, before anything was changed.</summary>
    public void EnsureOriginal(IEnumerable<TranscriptParagraph> paragraphs)
    {
        if (Original.Count == 0) Original = [.. paragraphs];
    }

    public void Add(string kind, double seconds, string? speakerBefore, string? speakerAfter, string textBefore, string textAfter) =>
        Edits.Add(new Correction(DateTime.Now, kind, seconds, speakerBefore, speakerAfter, textBefore, textAfter));
}

/// <summary>A word or phrase the user changed, as the dictionary could use it.</summary>
public sealed record WordChange(string Before, string After);

/// <summary>Finds which words differ between a paragraph as transcribed and as corrected.</summary>
public static partial class WordDiff
{
    const int MaxPhrase = 3; // a bigger rewrite is a rewording, not a misheard word

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}'’\-]*")]
    private static partial Regex Word();

    static List<string> Words(string text) => Word().Matches(text).Select(m => m.Value).ToList();

    /// <summary>
    /// Runs of words that were replaced ("beta bid" became "Betabit") or added ("Aurora"), at most three words each.
    /// Case-only changes count: "aurora" became "Aurora".
    /// </summary>
    public static IReadOnlyList<WordChange> Changes(string before, string after)
    {
        var a = Words(before);
        var b = Words(after);

        // Longest common subsequence of the words (ignoring capitals), then walk it to find what differs.
        var lcs = new int[a.Count + 1, b.Count + 1];
        for (var i = a.Count - 1; i >= 0; i--)
            for (var j = b.Count - 1; j >= 0; j--)
                lcs[i, j] = Same(a[i], b[j]) ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var changes = new List<WordChange>();
        List<string> removed = [], added = [];
        void Flush()
        {
            if (added.Count > 0 && added.Count <= MaxPhrase && removed.Count <= MaxPhrase)
                changes.Add(new WordChange(string.Join(" ", removed), string.Join(" ", added)));
            removed = [];
            added = [];
        }

        int x = 0, y = 0;
        while (x < a.Count || y < b.Count)
        {
            if (x < a.Count && y < b.Count && Same(a[x], b[y]))
            {
                // Same word apart from capitals: a pure capitalisation fix is a change worth keeping.
                if (a[x] != b[y]) { Flush(); changes.Add(new WordChange(a[x], b[y])); }
                else Flush();
                x++;
                y++;
            }
            else if (y < b.Count && (x == a.Count || lcs[x, y + 1] >= lcs[x + 1, y])) added.Add(b[y++]);
            else removed.Add(a[x++]);
        }
        Flush();
        return changes;
    }

    /// <summary>
    /// The changes worth offering to the dictionary: a misheard word that was fixed, or a name that was added.
    /// Small filler words that were added or changed are left out.
    /// </summary>
    public static IReadOnlyList<WordChange> DictionaryCandidates(string before, string after) =>
        Changes(before, after)
            .Where(c => c.After.Length >= 3 && (c.Before.Length > 0 || c.After.Any(char.IsUpper) || c.After.Any(char.IsDigit)))
            .ToList();

    static bool Same(string x, string y) => x.Equals(y, StringComparison.OrdinalIgnoreCase);
}
