using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ZeppieSays.Notes;

/// <summary>What Teams' call window shows: a call between two people (not a meeting), and the other people in it.</summary>
public sealed record TeamsCall(bool OneToOne, IReadOnlyList<string> Others);

/// <summary>
/// Who or what a Teams call is about, from Teams' own window titles. The main window reads
/// "Chat | Esmee van der Made | Microsoft Teams" (a section first); a call or meeting gets a window of its own,
/// "Esmee van der Made | Microsoft Teams" or "Weekly planning | Microsoft Teams".
/// </summary>
public static class TeamsWindow
{
    const string Suffix = " | Microsoft Teams";

    /// <summary>The call's name (a person, or a meeting's subject) from the open Teams windows, or null when there is no call window.</summary>
    public static string? CallTitle()
    {
        try { return CallTitle(TitlesOf("ms-teams", "Teams")); }
        catch { return null; } // a name is a nicety; the recording goes on without it
    }

    /// <summary>The first title of the form "X | Microsoft Teams": one part before the suffix, so not the main window.</summary>
    public static string? CallTitle(IEnumerable<string> titles)
    {
        foreach (var title in titles)
        {
            if (!title.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase)) continue;
            var name = title[..^Suffix.Length].Trim();
            if (name.Length == 0 || name.Contains(" | ") || name.Equals("Microsoft Teams", StringComparison.OrdinalIgnoreCase)) continue;
            return Clean(name);
        }
        return null;
    }

    /// <summary>
    /// Who is in the Teams call, from the call window's accessibility tree (what a screen reader sees); null when
    /// there's no call window. Teams fills in that tree only once something asks, so the first read may be empty.
    /// </summary>
    public static TeamsCall? ReadCall()
    {
        try
        {
            var ids = Process.GetProcessesByName("ms-teams").Concat(Process.GetProcessesByName("Teams")).Select(p => p.Id).ToHashSet();
            if (ids.Count == 0) return null;
            var any = System.Windows.Automation.Condition.TrueCondition;
            var window = System.Windows.Automation.AutomationElement.RootElement
                .FindAll(System.Windows.Automation.TreeScope.Children, any).Cast<System.Windows.Automation.AutomationElement>()
                .FirstOrDefault(w => ids.Contains(w.Current.ProcessId) && CallTitle([w.Current.Name]) is not null);
            if (window is null) return null;
            var elements = window.FindAll(System.Windows.Automation.TreeScope.Descendants, any).Cast<System.Windows.Automation.AutomationElement>()
                .Select(e => e.Current)
                .Select(c => (c.ControlType.ProgrammaticName.Replace("ControlType.", ""), c.Name, c.AutomationId));
            return ParseCall(elements);
        }
        catch
        {
            return null; // the call goes on being recorded; only the names are missing
        }
    }

    /// <summary>Buttons only a call between two people has, not a meeting; their ids don't change with Teams' language.</summary>
    static readonly string[] CallOnlyButtons = ["transfer-button", "consultative-transfer-button", "hold-button", "dialpad-button"];

    /// <summary>How Teams labels your own video tile ("Video over mezelf, Dirk Vaessen, …").</summary>
    static readonly string[] Yourself = ["mezelf", "yourself", "myself"];

    /// <summary>
    /// The call from the elements of its window: whether it is a call between two people, and the other people's
    /// names. Each person has a video tile named "Kaz Schellekens, video is ingeschakeld, …", with or without
    /// their camera on; yours starts with "Video over mezelf".
    /// </summary>
    public static TeamsCall ParseCall(IEnumerable<(string ControlType, string Name, string AutomationId)> elements)
    {
        var oneToOne = false;
        var others = new List<string>();
        foreach (var (type, name, id) in elements)
        {
            if (CallOnlyButtons.Contains(id)) oneToOne = true;
            if (type is not ("Image" or "MenuItem" or "ListItem" or "Button")) continue;
            var parts = name.Split(", ", StringSplitOptions.TrimEntries);
            if (parts.Length < 2 || !parts.Skip(1).Any(p => p.Contains("video", StringComparison.OrdinalIgnoreCase))) continue;
            if (Yourself.Any(y => parts[0].Contains(y, StringComparison.OrdinalIgnoreCase))) continue;
            var person = Clean(parts[0]);
            if (person.Length > 0 && !others.Contains(person)) others.Add(person);
        }
        return new TeamsCall(oneToOne, others);
    }

    /// <summary>Safe as part of a file name, and not absurdly long.</summary>
    public static string Clean(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? ' ' : c).ToArray());
        clean = string.Join(' ', clean.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return clean.Length > 60 ? clean[..60].TrimEnd() : clean;
    }

    static List<string> TitlesOf(params string[] processNames)
    {
        var ids = processNames.SelectMany(Process.GetProcessesByName).Select(p => (uint)p.Id).ToHashSet();
        var titles = new List<string>();
        if (ids.Count == 0) return titles;
        EnumWindows((window, _) =>
        {
            if (IsWindowVisible(window) && GetWindowThreadProcessId(window, out var id) != 0 && ids.Contains(id))
            {
                var text = new StringBuilder(512);
                if (GetWindowText(window, text, text.Capacity) > 0) titles.Add(text.ToString());
            }
            return true;
        }, IntPtr.Zero);
        return titles;
    }

    delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr window, StringBuilder text, int max);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
}
