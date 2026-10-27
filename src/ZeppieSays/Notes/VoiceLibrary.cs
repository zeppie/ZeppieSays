using System.IO;
using System.Text.Json;
using SherpaOnnx;
using ZeppieSays.Core;
using ZeppieSays.Engines;

namespace ZeppieSays.Notes;

/// <summary>One person's voice as one voice model hears it: the average of their voiceprints so far.</summary>
public sealed class ModelVoiceprint
{
    public float[] Voiceprint { get; set; } = [];
    /// <summary>How many recordings the average is built from.</summary>
    public int Samples { get; set; }
}

/// <summary>
/// A person whose voice Zeppie Says knows. Voiceprints of different voice models can't be compared, so there is
/// one per model. The WeSpeaker one stays in <see cref="Voiceprint"/> and <see cref="Samples"/>, where versions
/// before the choice of model (perhaps on another PC sharing the settings folder) look for it.
/// </summary>
public sealed class KnownVoice
{
    public string Name { get; set; } = "";
    public float[] Voiceprint { get; set; } = [];
    public int Samples { get; set; }
    /// <summary>Voiceprints by any other voice model, by its id.</summary>
    public Dictionary<string, ModelVoiceprint> Models { get; set; } = [];

    public ModelVoiceprint? For(string model) =>
        model == VoiceModel.LegacyId
            ? Voiceprint.Length > 0 ? new ModelVoiceprint { Voiceprint = Voiceprint, Samples = Samples } : null
            : Models.GetValueOrDefault(model);

    public void Set(string model, ModelVoiceprint print)
    {
        if (model == VoiceModel.LegacyId) (Voiceprint, Samples) = (print.Voiceprint, print.Samples);
        else Models[model] = print;
    }

    /// <summary>The most recordings any model's voiceprint is built from.</summary>
    public int MostSamples => Models.Values.Select(m => m.Samples).Append(Samples).Max();
}

/// <summary>
/// Remembers voices by name (voices.json in the app's data folder; voiceprints only, never audio). Once a speaker
/// in a transcript is named, later recordings label that voice with the name automatically.
/// Voiceprints come from the same voice model the speaker detection uses.
/// </summary>
public sealed class VoiceLibrary
{
    /// <summary>Cosine similarity above which two voiceprints are taken to be the same person.</summary>
    const float SameVoice = 0.5f;
    const int MaxSeconds = 60; // enough speech for a stable voiceprint

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    static string FilePath => Path.Combine(AppPaths.Settings, "voices.json");

    readonly object _lock = new();
    List<KnownVoice> _voices = [];

    public static VoiceLibrary Load()
    {
        var library = new VoiceLibrary();
        library.ReloadFromDisk();
        return library;
    }

    /// <summary>Picks up voices another PC added to the same file (a settings folder in OneDrive).</summary>
    void ReloadFromDisk()
    {
        try
        {
            if (File.Exists(FilePath))
                _voices = JsonSerializer.Deserialize<List<KnownVoice>>(File.ReadAllText(FilePath)) ?? [];
        }
        catch (Exception e)
        {
            Log.Error("Could not read the voice library", e);
        }
    }

    public IReadOnlyList<string> Names
    {
        get { lock (_lock) return _voices.Select(v => v.Name).Order().ToList(); }
    }

    /// <summary>
    /// Everyone known, by name, with how many recordings their voice is learned from and whether
    /// <paramref name="model"/> knows it yet; fresh from disk.
    /// </summary>
    public IReadOnlyList<(string Name, int Samples, bool Learned)> Voices(string model)
    {
        lock (_lock)
        {
            ReloadFromDisk();
            return _voices.Select(v => (v.Name, v.MostSamples, v.For(model) is not null))
                .OrderBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
    }

    /// <summary>The people <paramref name="model"/> has no voiceprint of yet (known only to another voice model).</summary>
    public IReadOnlyList<string> Unlearned(string model)
    {
        lock (_lock)
        {
            ReloadFromDisk();
            return _voices.Where(v => v.For(model) is null).Select(v => v.Name).ToList();
        }
    }

    /// <summary>A voiceprint from up to a minute of the given stretches of speech, or null when there's too little.</summary>
    public static float[]? Voiceprint(string model, float[] samples, IEnumerable<(int Start, int End)> ranges)
    {
        if (!SpeakerDiarizer.IsInstalled(model)) return null;
        var speech = new List<float>();
        foreach (var (start, end) in ranges)
        {
            speech.AddRange(samples.AsSpan(start, end - start));
            if (speech.Count >= MaxSeconds * Audio.SampleRate) break;
        }
        if (speech.Count < Audio.SampleRate * 2) return null; // under two seconds says little about a voice

        var config = new SpeakerEmbeddingExtractorConfig { Model = SpeakerDiarizer.EmbeddingPath(model), NumThreads = 2 };
        using var extractor = new SpeakerEmbeddingExtractor(config);
        using var stream = extractor.CreateStream();
        stream.AcceptWaveform(Audio.SampleRate, [.. speech]);
        stream.InputFinished();
        return extractor.IsReady(stream) ? Normalize(extractor.Compute(stream)) : null;
    }

    /// <summary>
    /// Names for speakers by their voiceprints: each known person at most once, best matches first.
    /// Speakers without a confident match are left out.
    /// </summary>
    public Dictionary<int, string> Recognize(string model, IReadOnlyDictionary<int, float[]> speakers)
    {
        List<(string Name, float[] Voiceprint)> voices;
        lock (_lock)
        {
            ReloadFromDisk(); // someone named on another PC is recognised here too
            voices = _voices.Select(v => (v.Name, Print: v.For(model)?.Voiceprint)).Where(v => v.Print is not null)
                .Select(v => (v.Name, v.Print!)).ToList();
        }
        var candidates = (from s in speakers
                          from v in voices
                          let score = Similarity(s.Value, v.Voiceprint)
                          where score >= SameVoice
                          orderby score descending
                          select (Speaker: s.Key, v.Name, score)).ToList();
        var names = new Dictionary<int, string>();
        foreach (var (speaker, name, score) in candidates)
        {
            if (names.ContainsKey(speaker) || names.ContainsValue(name)) continue;
            names[speaker] = name;
            Log.Info($"Recognized {name} (similarity {score:F2})");
        }
        return names;
    }

    /// <summary>Learns (or refines) a person's voice as <paramref name="model"/> hears it.</summary>
    public void Remember(string model, string name, float[] voiceprint)
    {
        lock (_lock)
        {
            ReloadFromDisk();
            var voice = _voices.FirstOrDefault(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (voice is null) _voices.Add(voice = new KnownVoice { Name = name });
            if (voice.For(model) is not { } known || known.Voiceprint.Length != voiceprint.Length)
                voice.Set(model, new ModelVoiceprint { Voiceprint = Normalize(voiceprint), Samples = 1 });
            else
            {
                // A running average keeps one voiceprint per person that gets better with every recording.
                var n = known.Samples;
                voice.Set(model, new ModelVoiceprint
                {
                    Voiceprint = Normalize(known.Voiceprint.Zip(voiceprint, (a, b) => (a * n + b) / (n + 1)).ToArray()),
                    Samples = n + 1,
                });
            }
            Save();
        }
    }

    public void Forget(string name)
    {
        lock (_lock)
        {
            ReloadFromDisk();
            _voices.RemoveAll(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            Save();
        }
    }

    void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(_voices, Json)); }
        catch (Exception e) { Log.Error("Could not save the voice library", e); }
    }

    static float[] Normalize(float[] v)
    {
        var length = (float)Math.Sqrt(v.Sum(x => x * x));
        return length > 0 ? v.Select(x => x / length).ToArray() : v;
    }

    /// <summary>Cosine similarity of two voiceprints: 1 is the same voice, around 0 unrelated voices.</summary>
    public static float Similarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;
        float dot = 0;
        for (var i = 0; i < a.Length; i++) dot += a[i] * b[i];
        return dot; // both are normalized
    }
}
