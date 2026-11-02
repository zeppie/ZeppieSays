using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;
using ZeppieSays.Core;
using ZeppieSays.Llm;

namespace ZeppieSays.Services;

public sealed record TodoList(string Id, string Name, bool IsDefault);

/// <summary>A task just made, so it can be undone.</summary>
public sealed record CreatedTask(string ListId, string TaskId, string ListName);

/// <summary>Microsoft To Do refused, or isn't set up; the message says what to do.</summary>
public sealed class TodoException(string message) : Exception(message);

/// <summary>
/// Microsoft To Do through Microsoft Graph, signed in with the user's own app registration. The sign-in is cached
/// on this PC, encrypted for this Windows user (next to keys.json), and never in the synced settings folder.
/// </summary>
public sealed class TodoClient(AppSettings settings)
{
    public const string WebUrl = "https://to-do.office.com/tasks/";
    static readonly string[] Scopes = ["Tasks.ReadWrite", "User.Read"];
    static readonly HttpClient Http = new() { BaseAddress = new Uri("https://graph.microsoft.com/v1.0/"), Timeout = TimeSpan.FromSeconds(30) };

    IPublicClientApplication? _app;
    string? _appFor;
    IReadOnlyList<TodoList>? _lists;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(settings.Todo.ClientId);

    /// <summary>Built again when the client ID or tenant changes in Settings.</summary>
    async Task<IPublicClientApplication> AppAsync()
    {
        var clientId = settings.Todo.ClientId.Trim();
        var tenant = string.IsNullOrWhiteSpace(settings.Todo.Tenant) ? "organizations" : settings.Todo.Tenant.Trim();
        if (clientId.Length == 0)
            throw new TodoException("Fill in the app registration's client ID in Settings, under Microsoft To Do.");
        if (_app is not null && _appFor == $"{clientId}|{tenant}") return _app;

        var app = PublicClientApplicationBuilder.Create(clientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, tenant)
            .WithRedirectUri("http://localhost") // the system browser signs in and hands the result back to us here
            .Build();
        var cache = await MsalCacheHelper.CreateAsync(new StorageCreationPropertiesBuilder("todo.msalcache", AppPaths.Data).Build());
        cache.RegisterCache(app.UserTokenCache);
        (_app, _appFor, _lists) = (app, $"{clientId}|{tenant}", null);
        return app;
    }

    /// <summary>The account signed in, or null.</summary>
    public async Task<string?> SignedInAsAsync()
    {
        if (!IsConfigured) return null;
        try { return (await (await AppAsync()).GetAccountsAsync()).FirstOrDefault()?.Username; }
        catch (Exception e) when (e is TodoException or MsalException)
        {
            Log.Warn($"Could not read the Microsoft sign-in: {e.Message}");
            return null;
        }
    }

    /// <summary>Opens the browser to sign in; returns the account's name.</summary>
    public async Task<string> SignInAsync(IntPtr parentWindow)
    {
        var app = await AppAsync();
        try
        {
            var result = await app.AcquireTokenInteractive(Scopes)
                .WithParentActivityOrWindow(parentWindow)
                .WithUseEmbeddedWebView(false)
                .WithPrompt(Prompt.SelectAccount)
                .ExecuteAsync();
            _lists = null;
            Log.Info($"Signed in to Microsoft To Do as {result.Account.Username}");
            return result.Account.Username;
        }
        catch (MsalException e) when (e.ErrorCode != MsalError.AuthenticationCanceledError)
        {
            Log.Warn($"Microsoft sign-in failed: {e.ErrorCode} {e.Message}");
            throw new TodoException(e.Message.Contains("AADSTS65001") || e.Message.Contains("consent")
                ? "Your organisation needs to approve this app first (an administrator can grant consent in Entra)."
                : $"Signing in failed: {e.Message.Split('\n')[0]}");
        }
    }

    public async Task SignOutAsync()
    {
        var app = await AppAsync();
        foreach (var account in await app.GetAccountsAsync()) await app.RemoveAsync(account);
        _lists = null;
    }

    async Task<string> TokenAsync(CancellationToken ct)
    {
        var app = await AppAsync();
        var account = (await app.GetAccountsAsync()).FirstOrDefault()
            ?? throw new TodoException("Sign in to Microsoft To Do first, in Settings.");
        try { return (await app.AcquireTokenSilent(Scopes, account).ExecuteAsync(ct)).AccessToken; }
        catch (MsalUiRequiredException)
        {
            throw new TodoException("Your Microsoft sign-in has expired. Sign in again in Settings, under Microsoft To Do.");
        }
    }

    async Task<JsonObject> SendAsync(HttpMethod method, string path, JsonObject? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Authorization = new("Bearer", await TokenAsync(ct));
        using var response = await Http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            string? message = null;
            try { message = (await response.Content.ReadFromJsonAsync<JsonObject>(ct))?["error"]?["message"]?.GetValue<string>(); }
            catch { /* not JSON: the status code is all there is */ }
            Log.Warn($"Graph {method} {path.Split('/')[^1]} failed: {(int)response.StatusCode} {message}");
            throw new TodoException(response.StatusCode == HttpStatusCode.Forbidden
                ? "Microsoft To Do refused: the app registration needs the Tasks.ReadWrite permission."
                : $"Microsoft To Do refused ({(int)response.StatusCode}): {message ?? response.ReasonPhrase}");
        }
        return response.StatusCode == HttpStatusCode.NoContent
            ? new JsonObject()
            : await response.Content.ReadFromJsonAsync<JsonObject>(ct) ?? new JsonObject();
    }

    /// <summary>The user's lists; asked once, then remembered until signing in again.</summary>
    public async Task<IReadOnlyList<TodoList>> ListsAsync(CancellationToken ct)
    {
        if (_lists is not null) return _lists;
        var json = await SendAsync(HttpMethod.Get, "me/todo/lists", null, ct);
        _lists = (json["value"] as JsonArray ?? [])
            .Select(l => new TodoList(l!["id"]!.GetValue<string>(), l["displayName"]?.GetValue<string>() ?? "",
                l["wellknownListName"]?.GetValue<string>() == "defaultList"))
            .ToList();
        return _lists;
    }

    /// <summary>Makes the task on its list (or the default "Tasks" list), then its steps.</summary>
    public async Task<CreatedTask> CreateAsync(TaskDraft task, CancellationToken ct)
    {
        var lists = await ListsAsync(ct);
        var list = lists.FirstOrDefault(l => task.List is not null && l.Name.Equals(task.List, StringComparison.OrdinalIgnoreCase))
            ?? lists.FirstOrDefault(l => l.IsDefault) ?? lists.FirstOrDefault()
            ?? throw new TodoException("No lists found in Microsoft To Do.");
        var path = $"me/todo/lists/{Uri.EscapeDataString(list.Id)}/tasks";
        var created = await SendAsync(HttpMethod.Post, path, TaskBody(task, TimeZoneInfo.Local.Id), ct);
        var id = created["id"]!.GetValue<string>();
        foreach (var step in task.StepList)
            await SendAsync(HttpMethod.Post, $"{path}/{Uri.EscapeDataString(id)}/checklistItems", new JsonObject { ["displayName"] = step }, ct);
        return new CreatedTask(list.Id, id, list.Name);
    }

    public Task DeleteAsync(CreatedTask task, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"me/todo/lists/{Uri.EscapeDataString(task.ListId)}/tasks/{Uri.EscapeDataString(task.TaskId)}", null, ct);

    /// <param name="timeZone">A Windows time zone name, as Graph accepts it, e.g. "W. Europe Standard Time".</param>
    public static JsonObject TaskBody(TaskDraft task, string timeZone)
    {
        var body = new JsonObject { ["title"] = task.Title, ["importance"] = task.Important ? "high" : "normal" };
        if (task.Notes is { } notes) body["body"] = new JsonObject { ["content"] = notes, ["contentType"] = "text" };
        if (task.Due is { } due) body["dueDateTime"] = At(due.ToDateTime(TimeOnly.MinValue), timeZone);
        if (task.Reminder is { } reminder)
        {
            body["isReminderOn"] = true;
            body["reminderDateTime"] = At(reminder, timeZone);
        }
        // To Do only repeats a task that has a due date; the reader gives a repeating task one.
        if (task.Repeat is { } repeat && task.Due is { } start)
            body["recurrence"] = new JsonObject
            {
                ["pattern"] = Pattern(repeat, start),
                ["range"] = new JsonObject { ["type"] = "noEnd", ["startDate"] = start.ToString("yyyy-MM-dd") },
            };
        return body;
    }

    static JsonObject At(DateTime time, string timeZone) =>
        new() { ["dateTime"] = time.ToString("yyyy-MM-ddTHH:mm:ss"), ["timeZone"] = timeZone };

    static JsonObject Pattern(string repeat, DateOnly start)
    {
        static JsonArray Days(params DayOfWeek[] days) => new(days.Select(d => (JsonNode)d.ToString().ToLowerInvariant()).ToArray());
        return repeat switch
        {
            "daily" => new JsonObject { ["type"] = "daily", ["interval"] = 1 },
            "weekdays" => new JsonObject
            {
                ["type"] = "weekly", ["interval"] = 1, ["firstDayOfWeek"] = "monday",
                ["daysOfWeek"] = Days(DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday),
            },
            "weekly" => new JsonObject { ["type"] = "weekly", ["interval"] = 1, ["firstDayOfWeek"] = "monday", ["daysOfWeek"] = Days(start.DayOfWeek) },
            "monthly" => new JsonObject { ["type"] = "absoluteMonthly", ["interval"] = 1, ["dayOfMonth"] = start.Day },
            _ => new JsonObject { ["type"] = "absoluteYearly", ["interval"] = 1, ["dayOfMonth"] = start.Day, ["month"] = start.Month },
        };
    }
}
