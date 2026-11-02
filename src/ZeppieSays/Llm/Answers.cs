using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using ZeppieSays.Core;
using ZeppieSays.Services;

namespace ZeppieSays.Llm;

/// <summary>A web page an answer is based on.</summary>
public sealed record AnswerSource(string Title, string Url)
{
    /// <summary>"wikipedia.org" for the link's label when the page has no title.</summary>
    public string Site => Uri.TryCreate(Url, UriKind.Absolute, out var uri) ? uri.Host.Replace("www.", "") : Url;
}

/// <summary>An answer as it streams in. Changes come from a background thread.</summary>
public sealed class AnswerProgress
{
    readonly object _lock = new();
    readonly StringBuilder _text = new();
    readonly List<AnswerSource> _cited = [];
    readonly List<AnswerSource> _found = [];
    string? _status;

    public event Action? Changed;

    public string Text { get { lock (_lock) return _text.ToString(); } }

    /// <summary>"Searching the web…" while that happens; null once the answer is being written.</summary>
    public string? Status { get { lock (_lock) return _status; } }

    /// <summary>The pages the answer cites, or else the first few the search found.</summary>
    public IReadOnlyList<AnswerSource> Sources
    {
        get { lock (_lock) return _cited.Count > 0 ? [.. _cited] : [.. _found.Take(3)]; }
    }

    public void AddText(string text)
    {
        lock (_lock) { _text.Append(text); _status = null; }
        Changed?.Invoke();
    }

    public void SetStatus(string? status)
    {
        lock (_lock) _status = status;
        Changed?.Invoke();
    }

    public void AddCited(string? url, string? title) => Add(_cited, url, title);
    public void AddFound(string? url, string? title) => Add(_found, url, title);

    void Add(List<AnswerSource> list, string? url, string? title)
    {
        if (url is null || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return;
        lock (_lock)
        {
            if (list.Any(s => s.Url == url)) return;
            list.Add(new AnswerSource(string.IsNullOrWhiteSpace(title) ? "" : title.Trim(), url));
        }
        Changed?.Invoke();
    }
}

/// <summary>Answers a question, streaming the text into an <see cref="AnswerProgress"/>.</summary>
public interface IAnswerModel
{
    /// <summary>Looks things up on the web when the question needs it.</summary>
    bool SearchesWeb { get; }
    Task AnswerAsync(string system, string user, AnswerProgress progress, CancellationToken ct);
}

/// <summary>Server-sent events, as Anthropic and OpenAI stream them: an optional "event:" line, then "data:" lines.</summary>
public static class ServerSentEvents
{
    public static async IAsyncEnumerable<(string Event, string Data)> ReadAsync(Stream stream, [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream);
        string? name = null;
        var data = new StringBuilder();
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length > 0) yield return (name ?? "", data.ToString());
                name = null;
                data.Clear();
            }
            else if (line.StartsWith("event:")) name = line[6..].Trim();
            else if (line.StartsWith("data:")) data.Append(data.Length > 0 ? "\n" : "").Append(line[5..].TrimStart());
        }
        if (data.Length > 0) yield return (name ?? "", data.ToString());
    }

    public static async Task<HttpResponseMessage> SendAsync(HttpClient http, HttpRequestMessage request, string provider, CancellationToken ct)
    {
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await LlmException.ThrowIfFailedAsync(response, provider, ct);
        return response;
    }
}

/// <summary>Claude with Anthropic's own web search tool. The question and screen text leave the machine.</summary>
public sealed class AnthropicAnswer(string apiKey, string model) : IAnswerModel
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };

    public bool SearchesWeb => true;

    public async Task AnswerAsync(string system, string user, AnswerProgress progress, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
        {
            Content = JsonContent.Create(new JsonObject
            {
                ["model"] = model,
                ["max_tokens"] = 2000,
                ["stream"] = true,
                ["system"] = system,
                ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = user }),
                // A few searches at most keeps the answer quick; most questions need none.
                ["tools"] = new JsonArray(new JsonObject { ["type"] = "web_search_20250305", ["name"] = "web_search", ["max_uses"] = 3 }),
            }),
        };
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        using var response = await ServerSentEvents.SendAsync(Http, request, "Anthropic", ct);
        await foreach (var (_, data) in ServerSentEvents.ReadAsync(await response.Content.ReadAsStreamAsync(ct), ct))
            if (JsonNode.Parse(data) is JsonObject json) Handle(json, progress);
    }

    /// <summary>One streamed event: text as it's written, the searches, and the pages the text cites.</summary>
    public static void Handle(JsonObject json, AnswerProgress progress)
    {
        switch (json["type"]?.GetValue<string>())
        {
            case "content_block_start":
                var block = json["content_block"];
                switch (block?["type"]?.GetValue<string>())
                {
                    case "server_tool_use":
                        progress.SetStatus("Searching the web…");
                        break;
                    case "web_search_tool_result":
                        foreach (var result in block["content"] as JsonArray ?? [])
                            progress.AddFound(result?["url"]?.GetValue<string>(), result?["title"]?.GetValue<string>());
                        progress.SetStatus("Reading what it found…");
                        break;
                }
                break;
            case "content_block_delta":
                var delta = json["delta"];
                switch (delta?["type"]?.GetValue<string>())
                {
                    case "text_delta":
                        progress.AddText(delta["text"]?.GetValue<string>() ?? "");
                        break;
                    case "citations_delta":
                        progress.AddCited(delta["citation"]?["url"]?.GetValue<string>(), delta["citation"]?["title"]?.GetValue<string>());
                        break;
                }
                break;
            case "error":
                throw new LlmException($"Anthropic error: {json["error"]?["message"]?.GetValue<string>() ?? "unknown"}");
        }
    }
}

/// <summary>OpenAI's Responses API with its web search tool. The question and screen text leave the machine.</summary>
public sealed class OpenAiAnswer(string apiKey, string model) : IAnswerModel
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };

    public bool SearchesWeb => true;

    public async Task AnswerAsync(string system, string user, AnswerProgress progress, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses")
        {
            Content = JsonContent.Create(new JsonObject
            {
                ["model"] = model,
                ["instructions"] = system,
                ["input"] = user,
                ["stream"] = true,
                ["tools"] = new JsonArray(new JsonObject { ["type"] = "web_search" }),
            }),
        };
        request.Headers.Authorization = new("Bearer", apiKey);
        using var response = await ServerSentEvents.SendAsync(Http, request, "OpenAI", ct);
        await foreach (var (_, data) in ServerSentEvents.ReadAsync(await response.Content.ReadAsStreamAsync(ct), ct))
            if (data != "[DONE]" && JsonNode.Parse(data) is JsonObject json) Handle(json, progress);
    }

    public static void Handle(JsonObject json, AnswerProgress progress)
    {
        switch (json["type"]?.GetValue<string>())
        {
            case "response.web_search_call.in_progress" or "response.web_search_call.searching":
                progress.SetStatus("Searching the web…");
                break;
            case "response.output_text.delta":
                progress.AddText(json["delta"]?.GetValue<string>() ?? "");
                break;
            case "response.output_text.annotation.added":
                var annotation = json["annotation"];
                if (annotation?["type"]?.GetValue<string>() == "url_citation")
                    progress.AddCited(annotation["url"]?.GetValue<string>(), annotation["title"]?.GetValue<string>());
                break;
            case "error":
                throw new LlmException($"OpenAI error: {json["message"]?.GetValue<string>() ?? json["error"]?["message"]?.GetValue<string>() ?? "unknown"}");
            case "response.failed":
                throw new LlmException($"OpenAI error: {json["response"]?["error"]?["message"]?.GetValue<string>() ?? "the answer failed"}");
        }
    }
}

/// <summary>A model in Ollama on this PC. Nothing leaves the machine, so there's no web search either.</summary>
public sealed class OllamaAnswer(string baseUrl, string model, string keepLoaded) : IAnswerModel
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };

    public bool SearchesWeb => false;

    public async Task AnswerAsync(string system, string user, AnswerProgress progress, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + "/api/chat")
        {
            Content = JsonContent.Create(new JsonObject
            {
                ["model"] = model,
                ["stream"] = true,
                ["keep_alive"] = OllamaChat.KeepAlive(keepLoaded),
                ["messages"] = new JsonArray(
                    new JsonObject { ["role"] = "system", ["content"] = system },
                    new JsonObject { ["role"] = "user", ["content"] = user }),
            }),
        };
        using var response = await ServerSentEvents.SendAsync(Http, request, "Ollama", ct);
        // Ollama streams one JSON object per line rather than server-sent events.
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0 || JsonNode.Parse(line) is not JsonObject json) continue;
            if (json["error"]?.GetValue<string>() is { } error) throw new LlmException($"Ollama error: {error}");
            progress.AddText(json["message"]?["content"]?.GetValue<string>() ?? "");
        }
    }
}

/// <summary>Turns a spoken question, and what was on screen, into an answer from the key's answering model.</summary>
public sealed class Answerer(AppSettings settings)
{
    const int MaxSelectedChars = 6000, MaxWindowChars = 4000;

    /// <summary>The key's own answering model, or the default model when it uses that.</summary>
    public (LlmProvider Provider, string Model) Resolve(KeyBinding key) =>
        key.AskProvider is LlmProvider.Default or LlmProvider.None || string.IsNullOrWhiteSpace(key.AskModel)
            ? (settings.Llm.DefaultProvider, settings.Llm.DefaultModel)
            : (key.AskProvider, key.AskModel);

    /// <summary>Null when a cloud provider has no API key.</summary>
    public IAnswerModel? Create(LlmProvider provider, string model) => provider switch
    {
        LlmProvider.Ollama => new OllamaAnswer(settings.Llm.OllamaUrl, model, settings.Llm.KeepLoaded),
        LlmProvider.OpenAI when SecretStore.Unprotect(settings.Llm.OpenAiKeyProtected) is { Length: > 0 } key => new OpenAiAnswer(key, model),
        LlmProvider.Anthropic when SecretStore.Unprotect(settings.Llm.AnthropicKeyProtected) is { Length: > 0 } key => new AnthropicAnswer(key, model),
        _ => null,
    };

    /// <param name="target">The language to answer in, or null for the question's own language.</param>
    public static string SystemPrompt(bool searchesWeb, string? target, DateTime today)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine("You answer a question the user just asked out loud while working at their computer. The answer appears in a small pop-up.");
        prompt.AppendLine("Be brief and to the point: a few sentences, or a short list when that is clearer. No preamble, no repeating the question, no offer of more help.");
        prompt.AppendLine("The question was transcribed from speech, so it may contain small transcription mistakes; read it for what was meant.");
        prompt.AppendLine("When the question says \"this\", \"this word\" or \"here\", it means the selected text if there is any, and otherwise what is on the screen.");
        prompt.AppendLine("You may use Markdown: **bold**, and lists with lines starting with \"- \". No tables, no headings.");
        prompt.AppendLine(searchesWeb
            ? "Search the web only when the answer depends on facts you are not sure of, or on anything recent. Otherwise answer straight away."
            : "You cannot look anything up. If the answer depends on recent facts, say that you may be out of date.");
        prompt.AppendLine(target is null
            ? "Answer in the language of the question."
            : $"Answer in {ModelCatalog.Languages.FirstOrDefault(l => l.Code == target).Name ?? target}.");
        prompt.Append($"Today is {today:dddd d MMMM yyyy}.");
        return prompt.ToString();
    }

    /// <summary>The question, then what was on screen: the selection, the text around the cursor and the window's text.</summary>
    public static string UserMessage(string question, ScreenContext context)
    {
        var message = new StringBuilder();
        message.AppendLine($"Question: {question.Trim()}");
        if (Clip(context.SelectedText, MaxSelectedChars) is { } selected)
            message.AppendLine().AppendLine("Selected text:").AppendLine(selected);
        if (context.BeforeText is { Length: > 0 } || context.AfterText is { Length: > 0 })
            message.AppendLine().AppendLine("Text around the cursor ([cursor] marks where it is):")
                .AppendLine($"{context.BeforeText}[cursor]{context.AfterText}".Trim());
        if (Clip(context.WindowText, MaxWindowChars) is { } window)
            message.AppendLine().AppendLine("Text visible in the window (read with OCR, may be imperfect):").AppendLine(window);
        if (context.Title.Length > 0 || context.ProcessName.Length > 0)
            message.AppendLine().AppendLine($"Window: {context.Title} ({context.ProcessName})");
        return message.ToString().TrimEnd();
    }

    static string? Clip(string? text, int max)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        return text.Length <= max ? text : text[..max] + " …";
    }

    /// <summary>The answer without its Markdown, for pasting into an app.</summary>
    public static string PlainText(string markdown) =>
        markdown.Replace("**", "").Replace("\r", "").Trim();
}
