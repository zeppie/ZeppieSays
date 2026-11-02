using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ZeppieSays.Core;

namespace ZeppieSays.Engines;

/// <summary>ElevenLabs Scribe, in the cloud. The audio leaves the machine; nothing is loaded locally.</summary>
public sealed class ElevenLabsEngine(string name, string modelId, string apiKey) : ITranscriptionEngine
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public string Name => name;
    public string Backend => "Cloud";

    public async Task<string> TranscribeAsync(TranscriptionRequest request, CancellationToken ct) =>
        (await TranscribeWordsAsync(request, ct)).Text;

    public async Task<(string Text, IReadOnlyList<TimedWord> Words)> TranscribeWordsAsync(TranscriptionRequest request, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent(modelId), "model_id" },
            // Raw 16 kHz mono PCM skips decoding on their side, which ElevenLabs says lowers the latency.
            { new StringContent("pcm_s16le_16"), "file_format" },
            { new StringContent("false"), "tag_audio_events" },
            { new StringContent("word"), "timestamps_granularity" },
        };
        if (request.Language is { Length: > 0 } language && language != "auto")
            form.Add(new StringContent(language), "language_code");
        // A list goes as one form field per term. Key terms add 20% to the price, so they're only sent when there are some.
        foreach (var term in request.KeyTerms ?? [])
            form.Add(new StringContent(term), "keyterms");
        var audio = new ByteArrayContent(ToPcm16(request.Samples));
        audio.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(audio, "file", "audio.pcm");

        using var message = new HttpRequestMessage(HttpMethod.Post, "https://api.elevenlabs.io/v1/speech-to-text") { Content = form };
        message.Headers.Add("xi-api-key", apiKey);
        using var response = await Http.SendAsync(message, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(await DescribeErrorAsync(response, ct));

        var json = await response.Content.ReadFromJsonAsync<JsonObject>(ct);
        var text = json?["text"]?.GetValue<string>().Trim() ?? "";
        // The word list also holds the spaces between words and audio events; only the words have a place in the timeline.
        var words = (json?["words"] as JsonArray ?? [])
            .Where(w => w?["type"]?.GetValue<string>() == "word")
            .Select(w => new TimedWord(w!["text"]!.GetValue<string>().Trim(),
                w["start"]?.GetValue<double>() ?? 0, w["end"]?.GetValue<double>() ?? 0))
            .Where(w => w.Text.Length > 0)
            .ToList();
        return (text, words);
    }

    static byte[] ToPcm16(float[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            var value = (short)Math.Round(Math.Clamp(samples[i], -1f, 1f) * short.MaxValue);
            bytes[2 * i] = (byte)value;
            bytes[2 * i + 1] = (byte)(value >> 8);
        }
        return bytes;
    }

    /// <summary>ElevenLabs answers {"detail":{"status","message"}} (or a plain "detail" string) when it refuses.</summary>
    static async Task<string> DescribeErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string? status = null, detail = null;
        try
        {
            var error = (await response.Content.ReadFromJsonAsync<JsonObject>(ct))?["detail"];
            if (error is JsonValue) detail = error.GetValue<string>();
            else
            {
                status = error?["status"]?.ToString();
                detail = error?["message"]?.ToString();
            }
        }
        catch { /* not JSON: the status code is all there is */ }

        var code = (int)response.StatusCode;
        Log.Warn($"ElevenLabs speech-to-text failed: {code} {status} {detail}");
        return status switch
        {
            "invalid_api_key" => "ElevenLabs rejected the API key. Check it on the Settings page.",
            "quota_exceeded" => "Your ElevenLabs account has no credits left for speech-to-text.",
            _ when detail is { Length: > 0 } => $"ElevenLabs error {code}: {detail}",
            _ => $"ElevenLabs error {code} ({response.ReasonPhrase})",
        };
    }

    public void Dispose() { }
}
