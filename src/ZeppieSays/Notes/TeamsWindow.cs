using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ZeppieSays.Notes;

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
