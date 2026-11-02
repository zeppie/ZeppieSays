using ZeppieSays.Core;
using ZeppieSays.Llm;
using ZeppieSays.Services;

namespace ZeppieSays.Tests;

public class TaskTests
{
    static readonly DateTime Now = new(2026, 11, 2, 14, 30, 0); // a Monday
    static readonly string[] Lists = ["Tasks", "Boodschappen", "Werk"];

    [Fact]
    public void A_full_reply_becomes_a_task_with_every_detail()
    {
        var task = TaskReader.Parse("""
            {"title":"bel Anna over de offerte","list":"werk","due":"2026-11-06","reminder":"2026-11-06T09:00",
             "important":true,"notes":"Zij wacht op de prijs","steps":["Prijs nakijken","Bellen"],"repeat":null}
            """, "spoken", Lists, Now);
        Assert.Equal("Bel Anna over de offerte", task.Title);
        Assert.Equal("Werk", task.List); // as the user named the list
        Assert.Equal(new DateOnly(2026, 11, 6), task.Due);
        Assert.Equal(new DateTime(2026, 11, 6, 9, 0, 0), task.Reminder);
        Assert.True(task.Important);
        Assert.Equal("Zij wacht op de prijs", task.Notes);
        Assert.Equal(["Prijs nakijken", "Bellen"], task.StepList);
    }

    [Fact]
    public void Text_around_the_JSON_and_a_code_fence_are_ignored()
    {
        var task = TaskReader.Parse("Here you go:\n```json\n{\"title\":\"Melk kopen\",\"list\":\"Boodschappen\"}\n```", "x", Lists, Now);
        Assert.Equal("Melk kopen", task.Title);
        Assert.Equal("Boodschappen", task.List);
    }

    [Fact]
    public void Without_a_usable_reply_the_whole_spoken_text_becomes_the_title()
    {
        Assert.Equal("Melk kopen morgen", TaskReader.Parse("Sorry, I can't.", "melk kopen morgen.", Lists, Now).Title);
        Assert.Equal("Melk kopen", TaskReader.Parse("{\"title\": \"\"}", "melk kopen", Lists, Now).Title);
    }

    [Fact]
    public void Details_the_model_got_wrong_are_left_out_rather_than_guessed()
    {
        var task = TaskReader.Parse("""{"title":"X","list":"Vakantie","due":"friday","reminder":"9am","repeat":"fortnightly"}""", "x", Lists, Now);
        Assert.Null(task.List); // no such list: the default list
        Assert.Null(task.Due);
        Assert.Null(task.Reminder);
        Assert.Null(task.Repeat);
    }

    [Fact]
    public void Today_a_reminder_and_a_repeat_each_give_the_task_a_due_date()
    {
        Assert.Equal(new DateOnly(2026, 11, 2), TaskReader.Parse("""{"title":"X","today":true}""", "x", Lists, Now).Due);
        Assert.Equal(new DateOnly(2026, 11, 3), TaskReader.Parse("""{"title":"X","reminder":"2026-11-03T08:15"}""", "x", Lists, Now).Due);
        Assert.Equal(new DateOnly(2026, 11, 2), TaskReader.Parse("""{"title":"X","repeat":"weekly"}""", "x", Lists, Now).Due);
    }

    [Fact]
    public void The_prompt_has_the_calendar_and_the_lists()
    {
        var prompt = TaskReader.SystemPrompt(Now, Lists);
        Assert.Contains("2026-11-06 Friday / vrijdag", prompt);
        Assert.Contains("2026-11-03 Tuesday / dinsdag (tomorrow)", prompt);
        Assert.Contains("\"Boodschappen\"", prompt);
        Assert.Contains("Write the title, notes and steps in English", TaskReader.SystemPrompt(Now, Lists, "en"));
    }

    [Fact]
    public void Graph_gets_the_due_date_reminder_importance_and_notes_in_the_local_time_zone()
    {
        var body = TodoClient.TaskBody(new TaskDraft("Bel Anna", Due: new DateOnly(2026, 11, 6),
            Reminder: new DateTime(2026, 11, 6, 9, 0, 0), Important: true, Notes: "Over de offerte"), "W. Europe Standard Time");
        Assert.Equal("Bel Anna", body["title"]!.GetValue<string>());
        Assert.Equal("high", body["importance"]!.GetValue<string>());
        Assert.Equal("2026-11-06T00:00:00", body["dueDateTime"]!["dateTime"]!.GetValue<string>());
        Assert.Equal("W. Europe Standard Time", body["dueDateTime"]!["timeZone"]!.GetValue<string>());
        Assert.True(body["isReminderOn"]!.GetValue<bool>());
        Assert.Equal("2026-11-06T09:00:00", body["reminderDateTime"]!["dateTime"]!.GetValue<string>());
        Assert.Equal("Over de offerte", body["body"]!["content"]!.GetValue<string>());
        Assert.Null(body["recurrence"]);
    }

    [Theory]
    [InlineData("daily", "daily", null)]
    [InlineData("weekdays", "weekly", "monday,tuesday,wednesday,thursday,friday")]
    [InlineData("weekly", "weekly", "friday")]
    [InlineData("monthly", "absoluteMonthly", null)]
    [InlineData("yearly", "absoluteYearly", null)]
    public void A_repeating_task_starts_on_its_due_date(string repeat, string type, string? days)
    {
        var body = TodoClient.TaskBody(new TaskDraft("X", Due: new DateOnly(2026, 11, 6), Repeat: repeat), "UTC");
        var pattern = body["recurrence"]!["pattern"]!;
        Assert.Equal(type, pattern["type"]!.GetValue<string>());
        Assert.Equal("2026-11-06", body["recurrence"]!["range"]!["startDate"]!.GetValue<string>());
        if (days is not null)
            Assert.Equal(days, string.Join(",", pattern["daysOfWeek"]!.AsArray().Select(d => d!.GetValue<string>())));
        if (type == "absoluteMonthly") Assert.Equal(6, pattern["dayOfMonth"]!.GetValue<int>());
    }

    [Fact]
    public void A_profile_keeps_a_task_keys_reading_model()
    {
        var settings = new AppSettings();
        var key = settings.Keys[0];
        key.Action = KeyAction.Task;
        key.AskProvider = LlmProvider.Ollama;
        key.AskModel = "qwen3:8b";
        var local = ModelProfile.From(settings, "Local");
        key.AskProvider = LlmProvider.Anthropic;
        key.AskModel = "claude-haiku-5-5";

        local.ApplyTo(settings);
        Assert.Equal((LlmProvider.Ollama, "qwen3:8b"), (key.AskProvider, key.AskModel));
        Assert.True(key.AddsTasks);
    }
}
