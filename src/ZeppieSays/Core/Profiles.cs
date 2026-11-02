namespace ZeppieSays.Core;

/// <summary>A style's model choice inside a profile.</summary>
public sealed class StyleModelChoice
{
    public LlmProvider Provider { get; set; } = LlmProvider.Default;
    public string Model { get; set; } = "";
}

/// <summary>A dictation key's speech model and spoken language inside a profile, and for an Ask key who answers.</summary>
public sealed class KeyModelChoice
{
    public string ModelId { get; set; } = "";
    public string Language { get; set; } = "auto";
    public LlmProvider AskProvider { get; set; } = LlmProvider.Default;
    public string AskModel { get; set; } = "";
}

/// <summary>
/// A named set of model choices, e.g. "Local", "Cloud" or "Test": the speech model and spoken language of every
/// dictation key (and who answers an Ask key), the default AI model with its keep-loaded time, and the AI model of every style. Switching
/// profiles swaps all of them at once. The hotkeys, what they type in, and the styles' names, strengths and rules
/// stay the same in every profile.
/// </summary>
public sealed class ModelProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "New profile";
    public LlmProvider DefaultProvider { get; set; } = LlmProvider.Ollama;
    public string DefaultModel { get; set; } = StyleDefaults.LocalModel;
    public string KeepLoaded { get; set; } = "4h";
    /// <summary>By style id.</summary>
    public Dictionary<string, StyleModelChoice> Styles { get; set; } = [];
    /// <summary>By key id. Null in a profile saved before profiles held the keys.</summary>
    public Dictionary<string, KeyModelChoice>? Keys { get; set; }

    /// <summary>Takes the models the settings use right now.</summary>
    public void CaptureFrom(AppSettings settings)
    {
        DefaultProvider = settings.Llm.DefaultProvider;
        DefaultModel = settings.Llm.DefaultModel;
        KeepLoaded = settings.Llm.KeepLoaded;
        Styles = settings.Styles.ToDictionary(s => s.Id, s => new StyleModelChoice { Provider = s.Provider, Model = s.Model });
        Keys = CaptureKeys(settings);
    }

    static Dictionary<string, KeyModelChoice> CaptureKeys(AppSettings settings) =>
        settings.Keys.ToDictionary(k => k.Id, k => new KeyModelChoice
        {
            ModelId = k.ModelId, Language = k.Language, AskProvider = k.AskProvider, AskModel = k.AskModel,
        });

    /// <summary>
    /// Puts this profile's models into the settings. A style the profile doesn't know yet (added while another
    /// profile was active) uses the default model, so a cloud model chosen elsewhere never slips into this one.
    /// A key it doesn't know yet keeps its model, since a key has no default to fall back to.
    /// </summary>
    public void ApplyTo(AppSettings settings)
    {
        settings.Llm.DefaultProvider = DefaultProvider;
        settings.Llm.DefaultModel = DefaultModel;
        settings.Llm.KeepLoaded = KeepLoaded;
        foreach (var style in settings.Styles)
        {
            var choice = Styles.GetValueOrDefault(style.Id);
            // Raw is the "no AI" style in every profile.
            style.Provider = choice?.Provider ?? (style.Id == StyleDefaults.RawId ? LlmProvider.None : LlmProvider.Default);
            style.Model = choice?.Model ?? "";
        }
        foreach (var key in settings.Keys)
        {
            if (Keys?.GetValueOrDefault(key.Id) is not { } choice) continue;
            key.ModelId = choice.ModelId;
            key.Language = choice.Language;
            key.AskProvider = choice.AskProvider;
            key.AskModel = choice.AskModel;
        }
    }

    public static ModelProfile From(AppSettings settings, string name)
    {
        var profile = new ModelProfile { Name = name };
        profile.CaptureFrom(settings);
        return profile;
    }

    /// <summary>A profile from before profiles held the keys takes the keys as they are now, as they were back then.</summary>
    internal bool FillKeys(AppSettings settings)
    {
        if (Keys is not null) return false;
        Keys = CaptureKeys(settings);
        return true;
    }
}

public static class ModelProfiles
{
    /// <summary>
    /// Settings from before profiles get one holding the models they already use, so nothing changes for them; profiles
    /// from before they held the keys get the keys as they are. Returns true when something changed and needs saving.
    /// </summary>
    public static bool EnsureOne(AppSettings settings)
    {
        var changed = false;
        if (settings.Profiles.Count == 0)
        {
            settings.Profiles.Add(ModelProfile.From(settings, "My models"));
            changed = true;
        }
        foreach (var profile in settings.Profiles)
            changed |= profile.FillKeys(settings);
        if (settings.Profiles.All(p => p.Id != settings.ActiveProfileId))
        {
            settings.ActiveProfileId = settings.Profiles[0].Id;
            changed = true;
        }
        return changed;
    }

    public static ModelProfile? Active(AppSettings settings) =>
        settings.Profiles.FirstOrDefault(p => p.Id == settings.ActiveProfileId);

    /// <summary>A removed key or style leaves no choices behind in any profile.</summary>
    public static void Forget(AppSettings settings, string id)
    {
        foreach (var profile in settings.Profiles)
        {
            profile.Styles.Remove(id);
            profile.Keys?.Remove(id);
        }
    }
}
