using ZeppieSays.Core;
using ZeppieSays.UI;

namespace ZeppieSays.Notes;

/// <summary>Who tells the speakers apart when the speech model is ElevenLabs.</summary>
public static class SpeakerSources
{
    public const string Local = "local";
    public const string Cloud = "cloud";

    public static IReadOnlyList<Choice> All { get; } =
        [new(Local, "On this PC"), new(Cloud, "ElevenLabs (cloud)")];

    public static bool IsElevenLabs(string modelId) => ModelCatalog.Cloud.Any(m => m.Id == modelId);
}

/// <summary>
/// How one file is transcribed. Starts as a copy of the general Notetaker settings, so a dropped file can be
/// adjusted on its own without touching them.
/// </summary>
public sealed class NoteOptions : ObservableObject
{
    string _modelId = "";
    string _language = "auto";
    bool _labelSpeakers;
    int _speakerCount;
    bool _cloudSpeakers;
    bool _summarize;
    bool _useDictionary;
    string _title = "";
    string _context = "";

    public static NoteOptions From(NotetakerSettings settings) => new()
    {
        _modelId = settings.ModelId,
        _language = settings.Language,
        _labelSpeakers = settings.LabelSpeakers,
        _speakerCount = settings.Speakers,
        _cloudSpeakers = settings.CloudSpeakers,
        _summarize = settings.Summarize,
        _useDictionary = settings.UseDictionary,
        Speaker = settings.SpeakerDetection.Copy(),
        _voiceModel = settings.VoiceModel,
    };

    /// <summary>Speaker detection's finer settings for this file.</summary>
    public SpeakerSettings Speaker { get; private init; } = new();

    string _voiceModel = Notes.VoiceModel.DefaultId;
    bool _keepText;
    bool _canKeepText;

    /// <summary>The voice model that tells this file's speakers apart (<see cref="Notes.VoiceModel"/>).</summary>
    public string VoiceModel
    {
        get => _voiceModel;
        set { if (value is not null) Set(ref _voiceModel, value); }
    }

    /// <summary>
    /// Doing a finished transcript again: true only works out the speakers again and hands them the words already
    /// heard (seconds); false transcribes the recording again (minutes).
    /// </summary>
    public bool KeepText
    {
        get => _keepText;
        set
        {
            if (!Set(ref _keepText, value && _canKeepText)) return;
            Raise(nameof(TranscribeAgain));
            Raise(nameof(CanPickSpeakerSource));
        }
    }

    public bool TranscribeAgain
    {
        get => !_keepText;
        set => KeepText = !value;
    }

    /// <summary>The transcript has its words with their times (made since they are kept), so the text can be kept.</summary>
    public bool CanKeepText
    {
        get => _canKeepText;
        set
        {
            if (!Set(ref _canKeepText, value)) return;
            if (!value) KeepText = false;
        }
    }

    public string ModelId
    {
        get => _modelId;
        set
        {
            // the picker briefly clears while its list refreshes
            if (value is not null && Set(ref _modelId, value)) Raise(nameof(CanPickSpeakerSource));
        }
    }

    /// <summary>ElevenLabs can tell the speakers apart, but only when it transcribes (not when only the speakers are found again).</summary>
    public bool CanPickSpeakerSource => TranscribeAgain && SpeakerSources.IsElevenLabs(_modelId);

    /// <summary>The "speakers found by" picker's value (<see cref="SpeakerSources"/>).</summary>
    public string SpeakerSourceChoice
    {
        get => _cloudSpeakers ? SpeakerSources.Cloud : SpeakerSources.Local;
        set { if (value is not null && Set(ref _cloudSpeakers, value == SpeakerSources.Cloud)) Raise(nameof(CloudSpeakers)); }
    }

    /// <summary>ElevenLabs finds the speakers; only counts when the speech model is ElevenLabs.</summary>
    public bool CloudSpeakers => _cloudSpeakers && SpeakerSources.IsElevenLabs(_modelId);

    public string Language
    {
        get => _language;
        set { if (value is not null) Set(ref _language, value); }
    }

    public bool LabelSpeakers
    {
        get => _labelSpeakers;
        set => Set(ref _labelSpeakers, value);
    }

    /// <summary>How many people speak; 0 lets Zeppie Says work it out.</summary>
    public int SpeakerCount => _speakerCount;

    /// <summary>The people picker's value ("0" for auto).</summary>
    public string SpeakersChoice
    {
        get => _speakerCount.ToString();
        set
        {
            if (value is null) return;
            var count = int.TryParse(value, out var n) ? n : 0;
            if (Set(ref _speakerCount, count)) Raise(nameof(SpeakerCount));
        }
    }

    public bool Summarize
    {
        get => _summarize;
        set => Set(ref _summarize, value);
    }

    public bool UseDictionary
    {
        get => _useDictionary;
        set => Set(ref _useDictionary, value);
    }

    /// <summary>The heading of the transcript; empty keeps the file's name.</summary>
    public string Title
    {
        get => _title;
        set => Set(ref _title, value ?? "");
    }

    /// <summary>Who or what the recording is about: spelling hints for the speech model and background for the summary.</summary>
    public string Context
    {
        get => _context;
        set => Set(ref _context, value ?? "");
    }
}
