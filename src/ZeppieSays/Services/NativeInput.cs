using System.Runtime.InteropServices;

namespace ZeppieSays.Services;

/// <summary>Synthetic keyboard input through SendInput.</summary>
public static class NativeInput
{
    const uint KEYEVENTF_KEYUP = 0x0002;
    public const ushort VK_CONTROL = 0x11, VK_V = 0x56, VK_C = 0x43;
    /// <summary>An unassigned key code; pressing it has no effect except breaking up "Win/Alt pressed alone".</summary>
    const ushort VK_MASK = 0xE8;

    /// <summary>Marks keystrokes sent by ZeppieSays so its own keyboard hook can recognise and ignore them.</summary>
    public static readonly IntPtr Signature = new(0x5A455050); // "ZEPP"

    /// <summary>Presses and releases the given keys in order, e.g. Ctrl+V as [Ctrl down, V down, V up, Ctrl up].</summary>
    public static uint Keys(params (ushort Vk, bool Up)[] keys)
    {
        var inputs = keys.Select(k => new INPUT
        {
            type = 1,
            ki = new KEYBDINPUT { wVk = k.Vk, dwFlags = k.Up ? KEYEVENTF_KEYUP : 0, dwExtraInfo = Signature },
        }).ToArray();
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    /// <summary>
    /// Windows opens the Start menu when Win is released without another key in between, and Alt does
    /// the same for menu bars. A hotkey like Ctrl+Win would trigger that, so we slip in a dummy key.
    /// </summary>
    public static void MaskMenu() => Keys((VK_MASK, false), (VK_MASK, true));

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    // INPUT is a union; pad it to the size of the largest member (MOUSEINPUT) so SendInput accepts it.
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public KEYBDINPUT ki;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint count, INPUT[] inputs, int size);
}
