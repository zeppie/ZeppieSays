namespace ZeppieSays.Core;

/// <summary>A style's model choice inside a profile.</summary>
public sealed class StyleModelChoice
{
    public LlmProvider Provider { get; set; } = LlmProvider.Default;
    public string Model { get; set; } = "";
}

/// <summary>
/// A named set of model choices, e.g. "Local", "Cloud" or "Test": the default model with its keep-loaded time, and
/// the model of every style. Switching profiles swaps all of them at once. The styles' names, strengths, rules and
/// where they are used stay the same in every profile.
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

    /// <summary>Takes the models the settings use right now.</summary>
    public void CaptureFrom(AppSettings settings)
    {
        DefaultProvider = settings.Llm.DefaultProvider;
        DefaultModel = settings.Llm.DefaultModel;
        KeepLoaded = settings.Llm.KeepLoaded;
        Styles = settings.Styles.ToDictionary(s => s.Id, s => new StyleModelChoice { Provider = s.Provider, Model = s.Model });
    }

    /// <summary>
    /// Puts this profile's models into the settings. A style the profile doesn't know yet (added while another
    /// profile was active) uses the default model, so a cloud model chosen elsewhere never slips into this one.
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
    }

    public static ModelProfile From(AppSettings settings, string name)
    {
        var profile = new ModelProfile { Name = name };
        profile.CaptureFrom(settings);
        return profile;
    }
}

public static class ModelProfiles
{
    /// <summary>
    /// Settings from before profiles get one holding the models they already use, so nothing changes for them.
    /// Returns true when it added one.
    /// </summary>
    public static bool EnsureOne(AppSettings settings)
    {
        if (settings.Profiles.Count > 0 && settings.Profiles.Any(p => p.Id == settings.ActiveProfileId)) return false;
        if (settings.Profiles.Count == 0) settings.Profiles.Add(ModelProfile.From(settings, "My models"));
        settings.ActiveProfileId = settings.Profiles[0].Id;
        return true;
    }

    public static ModelProfile? Active(AppSettings settings) =>
        settings.Profiles.FirstOrDefault(p => p.Id == settings.ActiveProfileId);
}
