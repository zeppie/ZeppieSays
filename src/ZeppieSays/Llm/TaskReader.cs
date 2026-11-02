using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using ZeppieSays.Core;

namespace ZeppieSays.Llm;

/// <summary>A task as read from what was said, before it goes to Microsoft To Do.</summary>
public sealed record TaskDraft(
    string Title,
    /// <summary>The To Do list it goes on, as the user named it; null for the default list ("Tasks").</summary>
    string? List = null,
    DateOnly? Due = null,
    /// <summary>When To Do reminds the user; only when a time was said.</summary>
    DateTime? Reminder = null,
    bool Important = false,
    string? Notes = null,
    IReadOnlyList<string>? Steps = null,
    /// <summary>"daily", "weekdays", "weekly", "monthly" or "yearly"; null for a one-off.</summary>
    string? Repeat = null)
{
    public IReadOnlyList<string> StepList => Steps ?? [];
}

/// <summary>Reads a spoken task ("remind me Friday at nine to call Anna, important") into a <see cref="TaskDraft"/>.</summary>
public static class TaskReader
{
    public static readonly string[] Repeats = ["daily", "weekdays", "weekly", "monthly", "yearly"];

    /// <param name="lists">The user's To Do lists, so "put it on the shopping list" finds the right one.</param>
    /// <param name="target">The language to write the task in, or null for the language it was said in.</param>
    public static string SystemPrompt(DateTime now, IReadOnlyList<string> lists, string? target = null)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine("You turn a spoken to-do into one JSON object for Microsoft To Do. Reply with the JSON object only: no other text, no code fence.");
        if (target is not null)
            prompt.AppendLine($"Write the title, notes and steps in {ModelCatalog.Languages.FirstOrDefault(l => l.Code == target).Name ?? target}, whatever language it was said in.");
        prompt.AppendLine("""
            Fields:
            - "title": the task itself, short, in the language it was said in. Leave out the request around it ("add a task", "zet op mijn lijst", "herinner me eraan") and the date, time, list and other details that have their own field. Start with a capital letter.
            - "list": the name of the list it should go on, only when one is mentioned; else null.
            - "due": the due date as "yyyy-MM-dd", only when a day is mentioned; else null. "today", "vandaag", "in my day", "in mijn dag" all mean today.
            - "reminder": "yyyy-MM-ddTHH:mm" when a time to be reminded is mentioned ("at nine", "om 14:30", "remind me tomorrow morning" = 09:00); else null.
            - "important": true when it's called important, urgent or a priority; else false.
            - "notes": extra details that don't belong in the title; else null.
            - "steps": an array of sub-steps when several are listed ("buy milk, bread and eggs" on a shopping task gives three steps); else [].
            - "repeat": "daily", "weekdays", "weekly", "monthly" or "yearly" when it repeats; else null.
            """);
        prompt.AppendLine($"Now it is {now.ToString("dddd yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}. The coming days, to look dates up:");
        for (var day = 0; day <= 14; day++)
        {
            var date = now.Date.AddDays(day);
            prompt.AppendLine($"  {date:yyyy-MM-dd} {date.ToString("dddd", CultureInfo.InvariantCulture)} / " +
                              date.ToString("dddd", CultureInfo.GetCultureInfo("nl-NL")) + (day == 0 ? " (today)" : day == 1 ? " (tomorrow)" : ""));
        }
        prompt.AppendLine("A weekday without \"next\" means the first one after today. \"Next week\" alone means next Monday.");
        prompt.AppendLine("Never set a reminder in the past: if that time today has gone, use the next day it can happen (for a repeating task, its next occurrence).");
        prompt.AppendLine("In Dutch, \"half negen\" is 08:30 (half an hour before nine), \"kwart over negen\" 09:15, \"kwart voor negen\" 08:45.");
        prompt.AppendLine("Phrases like \"in my day\", \"in mijn dag\", \"graag\", \"please\" only say when or how to add it: never put them in the title or notes. \"Vandaag nog\" or \"today\" alone is not important.");
        prompt.Append(lists.Count > 0
            ? $"The user's lists: {string.Join(", ", lists.Select(l => $"\"{l}\""))}. Use one of these names exactly for \"list\", or null."
            : "Use null for \"list\".");
        return prompt.ToString();
    }

    public static string UserMessage(string spoken) => $"Spoken task: {spoken.Trim()}";

    /// <summary>
    /// The model's reply as a task. Anything it got wrong is left out rather than guessed; with no usable reply at all,
    /// the whole spoken text becomes the title, so nothing that was said is lost.
    /// </summary>
    public static TaskDraft Parse(string reply, string spoken, IReadOnlyList<string> lists, DateTime now)
    {
        var fallback = new TaskDraft(Capitalize(spoken.Trim().TrimEnd('.')));
        var start = reply.IndexOf('{');
        var end = reply.LastIndexOf('}');
        if (start < 0 || end <= start) return fallback;
        JsonObject? json;
        try { json = JsonNode.Parse(reply[start..(end + 1)]) as JsonObject; }
        catch (System.Text.Json.JsonException) { return fallback; }
        if (json is null) return fallback;

        var title = Text(json["title"]);
        var due = DateOnly.TryParseExact(Text(json["due"]), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : (DateOnly?)null;
        DateTime? reminder = DateTime.TryParseExact(Text(json["reminder"]), ["yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm"],
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var r) ? r : null;
        if (json["today"]?.GetValueKind() == System.Text.Json.JsonValueKind.True) due ??= DateOnly.FromDateTime(now);
        // A reminder belongs to a day; To Do shows the task on that day too.
        if (reminder is { } at) due ??= DateOnly.FromDateTime(at);
        var repeat = Text(json["repeat"])?.ToLowerInvariant();
        if (!Repeats.Contains(repeat)) repeat = null;
        // To Do only repeats a task with a due date: start it today.
        if (repeat is not null) due ??= DateOnly.FromDateTime(now);
        var steps = (json["steps"] as JsonArray ?? []).Select(Text).OfType<string>().Where(s => s.Length > 0).ToList();

        return new TaskDraft(
            Title: title is { Length: > 0 } ? Capitalize(title) : fallback.Title,
            List: MatchList(Text(json["list"]), lists),
            Due: due,
            Reminder: reminder,
            Important: json["important"]?.GetValueKind() == System.Text.Json.JsonValueKind.True,
            Notes: Text(json["notes"]),
            Steps: steps,
            Repeat: repeat);
    }

    /// <summary>The list as the user has it named, or null (the default list) when there's no such list.</summary>
    static string? MatchList(string? named, IReadOnlyList<string> lists)
    {
        if (named is null) return null;
        return lists.FirstOrDefault(l => l.Equals(named, StringComparison.OrdinalIgnoreCase))
            ?? lists.FirstOrDefault(l => l.Contains(named, StringComparison.OrdinalIgnoreCase) || named.Contains(l, StringComparison.OrdinalIgnoreCase));
    }

    static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) && s != "null" ? s.Trim() : null;

    static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpper(text[0]) + text[1..];

    /// <summary>"Call Anna · Work · Fri 6 Nov · 09:00 · important" for the card and the log.</summary>
    public static string Describe(TaskDraft task, string? listName)
    {
        var parts = new List<string> { listName ?? task.List ?? "Tasks" };
        if (task.Due is { } due) parts.Add(due.ToString("ddd d MMM", CultureInfo.CurrentCulture));
        if (task.Reminder is { } at) parts.Add($"reminder {at:HH:mm}");
        if (task.Repeat is { } repeat) parts.Add($"repeats {repeat}");
        if (task.Important) parts.Add("important");
        return string.Join(" · ", parts);
    }
}
