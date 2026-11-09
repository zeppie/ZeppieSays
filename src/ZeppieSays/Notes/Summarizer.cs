using System.Text;
using System.Text.RegularExpressions;
using ZeppieSays.Core;
using ZeppieSays.Llm;

namespace ZeppieSays.Notes;

/// <summary>
/// Writes a summary, decisions and action items for a transcript with the default style model.
/// Long transcripts are summarized in parts first, so they fit a small local model's context.
/// </summary>
public sealed partial class Summarizer(TextRewriter rewriter)
{
    const int WordsPerPart = 2500;

    /// <summary>
    /// The instructions are English, and a model drifts to the language it is instructed in. So the language of the
    /// transcript is named outright, and for Dutch the headings are Dutch too.
    /// </summary>
    static string Instructions(string? language)
    {
        var (summary, decisions, actions, none, example) = language == "Dutch"
            ? ("Samenvatting", "Besluiten", "Actiepunten", "Geen", "Spreker 2: stuurt vrijdag de planning")
            : ("Summary", "Decisions", "Action items", "None", "Speaker 2: send the planning by Friday");
        return $"""
            You write meeting notes from a transcript made with speech recognition. Speakers may be labelled "Speaker 1", "Speaker 2".
            Reply in Markdown with exactly these three sections and nothing else:
            ## {summary}
            Three to six sentences on what was discussed.
            ## {decisions}
            A bullet list of decisions that were made, or "- {none}".
            ## {actions}
            A bullet list like "- [ ] {example}", or "- {none}".
            Rules:
            {LanguageRule(language)}
            - Use only what was said. Do not invent names, dates or tasks.
            - Speech recognition makes mistakes; read past odd words instead of quoting them.
            """;
    }

    static string PartInstructions(string? language) => $"""
        You take notes on one part of a longer transcript made with speech recognition.
        Reply with short bullet points: the topics discussed, any decisions, and any tasks with who does them.
        {LanguageRule(language)}
        Use only what was said.
        """;

    static string LanguageRule(string? language) => language is null
        ? "- Write in the language the people in the transcript speak, not the language of these instructions. Never translate."
        : $"- The transcript is in {language}. Write everything in {language}, not in English. Never translate.";

    // The most common words that tell these languages apart; words that several of them share are left out.
    static readonly (string Language, HashSet<string> Words)[] Languages =
    [
        ("Dutch", new(StringComparer.Ordinal) { "de", "het", "een", "en", "van", "dat", "niet", "ik", "je", "ze", "voor", "met", "zijn", "maar", "ook", "er", "dan", "nog", "wel", "dit", "die", "aan", "om", "als", "bij", "naar", "kan", "wat", "hebben", "dus", "gewoon", "hij", "wij", "mijn", "heel", "geen", "worden", "wordt", "deze", "uit", "over", "ja", "nee", "even", "toch", "zo", "hoe", "waar", "moeten", "kunnen" }),
        ("English", new(StringComparer.Ordinal) { "the", "and", "that", "you", "for", "with", "this", "are", "have", "not", "but", "they", "was", "what", "just", "like", "yeah", "would", "there", "about", "from", "will", "can", "so", "it", "to", "of", "be", "we", "do", "if", "on", "at", "my", "your", "know", "think" }),
        ("German", new(StringComparer.Ordinal) { "der", "die", "das", "und", "ist", "nicht", "ich", "wir", "sie", "ein", "eine", "mit", "auf", "für", "aber", "auch", "wie", "dass", "den", "dem", "von", "zu", "ja", "nein", "noch", "oder", "wenn", "dann", "haben", "kann" }),
        ("French", new(StringComparer.Ordinal) { "le", "la", "les", "des", "une", "et", "est", "que", "pas", "pour", "dans", "qui", "je", "nous", "vous", "ce", "sur", "avec", "mais", "ça", "oui", "non", "du", "au", "ont", "être", "avoir", "faire", "très", "bien" }),
    ];

    /// <summary>
    /// The language a transcript is written in, by counting common words, or null when it isn't clear (too short, or
    /// two languages close together). "Dutch", "English", "German" or "French".
    /// </summary>
    public static string? DetectLanguage(string text)
    {
        var counts = new int[Languages.Length];
        var seen = 0;
        foreach (Match m in Word().Matches(text))
        {
            if (++seen > 4000) break; // plenty to tell
            var word = m.Value.ToLowerInvariant();
            for (var i = 0; i < Languages.Length; i++)
                if (Languages[i].Words.Contains(word)) counts[i]++;
        }
        var order = Enumerable.Range(0, Languages.Length).OrderByDescending(i => counts[i]).ToArray();
        var best = counts[order[0]];
        var next = counts[order[1]];
        return best >= 15 && best >= next * 1.4 ? Languages[order[0]].Language : null;
    }

    [GeneratedRegex(@"\p{L}+")]
    private static partial Regex Word();

    public bool IsAvailable => Model() is not null;

    IChatModel? Model()
    {
        var model = rewriter.CreateModel(rewriter.Effective(new Style { Provider = LlmProvider.Default }));
        if (model is OllamaChat ollama) ollama.ContextLength = 16_384; // room for a part plus the reply
        return model;
    }

    /// <param name="context">What the user says the recording is about (people, project); helps with names and focus. May be empty.</param>
    public async Task<string?> SummarizeAsync(Transcript transcript, CancellationToken ct, string? context = null)
    {
        if (Model() is not { } model) return null;
        var text = transcript.PlainText();
        var language = DetectLanguage(text);
        var lines = text.Split('\n');
        if (CountWords(text) > WordsPerPart)
        {
            // Map: notes per part. Reduce: the final notes are written from those.
            var notes = new StringBuilder();
            var part = new List<string>();
            var words = 0;
            foreach (var line in lines.Append(""))
            {
                var w = CountWords(line);
                if ((words + w > WordsPerPart || line.Length == 0) && part.Count > 0)
                {
                    notes.AppendLine(await model.CompleteAsync(PartInstructions(language), string.Join("\n", part), ct));
                    part.Clear();
                    words = 0;
                }
                part.Add(line);
                words += w;
            }
            text = "Notes on the parts of a long meeting, in order:\n" + notes;
        }
        if (!string.IsNullOrWhiteSpace(context))
            text = $"Background from the person who made this recording (use it for names and focus):\n{context.Trim()}\n\nTranscript:\n{text}";
        var reply = ThinkBlock().Replace(await model.CompleteAsync(Instructions(language), text, ct), "").Trim();
        if (reply.StartsWith("```")) reply = reply.Trim('`').Replace("markdown\n", "", StringComparison.OrdinalIgnoreCase).Trim();
        return reply.Length > 0 ? reply : null;
    }

    static int CountWords(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    [GeneratedRegex(@"<think>.*?</think>", RegexOptions.Singleline)]
    private static partial Regex ThinkBlock();
}
