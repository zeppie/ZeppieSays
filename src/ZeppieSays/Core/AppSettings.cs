using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZeppieSays.Core;

/// <summary>One dictation key: which key starts it, which model transcribes, in which language, and what language comes out.</summary>
public sealed class KeyBinding
{
    /// <summary>Output language that means "as spoken": no translation.</summary>
    public const string SameLanguage = "same";

    /// <summary>Stays the same when the hotkey changes, so a profile knows which key its choices are for.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    public string Key { get; set; } = "F8";
    public string ModelId { get; set; } = "";
    /// <summary>ISO language code ("nl", "en", ...) or "auto". Ignored by models that detect language themselves.</summary>
    public string Language { get; set; } = "auto";
    /// <summary>The language the text is typed in: <see cref="SameLanguage"/>, or an ISO code to translate into.</summary>
    public string OutputLanguage { get; set; } = SameLanguage;

    public bool Translates => OutputLanguage is { Length: > 0 } and not SameLanguage;
}

public sealed class DictionaryEntry
{
    /// <summary>The exact spelling that should appear in the text.</summary>
    public string Term { get; set; } = "";
    /// <summary>Alternative spellings the model tends to produce, replaced by <see cref="Term"/>.</summary>
    public List<string> SoundsLike { get; set; } = [];

    /// <summary>The dictionary file the word comes from; null for the user's own words in settings.json.</summary>
    [JsonIgnore] public string? Source { get; set; }

    /// <summary>"Betabit" or "Betabit = beta bit, beta bid"; null for an empty line or a "# comment".</summary>
    public static DictionaryEntry? Parse(string line)
    {
        line = line.Trim();
        if (line.Length == 0 || line.StartsWith('#')) return null;
        var parts = line.Split('=', 2);
        var term = parts[0].Trim();
        if (term.Length == 0) return null;
        return new DictionaryEntry
        {
            Term = term,
            SoundsLike = parts.Length > 1
                ? parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
                : [],
        };
    }

    /// <summary>The line <see cref="Parse"/> reads back.</summary>
    public override string ToString() => SoundsLike.Count > 0 ? $"{Term} = {string.Join(", ", SoundsLike)}" : Term;
}

public sealed class AppSettings
{
    /// <summary>The dictation keys, in order ("Key 1", "Key 2", …). At least one.</summary>
    public List<KeyBinding> Keys { get; set; } =
    [
        new() { Key = "F8", ModelId = "whisper-large-v3-turbo", Language = "nl" },
        new() { Key = "F9", ModelId = "parakeet-tdt-0.6b-v2", Language = "en" },
    ];
    /// <summary>Settings from before any number of keys was possible; moved into <see cref="Keys"/> on load.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public KeyBinding? Primary { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public KeyBinding? Secondary { get; set; }
    public bool UseScreenContext { get; set; } = true;
    public bool RemoveFillerWords { get; set; } = true;
    public bool UseGpu { get; set; } = true;
    /// <summary>Shows the latest dictations on the Dictation page. Off until the user turns it on; never written to disk.</summary>
    public bool KeepHistory { get; set; }
    public bool StartWithWindows { get; set; }
    /// <summary>NAudio WaveIn device number; -1 is the Windows default microphone.</summary>
    public int MicrophoneDevice { get; set; } = -1;
    /// <summary>The user's own words, edited on the Dictionary page.</summary>
    public List<DictionaryEntry> Dictionary { get; set; } = [];
    /// <summary>
    /// Text files with more words (one per line). A file inside the settings folder is stored relative to it,
    /// so the same settings work on another PC where that folder is somewhere else.
    /// </summary>
    public List<string> DictionaryFiles { get; set; } = [];
    /// <summary>The words read from <see cref="DictionaryFiles"/>; filled at runtime, never saved.</summary>
    [JsonIgnore] public List<DictionaryEntry> FileWords { get; set; } = [];
    /// <summary>Everything the transcription and cleanup should know: own words first, then the files.</summary>
    [JsonIgnore] public IEnumerable<DictionaryEntry> AllWords => Dictionary.Concat(FileWords);
    public List<Style> Styles { get; set; } = StyleDefaults.Styles();
    /// <summary>Checked top to bottom; the first match decides the style. Website (title) rules win over app rules.</summary>
    public List<StyleRule> StyleRules { get; set; } = StyleDefaults.Rules();
    /// <summary>The style for every app no rule matches.</summary>
    public string DefaultStyleId { get; set; } = StyleDefaults.RawId;
    public LlmSettings Llm { get; set; } = new();
    /// <summary>Saved sets of model choices to switch between; the active one follows every change to the models.</summary>
    public List<ModelProfile> Profiles { get; set; } = [];
    public string ActiveProfileId { get; set; } = "";
    public List<SeenApp> RecentApps { get; set; } = [];
    /// <summary>Whisper ggml files the user added by hand, outside the built-in catalog.</summary>
    public List<CustomModel> CustomModels { get; set; } = [];
    public NotetakerSettings Notetaker { get; set; } = new();
}

/// <summary>Transcribing audio files: every audio file put in <see cref="Folder"/> is transcribed and moved to Done.</summary>
public sealed class NotetakerSettings
{
    public string Folder { get; set; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Zeppie Says", "Notes");
    public string ModelId { get; set; } = "whisper-large-v3-turbo";
    public string Language { get; set; } = "auto";
    /// <summary>"Speaker 1:", "Speaker 2:" per paragraph, from local speaker diarization.</summary>
    public bool LabelSpeakers { get; set; } = true;
    /// <summary>How many people speak, when known (more reliable); 0 lets Zeppie Says work it out.</summary>
    public int Speakers { get; set; }
    /// <summary>The finer settings of speaker detection; each file starts with a copy it can change.</summary>
    public Notes.SpeakerSettings SpeakerDetection { get; set; } = new();
    /// <summary>The model that tells voices apart and recognises named people (<see cref="Notes.VoiceModel"/>).</summary>
    public string VoiceModel { get; set; } = Notes.VoiceModel.DefaultId;
    /// <summary>A summary, decisions and action items at the top, written by the default style model.</summary>
    public bool Summarize { get; set; } = true;
    /// <summary>Passes the dictionary to Whisper as spelling hints.</summary>
    public bool UseDictionary { get; set; } = true;
    /// <summary>Shows "Record this meeting?" when Teams, Zoom or another meeting app starts using the microphone.</summary>
    public bool AskToRecordMeetings { get; set; } = true;
}

public sealed class CustomModel
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
}

public static class SettingsStore
{
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() },
    };

    static string FilePath => System.IO.Path.Combine(AppPaths.Settings, "settings.json");

    /// <summary>API keys, on this PC only (they're encrypted for this Windows user and useless elsewhere).</summary>
    static string KeysPath => System.IO.Path.Combine(AppPaths.Data, "keys.json");

    public static AppSettings Load()
    {
        var settings = new AppSettings();
        try
        {
            if (File.Exists(FilePath))
                settings = Parse(File.ReadAllText(FilePath));
        }
        catch (Exception e)
        {
            Log.Error("Could not read settings, using defaults", e);
        }

        var legacyKeys = settings.Llm.OpenAiKeyProtected.Length > 0 || settings.Llm.AnthropicKeyProtected.Length > 0;
        if (File.Exists(KeysPath)) LoadKeys(settings.Llm);
        // Keys found in settings.json (where older versions kept them) move to keys.json and out of settings.json.
        if (legacyKeys && !File.Exists(KeysPath)) Save(settings);
        return settings;
    }

    public static AppSettings Parse(string json)
    {
        var settings = Migrate(JsonSerializer.Deserialize<AppSettings>(json, Json) ?? new());
        // Older versions kept the API keys in settings.json; pick them up so Load can move them.
        var llm = System.Text.Json.Nodes.JsonNode.Parse(json)?["Llm"];
        settings.Llm.OpenAiKeyProtected = llm?["OpenAiKeyProtected"]?.GetValue<string>() ?? "";
        settings.Llm.AnthropicKeyProtected = llm?["AnthropicKeyProtected"]?.GetValue<string>() ?? "";
        return settings;
    }

    static void LoadKeys(LlmSettings llm)
    {
        try
        {
            var keys = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(KeysPath)) ?? [];
            llm.OpenAiKeyProtected = keys.GetValueOrDefault("OpenAI", "");
            llm.AnthropicKeyProtected = keys.GetValueOrDefault("Anthropic", "");
            llm.ElevenLabsKeyProtected = keys.GetValueOrDefault("ElevenLabs", "");
        }
        catch (Exception e)
        {
            Log.Error("Could not read the API keys", e);
        }
    }

    static void SaveKeys(LlmSettings llm) =>
        File.WriteAllText(KeysPath, JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["OpenAI"] = llm.OpenAiKeyProtected,
            ["Anthropic"] = llm.AnthropicKeyProtected,
            ["ElevenLabs"] = llm.ElevenLabsKeyProtected,
        }));

    public static string Serialize(AppSettings settings) => JsonSerializer.Serialize(settings, Json);

    /// <summary>Two fixed keys (Primary, Secondary) became a list.</summary>
    static AppSettings Migrate(AppSettings settings)
    {
        if (settings.Primary is not null || settings.Secondary is not null)
        {
            settings.Keys = new[] { settings.Primary, settings.Secondary }.OfType<KeyBinding>().ToList();
            settings.Primary = settings.Secondary = null;
        }
        if (settings.Keys.Count == 0) settings.Keys.Add(new KeyBinding());
        return settings;
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            // Whatever changed a model, the active profile follows, so switching back finds it as it was left.
            ModelProfiles.Active(settings)?.CaptureFrom(settings);
            SaveKeys(settings.Llm);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, Serialize(settings)); // without the keys: they're [JsonIgnore]
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception e)
        {
            Log.Error("Could not save settings", e);
        }
    }
}
