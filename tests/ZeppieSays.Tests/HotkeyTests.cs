using ZeppieSays.Core;
using ZeppieSays.Services;

namespace ZeppieSays.Tests;

public class HotkeyTests
{
    const int F8 = 0x77, F9 = 0x78, Space = 0x20, D = 0x44, C = 0x43;
    const int LCtrl = 0xA2, RCtrl = 0xA3, LShift = 0xA0, LAlt = 0xA4, LWin = 0x5B;

    [Theory]
    [InlineData("F8", "F8")]
    [InlineData("ctrl+win", "Ctrl+Win")]
    [InlineData("Win + Ctrl", "Ctrl+Win")]
    [InlineData("Ctrl+Shift+Space", "Ctrl+Shift+Space")]
    [InlineData("Right Ctrl", "Right Ctrl")]
    [InlineData("Alt+F13", "Alt+F13")]
    public void Parses_and_normalizes(string text, string expected) =>
        Assert.Equal(expected, Hotkey.Parse(text)!.Text);

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+Ctrl")]
    [InlineData("F8+F9")]
    [InlineData("Banana")]
    public void Rejects_nonsense(string text) => Assert.Null(Hotkey.Parse(text));

    [Fact]
    public void Recorded_combination_uses_either_side_modifiers() =>
        Assert.Equal("Ctrl+Win", Hotkey.FromPressed([LWin, RCtrl])!.Text);

    [Fact]
    public void Recorded_single_modifier_keeps_its_side() =>
        Assert.Equal("Right Ctrl", Hotkey.FromPressed([RCtrl])!.Text);

    [Fact]
    public void Recorded_key_with_modifiers() =>
        Assert.Equal("Ctrl+Shift+Space", Hotkey.FromPressed([Space, LShift, LCtrl])!.Text);

    [Theory]
    [InlineData("A")]
    [InlineData("Space")]
    [InlineData("Ctrl+Esc")]
    [InlineData("Left Ctrl")]
    [InlineData("Right Shift")]
    public void Flags_hotkeys_that_get_in_the_way(string text) => Assert.NotNull(Hotkey.Parse(text)!.Problem());

    [Theory]
    [InlineData("F8")]
    [InlineData("Ctrl+Win")]
    [InlineData("Ctrl+Shift+Space")]
    [InlineData("Right Ctrl")]
    [InlineData("Caps Lock")]
    public void Accepts_good_hotkeys(string text) => Assert.Null(Hotkey.Parse(text)!.Problem());

    static HotkeyMatcher Matcher(params string[] hotkeys)
    {
        var matcher = new HotkeyMatcher();
        matcher.SetHotkeys(hotkeys.Select(Hotkey.Parse).ToArray());
        return matcher;
    }

    [Fact]
    public void Single_key_is_swallowed_and_reported()
    {
        var m = Matcher("F8", "F9");
        Assert.Equal(new HotkeyAction(true, Pressed: 0), m.OnKey(F8, true));
        Assert.Equal(new HotkeyAction(true), m.OnKey(F8, true)); // auto-repeat
        Assert.Equal(new HotkeyAction(true, Released: 0), m.OnKey(F8, false));
    }

    [Fact]
    public void Single_key_does_not_fire_with_extra_modifiers()
    {
        var m = Matcher("F8");
        m.OnKey(LCtrl, true);
        Assert.Equal(new HotkeyAction(false), m.OnKey(F8, true)); // Ctrl+F8 belongs to other apps
    }

    [Fact]
    public void Ctrl_win_fires_when_both_are_down_and_masks_the_start_menu()
    {
        var m = Matcher("Ctrl+Win");
        Assert.Equal(new HotkeyAction(false), m.OnKey(LCtrl, true));
        Assert.Equal(new HotkeyAction(false, Pressed: 0, MaskMenu: true), m.OnKey(LWin, true));
        Assert.Equal(new HotkeyAction(false, Released: 0), m.OnKey(LWin, false));
        Assert.Equal(new HotkeyAction(false), m.OnKey(LCtrl, false));
    }

    [Fact]
    public void Ctrl_win_followed_by_another_key_is_an_interruption()
    {
        var m = Matcher("Ctrl+Win");
        m.OnKey(LWin, true);
        m.OnKey(LCtrl, true);
        Assert.Equal(new HotkeyAction(false, Interrupted: 0), m.OnKey(D, true)); // Ctrl+Win+D: new desktop
        Assert.Equal(new HotkeyAction(false), m.OnKey(LCtrl, false));
    }

    [Fact]
    public void Ctrl_win_does_not_fire_while_another_key_is_held()
    {
        var m = Matcher("Ctrl+Win");
        m.OnKey(C, true);
        m.OnKey(LCtrl, true);
        Assert.Null(m.OnKey(LWin, true).Pressed);
    }

    [Fact]
    public void Key_with_modifiers_swallows_only_the_key()
    {
        var m = Matcher("Ctrl+Shift+Space");
        Assert.False(m.OnKey(LCtrl, true).Swallow);
        Assert.False(m.OnKey(LShift, true).Swallow);
        Assert.Equal(new HotkeyAction(true, Pressed: 0), m.OnKey(Space, true));
        // Letting go of a modifier first ends the hotkey; the key's own release is still swallowed.
        Assert.Equal(new HotkeyAction(false, Released: 0), m.OnKey(LShift, false));
        Assert.Equal(new HotkeyAction(true), m.OnKey(Space, false));
    }

    [Fact]
    public void Alt_combination_masks_the_menu_bar()
    {
        var m = Matcher("Alt+F13");
        m.OnKey(LAlt, true);
        Assert.True(m.OnKey(0x7C, true).MaskMenu);
    }

    [Fact]
    public void Right_ctrl_alone_does_not_match_left_ctrl()
    {
        var m = Matcher("Right Ctrl");
        Assert.Null(m.OnKey(LCtrl, true).Pressed);
        m.OnKey(LCtrl, false);
        Assert.Equal(0, m.OnKey(RCtrl, true).Pressed);
    }

    [Fact]
    public void Second_binding_works_independently()
    {
        var m = Matcher("Ctrl+Win", "F9");
        Assert.Equal(1, m.OnKey(F9, true).Pressed);
        m.OnKey(F9, false);
        m.OnKey(LCtrl, true);
        Assert.Equal(0, m.OnKey(LWin, true).Pressed);
    }

    [Fact]
    public void Forgets_keys_whose_release_was_missed()
    {
        var m = Matcher("F8");
        m.StillDown = _ => false; // Windows says nothing else is held
        m.OnKey(LWin, true);      // Win+L locked the PC; Win's key-up never arrived
        Assert.Equal(0, m.OnKey(F8, true).Pressed);
    }
}
