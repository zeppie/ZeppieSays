using System.IO;
using System.Text.Json;
using ZeppieSays.Core;
using ZeppieSays.Notes;
using ZeppieSays.UI;

namespace ZeppieSays.Tests;

public class SpeakerDetectionTests
{
    [Fact]
    public void Automatic_minimum_speech_scales_with_the_recording()
    {
        var settings = new SpeakerSettings();
        Assert.Equal(4.2, settings.MinSpeechFor(42), 3); // a short clip: someone saying 11 s is not a fragment
        Assert.Equal(15, settings.MinSpeechFor(3600));    // an hour: at most 15 s, as before
        settings.MinSpeech = 0;
        Assert.Equal(0, settings.MinSpeechFor(3600));     // off: keep every speaker found
        settings.MinSpeech = 30;
        Assert.Equal(30, settings.MinSpeechFor(42));
    }

    [Fact]
    public void The_threshold_follows_the_voice_model_until_the_user_moves_it()
    {
        var model = VoiceModel.Get("campplus");
        var panel = new SpeakerSettingsViewModel(new SpeakerSettings(), () => model);
        Assert.Equal(0.7, panel.Threshold, 3);
        Assert.False(panel.IsCustomThreshold);

        panel.Threshold = 0.55;
        Assert.Equal(0.55f, panel.Settings.Threshold);
        model = VoiceModel.Get("wespeaker");
        Assert.Equal(0.55, panel.Threshold, 3); // the user's own choice stays

        panel.ResetThreshold.Execute(null);
        Assert.Equal(VoiceModel.Get("wespeaker").DefaultThreshold, panel.Threshold, 3);
    }

    [Fact]
    public void Older_settings_get_CAM_plus_plus_and_the_default_detection()
    {
        var settings = SettingsStore.Parse("""{ "Notetaker": { "Speakers": 3 } }""");
        Assert.Equal("campplus", settings.Notetaker.VoiceModel);
        Assert.Null(settings.Notetaker.SpeakerDetection.Threshold);
        Assert.Equal(SpeakerSettings.AutoMinSpeech, settings.Notetaker.SpeakerDetection.MinSpeech);
    }

    [Fact]
    public void Speaker_settings_survive_a_save()
    {
        var settings = new AppSettings();
        settings.Notetaker.VoiceModel = "titanet";
        settings.Notetaker.SpeakerDetection.Threshold = 0.45f;
        settings.Notetaker.SpeakerDetection.MinSpeech = 5;
        settings.Notetaker.SpeakerDetection.ShortestPause = 1;

        var again = SettingsStore.Parse(SettingsStore.Serialize(settings)).Notetaker;
        Assert.Equal("titanet", again.VoiceModel);
        Assert.Equal(0.45f, again.SpeakerDetection.Threshold);
        Assert.Equal(5, again.SpeakerDetection.MinSpeech);
        Assert.Equal(1, again.SpeakerDetection.ShortestPause);
    }

    [Fact]
    public void A_file_changes_its_own_copy_of_the_speaker_settings()
    {
        var general = new NotetakerSettings();
        general.SpeakerDetection.Threshold = 0.6f;
        var options = NoteOptions.From(general);
        Assert.Equal(0.6f, options.Speaker.Threshold);

        options.Speaker.Threshold = 0.4f;
        Assert.Equal(0.6f, general.SpeakerDetection.Threshold);
    }

    [Fact]
    public void Reads_the_voiceprints_of_older_transcripts_as_WeSpeaker_ones()
    {
        var markdown = Path.Combine(Directory.CreateTempSubdirectory().FullName, "old.md");
        File.WriteAllText(Path.ChangeExtension(markdown, ".speakers.json"),
            JsonSerializer.Serialize(new Dictionary<string, float[]> { ["Speaker 1"] = [1, 0] }));

        var (model, voices) = Transcript.ReadVoiceprints(markdown);
        Assert.Equal("wespeaker", model);
        Assert.Equal([1f, 0f], voices["Speaker 1"]);
    }

    [Fact]
    public void Keeps_the_voice_model_with_a_transcripts_voiceprints_when_a_speaker_is_named()
    {
        var markdown = Path.Combine(Directory.CreateTempSubdirectory().FullName, "new.md");
        File.WriteAllText(markdown, "**[00:01] Speaker 1:** Hallo.");
        var transcript = new Transcript { Title = "t", Duration = TimeSpan.FromSeconds(5), ModelName = "m", VoiceModel = "campplus" };
        transcript.Voiceprints["Speaker 1"] = [0, 1];
        transcript.SaveVoiceprints(markdown);

        Transcript.RenameSpeaker(markdown, "Speaker 1", "Sam");

        var (model, voices) = Transcript.ReadVoiceprints(markdown);
        Assert.Equal("campplus", model);
        Assert.Equal([0f, 1f], voices["Sam"]);
    }

    [Fact]
    public void Keeps_WeSpeaker_voiceprints_where_older_versions_look_for_them()
    {
        var voice = new KnownVoice { Name = "Alfo", Voiceprint = [1, 0], Samples = 2 };
        Assert.Equal(2, voice.For("wespeaker")!.Samples);
        Assert.Null(voice.For("campplus")); // not learned by the new model yet

        voice.Set("campplus", new ModelVoiceprint { Voiceprint = [0, 1, 0], Samples = 1 });
        var json = JsonSerializer.Serialize(voice);
        var again = JsonSerializer.Deserialize<KnownVoice>(json)!;
        Assert.Equal([1f, 0f], again.Voiceprint);
        Assert.Equal(2, again.Samples);
        Assert.Equal([0f, 1f, 0f], again.For("campplus")!.Voiceprint);
        Assert.Equal(2, again.MostSamples);
    }

    [Fact]
    public void Finds_every_stretch_someone_talks_alone()
    {
        List<SpeakerSegment> segments = [new("Sam", 0, 10), new("Dirk", 3, 6), new("Sam", 20, 21)];
        Assert.Equal([(6.0, 10.0), (0.0, 3.0), (20.0, 21.0)], VoiceSample.Alone(segments, "Sam")); // longest first
    }
}
