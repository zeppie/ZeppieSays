using ZeppieSays.UI;

namespace ZeppieSays.Notes;

/// <summary>
/// The knobs of speaker detection, for the advanced panel: the general ones in Settings, and a copy per file that
/// can be changed before it is transcribed (or before its speakers are worked out again).
/// </summary>
public sealed class SpeakerSettings : ObservableObject
{
    /// <summary>Speech per person below which an extra "speaker" is taken to be a fragment of someone else.</summary>
    public const double AutoMinSpeech = -1;

    float? _threshold;
    double _minSpeech = AutoMinSpeech;
    double _shortestSpeech = 0.3;
    double _shortestPause = 0.5;

    /// <summary>
    /// How different two voices must sound to count as two people: lower finds more speakers, higher fewer.
    /// Null uses the voice model's own default (see <see cref="VoiceModel.DefaultThreshold"/>).
    /// </summary>
    public float? Threshold
    {
        get => _threshold;
        set => Set(ref _threshold, value is { } v ? MathF.Round(Math.Clamp(v, 0.2f, 0.95f), 2) : null);
    }

    /// <summary>Seconds of speech a person needs to count as a speaker; <see cref="AutoMinSpeech"/> scales with the recording, 0 keeps everyone.</summary>
    public double MinSpeech
    {
        get => _minSpeech;
        set => Set(ref _minSpeech, value);
    }

    /// <summary>Speech shorter than this (seconds) is ignored by the detection.</summary>
    public double ShortestSpeech
    {
        get => _shortestSpeech;
        set => Set(ref _shortestSpeech, value);
    }

    /// <summary>A silence shorter than this (seconds) does not end someone's turn.</summary>
    public double ShortestPause
    {
        get => _shortestPause;
        set => Set(ref _shortestPause, value);
    }

    public SpeakerSettings Copy() => new()
    {
        _threshold = _threshold,
        _minSpeech = _minSpeech,
        _shortestSpeech = _shortestSpeech,
        _shortestPause = _shortestPause,
    };

    /// <summary>The minimum speech per speaker for a recording of this length: a tenth of it, at most 15 s, when automatic.</summary>
    public double MinSpeechFor(double recordingSeconds) =>
        MinSpeech >= 0 ? MinSpeech : Math.Min(15, recordingSeconds / 10);
}

/// <summary>A model that turns a stretch of speech into a voiceprint, used to tell speakers apart and to recognise them.</summary>
/// <param name="DefaultThreshold">The same-voice threshold that worked best for this model in our tests.</param>
public sealed record VoiceModel(string Id, string Name, string FileName, long Size, float DefaultThreshold, string Note)
{
    public const string DefaultId = "campplus";
    /// <summary>The model voiceprints were made with before there was a choice.</summary>
    public const string LegacyId = "wespeaker";
    /// <summary>
    /// Not a model on this PC: ElevenLabs tells the speakers apart while it transcribes. Only works with ElevenLabs
    /// as the speech model, has no settings, and names no one (no voiceprints are made).
    /// </summary>
    public const string CloudId = "elevenlabs";

    public bool IsCloud => Id == CloudId;

    public static IReadOnlyList<VoiceModel> All { get; } =
    [
        new("campplus", "CAM++ (3D-Speaker)", "3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx", 28_000_000, 0.7f,
            "Told two people on one microphone apart best in tests, and is the fastest."),
        new("wespeaker", "WeSpeaker ResNet34", "wespeaker_en_voxceleb_resnet34_LM.onnx", 26_500_000, 0.6f,
            "The model Zeppie Says used first."),
        new("titanet", "TitaNet small (NVIDIA)", "nemo_en_titanet_small.onnx", 39_000_000, 0.65f,
            "An alternative to compare with; draws coarser turns."),
        new(CloudId, "ElevenLabs (cloud)", "", 0, 0.7f,
            "ElevenLabs tells the speakers apart while it transcribes; the audio goes there anyway. Needs ElevenLabs as the speech model (otherwise CAM++ is used). It can't recognise people you named."),
    ];

    public static VoiceModel Get(string? id) => All.FirstOrDefault(m => m.Id == id) ?? All[0];
}
