using System.Globalization;
using ZeppieSays.Notes;

namespace ZeppieSays.UI;

public sealed record NumberChoice(double Value, string Label);

/// <summary>
/// The advanced speaker detection panel, over the general settings or one file's copy. The same-voice threshold
/// follows the voice model's own default until the user moves the slider.
/// </summary>
public sealed class SpeakerSettingsViewModel : ObservableObject
{
    readonly Func<VoiceModel> _model;

    public SpeakerSettingsViewModel(SpeakerSettings settings, Func<VoiceModel> model)
    {
        Settings = settings;
        _model = model;
        ResetThreshold = new RelayCommand(_ => Settings.Threshold = null);
        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SpeakerSettings.Threshold)) RaiseThreshold();
            else Raise(e.PropertyName);
        };
    }

    public SpeakerSettings Settings { get; }
    public RelayCommand ResetThreshold { get; }

    public double Threshold
    {
        get => Settings.Threshold ?? _model().DefaultThreshold;
        set => Settings.Threshold = (float)value;
    }

    public bool IsCustomThreshold => Settings.Threshold is not null;

    public string ThresholdText => Threshold.ToString("0.00", CultureInfo.InvariantCulture) +
        (IsCustomThreshold ? "" : $" · default for {_model().Name}");

    public double MinSpeech
    {
        get => Settings.MinSpeech;
        set => Settings.MinSpeech = value;
    }

    public double ShortestSpeech
    {
        get => Settings.ShortestSpeech;
        set => Settings.ShortestSpeech = value;
    }

    public double ShortestPause
    {
        get => Settings.ShortestPause;
        set => Settings.ShortestPause = value;
    }

    /// <summary>The voice model changed, and with it the default threshold.</summary>
    public void RaiseThreshold()
    {
        Raise(nameof(Threshold));
        Raise(nameof(IsCustomThreshold));
        Raise(nameof(ThresholdText));
    }

    public static IReadOnlyList<NumberChoice> MinSpeechChoices { get; } =
    [
        new(SpeakerSettings.AutoMinSpeech, "Auto (a tenth of the recording, at most 15 s)"),
        new(0, "Off: keep every speaker found"),
        new(3, "3 s"), new(5, "5 s"), new(10, "10 s"), new(15, "15 s"), new(30, "30 s"), new(60, "1 min"),
    ];

    public static IReadOnlyList<NumberChoice> ShortestSpeechChoices { get; } =
        [new(0.1, "0.1 s"), new(0.2, "0.2 s"), new(0.3, "0.3 s (default)"), new(0.5, "0.5 s"), new(1, "1 s")];

    public static IReadOnlyList<NumberChoice> ShortestPauseChoices { get; } =
        [new(0.2, "0.2 s"), new(0.5, "0.5 s (default)"), new(1, "1 s"), new(2, "2 s")];
}
