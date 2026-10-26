using ZeppieSays.Core;
using ZeppieSays.Notes;

namespace ZeppieSays.Tests;

public class NoteOptionsTests
{
    [Fact]
    public void Starts_as_a_copy_of_the_general_settings()
    {
        var general = new NotetakerSettings { ModelId = "parakeet-tdt-0.6b-v3", Language = "nl", Speakers = 3, Summarize = false };
        var options = NoteOptions.From(general);
        Assert.Equal("parakeet-tdt-0.6b-v3", options.ModelId);
        Assert.Equal("nl", options.Language);
        Assert.Equal(3, options.SpeakerCount);
        Assert.False(options.Summarize);
        Assert.Equal("", options.Title);
    }

    [Fact]
    public void Changing_one_file_leaves_the_general_settings_alone()
    {
        var general = new NotetakerSettings { Language = "auto", Speakers = 0 };
        var options = NoteOptions.From(general);
        options.Language = "en";
        options.SpeakersChoice = "4";
        options.Summarize = !general.Summarize;
        Assert.Equal("auto", general.Language);
        Assert.Equal(0, general.Speakers);
        Assert.Equal(4, options.SpeakerCount);
    }

    [Fact]
    public void A_cleared_picker_keeps_the_previous_choice()
    {
        var options = NoteOptions.From(new NotetakerSettings { ModelId = "whisper-small" });
        options.ModelId = null!; // the combo box briefly clears while its list refreshes
        Assert.Equal("whisper-small", options.ModelId);
    }
}
