using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using ZeppieSays.Core;

namespace ZeppieSays.Services;

/// <summary>
/// Low-level keyboard hook that watches the dictation hotkeys anywhere in Windows. The hotkey's own key
/// (F8 in "F8", Space in "Ctrl+Space") is swallowed so it doesn't reach the app; modifiers pass through.
/// Must be created on a thread with a message loop (the WPF UI thread).
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    const int WH_KEYBOARD_LL = 13;
    const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    const int VK_ESCAPE = 0x1B;

    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct KBDLLHOOKSTRUCT
    {
        public uint vkCode, scanCode, flags, time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, HookProc fn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")]
    static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int vk);
    [DllImport("kernel32.dll")]
    static extern IntPtr GetModuleHandle(string? name);

    readonly HookProc _proc; // kept in a field so the GC does not collect the callback
    readonly IntPtr _hook;
    readonly Dispatcher _ui;
    readonly HotkeyMatcher _matcher = new() { StillDown = vk => GetAsyncKeyState(vk) < 0 };
    KeyRecording? _recording;

    /// <summary>
    /// Raised with the binding index (0 or 1) and the event's timestamp in ms when a hotkey goes down.
    /// The timestamp comes from Windows, so it stays accurate even if the UI is busy.
    /// </summary>
    public event Action<int, uint>? KeyDown;
    public event Action<int, uint>? KeyUp;
    /// <summary>A modifier-only hotkey (like Ctrl+Win) turned out to be the start of another shortcut.</summary>
    public event Action<int>? Interrupted;
    /// <summary>Raised for Escape; return true to swallow it.</summary>
    public Func<bool>? Escape;

    public KeyboardHook(Dispatcher ui)
    {
        _ui = ui;
        _proc = Callback;
        using var module = Process.GetCurrentProcess().MainModule!;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(module.ModuleName), 0);
        if (_hook == IntPtr.Zero)
            throw new InvalidOperationException($"Keyboard hook failed ({Marshal.GetLastWin32Error()})");
    }

    /// <summary>Sets the hotkeys to watch, in binding order. Text that doesn't parse disables that binding.</summary>
    public void Watch(params string[] hotkeys) => _matcher.SetHotkeys(hotkeys.Select(Hotkey.Parse).ToArray());

    /// <summary>
    /// Captures the next key combination instead of dictating: every key is swallowed (so Win doesn't open
    /// Start) until all keys are released. Calls back with the combination, or null when Esc was pressed.
    /// </summary>
    public void Record(Action<Hotkey?> done)
    {
        _recording = new KeyRecording(done);
        _matcher.Reset();
    }

    public void StopRecording() => _recording = null;

    IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0) return CallNextHookEx(_hook, code, wParam, lParam);

        var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        // Keys ZeppieSays sends itself (Ctrl+V, the menu mask) are not the user's and must not match hotkeys.
        // Other injected keys are welcome: AutoHotkey and mouse software send F13-F24 that way.
        if (data.dwExtraInfo == NativeInput.Signature)
            return CallNextHookEx(_hook, code, wParam, lParam);

        var vk = (int)data.vkCode;
        var down = wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN;
        if (!down && wParam != WM_KEYUP && wParam != WM_SYSKEYUP)
            return CallNextHookEx(_hook, code, wParam, lParam);

        try
        {
            if (_recording is { } recording)
            {
                if (recording.OnKey(vk, down) is { } finished)
                {
                    _recording = null;
                    _ui.BeginInvoke(() => recording.Done(finished.Hotkey));
                }
                return 1;
            }

            var action = _matcher.OnKey(vk, down);
            if (action.MaskMenu) _ui.BeginInvoke(NativeInput.MaskMenu);
            if (action.Pressed is { } pressed) KeyDown?.Invoke(pressed, data.time);
            if (action.Released is { } released) KeyUp?.Invoke(released, data.time);
            if (action.Interrupted is { } interrupted) Interrupted?.Invoke(interrupted);
            if (action.Swallow) return 1;
        }
        catch (Exception e)
        {
            Log.Error("Hotkey handler failed", e);
        }

        if (down && vk == VK_ESCAPE && Escape?.Invoke() == true)
            return 1;
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose() => UnhookWindowsHookEx(_hook);

    /// <summary>Collects the keys of one combination while the user presses it in the key recorder.</summary>
    sealed class KeyRecording(Action<Hotkey?> done)
    {
        readonly HashSet<int> _down = [];
        readonly HashSet<int> _all = [];

        public Action<Hotkey?> Done { get; } = done;

        /// <summary>Returns a result once every key is released again (Hotkey null means cancelled).</summary>
        public (Hotkey? Hotkey, bool)? OnKey(int vk, bool down)
        {
            if (down)
            {
                if (vk == VK_ESCAPE && _all.Count == 0) return (null, true);
                _down.Add(vk);
                _all.Add(vk);
                return null;
            }
            _down.Remove(vk);
            return _down.Count == 0 && _all.Count > 0 ? (Hotkey.FromPressed(_all), true) : null;
        }
    }
}
