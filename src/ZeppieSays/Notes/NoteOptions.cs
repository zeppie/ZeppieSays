using ZeppieSays.Core;
using ZeppieSays.UI;

namespace ZeppieSays.Notes;

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
        set { if (Set(ref _keepText, value && _canKeepText)) Raise(nameof(TranscribeAgain)); }
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
        set { if (value is not null) Set(ref _modelId, value); } // the picker briefly clears while its list refreshes
    }

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
