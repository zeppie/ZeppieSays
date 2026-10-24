using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ZeppieSays.Core;

/// <summary>Totals for one day of dictating. Only counts are kept, never the dictated text.</summary>
public sealed class DayUsage
{
    public DateOnly Date { get; set; }
    public int Dictations { get; set; }
    public int Words { get; set; }
    public double Seconds { get; set; }
    public int FillerWords { get; set; }
    public int DictionaryFixes { get; set; }
    public int ScreenWords { get; set; }
    public int Rewrites { get; set; }
    /// <summary>Words per <see cref="UsageCategory"/> name.</summary>
    public Dictionary<string, int> CategoryWords { get; set; } = [];
    /// <summary>Dictations per <see cref="UsageCategory"/> name.</summary>
    public Dictionary<string, int> CategoryDictations { get; set; } = [];
    /// <summary>Words per app (process name).</summary>
    public Dictionary<string, int> Apps { get; set; } = [];
}

/// <summary>What one dictation added: the words that were pasted and the fixes made along the way.</summary>
public sealed record DictationUsage(
    int Words, double Seconds, string ProcessName, string Title,
    int FillerWords = 0, int DictionaryFixes = 0, int ScreenWords = 0, bool Rewritten = false);

public enum UsageCategory { AiPrompts, WorkMessages, Emails, Documents, Code, PersonalMessages, Other }

public static class UsageCategories
{
    /// <summary>Sorts an app into a category by its process name, or for browsers by words in the window title.</summary>
    public static UsageCategory Of(string processName, string title)
    {
        var p = processName.ToLowerInvariant();
        var t = title.ToLowerInvariant();
        if (p is "claude" or "chatgpt" or "copilot" or "perplexity" || ContainsAny(t, "claude", "chatgpt", "gemini", "copilot", "perplexity"))
            return UsageCategory.AiPrompts;
        if (p is "outlook" or "olk" or "thunderbird" or "hxoutlook" || ContainsAny(t, "gmail", "outlook", "inbox"))
            return UsageCategory.Emails;
        if (p is "ms-teams" or "teams" or "slack" || ContainsAny(t, "teams", "slack"))
            return UsageCategory.WorkMessages;
        if (p is "whatsapp" or "telegram" or "signal" or "discord" || ContainsAny(t, "whatsapp", "messenger", "telegram", "discord"))
            return UsageCategory.PersonalMessages;
        if (p is "code" or "cursor" or "devenv" or "rider64" or "windowsterminal" or "powershell" or "pwsh" or "cmd" or "wt"
            || ContainsAny(t, "github", "stack overflow"))
            return UsageCategory.Code;
        if (p is "winword" or "onenote" or "notion" or "obsidian" or "excel" or "powerpnt" or "notepad"
            || ContainsAny(t, "google docs", "confluence", "notion"))
            return UsageCategory.Documents;
        return UsageCategory.Other;
    }

    static bool ContainsAny(string text, params string[] words) => words.Any(text.Contains);
}

/// <summary>Keeps the per-day usage totals behind the Dictation and Insights pages, in usage.json.</summary>
public sealed class UsageStore
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    static string FilePath => Path.Combine(AppPaths.Data, "usage.json");

    readonly SortedDictionary<DateOnly, DayUsage> _days = [];

    /// <summary>Raised on the calling thread after a dictation was recorded.</summary>
    public event Action? Changed;

    public IReadOnlyCollection<DayUsage> Days => _days.Values;

    public static UsageStore Load()
    {
        var store = new UsageStore();
        try
        {
            if (File.Exists(FilePath))
            {
                foreach (var day in JsonSerializer.Deserialize<List<DayUsage>>(File.ReadAllText(FilePath), Json) ?? [])
                    store._days[day.Date] = day;
            }
            else
            {
                // First run with stats: start from the dictations already in the logs, so the streak isn't zero.
                foreach (var (date, usage) in LogImport.Read(AppPaths.Logs))
                    store.Add(date, usage);
                store.Save();
            }
        }
        catch (Exception e)
        {
            Log.Error("Could not read usage stats", e);
        }
        return store;
    }

    public void Record(DictationUsage usage)
    {
        if (usage.Words == 0) return;
        Add(DateOnly.FromDateTime(DateTime.Now), usage);
        Save();
        Changed?.Invoke();
    }

    void Add(DateOnly date, DictationUsage usage)
    {
        if (!_days.TryGetValue(date, out var day))
            _days[date] = day = new DayUsage { Date = date };
        day.Dictations++;
        day.Words += usage.Words;
        day.Seconds += usage.Seconds;
        day.FillerWords += usage.FillerWords;
        day.DictionaryFixes += usage.DictionaryFixes;
        day.ScreenWords += usage.ScreenWords;
        if (usage.Rewritten) day.Rewrites++;
        var category = UsageCategories.Of(usage.ProcessName, usage.Title).ToString();
        day.CategoryWords[category] = day.CategoryWords.GetValueOrDefault(category) + usage.Words;
        day.CategoryDictations[category] = day.CategoryDictations.GetValueOrDefault(category) + 1;
        if (usage.ProcessName.Length > 0)
            day.Apps[usage.ProcessName] = day.Apps.GetValueOrDefault(usage.ProcessName) + usage.Words;
    }

    void Save()
    {
        try
        {
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_days.Values.ToList(), Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception e)
        {
            Log.Error("Could not save usage stats", e);
        }
    }

    public static int CountWords(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Count(w => w.Any(char.IsLetterOrDigit));
}

/// <summary>
/// Rebuilds usage from old log files. The logs hold the audio length, the style rewrite and the pasted length of
/// each dictation, but not the text, so words are estimated from characters.
/// </summary>
static partial class LogImport
{
    const double CharsPerWord = 5.8; // average word plus its space, in Dutch and English

    public static IEnumerable<(DateOnly Date, DictationUsage Usage)> Read(string folder)
    {
        foreach (var file in Directory.GetFiles(folder, "zeppie-*.log").Order())
        {
            if (!DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(file)["zeppie-".Length..], "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                continue;

            double seconds = 0;
            var rewritten = false;
            foreach (var line in File.ReadLines(file))
            {
                if (Transcribed().Match(line) is { Success: true } t)
                {
                    seconds = double.Parse(t.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
                    rewritten = false;
                }
                else if (line.Contains(" INFO Style '") && line.Contains("rewritten to"))
                    rewritten = true;
                else if (Pasted().Match(line) is { Success: true } p && seconds > 0)
                {
                    var words = (int)Math.Round(int.Parse(p.Groups[1].Value) / CharsPerWord);
                    yield return (date, new DictationUsage(Math.Max(words, 1), seconds, p.Groups[2].Value, "", Rewritten: rewritten));
                    seconds = 0;
                }
            }
        }
    }

    [GeneratedRegex(@" INFO .+?: ([\d.,]+)s audio")]
    private static partial Regex Transcribed();

    [GeneratedRegex(@" INFO Pasted (\d+) chars into '([^']*)'")]
    private static partial Regex Pasted();
}
