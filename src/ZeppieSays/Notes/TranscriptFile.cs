using System.Globalization;
using System.Text.RegularExpressions;

namespace ZeppieSays.Notes;

/// <summary>A transcript read back from its Markdown file: the summary section and the spoken paragraphs.</summary>
public sealed record ParsedTranscript(string? Notes, IReadOnlyList<TranscriptParagraph> Paragraphs, string Title = "");

/// <summary>Reads the Markdown that <see cref="Transcript.ToMarkdown"/> writes, so the page can show it as a list that can be clicked.</summary>
public static partial class TranscriptFile
{
    const string TranscriptHeading = "## Transcript";

    /// <summary>"**[01:05] Speaker 1:** text" or, when speakers are unknown, "**[01:05]** text".</summary>
    [GeneratedRegex(@"^\*\*\[(?<t>(?:\d+:)?\d{1,2}:\d{2})\](?: (?<s>.+?):)?\*\* (?<text>.*)$")]
    private static partial Regex ParagraphLine();

    public static ParsedTranscript Parse(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var heading = Array.FindIndex(lines, l => l.Trim() == TranscriptHeading);
        var spoken = heading < 0 ? lines : lines[(heading + 1)..];

        var paragraphs = new List<TranscriptParagraph>();
        foreach (var line in spoken)
        {
            var match = ParagraphLine().Match(line.Trim());
            if (!match.Success) continue;
            paragraphs.Add(new TranscriptParagraph(ParseStamp(match.Groups["t"].Value),
                match.Groups["s"].Success ? match.Groups["s"].Value : null, match.Groups["text"].Value));
        }

        // The summary sits between the "*Transcribed by …*" line and the transcript.
        string? notes = null;
        if (heading > 0)
        {
            var byLine = Array.FindIndex(lines, l => l.StartsWith("*Transcribed by", StringComparison.Ordinal));
            var start = byLine >= 0 ? byLine + 1 : 0;
            if (start < heading && string.Join("\n", lines[start..heading]).Trim() is { Length: > 0 } text)
                notes = text;
        }
        var title = lines.FirstOrDefault(l => l.StartsWith("# ", StringComparison.Ordinal))?[2..].Trim() ?? "";
        return new ParsedTranscript(notes, paragraphs, title);
    }

    /// <summary>
    /// The same file with its spoken paragraphs replaced (after the user edited them). The heading, the summary and
    /// the speaker count in the details line are kept up to date; everything before the transcript stays as it was.
    /// </summary>
    public static string WithParagraphs(string markdown, IReadOnlyList<TranscriptParagraph> paragraphs)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n').ToList();
        var heading = lines.FindIndex(l => l.Trim() == TranscriptHeading);
        var head = heading < 0 ? lines.Append("").Append(TranscriptHeading).ToList() : lines[..(heading + 1)];

        var speakers = paragraphs.Select(p => p.Speaker).OfType<string>().Distinct().Count();
        var details = head.FindIndex(l => l.StartsWith("*Transcribed by", StringComparison.Ordinal));
        if (details >= 0)
            head[details] = SpeakerCount().Replace(head[details], speakers > 0 ? $" · {speakers} speaker{(speakers == 1 ? "" : "s")}" : "");

        var text = new System.Text.StringBuilder(string.Join("\n", head)).Append("\n\n");
        foreach (var paragraph in paragraphs) text.Append(Transcript.FormatParagraph(paragraph)).Append("\n\n");
        return text.ToString();
    }

    /// <summary>The same file with a new summary (or none), written between the details line and the transcript.</summary>
    public static string WithNotes(string markdown, string? notes)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n').ToList();
        var heading = lines.FindIndex(l => l.Trim() == TranscriptHeading);
        if (heading < 0) return markdown;
        var byLine = lines.FindIndex(l => l.StartsWith("*Transcribed by", StringComparison.Ordinal));
        var start = byLine >= 0 ? byLine + 1 : Math.Min(1, heading);
        var result = lines[..start].Append("");
        if (!string.IsNullOrWhiteSpace(notes)) result = result.Append(notes.Trim()).Append("");
        return string.Join("\n", result.Concat(lines[heading..]));
    }

    [GeneratedRegex(@" · \d+ speakers?")]
    private static partial Regex SpeakerCount();

    /// <summary>"05:03" or "1:05:03" in seconds.</summary>
    public static double ParseStamp(string stamp)
    {
        var seconds = 0d;
        foreach (var part in stamp.Split(':'))
            seconds = seconds * 60 + double.Parse(part, CultureInfo.InvariantCulture);
        return seconds;
    }
}
