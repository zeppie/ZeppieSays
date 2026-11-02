using System.Runtime.InteropServices;
using System.Windows;
using ZeppieSays.Core;

namespace ZeppieSays.Services;

/// <summary>
/// For an app that doesn't tell UI Automation what's selected: copies the selection with Ctrl+C, reads it, and puts
/// the user's clipboard back. Only for an Ask key's question, once the hotkey is released.
/// </summary>
public static class SelectionCopier
{
    /// <summary>Ctrl+C in a terminal stops what's running instead of copying.</summary>
    static readonly HashSet<string> Terminals = new(StringComparer.OrdinalIgnoreCase)
    {
        "WindowsTerminal", "OpenConsole", "conhost", "cmd", "powershell", "pwsh", "wezterm-gui", "alacritty",
        "mintty", "putty", "kitty", "Tabby", "Hyper", "wsl", "bash",
    };

    public static bool CanCopyFrom(string processName) => !Terminals.Contains(processName);

    /// <summary>The selected text in that window, or null when nothing is selected (the app then copies nothing).</summary>
    public static async Task<string?> CopyAsync(ScreenContext context)
    {
        if (context.Window == IntPtr.Zero || !CanCopyFrom(context.ProcessName) || GetForegroundWindow() != context.Window)
            return null;
        // With the hotkey's Ctrl, Win or Alt still down, Ctrl+C would become another shortcut.
        for (var waited = 0; waited < 1000 && ModifierDown(); waited += 25) await Task.Delay(25);
        if (ModifierDown()) return null;

        var saved = TextInserter.SaveClipboard();
        var before = GetClipboardSequenceNumber();
        NativeInput.Keys((NativeInput.VK_CONTROL, false), (NativeInput.VK_C, false), (NativeInput.VK_C, true), (NativeInput.VK_CONTROL, true));
        for (var waited = 0; waited < 500 && GetClipboardSequenceNumber() == before; waited += 25) await Task.Delay(25);
        if (GetClipboardSequenceNumber() == before) return null; // nothing selected, so nothing was copied

        string? text = null;
        await TextInserter.Retry(() => text = Clipboard.ContainsText() ? Clipboard.GetText() : null);
        await TextInserter.Retry(() =>
        {
            if (saved is not null) Clipboard.SetDataObject(saved, copy: true);
            else Clipboard.Clear();
        });
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    static bool ModifierDown() =>
        new[] { 0x10 /* Shift */, 0x11 /* Ctrl */, 0x12 /* Alt */, 0x5B /* left Win */, 0x5C /* right Win */ }
            .Any(vk => (GetAsyncKeyState(vk) & 0x8000) != 0);

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
}
