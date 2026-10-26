namespace ZeppieSays.Core;

/// <summary>Who rewrites a style's text. Default means "the default model", set once for all styles.</summary>
public enum LlmProvider { None, Default, Ollama, OpenAI, Anthropic }

public enum Strength { Light, Medium, Strong }

/// <summary>A named way of cleaning up dictated text, e.g. "Casual" for chat or "Formal" for email.</summary>
public sealed class Style
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "New style";
    /// <summary>None means no LLM: only the rule-based cleanup (fillers, dictionary, capitals).</summary>
    public LlmProvider Provider { get; set; } = LlmProvider.Default;
    /// <summary>Only for an override (Ollama, OpenAI, Anthropic); unused with the default model.</summary>
    public string Model { get; set; } = "";
    public Strength Strength { get; set; } = Strength.Light;
    /// <summary>The user's own rules in plain words.</summary>
    public string Instructions { get; set; } = "";

    /// <summary>Meaningful on a resolved style (see TextRewriter.Effective), where Default has been replaced.</summary>
    public bool UsesLlm => Provider is not (LlmProvider.None or LlmProvider.Default) && !string.IsNullOrWhiteSpace(Model);
    public bool IsCloud => Provider is LlmProvider.OpenAI or LlmProvider.Anthropic;

    public Style With(LlmProvider provider, string model) => new()
    {
        Id = Id, Name = Name, Provider = provider, Model = model, Strength = Strength, Instructions = Instructions,
    };
}

public enum RuleKind
{
    /// <summary>Matches the program, by process name ("ms-teams", "olk", "winword").</summary>
    App,
    /// <summary>Matches words in the window title, e.g. a browser tab ("Gmail", "ChatGPT").</summary>
    Title,
}

/// <summary>"Use this style when dictating into that app or website."</summary>
public sealed class StyleRule
{
    public RuleKind Kind { get; set; } = RuleKind.App;
    public string Match { get; set; } = "";
    public string StyleId { get; set; } = "";
}

/// <summary>Where each LLM provider lives. Cloud keys are encrypted for the current Windows user.</summary>
public sealed class LlmSettings
{
    public string OllamaUrl { get; set; } = "http://127.0.0.1:11434";
    /// <summary>
    /// Encrypted for this Windows user on this PC, so they can't be used anywhere else anyway. Kept in keys.json
    /// on this PC (see SettingsStore), never in settings.json, which may live in a synced folder.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore] public string OpenAiKeyProtected { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore] public string AnthropicKeyProtected { get; set; } = "";
    /// <summary>After this long the plain transcript is pasted instead of waiting for the LLM.</summary>
    public double TimeoutSeconds { get; set; } = 6;

    /// <summary>The model every style uses unless it overrides it. One shared local model never needs swapping.</summary>
    public LlmProvider DefaultProvider { get; set; } = LlmProvider.Ollama;
    public string DefaultModel { get; set; } = StyleDefaults.LocalModel;

    /// <summary>
    /// How long Ollama keeps the model in graphics memory after the last use: "30m", "4h", "24h" or "forever".
    /// Longer means no reload delay after a break, at the cost of the memory staying in use.
    /// </summary>
    public string KeepLoaded { get; set; } = "4h";
}

/// <summary>An app or website recently dictated into, offered when adding a rule.</summary>
public sealed class SeenApp
{
    public string ProcessName { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTime LastUsed { get; set; }
}

public static class StyleDefaults
{
    public const string RawId = "raw";
    // The instruct variant answers directly; plain "qwen3:4b" always reasons first and takes ~60 s per rewrite.
    public const string LocalModel = "qwen3:4b-instruct";

    public static List<Style> Styles() =>
    [
        new()
        {
            Id = RawId, Name = "Raw", Provider = LlmProvider.None,
            Instructions = "",
        },
        new()
        {
            Id = "casual", Name = "Casual", Provider = LlmProvider.Default, Strength = Strength.Light,
            Instructions = "This is a chat message to a colleague. Keep it relaxed and short. " +
                           "No greeting or sign-off. A missing full stop at the end is fine.",
        },
        new()
        {
            Id = "formal", Name = "Formal", Provider = LlmProvider.Default, Strength = Strength.Medium,
            Instructions = "This is a business email or document. Write complete, well-formed sentences in a polite, " +
                           "professional tone. Drop spoken filler such as 'so', 'ja dus', 'I think maybe'. Use 'Hi' instead " +
                           "of 'hey' and 'Groeten' instead of 'groetjes'. Phrase requests politely ('Could you…', 'Zou je…'). " +
                           "Split into paragraphs where the topic changes.",
        },
        new()
        {
            Id = "code", Name = "Code", Provider = LlmProvider.Default, Strength = Strength.Light,
            Instructions = "This is developer writing: a code comment, commit message or chat about code. " +
                           "Keep identifiers, file names and technical terms exactly as written. Be concise.",
        },
        new()
        {
            Id = "prompt", Name = "Prompt", Provider = LlmProvider.Default, Strength = Strength.Strong,
            Instructions = "This is a request to an AI assistant. Turn rambling speech into a clear, well-organised " +
                           "request. Use a short bullet list when there are several requirements.",
        },
    ];

    public static List<StyleRule> Rules() =>
    [
        new() { Kind = RuleKind.App, Match = "ms-teams", StyleId = "casual" },
        new() { Kind = RuleKind.App, Match = "slack", StyleId = "casual" },
        new() { Kind = RuleKind.App, Match = "WhatsApp", StyleId = "casual" },
        new() { Kind = RuleKind.App, Match = "olk", StyleId = "formal" },
        new() { Kind = RuleKind.App, Match = "outlook", StyleId = "formal" },
        new() { Kind = RuleKind.App, Match = "winword", StyleId = "formal" },
        new() { Kind = RuleKind.Title, Match = "Gmail", StyleId = "formal" },
        new() { Kind = RuleKind.Title, Match = "Outlook", StyleId = "formal" },
        new() { Kind = RuleKind.App, Match = "code", StyleId = "code" },
        new() { Kind = RuleKind.App, Match = "devenv", StyleId = "code" },
        new() { Kind = RuleKind.Title, Match = "ChatGPT", StyleId = "prompt" },
        new() { Kind = RuleKind.App, Match = "claude", StyleId = "prompt" },
    ];
}
