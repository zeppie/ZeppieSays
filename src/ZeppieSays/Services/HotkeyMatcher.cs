using ZeppieSays.Core;

namespace ZeppieSays.Services;

/// <summary>What the keyboard hook should do with one key event.</summary>
public readonly record struct HotkeyAction(
    bool Swallow,
    /// <summary>Binding index whose hotkey just went down.</summary>
    int? Pressed = null,
    /// <summary>Binding index whose hotkey was just let go.</summary>
    int? Released = null,
    /// <summary>Binding index whose modifier-only hotkey turned out to be part of another shortcut (Ctrl+Win+D).</summary>
    int? Interrupted = null,
    /// <summary>Send a harmless key so releasing Win or Alt doesn't open the Start menu or a menu bar.</summary>
    bool MaskMenu = false);

/// <summary>
/// Tracks which keys are down and recognises the dictation hotkeys, including combinations.
/// A hotkey fires only when exactly its modifiers are held: F8 doesn't fire while Ctrl is down,
/// so Ctrl+F8 stays free for other apps.
/// </summary>
public sealed class HotkeyMatcher
{
    readonly HashSet<int> _down = [];
    readonly HashSet<int> _swallowed = [];
    Hotkey?[] _hotkeys = [];
    int _active = -1;

    public void SetHotkeys(params Hotkey?[] hotkeys)
    {
        _hotkeys = hotkeys;
        _active = -1;
    }

    public HotkeyAction OnKey(int vk, bool down) => down ? OnDown(vk) : OnUp(vk);

    /// <summary>
    /// Asks Windows whether a key is really still down. Key-ups can go missing (e.g. Win+L locks the PC
    /// before Win is released), which would otherwise leave a key "held" forever and block the hotkeys.
    /// </summary>
    public Func<int, bool>? StillDown { get; set; }

    HotkeyAction OnDown(int vk)
    {
        if (StillDown is { } check)
            _down.RemoveWhere(k => k != vk && !check(k));
        var repeat = !_down.Add(vk);

        if (_active >= 0 && _hotkeys[_active] is { } held)
        {
            if (vk == held.MainKey) return new(Swallow: true); // auto-repeat of the held key
            if (held.Involves(vk)) return new(Swallow: false);
            // Another key joined a modifier-only hotkey: the user is typing a shortcut, not dictating.
            if (!repeat && held.MainKey is null)
            {
                var interrupted = _active;
                _active = -1;
                return new(Swallow: false, Interrupted: interrupted);
            }
            return new(Swallow: _swallowed.Contains(vk));
        }
        if (repeat) return new(Swallow: _swallowed.Contains(vk));

        for (var i = 0; i < _hotkeys.Length; i++)
        {
            if (_hotkeys[i] is not { } hotkey || !hotkey.Involves(vk) || !ExactlyHeld(hotkey)) continue;
            _active = i;
            if (hotkey.MainKey == vk) _swallowed.Add(vk); // the key itself never reaches the app
            return new(Swallow: hotkey.MainKey == vk, Pressed: i, MaskMenu: hotkey.UsesWinOrAlt);
        }
        return new(Swallow: false);
    }

    HotkeyAction OnUp(int vk)
    {
        _down.Remove(vk);
        var swallow = _swallowed.Remove(vk);
        if (_active >= 0 && _hotkeys[_active] is { } held && held.Involves(vk))
        {
            var released = _active;
            _active = -1;
            return new(Swallow: swallow, Released: released);
        }
        return new(Swallow: swallow);
    }

    /// <summary>All of the hotkey's keys are down and nothing else is: no extra modifiers, no other keys.</summary>
    bool ExactlyHeld(Hotkey hotkey)
    {
        if (hotkey.MainKey is { } main && !_down.Contains(main)) return false;
        if (!hotkey.Modifiers.All(m => m.Keys.Any(_down.Contains))) return false;
        return _down.All(k => k == hotkey.MainKey || hotkey.Modifiers.Any(m => m.IsPressedBy(k)));
    }

    /// <summary>Forgets held keys, e.g. after the workstation was locked and key-ups were never seen.</summary>
    public void Reset()
    {
        _down.Clear();
        _swallowed.Clear();
        _active = -1;
    }
}
