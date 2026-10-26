using System.IO;
using ZeppieSays.Core;
using ZeppieSays.Services;

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

    [Fact]
    public void Api_keys_are_never_written_to_settings_json()
    {
        var settings = new AppSettings();
        settings.Llm.OpenAiKeyProtected = "protected-openai";
        settings.Llm.AnthropicKeyProtected = "protected-anthropic";
        var json = SettingsStore.Serialize(settings);
        Assert.DoesNotContain("protected-openai", json);
        Assert.DoesNotContain("KeyProtected", json);
    }

    [Fact]
    public void Api_keys_from_an_older_settings_json_are_picked_up_to_be_moved()
    {
        var settings = SettingsStore.Parse("""{ "Llm": { "OpenAiKeyProtected": "old-openai", "AnthropicKeyProtected": "" } }""");
        Assert.Equal("old-openai", settings.Llm.OpenAiKeyProtected);
    }

    [Theory]
    [InlineData("Betabit", "Betabit", 0)]
    [InlineData("  Betabit = beta bit, beta bid  ", "Betabit", 2)]
    [InlineData("userId = user id", "userId", 1)]
    public void Dictionary_lines_parse(string line, string term, int soundsLike)
    {
        var entry = DictionaryEntry.Parse(line)!;
        Assert.Equal(term, entry.Term);
        Assert.Equal(soundsLike, entry.SoundsLike.Count);
        Assert.Equal(entry.ToString(), DictionaryEntry.Parse(entry.ToString())!.ToString()); // round trip
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("# a comment")]
    [InlineData("= no term")]
    public void Blank_lines_and_comments_are_not_words(string line) => Assert.Null(DictionaryEntry.Parse(line));

    [Fact]
    public void A_dictionary_file_is_read_with_its_name_as_source()
    {
        var path = Path.Combine(Path.GetTempPath(), $"zs-dict-{Guid.NewGuid():N}.txt");
        try
        {
            DictionaryFiles.Write(path, [new DictionaryEntry { Term = "Betabit", SoundsLike = ["beta bit"] }], "Team words\nsecond line");
            File.AppendAllLines(path, ["", "# later comment", "ZeppieSays"]);
            var words = DictionaryFiles.Read(path);
            Assert.Equal(["Betabit", "ZeppieSays"], words.Select(w => w.Term));
            Assert.All(words, w => Assert.Equal(Path.GetFileNameWithoutExtension(path), w.Source));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Words_from_dictionary_files_fix_spelling_too()
    {
        var settings = new AppSettings { FileWords = [new DictionaryEntry { Term = "Betabit", SoundsLike = ["beta bit"] }] };
        Assert.Equal("I work at Betabit.", TextShaping.Clean("I work at beta bit.", settings, ScreenContext.Empty));
    }

    static string TempFile(params string[] lines)
    {
        var path = Path.Combine(Path.GetTempPath(), $"zs-dict-{Guid.NewGuid():N}.txt");
        File.WriteAllLines(path, lines);
        return path;
    }

    [Fact]
    public void Adding_a_word_keeps_the_rest_of_the_file()
    {
        var path = TempFile("# Team words", "# keep this comment", "", "Betabit", "DELA");
        try
        {
            DictionaryFiles.AddWordTo(path, new DictionaryEntry { Term = "Zeppie" });
            Assert.Equal(["# Team words", "# keep this comment", "", "Betabit", "DELA", "Zeppie"], File.ReadAllLines(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Adding_a_word_again_replaces_its_line_instead_of_duplicating_it()
    {
        var path = TempFile("# words", "betabit", "DELA", "Betabit = beta bid");
        try
        {
            DictionaryFiles.AddWordTo(path, DictionaryEntry.Parse("Betabit = beta bit, beta bid")!);
            Assert.Equal(["# words", "Betabit = beta bit, beta bid", "DELA"], File.ReadAllLines(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Removing_a_word_removes_only_its_lines()
    {
        var path = TempFile("# words", "Betabit = beta bit", "DELA", "# DELA in a comment stays");
        try
        {
            DictionaryFiles.RemoveWordFrom(path, "betabit");
            Assert.Equal(["# words", "DELA", "# DELA in a comment stays"], File.ReadAllLines(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Words_from_older_settings_move_into_the_default_file_once()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"zs-settings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var settings = new AppSettings
            {
                Dictionary = [new DictionaryEntry { Term = "Mikki", SoundsLike = ["Micky"] }, new DictionaryEntry { Term = "DELA" }],
                DictionaryFiles = [@"Z:\Team\words.txt"],
            };
            DictionaryFiles.EnsureDefault(settings, folder);
            DictionaryFiles.EnsureDefault(settings, folder); // a second start changes nothing

            var path = Path.Combine(folder, DictionaryFiles.DefaultFileName);
            Assert.Equal(["Mikki", "DELA"], DictionaryFiles.Read(path).Select(w => w.Term));
            Assert.Equal(["Micky"], DictionaryFiles.Read(path)[0].SoundsLike);
            Assert.Empty(settings.Dictionary);
            Assert.Equal([DictionaryFiles.DefaultFileName, @"Z:\Team\words.txt"], settings.DictionaryFiles);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void Dictionary_files_in_the_settings_folder_are_stored_relative_to_it()
    {
        var inside = Path.Combine(AppPaths.Settings, "Team", "words.txt");
        Assert.Equal(Path.Combine("Team", "words.txt"), DictionaryFiles.ToStored(inside));
        Assert.Equal(Path.GetFullPath(inside), DictionaryFiles.ToFull(DictionaryFiles.ToStored(inside)));
        var outside = @"Z:\Shared\Team words.txt";
        Assert.Equal(outside, DictionaryFiles.ToStored(outside));
    }
}
