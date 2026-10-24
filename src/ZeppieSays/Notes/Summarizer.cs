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

    const string Instructions = """
        You write meeting notes from a transcript made with speech recognition. Speakers may be labelled "Speaker 1", "Speaker 2".
        Reply in Markdown with exactly these three sections and nothing else:
        ## Summary
        Three to six sentences on what was discussed.
        ## Decisions
        A bullet list of decisions that were made, or "- None".
        ## Action items
        A bullet list like "- [ ] Speaker 2: send the planning by Friday", or "- None".
        Rules:
        - Write in the same language as the transcript. Never translate.
        - Use only what was said. Do not invent names, dates or tasks.
        - Speech recognition makes mistakes; read past odd words instead of quoting them.
        """;

    const string PartInstructions = """
        You take notes on one part of a longer transcript made with speech recognition.
        Reply with short bullet points: the topics discussed, any decisions, and any tasks with who does them.
        Write in the same language as the transcript. Use only what was said.
        """;

    public bool IsAvailable => Model() is not null;

    IChatModel? Model()
    {
        var model = rewriter.CreateModel(rewriter.Effective(new Style { Provider = LlmProvider.Default }));
        if (model is OllamaChat ollama) ollama.ContextLength = 16_384; // room for a part plus the reply
        return model;
    }

    public async Task<string?> SummarizeAsync(Transcript transcript, CancellationToken ct)
    {
        if (Model() is not { } model) return null;
        var text = transcript.PlainText();
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
                    notes.AppendLine(await model.CompleteAsync(PartInstructions, string.Join("\n", part), ct));
                    part.Clear();
                    words = 0;
                }
                part.Add(line);
                words += w;
            }
            text = "Notes on the parts of a long meeting, in order:\n" + notes;
        }
        var reply = ThinkBlock().Replace(await model.CompleteAsync(Instructions, text, ct), "").Trim();
        if (reply.StartsWith("```")) reply = reply.Trim('`').Replace("markdown\n", "", StringComparison.OrdinalIgnoreCase).Trim();
        return reply.Length > 0 ? reply : null;
    }

    static int CountWords(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    [GeneratedRegex(@"<think>.*?</think>", RegexOptions.Singleline)]
    private static partial Regex ThinkBlock();
}
