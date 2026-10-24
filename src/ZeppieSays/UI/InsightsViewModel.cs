using System.Globalization;
using System.Windows;
using System.Windows.Media;
using ZeppieSays.Core;

namespace ZeppieSays.UI;

/// <summary>The numbers behind the Dictation home page and the Insights page.</summary>
public sealed class InsightsViewModel : ObservableObject
{
    const double TypingWpm = 40; // an average typist
    const int HeatmapWeeks = 20;
    static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US"); // the UI is English, whatever the Windows locale

    static readonly Brush EmptyCell = Frozen("#267C5CFF");
    static readonly Brush[] Levels = [Frozen("#C9BBFB"), Frozen("#A68BFA"), Frozen("#7C5CFF"), Frozen("#5530D0")];
    static readonly Brush DarkBar = Frozen("#5530D0"), MidBar = Frozen("#7C5CFF"), LightBar = Frozen("#CDBFFB");
    static readonly Brush DarkBarText = Frozen("#2A1570");

    readonly UsageStore _usage;

    public InsightsViewModel(UsageStore usage)
    {
        _usage = usage;
        _usage.Changed += Refresh;
        Greeting = UserName.First is { Length: > 0 } name ? $"Welcome back, {name}" : "Welcome back";
        Refresh();
    }

    public string Greeting { get; }

    public int TotalWords { get; private set; }
    public int Wpm { get; private set; }
    public double WpmGauge { get; private set; }
    public string FasterThanTyping { get; private set; } = "";
    public int Streak { get; private set; }
    public string StreakUnit { get; private set; } = "";
    public string StreakTitle { get; private set; } = "";
    public string LongestStreak { get; private set; } = "";

    public int Fixes { get; private set; }
    public int FillerWords { get; private set; }
    public int DictionaryFixes { get; private set; }
    public int ScreenWords { get; private set; }
    public int Rewrites { get; private set; }

    public string Comparison { get; private set; } = "";
    public string TimeSaved { get; private set; } = "";
    public string AppsUsed { get; private set; } = "";

    public IReadOnlyList<CategoryRow> Categories { get; private set; } = [];
    public IReadOnlyList<HeatmapWeek> Weeks { get; private set; } = [];
    public IReadOnlyList<Brush> Legend { get; } = [EmptyCell, .. Levels];

    public void Refresh()
    {
        var days = _usage.Days.ToList();
        TotalWords = days.Sum(d => d.Words);
        var minutes = days.Sum(d => d.Seconds) / 60;
        Wpm = minutes > 0 ? (int)Math.Round(TotalWords / minutes) : 0;
        WpmGauge = Math.Clamp(Wpm / 200.0, 0, 1);
        FasterThanTyping = Wpm > 0 ? (Wpm / TypingWpm).ToString("0.#", English) + "×" : "–";

        var used = days.Where(d => d.Words > 0).Select(d => d.Date).ToHashSet();
        Streak = CurrentStreak(used);
        StreakUnit = "day streak";
        StreakTitle = $"{Streak} day streak";
        var longest = LongestRun(used);
        LongestStreak = $"LONGEST STREAK  |  {longest} {(longest == 1 ? "DAY" : "DAYS")}";

        FillerWords = days.Sum(d => d.FillerWords);
        DictionaryFixes = days.Sum(d => d.DictionaryFixes);
        ScreenWords = days.Sum(d => d.ScreenWords);
        Rewrites = days.Sum(d => d.Rewrites);
        Fixes = FillerWords + DictionaryFixes + ScreenWords + Rewrites;

        Comparison = TotalWords < 1000
            ? $"That's about {Math.Max(1, TotalWords / 30)} tweets!"
            : $"That's about {TotalWords / 250} book pages!";
        var saved = TimeSpan.FromMinutes(Math.Max(0, TotalWords / TypingWpm - minutes));
        TimeSaved = saved.TotalHours >= 1
            ? $"{(int)saved.TotalHours} h {saved.Minutes} min saved compared to typing"
            : $"{Math.Max(0, (int)saved.TotalMinutes)} min saved compared to typing";
        var apps = days.SelectMany(d => d.Apps.Keys).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        AppsUsed = $"APPS USED  |  {apps}";

        Categories = BuildCategories(days);
        Weeks = BuildHeatmap(days);
        Raise(""); // everything changed
    }

    static int CurrentStreak(HashSet<DateOnly> used)
    {
        var day = DateOnly.FromDateTime(DateTime.Today);
        if (!used.Contains(day)) day = day.AddDays(-1); // today still counts until it's over
        var streak = 0;
        while (used.Contains(day)) { streak++; day = day.AddDays(-1); }
        return streak;
    }

    static int LongestRun(HashSet<DateOnly> used)
    {
        var best = 0;
        foreach (var start in used.Where(d => !used.Contains(d.AddDays(-1))))
        {
            var length = 0;
            while (used.Contains(start.AddDays(length))) length++;
            best = Math.Max(best, length);
        }
        return best;
    }

    static IReadOnlyList<CategoryRow> BuildCategories(List<DayUsage> days)
    {
        var total = Math.Max(1, days.Sum(d => d.Words));
        var rows = Enum.GetValues<UsageCategory>()
            .Select(c => (Category: c,
                Words: days.Sum(d => d.CategoryWords.GetValueOrDefault(c.ToString())),
                Count: days.Sum(d => d.CategoryDictations.GetValueOrDefault(c.ToString()))))
            .OrderByDescending(r => r.Words)
            .ThenBy(r => r.Category == UsageCategory.Other) // "Other" last among equals
            .ToList();
        return rows.Select((r, i) =>
        {
            var share = r.Words / (double)total;
            var strong = i < 3 && share >= 0.05;
            return new CategoryRow(
                Glyph(r.Category),
                $"{r.Count} {Label(r.Category, r.Count)}".ToUpperInvariant(),
                $"{Math.Round(share * 100)}%",
                new GridLength(Math.Max(share, 0.0001), GridUnitType.Star),
                new GridLength(1 - share + 0.6, GridUnitType.Star), // the label needs room, even next to a full bar
                i == 0 && strong ? DarkBar : strong ? MidBar : LightBar,
                strong ? Brushes.White : DarkBarText);
        }).ToList();
    }

    static string Glyph(UsageCategory c) => c switch
    {
        UsageCategory.AiPrompts => "",
        UsageCategory.WorkMessages => "",
        UsageCategory.Emails => "",
        UsageCategory.Documents => "",
        UsageCategory.Code => "",
        UsageCategory.PersonalMessages => "",
        _ => "",
    };

    static string Label(UsageCategory c, int count)
    {
        var one = count == 1;
        return c switch
        {
            UsageCategory.AiPrompts => one ? "AI prompt" : "AI prompts",
            UsageCategory.WorkMessages => one ? "work message" : "work messages",
            UsageCategory.Emails => one ? "email" : "emails",
            UsageCategory.Documents => one ? "document" : "documents",
            UsageCategory.Code => one ? "code task" : "code tasks",
            UsageCategory.PersonalMessages => one ? "personal message" : "personal messages",
            _ => one ? "other task" : "other tasks",
        };
    }

    /// <summary>The last weeks as columns of Monday to Sunday, coloured by how much was dictated that day.</summary>
    static IReadOnlyList<HeatmapWeek> BuildHeatmap(List<DayUsage> days)
    {
        var words = days.ToDictionary(d => d.Date, d => d.Words);
        var max = Math.Max(1, words.Values.DefaultIfEmpty(0).Max());
        var today = DateOnly.FromDateTime(DateTime.Today);
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var start = monday.AddDays(-7 * (HeatmapWeeks - 1));

        var weeks = new List<HeatmapWeek>();
        var lastMonth = -1;
        for (var w = 0; w < HeatmapWeeks; w++)
        {
            var first = start.AddDays(7 * w);
            var cells = Enumerable.Range(0, 7).Select(i =>
            {
                var date = first.AddDays(i);
                if (date > today) return new HeatmapCell(Brushes.Transparent, "");
                var count = words.GetValueOrDefault(date);
                var brush = count == 0 ? EmptyCell : Levels[Math.Min(3, (int)Math.Ceiling(count * 4.0 / max) - 1)];
                return new HeatmapCell(brush, $"{date.ToString("ddd d MMM", English)}: {count} {(count == 1 ? "word" : "words")}");
            }).ToList();
            // Label a column with its month when the month starts in that week.
            var month = first.AddDays(6).Month;
            weeks.Add(new HeatmapWeek(month != lastMonth ? first.AddDays(6).ToString("MMM", English) : "", cells));
            lastMonth = month;
        }
        return weeks;
    }

    static Brush Frozen(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}

public sealed record CategoryRow(string Glyph, string Label, string Percent, GridLength BarWidth, GridLength RestWidth,
    Brush BarBrush, Brush TextBrush);

public sealed record HeatmapWeek(string Month, IReadOnlyList<HeatmapCell> Days);

public sealed record HeatmapCell(Brush Fill, string ToolTip);
