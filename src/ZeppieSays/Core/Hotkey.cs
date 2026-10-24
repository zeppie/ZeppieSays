namespace ZeppieSays.Core;

public enum ModifierKind { None, Ctrl, Shift, Alt, Win }

/// <summary>A modifier a hotkey needs held: either side ("Ctrl") or one specific key ("Right Ctrl").</summary>
public sealed record Modifier(string Name, ModifierKind Kind, int[] Keys)
{
    public bool IsPressedBy(int vk) => Keys.Contains(vk);
}

/// <summary>
/// A dictation hotkey: zero or more modifiers plus at most one other key. Written as text such as
/// "F8", "Ctrl+Win", "Ctrl+Shift+Space" or "Right Ctrl", which is also how it is stored in settings.
/// </summary>
public sealed class Hotkey
{
    public IReadOnlyList<Modifier> Modifiers { get; }
    /// <summary>The non-modifier key, or null for modifier-only hotkeys like Ctrl+Win.</summary>
    public int? MainKey { get; }
    public string Text { get; }

    Hotkey(IReadOnlyList<Modifier> modifiers, int? mainKey)
    {
        Modifiers = modifiers;
        MainKey = mainKey;
        Text = string.Join("+", modifiers.Select(m => m.Name).Concat(mainKey is { } k ? [NameOf(k)] : []));
    }

    /// <summary>Releasing Win or Alt on its own opens the Start menu or a menu bar, which needs suppressing.</summary>
    public bool UsesWinOrAlt => Modifiers.Any(m => m.Kind is ModifierKind.Win or ModifierKind.Alt);

    public bool Involves(int vk) => vk == MainKey || Modifiers.Any(m => m.IsPressedBy(vk));

    public override string ToString() => Text;

    // ---- Modifier keys -------------------------------------------------------------------------

    const int LShift = 0xA0, RShift = 0xA1, LCtrl = 0xA2, RCtrl = 0xA3, LAlt = 0xA4, RAlt = 0xA5, LWin = 0x5B, RWin = 0x5C;

    static readonly Modifier[] KnownModifiers =
    [
        new("Ctrl", ModifierKind.Ctrl, [LCtrl, RCtrl]),
        new("Shift", ModifierKind.Shift, [LShift, RShift]),
        new("Alt", ModifierKind.Alt, [LAlt, RAlt]),
        new("Win", ModifierKind.Win, [LWin, RWin]),
        new("Left Ctrl", ModifierKind.Ctrl, [LCtrl]), new("Right Ctrl", ModifierKind.Ctrl, [RCtrl]),
        new("Left Shift", ModifierKind.Shift, [LShift]), new("Right Shift", ModifierKind.Shift, [RShift]),
        new("Left Alt", ModifierKind.Alt, [LAlt]), new("Right Alt", ModifierKind.Alt, [RAlt]),
        new("Left Win", ModifierKind.Win, [LWin]), new("Right Win", ModifierKind.Win, [RWin]),
    ];

    /// <summary>Which modifier a virtual key is, if any. Windows also reports generic codes (0x10-0x12) now and then.</summary>
    public static ModifierKind KindOf(int vk) => vk switch
    {
        LCtrl or RCtrl or 0x11 => ModifierKind.Ctrl,
        LShift or RShift or 0x10 => ModifierKind.Shift,
        LAlt or RAlt or 0x12 => ModifierKind.Alt,
        LWin or RWin => ModifierKind.Win,
        _ => ModifierKind.None,
    };

    static Modifier Generic(ModifierKind kind) => KnownModifiers.First(m => m.Kind == kind);

    // ---- Other keys ----------------------------------------------------------------------------

    static readonly Dictionary<int, string> Names = BuildNames();

    static Dictionary<int, string> BuildNames()
    {
        var names = new Dictionary<int, string>
        {
            [0x08] = "Backspace", [0x09] = "Tab", [0x0D] = "Enter", [0x13] = "Pause", [0x14] = "Caps Lock",
            [0x1B] = "Esc", [0x20] = "Space", [0x21] = "Page Up", [0x22] = "Page Down", [0x23] = "End",
            [0x24] = "Home", [0x25] = "Left", [0x26] = "Up", [0x27] = "Right", [0x28] = "Down",
            [0x2C] = "Print Screen", [0x2D] = "Insert", [0x2E] = "Delete", [0x5D] = "Menu", [0x91] = "Scroll Lock",
        };
        for (var i = 0; i < 24; i++) names[0x70 + i] = $"F{i + 1}";
        for (var c = '0'; c <= '9'; c++) names[c] = c.ToString();
        for (var c = 'A'; c <= 'Z'; c++) names[c] = c.ToString();
        for (var i = 0; i <= 9; i++) names[0x60 + i] = $"Num {i}";
        return names;
    }

    public static string NameOf(int vk) => Names.TryGetValue(vk, out var name) ? name : $"Key {vk:X2}";

    static int? VkOf(string name)
    {
        foreach (var (vk, n) in Names)
            if (n.Equals(name, StringComparison.OrdinalIgnoreCase)) return vk;
        return name.StartsWith("Key ", StringComparison.OrdinalIgnoreCase)
               && int.TryParse(name[4..], System.Globalization.NumberStyles.HexNumber, null, out var code)
            ? code : null;
    }

    // ---- Text form -----------------------------------------------------------------------------

    /// <summary>Reads "Ctrl+Win", "F8", "Right Ctrl", ... Returns null for text it doesn't understand.</summary>
    public static Hotkey? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var modifiers = new List<Modifier>();
        int? main = null;
        foreach (var part in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var modifier = KnownModifiers.FirstOrDefault(m => m.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
            if (modifier is not null)
            {
                if (modifiers.Any(m => m.Kind == modifier.Kind)) return null;
                modifiers.Add(modifier);
            }
            else if (main is null && VkOf(part) is { } vk) main = vk;
            else return null;
        }
        return modifiers.Count == 0 && main is null ? null : new Hotkey(Ordered(modifiers), main);
    }

    /// <summary>
    /// Names the keys a user held down together in the key recorder. A lone modifier keeps its side
    /// ("Right Ctrl"); in a combination either side counts ("Ctrl+Win").
    /// </summary>
    public static Hotkey? FromPressed(IReadOnlyCollection<int> keys)
    {
        var modifierKeys = keys.Where(k => KindOf(k) != ModifierKind.None).ToList();
        var others = keys.Where(k => KindOf(k) == ModifierKind.None).ToList();
        if (others.Count > 1) return null;

        if (others.Count == 0 && modifierKeys.Count == 1)
            return Parse(KnownModifiers.FirstOrDefault(m => m.Keys is [var only] && only == modifierKeys[0])?.Name);

        var modifiers = modifierKeys.Select(KindOf).Distinct().Select(Generic).ToList();
        if (modifiers.Count == 0 && others.Count == 0) return null;
        return new Hotkey(Ordered(modifiers), others.Count == 1 ? others[0] : null);
    }

    static List<Modifier> Ordered(IEnumerable<Modifier> modifiers) => modifiers.OrderBy(m => m.Kind).ToList();

    static readonly HashSet<int> TypingKeys = BuildTypingKeys();

    static HashSet<int> BuildTypingKeys()
    {
        var keys = new HashSet<int> { 0x08, 0x09, 0x0D, 0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2E };
        for (var c = '0'; c <= '9'; c++) keys.Add(c);
        for (var c = 'A'; c <= 'Z'; c++) keys.Add(c);
        for (var i = 0x60; i <= 0x69; i++) keys.Add(i);
        return keys;
    }

    /// <summary>Why this hotkey would get in the way of normal use, or null when it is fine.</summary>
    public string? Problem()
    {
        if (MainKey == 0x1B) return "Esc is used to cancel a dictation.";
        if (Modifiers.Count == 0 && MainKey is { } key && TypingKeys.Contains(key))
            return $"{NameOf(key)} alone would block normal typing. Add Ctrl, Alt or Win, or pick a function key.";
        if (MainKey is null && Modifiers.Count == 1 && Modifiers[0].Name is not ("Right Ctrl" or "Right Alt"))
            return $"{Modifiers[0].Name} alone would get in the way of shortcuts. Combine it with another key.";
        return null;
    }
}
