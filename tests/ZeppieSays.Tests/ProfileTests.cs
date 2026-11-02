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
    public void Applying_a_profile_swaps_each_keys_model_and_language_but_not_the_hotkey()
    {
        var settings = new AppSettings();
        var key = settings.Keys[0];
        var local = ModelProfile.From(settings, "Local");
        key.ModelId = "elevenlabs-scribe-v2";
        key.Language = "auto";
        key.OutputLanguage = "en";
        var cloud = ModelProfile.From(settings, "Cloud");

        local.ApplyTo(settings);
        Assert.Equal("whisper-large-v3-turbo", key.ModelId);
        Assert.Equal("nl", key.Language);
        Assert.Equal("F8", key.Key);
        Assert.Equal("en", key.OutputLanguage); // what a key types in is the same in every profile

        cloud.ApplyTo(settings);
        Assert.Equal("elevenlabs-scribe-v2", key.ModelId);
    }

    [Fact]
    public void A_profile_from_before_it_held_the_keys_takes_them_as_they_are()
    {
        var settings = SettingsStore.Parse("""
            { "Profiles": [ { "Id": "p1", "Name": "My models", "Styles": {} } ], "ActiveProfileId": "p1" }
            """);
        Assert.Null(settings.Profiles[0].Keys);

        Assert.True(ModelProfiles.EnsureOne(settings));
        var keys = settings.Profiles[0].Keys!;
        Assert.Equal(settings.Keys.Select(k => k.Id), keys.Keys);
        Assert.Equal("parakeet-tdt-0.6b-v2", keys[settings.Keys[1].Id].ModelId);
        Assert.False(ModelProfiles.EnsureOne(settings));
    }

    [Fact]
    public void A_removed_key_leaves_nothing_behind_in_the_profiles()
    {
        var settings = new AppSettings();
        ModelProfiles.EnsureOne(settings);
        var removed = settings.Keys[1].Id;
        ModelProfiles.Forget(settings, removed);
        Assert.DoesNotContain(removed, settings.Profiles[0].Keys!.Keys);
    }

    [Fact]
    public void Profiles_are_saved_with_the_settings()
    {
        var settings = new AppSettings();
        ModelProfiles.EnsureOne(settings);
        var read = SettingsStore.Parse(SettingsStore.Serialize(settings));
        Assert.Equal(settings.ActiveProfileId, read.ActiveProfileId);
        Assert.Equal(settings.Profiles[0].Styles.Keys, read.Profiles[0].Styles.Keys);
        Assert.Equal(settings.Keys.Select(k => k.Id), read.Keys.Select(k => k.Id)); // the ids the profiles refer to
        Assert.Equal(settings.Profiles[0].Keys!.Keys, read.Profiles[0].Keys!.Keys);
    }
}
