using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ZeppieSays.Notes;

/// <summary>One paragraph of a transcript: where it starts in the recording, who speaks (null when unknown) and what was said.</summary>
public sealed record TranscriptParagraph(double Seconds, string? Speaker, string Text);

/// <summary>A finished transcription, written as Markdown next to the audio file.</summary>
public sealed class Transcript
{
    public required string Title { get; set; }
    public required TimeSpan Duration { get; init; }
    public required string ModelName { get; init; }
    public DateTime Created { get; init; } = DateTime.Now;
    public List<TranscriptParagraph> Paragraphs { get; } = [];
    /// <summary>Markdown sections (summary, decisions, action items) from the LLM, or null.</summary>
    public string? Notes { get; set; }

    /// <summary>Each speaker's voiceprint by label, kept beside the transcript so speakers can be named later.</summary>
    public Dictionary<string, float[]> Voiceprints { get; } = [];
    /// <summary>Every word heard, with its time, so the speakers can be worked out again without transcribing again.</summary>
    public List<SpokenWord> Words { get; } = [];

    /// <summary>The voice model that made <see cref="Voiceprints"/>.</summary>
    public string VoiceModel { get; init; } = global::ZeppieSays.Notes.VoiceModel.DefaultId;

    /// <summary>When each speaker talks and the recording's loudness, for the page's player; saved beside the transcript.</summary>
    public Timeline? Timeline { get; set; }

    public int Speakers => Paragraphs.Select(p => p.Speaker).OfType<string>().Distinct().Count();

    /// <summary>The spoken text with timestamps and speakers, as the summarizer reads it.</summary>
    public string PlainText() => string.Join("\n", Paragraphs.Select(p =>
        $"[{Stamp(p.Seconds)}] {(p.Speaker is not null ? $"{p.Speaker}: " : "")}{p.Text}"));

    static string VoiceprintsPath(string markdownPath) => Path.ChangeExtension(markdownPath, ".speakers.json");

    /// <summary>The voiceprints file: which voice model made them, and the voiceprint per speaker label.</summary>
    sealed record VoiceprintFile(string Model, Dictionary<string, float[]> Voices);

    public void SaveVoiceprints(string markdownPath) => WriteVoiceprints(markdownPath, VoiceModel, Voiceprints);

    static void WriteVoiceprints(string markdownPath, string model, Dictionary<string, float[]> voices) =>
        File.WriteAllText(VoiceprintsPath(markdownPath), JsonSerializer.Serialize(new VoiceprintFile(model, voices)));

    public static Dictionary<string, float[]> LoadVoiceprints(string markdownPath) => ReadVoiceprints(markdownPath).Voices;

    /// <summary>
    /// The speakers' voiceprints and the voice model that made them. Files from before the choice of model hold
    /// just the voiceprints, all made by WeSpeaker.
    /// </summary>
    public static (string Model, Dictionary<string, float[]> Voices) ReadVoiceprints(string markdownPath)
    {
        try
        {
            var path = VoiceprintsPath(markdownPath);
            if (!File.Exists(path)) return (global::ZeppieSays.Notes.VoiceModel.LegacyId, []);
            var json = File.ReadAllText(path);
            if (System.Text.Json.Nodes.JsonNode.Parse(json)?["Voices"] is not null)
                return JsonSerializer.Deserialize<VoiceprintFile>(json) is { } file ? (file.Model, file.Voices) : (global::ZeppieSays.Notes.VoiceModel.LegacyId, []);
            return (global::ZeppieSays.Notes.VoiceModel.LegacyId, JsonSerializer.Deserialize<Dictionary<string, float[]>>(json) ?? []);
        }
        catch
        {
            return (global::ZeppieSays.Notes.VoiceModel.LegacyId, []);
        }
    }

    /// <summary>Renames a speaker everywhere in a written transcript (labels, summary and action items) and in its voiceprints.</summary>
    public static void RenameSpeaker(string markdownPath, string from, string to)
    {
        // "Speaker 1" must not also hit "Speaker 10".
        var pattern = $@"(?<![\w]){Regex.Escape(from)}(?![\w])";
        File.WriteAllText(markdownPath, Regex.Replace(File.ReadAllText(markdownPath), pattern, to.Replace("$", "$$")));
        var (model, voiceprints) = ReadVoiceprints(markdownPath);
        if (voiceprints.Remove(from, out var voice))
        {
            voiceprints[to] = voice;
            WriteVoiceprints(markdownPath, model, voiceprints);
        }
        if (Timeline.Load(markdownPath) is { } timeline)
        {
            timeline.Rename(from, to);
            timeline.Save(markdownPath);
        }
    }

    public string ToMarkdown()
    {
        var english = CultureInfo.GetCultureInfo("en-US");
        var md = new StringBuilder();
        md.AppendLine($"# {Title}");
        md.AppendLine();
        var details = new List<string>
        {
            Created.ToString("d MMMM yyyy, HH:mm", english),
            Length(Duration),
        };
        if (Speakers > 0) details.Add(Speakers == 1 ? "1 speaker" : $"{Speakers} speakers");
        details.Add(ModelName);
        md.AppendLine($"*Transcribed by Zeppie Says · {string.Join(" · ", details)}*");
        md.AppendLine();
        if (!string.IsNullOrWhiteSpace(Notes))
        {
            md.AppendLine(Notes.Trim());
            md.AppendLine();
        }
        md.AppendLine("## Transcript");
        md.AppendLine();
        foreach (var p in Paragraphs)
        {
            md.AppendLine(FormatParagraph(p));
            md.AppendLine();
        }
        return md.ToString();
    }

    /// <summary>"**[01:05] Speaker 1:** text", or "**[01:05]** text" when the speaker is unknown.</summary>
    public static string FormatParagraph(TranscriptParagraph p) =>
        $"**[{Stamp(p.Seconds)}]" + (p.Speaker is not null ? $" {p.Speaker}:** " : "** ") + p.Text;

    public static string Stamp(double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";
    }

    public static string Length(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min" : $"{t.Seconds} s";
}
