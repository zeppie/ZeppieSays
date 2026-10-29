using ZeppieSays.Notes;

namespace ZeppieSays.Tests;

public class MeetingNameTests
{
    [Fact]
    public void Finds_the_call_window_among_Teams_windows()
    {
        // The main window has a section in front; the call window is just the name.
        Assert.Equal("Esmee van der Made", TeamsWindow.CallTitle(
            ["Chat | Esmee van der Made | Microsoft Teams", "Esmee van der Made | Microsoft Teams"]));
        Assert.Null(TeamsWindow.CallTitle(["Chat | Esmee van der Made | Microsoft Teams", "Microsoft Teams"]));
        Assert.Null(TeamsWindow.CallTitle([]));
    }

    [Fact]
    public void Makes_a_window_title_safe_for_a_file_name()
    {
        Assert.Equal("Sprint review Q4 planning", TeamsWindow.Clean("Sprint review: Q4/planning"));
        Assert.Equal(60, TeamsWindow.Clean(new string('a', 100)).Length);
    }

    static RecordingInfo Teams(string? window) => new() { App = "Teams", CallTitle = window };

    [Fact]
    public void A_call_with_one_known_voice_is_named_after_them()
    {
        Assert.Equal("Call with Anna", Notetaker.MeetingName(Teams("Weekly sync"), ["Anna", "Anna", null]));
    }

    [Fact]
    public void A_call_with_one_unknown_voice_takes_the_name_from_the_call_window()
    {
        Assert.Equal("Call with Esmee van der Made", Notetaker.MeetingName(Teams("Esmee van der Made"), ["Speaker 1"]));
        Assert.Null(Notetaker.MeetingName(Teams(null), ["Speaker 1"])); // nothing to go on: keep the recording's own name
    }

    [Fact]
    public void A_meeting_with_several_people_is_named_after_the_call_window()
    {
        Assert.Equal("Sprint review", Notetaker.MeetingName(Teams("Sprint review"), ["Anna", "Speaker 1", "Speaker 2"]));
        Assert.Null(Notetaker.MeetingName(Teams(null), ["Anna", "Speaker 1"]));
    }

    [Fact]
    public void Knows_which_meeting_app_a_process_belongs_to()
    {
        Assert.Equal("Teams", MeetingApps.App("ms-teams"));
        Assert.Equal("Teams", MeetingApps.App("MS-TEAMS"));
        Assert.Equal("Webex", MeetingApps.App("CiscoCollabHost"));
        Assert.Null(MeetingApps.App("msedgewebview2"));
    }

    [Fact]
    public void Without_speaker_labels_the_call_window_names_it()
    {
        Assert.Equal("Esmee van der Made", Notetaker.MeetingName(Teams("Esmee van der Made"), ["Others"]));
    }
}
