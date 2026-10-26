using System.Text.RegularExpressions;
using ZeppieSays.Core;

namespace ZeppieSays.Services;

/// <summary>What <see cref="TextShaping.Clean"/> fixed, for the Insights page.</summary>
public sealed class CleanupCounts
{
    public int FillerWords { get; set; }
    public int DictionaryFixes { get; set; }
    public int ScreenWords { get; set; }
}

/// <summary>Builds model prompts and cleans up raw transcripts with plain rules (no LLM).</summary>
public static partial class TextShaping
{
    const int MaxPromptChars = 400;

    /// <summary>
    /// Whisper reads its prompt as "text that came before", so a plain list of names nudges it towards the
    /// right spellings. Only names, never the text around the cursor: an English chat before the cursor
    /// made Whisper translate Dutch speech into English, or return nothing at all.
    /// </summary>
    public static string BuildPrompt(IEnumerable<DictionaryEntry> dictionary, ScreenContext context)
    {
        var vocabulary = dictionary.Select(d => d.Term)
            .Concat(context.Identifiers.Take(context.IsCodeEditor ? 40 : 15))
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (vocabulary.Count == 0) return "";

        var prompt = string.Join(", ", vocabulary);
        if (prompt.Length > MaxPromptChars)
            prompt = prompt[..prompt.LastIndexOf(',', MaxPromptChars)];
        return prompt + ".";
    }

    public static string Clean(string raw, AppSettings settings, ScreenContext context, CleanupCounts? counts = null)
    {
        counts ??= new CleanupCounts();
        var text = Whitespace().Replace(raw, " ").Trim();
        if (text.Length == 0 || IsNonSpeech(text)) return "";

        if (settings.RemoveFillerWords)
        {
            counts.FillerWords += Fillers().Count(text);
            text = Whitespace().Replace(Fillers().Replace(text, ""), " ").Trim();
            text = SpaceBeforePunctuation().Replace(text, "$1").TrimStart(',', ' ');
        }

        foreach (var entry in settings.AllWords)
            text = ApplyEntry(text, entry, counts);

        if (context.IsCodeEditor)
            foreach (var identifier in context.Identifiers)
                text = ApplySpokenForm(text, identifier, counts);

        return FitToCursor(text, settings, context);
    }

    /// <summary>Capitalises and spaces the text to fit where it is inserted (also used after an LLM rewrite).</summary>
    public static string FitToCursor(string text, AppSettings settings, ScreenContext context)
    {
        text = text.Trim();
        if (text.Length == 0) return "";
        text = MatchCapitalization(text, context.BeforeText, settings);
        return AddSpacing(text, context);
    }

    /// <summary>Whisper turns silence into "[BLANK_AUDIO]", "(music)" or a lone "Thank you." from its training data.</summary>
    static bool IsNonSpeech(string text) =>
        NonSpeech().IsMatch(text) ||
        text.Trim('.', ' ', '!').ToLowerInvariant() is "thank you" or "thanks for watching" or "bedankt voor het kijken" or "you";

    static string ApplyEntry(string text, DictionaryEntry entry, CleanupCounts counts)
    {
        if (string.IsNullOrWhiteSpace(entry.Term)) return text;
        var variants = entry.SoundsLike.Append(entry.Term).Append(Spoken(entry.Term))
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(v => v.Length);
        foreach (var variant in variants)
            text = Replace(text, FlexiblePattern(variant), entry.Term, () => counts.DictionaryFixes++);

        // Models often split an unfamiliar word in two ("beta bit" for Betabit); allow a break anywhere inside.
        var letters = entry.Term.Where(char.IsLetterOrDigit).ToArray();
        if (letters.Length >= 4 && letters.Length == entry.Term.Length)
            text = Replace(text, $@"(?<![\w]){string.Join(@"[\s\-]?", letters.Select(c => Regex.Escape(c.ToString())))}(?![\w])",
                entry.Term, () => counts.DictionaryFixes++);
        return text;
    }

    /// <summary>"user id" or "user-id" becomes userId when userId is visible in the editor.</summary>
    static string ApplySpokenForm(string text, string identifier, CleanupCounts counts)
    {
        var spoken = Spoken(identifier);
        if (!spoken.Contains(' ')) return text;
        return Replace(text, FlexiblePattern(spoken), identifier, () => counts.ScreenWords++);
    }

    /// <summary>Replaces every match with <paramref name="term"/>, reporting each one that actually changed.</summary>
    static string Replace(string text, string pattern, string term, Action changed) =>
        Regex.Replace(text, pattern, m =>
        {
            if (m.Value != term) changed();
            return term;
        }, RegexOptions.IgnoreCase);

    /// <summary>Splits camelCase, PascalCase and snake_case into lowercase words.</summary>
    public static string Spoken(string term) =>
        Whitespace().Replace(HumpOrSeparator().Replace(term, " $1").Replace('_', ' ').Replace('-', ' '), " ")
            .Trim().ToLowerInvariant();

    /// <summary>Matches the words of a phrase with any mix of spaces, hyphens or nothing between them.</summary>
    static string FlexiblePattern(string phrase)
    {
        var words = phrase.Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape);
        return $@"(?<![\w]){string.Join(@"[\s\-]*", words)}(?![\w])";
    }

    static string MatchCapitalization(string text, string? before, AppSettings settings)
    {
        // Unknown surroundings: treat it as a new sentence (a removed leading "uh" can leave a lowercase start).
        if (before is null) return char.ToUpper(text[0]) + text[1..];
        var tail = before.TrimEnd();
        var startsSentence = tail.Length == 0 || ".!?:\n".Contains(tail[^1]) || before.EndsWith('\n');
        if (startsSentence)
            return char.ToUpper(text[0]) + text[1..];

        // Continuing a sentence: drop the capital the model put on the first word, unless it is a name we know.
        var firstWord = FirstWord().Match(text).Value;
        var keep = firstWord == "I" || firstWord.Skip(1).Any(char.IsUpper) ||
                   settings.AllWords.Any(d => d.Term.Equals(firstWord, StringComparison.Ordinal));
        return keep ? text : char.ToLower(text[0]) + text[1..];
    }

    static string AddSpacing(string text, ScreenContext context)
    {
        if (context.BeforeText is { Length: > 0 } before && !char.IsWhiteSpace(before[^1]) && !"([{\"'".Contains(before[^1])
            && !".,!?;:)".Contains(text[0]))
            text = " " + text;
        if (context.AfterText is { Length: > 0 } after && char.IsLetterOrDigit(after[0]))
            text += " ";
        return text;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    // Takes a comma in front along with it, so "we should, um, ship" becomes "we should ship".
    [GeneratedRegex(@"(?:,\s*)?(?<![\w])(?:u+m+|u+h+m*|e+r+m+|e+h+m*|e+u+h+|hmm+|mm+)(?![\w])[,.]?", RegexOptions.IgnoreCase)]
    private static partial Regex Fillers();

    [GeneratedRegex(@"\s+([,.!?;:])")]
    private static partial Regex SpaceBeforePunctuation();

    [GeneratedRegex(@"^\s*[\[\(][^\]\)]*[\]\)]\s*$")]
    private static partial Regex NonSpeech();

    [GeneratedRegex(@"(?<=[a-z0-9])([A-Z])")]
    private static partial Regex HumpOrSeparator();

    [GeneratedRegex(@"^[\p{L}\p{N}']+")]
    private static partial Regex FirstWord();
}
