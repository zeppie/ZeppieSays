using ZeppieSays.Core;
using ZeppieSays.Llm;
using ZeppieSays.Services;

namespace ZeppieSays.Tests;

public class TextRewriterTests
{
    static ScreenContext In(string process, string title = "") => ScreenContext.Empty with { ProcessName = process, Title = title };

    static Style StyleOf(Strength strength) => new() { Provider = LlmProvider.Ollama, Model = "m", Strength = strength };

    [Theory]
    [InlineData("ms-teams", "Chat | Microsoft Teams", "Casual")]
    [InlineData("olk", "Inbox - Outlook", "Formal")]
    [InlineData("WINWORD", "Report.docx - Word", "Formal")]
    [InlineData("chrome", "Inbox (3) - me@gmail.com - Gmail", "Formal")]
    [InlineData("chrome", "ChatGPT", "Prompt")]
    [InlineData("Code", "Program.cs - ZeppieSays - Visual Studio Code", "Code")]
    [InlineData("notepad", "Untitled - Notepad", "Raw")]
    public void Picks_the_style_for_the_app(string process, string title, string expected) =>
        Assert.Equal(expected, new TextRewriter(new AppSettings()).ResolveStyle(In(process, title)).Name);

    [Fact]
    public void Styles_on_the_default_model_get_it_filled_in()
    {
        var settings = new AppSettings { Llm = { DefaultProvider = LlmProvider.Ollama, DefaultModel = "qwen3:8b" } };
        var formal = new TextRewriter(settings).ResolveStyle(In("olk", "Inbox - Outlook"));
        Assert.Equal((LlmProvider.Ollama, "qwen3:8b"), (formal.Provider, formal.Model));
        Assert.True(formal.UsesLlm);
        Assert.Equal("Formal", formal.Name);
    }

    [Fact]
    public void Changing_the_default_changes_every_style_that_uses_it()
    {
        var settings = new AppSettings();
        var rewriter = new TextRewriter(settings);
        settings.Llm.DefaultProvider = LlmProvider.Anthropic;
        settings.Llm.DefaultModel = "claude-opus-5-5";
        Assert.All(new[] { In("ms-teams"), In("olk"), In("Code") },
            app => Assert.Equal("claude-opus-5-5", rewriter.ResolveStyle(app).Model));
    }

    [Fact]
    public void An_override_and_raw_ignore_the_default()
    {
        var settings = new AppSettings();
        settings.Styles.Single(s => s.Id == "formal").Provider = LlmProvider.Anthropic;
        settings.Styles.Single(s => s.Id == "formal").Model = "claude-sonnet-5-5";
        var rewriter = new TextRewriter(settings);
        Assert.Equal("claude-sonnet-5-5", rewriter.ResolveStyle(In("olk")).Model);
        Assert.False(rewriter.ResolveStyle(In("notepad")).UsesLlm); // Raw
    }

    [Fact]
    public void Website_rule_wins_over_app_rule()
    {
        var settings = new AppSettings
        {
            StyleRules =
            [
                new() { Kind = RuleKind.App, Match = "chrome.exe", StyleId = "casual" },
                new() { Kind = RuleKind.Title, Match = "gmail", StyleId = "formal" },
            ],
        };
        var rewriter = new TextRewriter(settings);
        Assert.Equal("Formal", rewriter.ResolveStyle(In("chrome", "Inbox - Gmail")).Name);
        Assert.Equal("Casual", rewriter.ResolveStyle(In("chrome", "YouTube")).Name);
    }

    [Fact]
    public async Task Raw_style_returns_text_unchanged_without_calling_a_model()
    {
        var raw = new Style { Provider = LlmProvider.None };
        var result = await new TextRewriter(new AppSettings()).RewriteAsync("hello", raw, In("x"), "en", CancellationToken.None);
        Assert.Equal(new RewriteResult("hello", false), result);
    }

    [Fact]
    public async Task Cloud_style_without_key_keeps_the_transcript()
    {
        var cloud = new Style { Provider = LlmProvider.Anthropic, Model = "claude-opus-5-5" };
        var result = await new TextRewriter(new AppSettings()).RewriteAsync("hello there", cloud, In("x"), "en", CancellationToken.None);
        Assert.False(result.Rewritten);
        Assert.Equal("hello there", result.Text);
    }

    [Theory]
    [InlineData("<dictation>So, can we start?</dictation>", "So, can we start?")]
    [InlineData("\"So, can we start?\"", "So, can we start?")]
    [InlineData("<think>hmm</think>\nSo, can we start?", "So, can we start?")]
    public void Strips_wrappers_from_the_reply(string reply, string expected) =>
        Assert.Equal(expected, TextRewriter.Accept("so uh can we start", reply, StyleOf(Strength.Light)));

    [Theory]
    [InlineData("Here is the cleaned text: So, can we start?")]
    [InlineData("Sure! We can start at nine.")]
    [InlineData("<think>the user wants")]
    [InlineData("")]
    public void Rejects_replies_that_are_not_a_rewrite(string reply) =>
        Assert.Null(TextRewriter.Accept("so uh can we start", reply, StyleOf(Strength.Light)));

    [Fact]
    public void Removes_an_invented_sign_off_with_placeholder()
    {
        var reply = "Hello Peter,\n\nThank you for the update.\n\nBest regards,\n[Your Name]";
        Assert.Equal("Hello Peter,\n\nThank you for the update.",
            TextRewriter.Accept("hey Peter thanks for the update", reply, StyleOf(Strength.Medium)));
    }

    [Fact]
    public void Keeps_a_sign_off_that_was_dictated()
    {
        var reply = "Hoi Mark,\n\nHeb je volgende week tijd?\n\nGroeten";
        Assert.Equal(reply, TextRewriter.Accept("hoi Mark heb je volgende week tijd groetjes", reply, StyleOf(Strength.Medium)));
    }

    [Fact]
    public void Rejects_a_reply_that_invents_a_lot_of_text()
    {
        var reply = "So, can we start? I have prepared an agenda with five topics, a budget review and the planning for next quarter.";
        Assert.Null(TextRewriter.Accept("so can we start", reply, StyleOf(Strength.Light)));
    }

    [Fact]
    public void Rejects_a_reply_that_drops_most_of_the_text()
    {
        var original = "we should move the meeting to Thursday because Anna cannot make it on Wednesday";
        Assert.Null(TextRewriter.Accept(original, "Thursday.", StyleOf(Strength.Medium)));
    }

    [Fact]
    public void Strong_style_may_restructure_into_a_list() =>
        Assert.NotNull(TextRewriter.Accept(
            "can you make the button bigger and show the error and add a remember me box",
            "- Make the button bigger\n- Show the error message\n- Add a \"remember me\" checkbox",
            StyleOf(Strength.Strong)));

    [Theory]
    [InlineData("ik denk dat we morgen kunnen beginnen met de planning", "nl")]
    [InlineData("I think we can start with the planning tomorrow", "en")]
    [InlineData("Betabit", null)]
    [InlineData("Wir können morgen anfangen", null)]
    public void Guesses_dutch_or_english(string text, string? expected) =>
        Assert.Equal(expected, TextRewriter.GuessLanguage(text));

    [Fact]
    public void Prompt_names_the_language_and_the_users_rules()
    {
        var style = new Style { Strength = Strength.Light, Instructions = "No sign-off." };
        var prompt = TextRewriter.BuildSystemPrompt(style, In("ms-teams", "Chat"), [new DictionaryEntry { Term = "Betabit" }], "nl");
        Assert.Contains("The dictation is in Dutch", prompt);
        Assert.Contains("No sign-off.", prompt);
        Assert.Contains("Betabit", prompt);
        Assert.Contains("ms-teams", prompt);
    }

    [Fact]
    public void Translating_asks_for_the_target_language_instead_of_forbidding_it()
    {
        var style = new Style { Strength = Strength.Light };
        var prompt = TextRewriter.BuildSystemPrompt(style, In("olk", "Mail"), [new DictionaryEntry { Term = "Betabit" }], "nl", "en");
        Assert.Contains("Translate it into English", prompt);
        Assert.DoesNotContain("Never translate", prompt);
        Assert.DoesNotContain("Examples:", prompt); // same-language examples would pull the reply back to Dutch
        Assert.Contains("Betabit", prompt);
    }

    [Fact]
    public void Translating_into_the_spoken_language_is_a_normal_cleanup()
    {
        var prompt = TextRewriter.BuildSystemPrompt(new Style(), In("olk", "Mail"), [], "en", "en");
        Assert.Contains("Never translate", prompt);
    }

    [Fact]
    public void A_raw_style_still_translates_with_the_default_model()
    {
        var settings = new AppSettings { DefaultStyleId = StyleDefaults.RawId };
        var rewriter = new TextRewriter(settings);
        Assert.False(rewriter.ResolveStyle(In("notepad")).UsesLlm);
        var style = rewriter.ResolveStyle(In("notepad"), new KeyBinding { OutputLanguage = "en" });
        Assert.True(style.UsesLlm);
        Assert.Equal(settings.Llm.DefaultModel, style.Model);
    }

    [Theory]
    [InlineData("gpt-4.1-mini", true)]
    [InlineData("gpt-4o", true)]
    [InlineData("o4-mini", true)]
    [InlineData("gpt-6.1-sol", true)]
    [InlineData("gpt-5.2-chat-latest", true)]
    [InlineData("gpt-4o-2024-08-06", false)]       // dated snapshot of a model already listed
    [InlineData("gpt-3.5-turbo-0125", false)]      // the older, shorter snapshot form
    [InlineData("gpt-4-0613", false)]
    [InlineData("gpt-live-1", false)]
    [InlineData("gpt-4o-mini-transcribe", false)]
    [InlineData("gpt-4o-realtime-preview", false)]
    [InlineData("text-embedding-3-small", false)]
    [InlineData("dall-e-3", false)]
    [InlineData("whisper-1", false)]
    public void Only_chat_models_are_offered_for_openai(string id, bool offered) =>
        Assert.Equal(offered, OpenAiChat.IsChatModel(id));

    [Theory]
    [InlineData("""{"error":{"message":"You have no credits remaining.","type":"insufficient_quota","code":"insufficient_quota"}}""", "no credit left")]
    [InlineData("""{"error":{"message":"You have no credits remaining. Add credits to continue.","type":"invalid_request_error","code":null}}""", "You have no credits remaining")]
    [InlineData("""{"error":"model 'qwen9' not found"}""", "model 'qwen9' not found")]
    [InlineData("<html>Bad gateway</html>", "error 429")]
    public async Task Provider_errors_say_what_the_provider_said(string body, string expected)
    {
        using var response = new System.Net.Http.HttpResponseMessage((System.Net.HttpStatusCode)429)
        {
            Content = new System.Net.Http.StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
        var e = await Assert.ThrowsAsync<LlmException>(() => LlmException.ThrowIfFailedAsync(response, "OpenAI", CancellationToken.None));
        Assert.Contains(expected, e.Message);
    }

    [Fact]
    public void Api_keys_round_trip_through_windows_encryption()
    {
        var stored = SecretStore.Protect("sk-test-123");
        Assert.DoesNotContain("sk-test", stored);
        Assert.Equal("sk-test-123", SecretStore.Unprotect(stored));
        Assert.Equal("", SecretStore.Unprotect("not-base64!"));
    }
}
