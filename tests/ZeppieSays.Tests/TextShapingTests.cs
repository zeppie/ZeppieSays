using ZeppieSays.Core;
using ZeppieSays.Services;

namespace ZeppieSays.Tests;

public class TextShapingTests
{
    static readonly AppSettings Defaults = new();

    static ScreenContext Context(string? before = null, string? after = null, bool code = false, params string[] identifiers) =>
        ScreenContext.Empty with { BeforeText = before, AfterText = after, IsCodeEditor = code, Identifiers = identifiers };

    [Fact]
    public void Removes_filler_words_and_capitalizes()
    {
        var text = TextShaping.Clean("Uh, so I think um we should go.", Defaults, Context());
        Assert.Equal("So I think we should go.", text);
    }

    [Fact]
    public void Removes_fillers_between_commas()
    {
        var text = TextShaping.Clean("I think we should, um, ship it on Monday.", Defaults, Context());
        Assert.Equal("I think we should ship it on Monday.", text);
    }

    [Fact]
    public void Removes_dutch_fillers()
    {
        var text = TextShaping.Clean("Ehm, ik denk euh dat het werkt.", Defaults, Context());
        Assert.Equal("Ik denk dat het werkt.", text);
    }

    [Fact]
    public void Keeps_fillers_when_disabled()
    {
        var settings = new AppSettings { RemoveFillerWords = false };
        Assert.Equal("Um, yes.", TextShaping.Clean("Um, yes.", settings, Context()));
    }

    [Fact]
    public void Dictionary_replaces_sounds_like_variants()
    {
        var settings = new AppSettings
        {
            Dictionary = [new DictionaryEntry { Term = "Betabit", SoundsLike = ["beta bid"] }],
        };
        Assert.Equal("I work at Betabit.", TextShaping.Clean("I work at beta bit.", settings, Context()));
        Assert.Equal("I work at Betabit.", TextShaping.Clean("I work at Beta-bid.", settings, Context()));
    }

    [Fact]
    public void Dictionary_matches_camel_case_terms_spoken_as_words()
    {
        var settings = new AppSettings { Dictionary = [new DictionaryEntry { Term = "ZeppieSays" }] };
        Assert.Equal("Open ZeppieSays now.", TextShaping.Clean("Open zeppie says now.", settings, Context()));
    }

    [Fact]
    public void Dictionary_does_not_touch_parts_of_other_words()
    {
        var settings = new AppSettings { Dictionary = [new DictionaryEntry { Term = "Ada", SoundsLike = ["ada"] }] };
        Assert.Equal("Canada is big.", TextShaping.Clean("Canada is big.", settings, Context()));
    }

    [Fact]
    public void Code_editor_turns_spoken_identifiers_into_code()
    {
        var text = TextShaping.Clean("Set the user id from get user name.", Defaults,
            Context(code: true, identifiers: ["userId", "getUserName"]));
        Assert.Equal("Set the userId from getUserName.", text);
    }

    [Fact]
    public void Identifiers_are_left_alone_outside_code_editors()
    {
        var text = TextShaping.Clean("Set the user id.", Defaults, Context(code: false, identifiers: ["userId"]));
        Assert.Equal("Set the user id.", text);
    }

    [Fact]
    public void Continues_a_sentence_in_lowercase_with_a_space()
    {
        var text = TextShaping.Clean("The plan works.", Defaults, Context(before: "I think that"));
        Assert.Equal(" the plan works.", text);
    }

    [Fact]
    public void Starts_a_new_sentence_with_a_capital()
    {
        var text = TextShaping.Clean("the plan works.", Defaults, Context(before: "Done. "));
        Assert.Equal("The plan works.", text);
    }

    [Fact]
    public void Adds_a_space_before_following_text()
    {
        var text = TextShaping.Clean("Hello", Defaults, Context(before: "", after: "world"));
        Assert.Equal("Hello ", text);
    }

    [Theory]
    [InlineData("[BLANK_AUDIO]")]
    [InlineData(" (music) ")]
    [InlineData("Thank you.")]
    [InlineData("um")]
    public void Drops_non_speech(string raw) =>
        Assert.Equal("", TextShaping.Clean(raw, Defaults, Context()));

    [Fact]
    public void Prompt_lists_dictionary_and_identifiers_but_not_surrounding_text()
    {
        var dictionary = new[] { new DictionaryEntry { Term = "Betabit" } };
        var prompt = TextShaping.BuildPrompt(dictionary, Context(before: "Dear Anna,", code: true, identifiers: ["userId"]));
        Assert.Equal("Betabit, userId.", prompt);
    }

    [Fact]
    public void Prompt_is_empty_without_vocabulary() =>
        Assert.Equal("", TextShaping.BuildPrompt([], Context(before: "Some English text before the cursor")));

    [Fact]
    public void Key_terms_skip_what_ElevenLabs_refuses_and_put_the_dictionary_first()
    {
        var dictionary = new[] { "Betabit", "betabit", "List<string>", "one two three four five six", new string('x', 50), "Zeppie  Says" }
            .Select(t => new DictionaryEntry { Term = t });
        var terms = TextShaping.KeyTerms(dictionary, Context(code: true, identifiers: ["userId", "Betabit"]));
        Assert.Equal(["Betabit", "Zeppie Says", "userId"], terms);
    }

    [Fact]
    public void Key_terms_stop_at_a_hundred() =>
        Assert.Equal(100, TextShaping.KeyTerms(
            Enumerable.Range(0, 150).Select(i => new DictionaryEntry { Term = $"term{i}" }), ScreenContext.Empty).Count);

    [Fact]
    public void Extracts_code_like_identifiers()
    {
        var ids = ContextCapture.ExtractIdentifiers("const userId = getUserName(); open Program.cs and my_var, then The End");
        Assert.Contains("userId", ids);
        Assert.Contains("getUserName", ids);
        Assert.Contains("Program.cs", ids);
        Assert.Contains("my_var", ids);
        Assert.DoesNotContain("The", ids);
        Assert.DoesNotContain("const", ids);
    }

    [Theory]
    [InlineData("Work", "Inbox - Outlook", "https://outlook.office.com/mail/", "Inbox - Outlook (outlook.office.com) - Work")]
    [InlineData("Inbox - Outlook - Work - Microsoft Edge", "Inbox - Outlook", "https://outlook.office.com/mail/",
        "Inbox - Outlook - Work - Microsoft Edge (outlook.office.com)")]
    [InlineData("Work", "New Tab", "edge://newtab/", "New Tab - Work")]
    [InlineData("Work", null, null, "Work")]
    [InlineData("", "ChatGPT", "https://chatgpt.com/", "ChatGPT (chatgpt.com)")]
    public void Adds_the_browser_page_to_the_window_title(string window, string? page, string? url, string expected) =>
        Assert.Equal(expected, ContextCapture.WithPage(window, page, url));

    [Theory]
    [InlineData("userId", "user id")]
    [InlineData("HttpClientFactory", "http client factory")]
    [InlineData("max_retry_count", "max retry count")]
    public void Spoken_form_splits_identifiers(string identifier, string spoken) =>
        Assert.Equal(spoken, TextShaping.Spoken(identifier));
}
