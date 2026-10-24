using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Models.Messages;
using ZeppieSays.Core;

namespace ZeppieSays.Llm;

/// <summary>One chat completion: instructions plus the text to work on, returning the model's text.</summary>
public interface IChatModel
{
    Task<string> CompleteAsync(string system, string user, CancellationToken ct);
}

/// <summary>A provider refused a request, with its own explanation in words the user can act on.</summary>
public sealed class LlmException(string message) : Exception(message)
{
    /// <summary>Turns an error response into an <see cref="LlmException"/> that says what the provider said.</summary>
    public static async Task ThrowIfFailedAsync(HttpResponseMessage response, string provider, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        string? code = null, message = null;
        try
        {
            // OpenAI: {"error":{"message","code","type"}}. Ollama: {"error":"..."}.
            var json = await response.Content.ReadFromJsonAsync<JsonObject>(ct);
            var error = json?["error"];
            if (error is JsonValue) message = error.GetValue<string>();
            else
            {
                code = error?["code"]?.ToString() ?? error?["type"]?.ToString();
                message = error?["message"]?.GetValue<string>();
            }
        }
        catch { /* not JSON: the status code is all there is */ }

        var status = (int)response.StatusCode;
        throw new LlmException(code switch
        {
            "insufficient_quota" => $"{provider} says the API account has no credit left (add credit or billing at platform.openai.com)",
            "rate_limit_exceeded" => $"{provider} rate limit reached for this model; try again in a moment or pick another model",
            "model_not_found" => $"{provider} doesn't offer this model to your account",
            "invalid_api_key" => $"{provider} rejected the API key",
            _ when message is { Length: > 0 } => $"{provider} error {status}: {message}",
            _ => $"{provider} error {status} ({response.ReasonPhrase})",
        });
    }
}

/// <summary>Local models through Ollama's HTTP API. Nothing leaves the machine.</summary>
public sealed class OllamaChat(string baseUrl, string model, string keepLoaded) : IChatModel
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };

    /// <summary>Ollama's keep_alive: a duration like "4h", or -1 to keep the model loaded until Ollama stops.</summary>
    static JsonNode KeepAlive(string keepLoaded) =>
        keepLoaded == "forever" ? JsonValue.Create(-1) : JsonValue.Create(string.IsNullOrWhiteSpace(keepLoaded) ? "30m" : keepLoaded);

    /// <summary>Ollama's num_ctx in tokens, for long inputs such as meeting transcripts; null keeps the model's default.</summary>
    public int? ContextLength { get; set; }

    public async Task<string> CompleteAsync(string system, string user, CancellationToken ct)
    {
        var options = new JsonObject
        {
            ["temperature"] = 0.2,
            // A rewrite is about as long as the dictation; the cap stops a rambling model from stalling us.
            ["num_predict"] = Math.Clamp(user.Length / 2 + 256, 256, 4096),
        };
        if (ContextLength is { } context) options["num_ctx"] = context;
        var request = new JsonObject
        {
            ["model"] = model,
            ["stream"] = false,
            ["think"] = false, // reasoning models (Qwen3) would otherwise spend seconds thinking
            ["keep_alive"] = KeepAlive(keepLoaded),
            ["options"] = options,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = user },
            },
        };
        using var response = await Http.PostAsJsonAsync($"{baseUrl.TrimEnd('/')}/api/chat", request, ct);
        await LlmException.ThrowIfFailedAsync(response, "Ollama", ct);
        var json = await response.Content.ReadFromJsonAsync<JsonObject>(ct);
        return json?["message"]?["content"]?.GetValue<string>() ?? "";
    }

    /// <summary>Loads the model into memory ahead of time so the first rewrite isn't slowed by loading.</summary>
    public static async Task WarmUpAsync(string baseUrl, string model, string keepLoaded)
    {
        try
        {
            var request = new JsonObject { ["model"] = model, ["keep_alive"] = KeepAlive(keepLoaded) };
            using var response = await Http.PostAsJsonAsync($"{baseUrl.TrimEnd('/')}/api/generate", request);
        }
        catch (Exception e)
        {
            Log.Warn($"Ollama warm-up for {model} failed: {e.Message}");
        }
    }

    /// <summary>The models pulled into Ollama, or an empty list when Ollama isn't running.</summary>
    public static async Task<IReadOnlyList<string>> ListModelsAsync(string baseUrl)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var json = await Http.GetFromJsonAsync<JsonObject>($"{baseUrl.TrimEnd('/')}/api/tags", cts.Token);
            return json?["models"]?.AsArray()
                .Select(m => m?["name"]?.GetValue<string>())
                .OfType<string>()
                .Order()
                .ToList() ?? [];
        }
        catch
        {
            return [];
        }
    }
}

/// <summary>OpenAI's chat completions API. Text leaves the machine.</summary>
public sealed partial class OpenAiChat(string apiKey, string model) : IChatModel
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };

    /// <summary>Offered when the account's models can't be listed (no key yet, or offline).</summary>
    public static readonly IReadOnlyList<string> KnownModels = ["gpt-4.1-mini", "gpt-4.1", "gpt-4o-mini"];

    /// <summary>A fast, inexpensive model that rewrites dictations well; the starting choice for a new style.</summary>
    public const string SuggestedModel = "gpt-4.1-mini";

    /// <summary>
    /// The chat models this API key can use, without dated snapshots and without audio, image, embedding and
    /// other non-chat models. Empty when the list can't be fetched.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ListModelsAsync(string apiKey)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.openai.com/v1/models");
            request.Headers.Authorization = new("Bearer", apiKey);
            using var response = await Http.SendAsync(request, cts.Token);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadFromJsonAsync<JsonObject>(cts.Token);
            // Newest first, like Anthropic's list, so the current models aren't buried among old ones.
            return json?["data"]?.AsArray()
                .Select(m => (Id: m?["id"]?.GetValue<string>(), Created: m?["created"]?.GetValue<long>() ?? 0))
                .Where(m => m.Id is not null && IsChatModel(m.Id))
                .OrderByDescending(m => m.Created)
                .Select(m => m.Id!)
                .ToList() ?? [];
        }
        catch (Exception e)
        {
            Log.Warn($"Could not list OpenAI models: {e.Message}");
            return [];
        }
    }

    public static bool IsChatModel(string id) =>
        ChatFamily().IsMatch(id) && !DatedSnapshot().IsMatch(id) &&
        !new[] { "audio", "realtime", "tts", "transcribe", "image", "search", "embedding", "moderation", "instruct", "codex", "live" }
            .Any(id.Contains);

    [System.Text.RegularExpressions.GeneratedRegex(@"^(gpt-|o\d)")]
    private static partial System.Text.RegularExpressions.Regex ChatFamily();

    /// <summary>"gpt-4o-2024-08-06", or the older short form "gpt-3.5-turbo-0125" / "gpt-4-0613".</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"\d{4}-\d{2}-\d{2}|-\d{4}$")]
    private static partial System.Text.RegularExpressions.Regex DatedSnapshot();

    public async Task<string> CompleteAsync(string system, string user, CancellationToken ct)
    {
        var request = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = user },
            },
        };
        using var message = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
        {
            Content = JsonContent.Create(request),
        };
        message.Headers.Authorization = new("Bearer", apiKey);
        using var response = await Http.SendAsync(message, ct);
        await LlmException.ThrowIfFailedAsync(response, "OpenAI", ct);
        var json = await response.Content.ReadFromJsonAsync<JsonObject>(ct);
        return json?["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
    }
}

/// <summary>Claude through the official Anthropic SDK. Text leaves the machine.</summary>
public sealed class AnthropicChat(string apiKey, string model) : IChatModel
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>Offered when the account's models can't be listed (no key yet, or offline).</summary>
    public static readonly IReadOnlyList<string> KnownModels = ["claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-5-5"];

    /// <summary>The models this API key can use, newest first (Anthropic's own order). Empty when the list can't be fetched.</summary>
    public static async Task<IReadOnlyList<string>> ListModelsAsync(string apiKey)
    {
        try
        {
            var models = new List<string>();
            string? after = null;
            do // the list is paged
            {
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    "https://api.anthropic.com/v1/models?limit=100" + (after is null ? "" : $"&after_id={Uri.EscapeDataString(after)}"));
                request.Headers.Add("x-api-key", apiKey);
                request.Headers.Add("anthropic-version", "2023-06-01");
                using var response = await Http.SendAsync(request);
                await LlmException.ThrowIfFailedAsync(response, "Anthropic", CancellationToken.None);
                var json = await response.Content.ReadFromJsonAsync<JsonObject>();
                models.AddRange(json?["data"]?.AsArray().Select(m => m?["id"]?.GetValue<string>()).OfType<string>() ?? []);
                after = json?["has_more"]?.GetValue<bool>() == true ? json["last_id"]?.GetValue<string>() : null;
            } while (after is not null);
            return models;
        }
        catch (Exception e)
        {
            Log.Warn($"Could not list Anthropic models: {e.Message}");
            return [];
        }
    }

    public async Task<string> CompleteAsync(string system, string user, CancellationToken ct)
    {
        var client = new AnthropicClient { ApiKey = apiKey };
        var response = await client.Messages.Create(new MessageCreateParams
        {
            Model = model,
            MaxTokens = 4000,
            System = system,
            // Rewriting a dictation is simple; low effort keeps it fast.
            OutputConfig = new OutputConfig { Effort = Effort.Low },
            Messages = [new UserMessageParam { Content = user }],
        }, ct);

        // A declined request returns no usable text; the caller then pastes the plain transcript.
        if (response.StopReason == "refusal")
        {
            Log.Warn($"Claude declined the rewrite ({response.StopDetails?.Category})");
            return "";
        }
        return string.Concat(response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));
    }
}

/// <summary>Stores API keys encrypted for the current Windows user (DPAPI), so the settings file never holds them in plain text.</summary>
public static class SecretStore
{
    static readonly byte[] Entropy = "ZeppieSays.ApiKey"u8.ToArray();

    public static string Protect(string secret) =>
        string.IsNullOrEmpty(secret)
            ? ""
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser));

    public static string Unprotect(string protectedSecret)
    {
        if (string.IsNullOrEmpty(protectedSecret)) return "";
        try
        {
            return Encoding.UTF8.GetString(
                ProtectedData.Unprotect(Convert.FromBase64String(protectedSecret), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            return ""; // damaged, or copied from another user or PC: treat as not set
        }
    }
}
