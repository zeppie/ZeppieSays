using ZeppieSays.Notes;

namespace ZeppieSays.Tests;

public class SummarizerLanguageTests
{
    [Fact]
    public void Recognises_a_dutch_meeting()
    {
        var text = string.Concat(Enumerable.Repeat("Ik denk dat we het gewoon niet moeten doen, want dit is een beetje te veel voor de planning. ", 4));
        Assert.Equal("Dutch", Summarizer.DetectLanguage(text));
    }

    [Fact]
    public void Recognises_an_english_meeting()
    {
        var text = string.Concat(Enumerable.Repeat("I think that we should not do this, because it is a bit too much for the plan and you know what they said. ", 4));
        Assert.Equal("English", Summarizer.DetectLanguage(text));
    }

    [Fact]
    public void Says_nothing_about_a_text_that_is_too_short()
    {
        Assert.Null(Summarizer.DetectLanguage("Ja, goed."));
    }

    [Fact]
    public void A_day_heading_says_today_yesterday_a_weekday_or_the_date()
    {
        var culture = new System.Globalization.CultureInfo("en-GB");
        var today = new DateTime(2026, 11, 6);
        Assert.Equal("Today", NoteJob.DayHeading(today, today, culture));
        Assert.Equal("Yesterday", NoteJob.DayHeading(today.AddDays(-1), today, culture));
        Assert.Equal("Tuesday", NoteJob.DayHeading(new DateTime(2026, 11, 3), today, culture));
        Assert.Equal("20 October", NoteJob.DayHeading(new DateTime(2026, 10, 20), today, culture));
        Assert.Equal("5 May 2025", NoteJob.DayHeading(new DateTime(2025, 5, 5), today, culture));
    }
}
