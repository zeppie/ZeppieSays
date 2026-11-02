using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using ZeppieSays.Core;
using ZeppieSays.Llm;
using ZeppieSays.Services;

namespace ZeppieSays.Tests;

public class AnswerTests
{
    static JsonObject Json(string json) => (JsonObject)JsonNode.Parse(json)!;

    [Fact]
    public async Task Server_sent_events_are_read_with_their_names_and_data()
    {
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(
            "event: message_start\ndata: {\"a\":1}\n\nevent: ping\ndata: {}\n\ndata: [DONE]\n"));
        var events = new List<(string, string)>();
        await foreach (var e in ServerSentEvents.ReadAsync(stream, CancellationToken.None)) events.Add(e);
        Assert.Equal([("message_start", "{\"a\":1}"), ("ping", "{}"), ("", "[DONE]")], events);
    }

    [Fact]
    public void Claude_streams_text_searches_and_citations()
    {
        var progress = new AnswerProgress();
        AnthropicAnswer.Handle(Json("""{"type":"content_block_start","content_block":{"type":"server_tool_use","name":"web_search"}}"""), progress);
        Assert.Equal("Searching the web…", progress.Status);
        AnthropicAnswer.Handle(Json("""
            {"type":"content_block_start","content_block":{"type":"web_search_tool_result","content":[
              {"type":"web_search_result","url":"https://en.wikipedia.org/wiki/Idempotence","title":"Idempotence"},
              {"type":"web_search_result","url":"https://developer.mozilla.org/x","title":"MDN"}]}}
            """), progress);
        AnthropicAnswer.Handle(Json("""{"type":"content_block_delta","delta":{"type":"text_delta","text":"Doing it **twice**"}}"""), progress);
        AnthropicAnswer.Handle(Json("""{"type":"content_block_delta","delta":{"type":"text_delta","text":" is like once."}}"""), progress);

        Assert.Null(progress.Status); // writing the answer now
        Assert.Equal("Doing it **twice** is like once.", progress.Text);
        Assert.Equal(["en.wikipedia.org", "developer.mozilla.org"], progress.Sources.Select(s => s.Site)); // found, nothing cited yet

        AnthropicAnswer.Handle(Json("""
            {"type":"content_block_delta","delta":{"type":"citations_delta","citation":{"type":"web_search_result_location","url":"https://developer.mozilla.org/x","title":"MDN"}}}
            """), progress);
        Assert.Equal(["developer.mozilla.org"], progress.Sources.Select(s => s.Site)); // once cited, only what's cited
    }

    [Fact]
    public void A_streamed_error_from_Claude_is_shown_as_its_message()
    {
        var error = Assert.Throws<LlmException>(() => AnthropicAnswer.Handle(
            Json("""{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}"""), new AnswerProgress()));
        Assert.Contains("Overloaded", error.Message);
    }

    [Fact]
    public void OpenAI_streams_text_and_url_citations()
    {
        var progress = new AnswerProgress();
        OpenAiAnswer.Handle(Json("""{"type":"response.web_search_call.searching"}"""), progress);
        Assert.Equal("Searching the web…", progress.Status);
        OpenAiAnswer.Handle(Json("""{"type":"response.output_text.delta","delta":"Hello"}"""), progress);
        OpenAiAnswer.Handle(Json("""{"type":"response.output_text.annotation.added","annotation":{"type":"url_citation","url":"https://www.example.com/a","title":"Example"}}"""), progress);
        Assert.Equal("Hello", progress.Text);
        Assert.Equal("example.com", Assert.Single(progress.Sources).Site);
    }

    [Fact]
    public void Only_web_links_count_as_sources()
    {
        var progress = new AnswerProgress();
        progress.AddCited("javascript:alert(1)", "x");
        progress.AddCited("file:///c:/secret.txt", "x");
        Assert.Empty(progress.Sources);
    }

    [Fact]
    public void The_question_goes_with_the_selection_the_cursor_text_and_the_window_text()
    {
        var context = ScreenContext.Empty with
        {
            ProcessName = "winword", Title = "Report.docx", BeforeText = "The API is ", AfterText = " by design.",
            SelectedText = "idempotent", WindowText = "Report about the API",
        };
        var message = Answerer.UserMessage(" what does this word mean ", context);
        Assert.StartsWith("Question: what does this word mean", message);
        Assert.Contains("Selected text:\nidempotent", message.Replace("\r", ""));
        Assert.Contains("The API is [cursor] by design.", message);
        Assert.Contains("Report about the API", message);
        Assert.Contains("Window: Report.docx (winword)", message);
    }

    [Fact]
    public void Without_anything_on_screen_only_the_question_goes()
    {
        Assert.Equal("Question: explain recursion", Answerer.UserMessage("explain recursion", ScreenContext.Empty));
    }

    [Fact]
    public void An_Ask_key_on_the_default_model_is_answered_by_the_profiles_default()
    {
        var settings = new AppSettings();
        settings.Llm.DefaultProvider = LlmProvider.Anthropic;
        settings.Llm.DefaultModel = "claude-haiku-5-5";
        var answerer = new Answerer(settings);

        Assert.Equal((LlmProvider.Anthropic, "claude-haiku-5-5"), answerer.Resolve(new KeyBinding { Action = KeyAction.Ask }));
        Assert.Equal((LlmProvider.Ollama, "qwen3:8b"),
            answerer.Resolve(new KeyBinding { Action = KeyAction.Ask, AskProvider = LlmProvider.Ollama, AskModel = "qwen3:8b" }));
        Assert.Null(answerer.Create(LlmProvider.Anthropic, "claude-haiku-5-5")); // no API key saved
    }

    [Fact]
    public void A_profile_keeps_each_keys_answering_model()
    {
        var settings = new AppSettings();
        var key = settings.Keys[0];
        key.Action = KeyAction.Ask;
        key.AskProvider = LlmProvider.Ollama;
        key.AskModel = "qwen3:8b";
        var local = ModelProfile.From(settings, "Local");
        key.AskProvider = LlmProvider.Anthropic;
        key.AskModel = "claude-sonnet-5-5";
        var cloud = ModelProfile.From(settings, "Cloud");

        local.ApplyTo(settings);
        Assert.Equal((LlmProvider.Ollama, "qwen3:8b"), (key.AskProvider, key.AskModel));
        Assert.Equal(KeyAction.Ask, key.Action); // what a key does is the same in every profile
        cloud.ApplyTo(settings);
        Assert.Equal("claude-sonnet-5-5", key.AskModel);
    }
}
