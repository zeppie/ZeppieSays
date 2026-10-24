using System.Collections.ObjectModel;
using ZeppieSays.Core;
using ZeppieSays.Llm;
using ZeppieSays.Services;

namespace ZeppieSays.UI;

/// <summary>The Styles tab: the styles themselves, which app or website uses which style, and the AI providers.</summary>
public sealed class StylesViewModel : ObservableObject
{
    readonly AppSettings _settings;
    readonly TextRewriter _rewriter;
    readonly Action _save;

    public ObservableCollection<StyleViewModel> Styles { get; } = [];
    public ObservableCollection<RuleViewModel> Rules { get; } = [];
    public ObservableCollection<string> OllamaModels { get; } = [];
    /// <summary>The OpenAI models the saved API key can use, or a few well-known ones until it's known.</summary>
    public ObservableCollection<string> OpenAiModels { get; } = [.. OpenAiChat.KnownModels];
    /// <summary>The Claude models the saved API key can use, or the current ones until it's known.</summary>
    public ObservableCollection<string> AnthropicModels { get; } = [.. AnthropicChat.KnownModels];

    public static IReadOnlyList<Choice> Providers { get; } =
    [
        new(nameof(LlmProvider.None), "No AI (rules only)"),
        new(nameof(LlmProvider.Default), "Default model"),
        new(nameof(LlmProvider.Ollama), "Other: Ollama (on this PC)"),
        new(nameof(LlmProvider.OpenAI), "Other: OpenAI (cloud)"),
        new(nameof(LlmProvider.Anthropic), "Other: Claude (cloud)"),
    ];

    public static IReadOnlyList<Choice> DefaultProviders { get; } =
    [
        new(nameof(LlmProvider.Ollama), "Ollama (on this PC)"),
        new(nameof(LlmProvider.OpenAI), "OpenAI (cloud)"),
        new(nameof(LlmProvider.Anthropic), "Claude (cloud)"),
    ];

    public static IReadOnlyList<Choice> KeepLoadedChoices { get; } =
    [
        new("30m", "30 minutes after last use"),
        new("4h", "4 hours after last use"),
        new("24h", "All day (24 hours)"),
        new("forever", "Always (until Ollama stops)"),
    ];

    public static IReadOnlyList<Choice> Strengths { get; } =
    [
        new(nameof(Strength.Light), "Light: fix, keep my words"),
        new(nameof(Strength.Medium), "Medium: smooth the tone"),
        new(nameof(Strength.Strong), "Strong: may restructure"),
    ];

    public static IReadOnlyList<Choice> RuleKinds { get; } =
    [
        new(nameof(RuleKind.App), "App"),
        new(nameof(RuleKind.Title), "Website or window title"),
    ];

    public RelayCommand AddStyle { get; }
    public RelayCommand DeleteStyle { get; }
    public RelayCommand AddRule { get; }
    public RelayCommand RemoveRule { get; }
    public RelayCommand TryStyle { get; }
    public RelayCommand RefreshOllama { get; }

    public StylesViewModel(AppSettings settings, TextRewriter rewriter, Action save)
    {
        _settings = settings;
        _rewriter = rewriter;
        _save = save;

        // An OpenAI style saved without a model (an earlier version left it empty) would never rewrite anything.
        var repaired = false;
        foreach (var style in settings.Styles.Where(s => s.Provider == LlmProvider.OpenAI && string.IsNullOrWhiteSpace(s.Model)))
        {
            style.Model = OpenAiChat.SuggestedModel;
            repaired = true;
        }
        if (settings.Llm.DefaultProvider == LlmProvider.OpenAI && string.IsNullOrWhiteSpace(settings.Llm.DefaultModel))
        {
            settings.Llm.DefaultModel = OpenAiChat.SuggestedModel;
            repaired = true;
        }
        if (repaired) save();
        foreach (var style in settings.Styles) Styles.Add(new StyleViewModel(style, this));
        foreach (var rule in settings.StyleRules) Rules.Add(new RuleViewModel(rule, this));
        _selected = Styles.FirstOrDefault(s => s.Model.Id != StyleDefaults.RawId) ?? Styles.FirstOrDefault();

        AddStyle = new RelayCommand(_ =>
        {
            var style = new Style { Name = "New style", Provider = LlmProvider.Default };
            _settings.Styles.Add(style);
            var vm = new StyleViewModel(style, this);
            Styles.Add(vm);
            Selected = vm;
            Save();
        });
        DeleteStyle = new RelayCommand(_ =>
        {
            if (Selected is not { CanDelete: true } doomed) return;
            _settings.Styles.Remove(doomed.Model);
            Styles.Remove(doomed);
            // Rules that pointed at it fall back to the default style.
            foreach (var rule in Rules.Where(r => r.StyleId == doomed.Model.Id).ToList()) RemoveRuleFor(rule);
            if (_settings.DefaultStyleId == doomed.Model.Id) DefaultStyleId = StyleDefaults.RawId;
            Selected = Styles.FirstOrDefault();
            Save();
        });
        AddRule = new RelayCommand(_ => AddNewRule());
        RemoveRule = new RelayCommand(p => { if (p is RuleViewModel rule) RemoveRuleFor(rule); });
        TryStyle = new RelayCommand(_ => _ = TryAsync());
        RefreshOllama = new RelayCommand(_ => _ = LoadOllamaModelsAsync());
        _ = LoadOllamaModelsAsync();
        _ = LoadOpenAiModelsAsync();
        _ = LoadAnthropicModelsAsync();
    }

    internal void Save() => _save();

    public StyleViewModel? Selected
    {
        get => _selected;
        set { if (Set(ref _selected, value)) { TryResult = null; Raise(nameof(CanDeleteSelected)); } }
    }
    StyleViewModel? _selected;

    public bool CanDeleteSelected => Selected?.CanDelete == true;

    public string DefaultStyleId
    {
        get => _settings.DefaultStyleId;
        set { if (value is null) return; _settings.DefaultStyleId = value; Save(); Raise(); }
    }

    // ---- Ollama ---------------------------------------------------------------------------------

    public string OllamaUrl
    {
        get => _settings.Llm.OllamaUrl;
        set { _settings.Llm.OllamaUrl = value.Trim(); Save(); Raise(); _ = LoadOllamaModelsAsync(); }
    }

    public string OllamaStatus
    {
        get => _ollamaStatus;
        private set => Set(ref _ollamaStatus, value);
    }
    string _ollamaStatus = "Looking for Ollama…";

    async Task LoadOllamaModelsAsync()
    {
        var models = await OllamaChat.ListModelsAsync(_settings.Llm.OllamaUrl);
        OllamaModels.Clear();
        foreach (var m in models) OllamaModels.Add(m);
        ShowModelNamesAgain();
        OllamaStatus = models.Count switch
        {
            0 => $"Ollama isn't running or has no models. Install it from ollama.com and run: ollama pull {StyleDefaults.LocalModel}",
            1 => "1 model available.",
            _ => $"{models.Count} models available.",
        };
    }

    // ---- OpenAI ---------------------------------------------------------------------------------

    /// <summary>Replaces the well-known models with the ones the saved key can actually use.</summary>
    async Task LoadOpenAiModelsAsync()
    {
        if (SecretStore.Unprotect(_settings.Llm.OpenAiKeyProtected) is not { Length: > 0 } key) return;
        var models = await OpenAiChat.ListModelsAsync(key);
        if (models.Count == 0) return; // keep the well-known ones
        OpenAiModels.Clear();
        foreach (var m in models) OpenAiModels.Add(m);
        ShowModelNamesAgain();
    }

    /// <summary>Replaces the current Claude models with the ones the saved key can actually use.</summary>
    async Task LoadAnthropicModelsAsync()
    {
        if (SecretStore.Unprotect(_settings.Llm.AnthropicKeyProtected) is not { Length: > 0 } key) return;
        var models = await AnthropicChat.ListModelsAsync(key);
        if (models.Count == 0) return; // keep the current ones
        AnthropicModels.Clear();
        foreach (var m in models) AnthropicModels.Add(m);
        ShowModelNamesAgain();
    }

    /// <summary>An editable combo box blanks its text when its list is cleared; put the saved model names back.</summary>
    void ShowModelNamesAgain()
    {
        Raise(nameof(DefaultModel));
        foreach (var style in Styles) style.Refresh();
    }

    /// <summary>The model a style (or the default) starts with when OpenAI is chosen.</summary>
    internal string SuggestedOpenAiModel =>
        OpenAiModels.Contains(OpenAiChat.SuggestedModel) ? OpenAiChat.SuggestedModel
        : OpenAiModels.FirstOrDefault(m => m.Contains("mini")) ?? OpenAiModels.FirstOrDefault() ?? OpenAiChat.SuggestedModel;

    // ---- Default model --------------------------------------------------------------------------

    public string DefaultProvider
    {
        get => _settings.Llm.DefaultProvider.ToString();
        set
        {
            if (value is null || !Enum.TryParse<LlmProvider>(value, out var provider) || provider == _settings.Llm.DefaultProvider) return;
            _settings.Llm.DefaultProvider = provider;
            _settings.Llm.DefaultModel = provider switch
            {
                LlmProvider.Ollama => OllamaModels.FirstOrDefault() ?? StyleDefaults.LocalModel,
                LlmProvider.OpenAI => SuggestedOpenAiModel,
                LlmProvider.Anthropic => "claude-opus-5-5",
                _ => "",
            };
            DefaultChanged();
            Raise(nameof(DefaultModelSuggestions));
            Raise(nameof(DefaultIsLocal));
        }
    }

    public string DefaultModel
    {
        get => _settings.Llm.DefaultModel;
        set
        {
            var model = value?.Trim() ?? "";
            if (model == _settings.Llm.DefaultModel) return;
            _settings.Llm.DefaultModel = model;
            DefaultChanged();
        }
    }

    public IEnumerable<string> DefaultModelSuggestions => ModelSuggestionsFor(_settings.Llm.DefaultProvider);

    /// <summary>"Keep loaded" only means something for a model on this PC.</summary>
    public bool DefaultIsLocal => _settings.Llm.DefaultProvider == LlmProvider.Ollama;

    public string KeepLoaded
    {
        get => _settings.Llm.KeepLoaded;
        set
        {
            if (value is null || value == _settings.Llm.KeepLoaded) return;
            _settings.Llm.KeepLoaded = value;
            Save();
            Raise();
            _rewriter.WarmUpDefault(); // re-sends the model with the new keep-loaded time
        }
    }

    /// <summary>Short description for styles that use the default, e.g. "qwen3:8b on this PC".</summary>
    public string DefaultModelLabel => _settings.Llm.DefaultProvider switch
    {
        LlmProvider.Ollama => $"{_settings.Llm.DefaultModel} on this PC",
        LlmProvider.OpenAI => $"{_settings.Llm.DefaultModel}, OpenAI cloud",
        LlmProvider.Anthropic => $"{_settings.Llm.DefaultModel}, Claude cloud",
        _ => _settings.Llm.DefaultModel,
    };

    void DefaultChanged()
    {
        Save();
        Raise(nameof(DefaultProvider));
        Raise(nameof(DefaultModel));
        Raise(nameof(DefaultModelLabel));
        foreach (var style in Styles) style.Refresh();
        _rewriter.WarmUpDefault(); // load the new model now, not on the next dictation
    }

    internal IEnumerable<string> ModelSuggestionsFor(LlmProvider provider) => provider switch
    {
        LlmProvider.Ollama => OllamaModels,
        LlmProvider.OpenAI => OpenAiModels,
        LlmProvider.Anthropic => AnthropicModels,
        _ => [],
    };

    internal Style Effective(Style style) => _rewriter.Effective(style);

    // ---- Cloud keys -----------------------------------------------------------------------------

    public bool HasOpenAiKey => _settings.Llm.OpenAiKeyProtected.Length > 0;
    public bool HasAnthropicKey => _settings.Llm.AnthropicKeyProtected.Length > 0;

    public void SetOpenAiKey(string key)
    {
        _settings.Llm.OpenAiKeyProtected = SecretStore.Protect(key.Trim());
        Save();
        Raise(nameof(HasOpenAiKey));
        foreach (var style in Styles) style.Refresh();
        _ = LoadOpenAiModelsAsync(); // the new key may unlock other models
    }

    public void SetAnthropicKey(string key)
    {
        _settings.Llm.AnthropicKeyProtected = SecretStore.Protect(key.Trim());
        Save();
        Raise(nameof(HasAnthropicKey));
        foreach (var style in Styles) style.Refresh();
        _ = LoadAnthropicModelsAsync(); // the new key may unlock other models
    }

    // ---- Rules ----------------------------------------------------------------------------------

    public string NewRuleKind
    {
        get => _newRuleKind;
        set { if (value is not null && Set(ref _newRuleKind, value)) Raise(nameof(MatchSuggestions)); }
    }
    string _newRuleKind = nameof(RuleKind.App);

    public string NewRuleMatch
    {
        get => _newRuleMatch;
        set => Set(ref _newRuleMatch, value ?? "");
    }
    string _newRuleMatch = "";

    public string NewRuleStyleId
    {
        get => _newRuleStyleId;
        set => Set(ref _newRuleStyleId, value ?? "");
    }
    string _newRuleStyleId = "formal";

    /// <summary>Apps (or window titles) recently dictated into, so you don't have to know process names.</summary>
    public IReadOnlyList<string> MatchSuggestions =>
        NewRuleKind == nameof(RuleKind.App)
            ? _settings.RecentApps.Select(a => a.ProcessName).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : _settings.RecentApps.Select(a => a.Title).Where(t => t.Length > 0).Distinct().ToList();

    void AddNewRule()
    {
        if (string.IsNullOrWhiteSpace(NewRuleMatch) || Styles.All(s => s.Model.Id != NewRuleStyleId)) return;
        var rule = new StyleRule
        {
            Kind = Enum.Parse<RuleKind>(NewRuleKind),
            Match = NewRuleMatch.Trim(),
            StyleId = NewRuleStyleId,
        };
        _settings.StyleRules.Insert(0, rule); // newest first, so it wins over an older rule for the same app
        Rules.Insert(0, new RuleViewModel(rule, this));
        NewRuleMatch = "";
        Save();
    }

    void RemoveRuleFor(RuleViewModel rule)
    {
        _settings.StyleRules.Remove(rule.Model);
        Rules.Remove(rule);
        Save();
    }

    /// <summary>Called when the tab opens, so recently used apps show up in the suggestions.</summary>
    public void Refresh()
    {
        Raise(nameof(MatchSuggestions));
        _ = LoadOllamaModelsAsync();
    }

    // ---- Try it ---------------------------------------------------------------------------------

    public string TryText
    {
        get => _tryText;
        set => Set(ref _tryText, value);
    }
    string _tryText = "uh so I think we should maybe move the meeting to Thursday because uh Anna can't make it on Wednesday";

    /// <summary>"same", or a language to translate the Try text into, as a key with that output language would.</summary>
    public string TryOutputLanguage
    {
        get => _tryOutputLanguage;
        set => Set(ref _tryOutputLanguage, value ?? KeyBinding.SameLanguage);
    }
    string _tryOutputLanguage = KeyBinding.SameLanguage;

    public static IReadOnlyList<Choice> OutputLanguages { get; } =
        ModelCatalog.Languages.Where(l => l.Code != "auto").Select(l => new Choice(l.Code, l.Name))
            .Prepend(new Choice(KeyBinding.SameLanguage, "As spoken")).ToList();

    public string? TryResult
    {
        get => _tryResult;
        private set => Set(ref _tryResult, value);
    }
    string? _tryResult;

    public bool IsTrying
    {
        get => _isTrying;
        private set => Set(ref _isTrying, value);
    }
    bool _isTrying;

    async Task TryAsync()
    {
        if (Selected is not { } style || IsTrying) return;
        IsTrying = true;
        TryResult = "Writing…";
        try
        {
            var context = ScreenContext.Empty with { ProcessName = "test", Title = "" };
            var cleaned = TextShaping.Clean(TryText, _settings, context).Trim();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var key = new KeyBinding { OutputLanguage = TryOutputLanguage };
            var target = key.Translates ? key.OutputLanguage : null;
            // Same as dictating: a key that translates gets an LLM even when this style has none.
            var effective = _rewriter.Effective(style.Model);
            if (target is not null && !effective.UsesLlm)
                effective = _rewriter.ResolveStyle(context with { ProcessName = "" }, key);
            var result = await _rewriter.RewriteAsync(cleaned, effective, context, "auto", CancellationToken.None, target);
            TryResult = result.Rewritten
                ? $"{result.Text}\n\n({watch.Elapsed.TotalSeconds:0.0} s with {effective.Model})"
                : effective.UsesLlm
                    ? $"{result.Text}\n\n(Not rewritten: {result.FallbackReason}. This is what would be pasted.)"
                    : $"{result.Text}\n\n(No AI for this style: only fillers, dictionary and capitals.)";
        }
        finally
        {
            IsTrying = false;
        }
    }
}

public sealed class StyleViewModel(Style style, StylesViewModel owner) : ObservableObject
{
    public Style Model { get; } = style;

    public bool CanDelete => Model.Id != StyleDefaults.RawId;

    public string Name
    {
        get => Model.Name;
        set { Model.Name = string.IsNullOrWhiteSpace(value) ? "Unnamed" : value.Trim(); owner.Save(); Raise(); }
    }

    public string Provider
    {
        get => Model.Provider.ToString();
        set
        {
            if (value is null || !Enum.TryParse<LlmProvider>(value, out var provider) || provider == Model.Provider) return;
            Model.Provider = provider;
            // Start an override from a sensible model for its provider; the user can type another.
            Model.Model = provider switch
            {
                LlmProvider.Ollama => owner.OllamaModels.FirstOrDefault() ?? StyleDefaults.LocalModel,
                LlmProvider.OpenAI => owner.SuggestedOpenAiModel,
                LlmProvider.Anthropic => "claude-opus-5-5",
                _ => "",
            };
            owner.Save();
            Raise();
            Raise(nameof(ModelName));
            Refresh();
        }
    }

    public string ModelName
    {
        get => Model.Model;
        set { Model.Model = value?.Trim() ?? ""; owner.Save(); Raise(); }
    }

    public string Strength
    {
        get => Model.Strength.ToString();
        set { if (value is not null && Enum.TryParse<Strength>(value, out var s)) { Model.Strength = s; owner.Save(); Raise(); } }
    }

    public string Instructions
    {
        get => Model.Instructions;
        set { Model.Instructions = value ?? ""; owner.Save(); Raise(); }
    }

    /// <summary>Any AI at all: strength and rules apply.</summary>
    public bool UsesAi => Model.Provider != LlmProvider.None;

    /// <summary>An override with its own model, instead of the default.</summary>
    public bool UsesOwnModel => Model.Provider is LlmProvider.Ollama or LlmProvider.OpenAI or LlmProvider.Anthropic;

    public bool UsesDefault => Model.Provider == LlmProvider.Default;

    public string DefaultNotice => $"Uses the default model ({owner.DefaultModelLabel}). You can change it under AI providers below.";

    /// <summary>Cloud is about where the text actually goes, so a style on a cloud default counts too.</summary>
    public bool IsCloud => owner.Effective(Model).IsCloud;

    public IEnumerable<string> ModelSuggestions => owner.ModelSuggestionsFor(Model.Provider);

    public void Refresh()
    {
        Raise(nameof(ModelName));
        Raise(nameof(UsesAi));
        Raise(nameof(UsesOwnModel));
        Raise(nameof(UsesDefault));
        Raise(nameof(DefaultNotice));
        Raise(nameof(IsCloud));
        Raise(nameof(ModelSuggestions));
        Raise(nameof(CloudNotice));
    }

    public string CloudNotice => owner.Effective(Model).Provider switch
    {
        LlmProvider.OpenAI => owner.HasOpenAiKey
            ? "Text dictated with this style is sent to OpenAI."
            : "Text dictated with this style is sent to OpenAI. Add your API key under AI providers below.",
        LlmProvider.Anthropic => owner.HasAnthropicKey
            ? "Text dictated with this style is sent to Anthropic."
            : "Text dictated with this style is sent to Anthropic. Add your API key under AI providers below.",
        _ => "",
    };
}

public sealed class RuleViewModel(StyleRule rule, StylesViewModel owner) : ObservableObject
{
    public StyleRule Model { get; } = rule;

    public string Label => Model.Kind == RuleKind.App ? $"App: {Model.Match}" : $"Window title contains \"{Model.Match}\"";

    public string StyleId
    {
        get => Model.StyleId;
        set { if (value is null) return; Model.StyleId = value; owner.Save(); Raise(); }
    }
}
