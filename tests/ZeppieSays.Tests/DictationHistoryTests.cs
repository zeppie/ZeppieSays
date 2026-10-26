using System.Windows.Threading;
using ZeppieSays.Core;
using ZeppieSays.UI;

namespace ZeppieSays.Tests;

public class DictationHistoryTests
{
    static DictationEntry Entry(string text, string? original = null) =>
        new(DateTime.Today.AddHours(14).AddMinutes(32), text, original, "outlook", original is null ? null : "Casual");

    static DictationHistory History(bool on) => new(new AppSettings { KeepHistory = on }, Dispatcher.CurrentDispatcher);

    [Fact]
    public void Is_off_until_the_user_turns_it_on()
    {
        Assert.False(new AppSettings().KeepHistory);
        var history = History(on: false);
        history.Add(Entry("hello"));
        Assert.Empty(history.Entries);
    }

    [Fact]
    public void Keeps_the_newest_dictation_on_top()
    {
        var history = History(on: true);
        history.Add(Entry("first"));
        history.Add(Entry("second"));
        Assert.Equal(["second", "first"], history.Entries.Select(e => e.Text));
    }

    [Fact]
    public void Holds_at_most_a_hundred_and_drops_the_oldest()
    {
        var history = History(on: true);
        for (var i = 1; i <= DictationHistory.Capacity + 5; i++) history.Add(Entry($"dictation {i}"));
        Assert.Equal(DictationHistory.Capacity, history.Entries.Count);
        Assert.Equal($"dictation {DictationHistory.Capacity + 5}", history.Entries[0].Text);
        Assert.Equal("dictation 6", history.Entries[^1].Text);
    }

    [Fact]
    public void Ignores_empty_text_and_can_remove_and_clear()
    {
        var history = History(on: true);
        history.Add(Entry("  "));
        Assert.Empty(history.Entries);

        history.Add(Entry("one"));
        history.Add(Entry("two"));
        history.Remove(history.Entries[0]);
        Assert.Equal(["one"], history.Entries.Select(e => e.Text));
        history.Clear();
        Assert.Empty(history.Entries);
    }

    [Fact]
    public void Shows_the_original_only_when_a_style_changed_the_words()
    {
        Assert.False(Entry("same", original: "same").HasOriginal);
        Assert.False(Entry("text", original: null).HasOriginal);
        var rewritten = Entry("Hello, how are you?", original: "hello how are you");
        Assert.True(rewritten.HasOriginal);
        Assert.Equal("Show original", rewritten.OriginalToggleLabel);
        rewritten.ToggleOriginal.Execute(null);
        Assert.True(rewritten.ShowOriginal);
        Assert.Equal("Hide original", rewritten.OriginalToggleLabel);
    }

    [Fact]
    public void Describes_when_where_and_with_which_style()
    {
        Assert.Equal("14:32 · outlook · Casual", Entry("Hello", original: "hello").Subtitle);
        Assert.Equal("14:32 · outlook", Entry("Hello").Subtitle);
    }
}
