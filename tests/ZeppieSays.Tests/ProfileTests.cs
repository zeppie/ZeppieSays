using ZeppieSays.Core;
using ZeppieSays.Services;

namespace ZeppieSays.Tests;

public class ProfileTests
{
    [Fact]
    public void Settings_from_before_profiles_get_one_with_the_models_they_use()
    {
        var settings = new AppSettings();
        settings.Llm.DefaultModel = "qwen3:8b";
        settings.Styles.Single(s => s.Id == "formal").Provider = LlmProvider.Anthropic;

        Assert.True(ModelProfiles.EnsureOne(settings));
        var profile = Assert.Single(settings.Profiles);
        Assert.Equal(profile.Id, settings.ActiveProfileId);
        Assert.Equal("qwen3:8b", profile.DefaultModel);
        Assert.Equal(LlmProvider.Anthropic, profile.Styles["formal"].Provider);
        Assert.False(ModelProfiles.EnsureOne(settings));
    }

    [Fact]
    public void Applying_a_profile_swaps_every_model_and_keeps_the_rest_of_the_style()
    {
        var settings = new AppSettings();
        var local = ModelProfile.From(settings, "Local");
        var formal = settings.Styles.Single(s => s.Id == "formal");
        settings.Llm.DefaultProvider = LlmProvider.OpenAI;
        settings.Llm.DefaultModel = "gpt-5-mini";
        formal.Provider = LlmProvider.Anthropic;
        formal.Model = "claude-opus-5-5";
        formal.Instructions = "Be brief.";
        var cloud = ModelProfile.From(settings, "Cloud");

        local.ApplyTo(settings);
        Assert.Equal(LlmProvider.Ollama, settings.Llm.DefaultProvider);
        Assert.Equal(LlmProvider.Default, formal.Provider);
        Assert.Equal("Be brief.", formal.Instructions);

        cloud.ApplyTo(settings);
        Assert.Equal("gpt-5-mini", settings.Llm.DefaultModel);
        Assert.Equal("claude-opus-5-5", formal.Model);
    }

    [Fact]
    public void A_style_the_profile_does_not_know_uses_the_default_model()
    {
        var settings = new AppSettings();
        var profile = ModelProfile.From(settings, "Local");
        settings.Styles.Add(new Style { Id = "new", Provider = LlmProvider.OpenAI, Model = "gpt-5" });

        profile.ApplyTo(settings);
        var added = settings.Styles.Single(s => s.Id == "new");
        Assert.Equal(LlmProvider.Default, added.Provider);
        Assert.Equal("", added.Model);
        Assert.Equal(LlmProvider.None, settings.Styles.Single(s => s.Id == StyleDefaults.RawId).Provider);
    }

    [Fact]
    public void Profiles_are_saved_with_the_settings()
    {
        var settings = new AppSettings();
        ModelProfiles.EnsureOne(settings);
        var read = SettingsStore.Parse(SettingsStore.Serialize(settings));
        Assert.Equal(settings.ActiveProfileId, read.ActiveProfileId);
        Assert.Equal(settings.Profiles[0].Styles.Keys, read.Profiles[0].Styles.Keys);
    }
}
