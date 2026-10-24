using ZeppieSays.Core;

namespace ZeppieSays.Tests;

public class SettingsTests
{
    [Fact]
    public void Two_fixed_keys_from_older_settings_become_the_key_list()
    {
        var settings = SettingsStore.Parse("""
            {
              "Primary": { "Key": "Ctrl+Win", "ModelId": "parakeet-tdt-0.6b-v3", "Language": "auto" },
              "Secondary": { "Key": "Ctrl+Shift", "ModelId": "parakeet-tdt-0.6b-v2", "Language": "en" }
            }
            """);
        Assert.Equal(["Ctrl+Win", "Ctrl+Shift"], settings.Keys.Select(k => k.Key));
        Assert.Equal("parakeet-tdt-0.6b-v2", settings.Keys[1].ModelId);
        Assert.All(settings.Keys, k => Assert.False(k.Translates));
        Assert.DoesNotContain("Primary", SettingsStore.Serialize(settings));
    }

    [Fact]
    public void Any_number_of_keys_round_trip()
    {
        var settings = new AppSettings();
        settings.Keys.Add(new KeyBinding { Key = "F10", ModelId = "whisper-small", Language = "nl", OutputLanguage = "en" });
        var again = SettingsStore.Parse(SettingsStore.Serialize(settings));
        Assert.Equal(3, again.Keys.Count);
        Assert.True(again.Keys[2].Translates);
        Assert.Equal("en", again.Keys[2].OutputLanguage);
    }

    [Fact]
    public void There_is_always_at_least_one_key()
    {
        Assert.Single(SettingsStore.Parse("""{ "Keys": [] }""").Keys);
    }
}
