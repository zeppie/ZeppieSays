using System.Collections.ObjectModel;
using System.Windows.Threading;
using ZeppieSays.Core;

namespace ZeppieSays.UI;

/// <summary>One dictation in the history: what was typed, when, where, and what you said before a style rewrote it.</summary>
public sealed class DictationEntry : ObservableObject
{
    public DictationEntry(DateTime when, string text, string? original, string app, string? style)
    {
        When = when;
        Text = text;
        // Only worth showing when a style changed the words.
        Original = original is { Length: > 0 } && original != text ? original : null;
        App = app;
        Style = style;
        ToggleOriginal = new RelayCommand(_ => ShowOriginal = !ShowOriginal);
    }

    public DateTime When { get; }
    public string Text { get; }
    /// <summary>The transcript before the style rewrote it; null when there was no rewrite.</summary>
    public string? Original { get; }
    public string App { get; }
    public string? Style { get; }

    public bool HasOriginal => Original is not null;
    public RelayCommand ToggleOriginal { get; }

    /// <summary>"14:32 · outlook · Casual", with the date when it wasn't today.</summary>
    public string Subtitle
    {
        get
        {
            var time = When.Date == DateTime.Today ? When.ToString("HH:mm") : When.ToString("d MMM HH:mm");
            return string.Join(" · ", new[] { time, App, Style }.Where(part => !string.IsNullOrWhiteSpace(part)));
        }
    }

    public bool ShowOriginal
    {
        get => _showOriginal;
        private set
        {
            if (Set(ref _showOriginal, value)) Raise(nameof(OriginalToggleLabel));
        }
    }
    bool _showOriginal;

    public string OriginalToggleLabel => ShowOriginal ? "Hide original" : "Show original";
}

/// <summary>
/// The latest dictations, newest first, for the Dictation page. Kept in memory only, so nothing dictated is ever
/// written to disk, and only while the user has the history switched on in Settings.
/// </summary>
public sealed class DictationHistory(AppSettings settings, Dispatcher ui)
{
    public const int Capacity = 100;

    public ObservableCollection<DictationEntry> Entries { get; } = [];

    /// <summary>Adds a dictation at the top, unless the history is off. Safe to call from any thread.</summary>
    public void Add(DictationEntry entry)
    {
        if (!settings.KeepHistory || string.IsNullOrWhiteSpace(entry.Text)) return;
        OnUi(() =>
        {
            Entries.Insert(0, entry);
            while (Entries.Count > Capacity) Entries.RemoveAt(Entries.Count - 1);
        });
    }

    public void Remove(DictationEntry entry) => OnUi(() => Entries.Remove(entry));

    public void Clear() => OnUi(Entries.Clear);

    void OnUi(Action change)
    {
        if (ui.CheckAccess()) change();
        else ui.BeginInvoke(change);
    }
}
