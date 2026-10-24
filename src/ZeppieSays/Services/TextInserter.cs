using System.Runtime.InteropServices;
using System.Windows;
using ZeppieSays.Core;

namespace ZeppieSays.Services;

/// <summary>
/// Puts text into whatever app has focus by pasting it, then restores the user's clipboard.
/// Pasting works in every app and keeps accents and emoji intact, unlike simulated typing.
/// </summary>
public static class TextInserter
{
    public static async Task InsertAsync(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        var saved = SaveClipboard();
        var data = new DataObject(DataFormats.UnicodeText, text);
        // Keep dictations out of Windows clipboard history (Win+V) and cloud clipboard sync.
        data.SetData("ExcludeClipboardContentFromMonitorProcessing", new System.IO.MemoryStream(new byte[4]));
        data.SetData("CanIncludeInClipboardHistory", new System.IO.MemoryStream(BitConverter.GetBytes(0)));
        data.SetData("CanUploadToCloudClipboard", new System.IO.MemoryStream(BitConverter.GetBytes(0)));

        if (!await Retry(() => Clipboard.SetDataObject(data, copy: true)))
        {
            Log.Warn("Clipboard busy, could not insert text");
            return;
        }

        var sent = SendCtrlV();
        Log.Info($"Pasted {text.Length} chars into '{ContextCapture.Foreground().ProcessName}' (SendInput: {sent}/4)");
        await Task.Delay(300); // let the target app read the clipboard before we put the old content back

        if (saved is not null)
            await Retry(() =>
            {
                if (Clipboard.ContainsText() && Clipboard.GetText() == text)
                    Clipboard.SetDataObject(saved, copy: true);
            });
    }

    static DataObject? SaveClipboard()
    {
        try
        {
            var current = Clipboard.GetDataObject();
            if (current is null) return null;
            var copy = new DataObject();
            foreach (var format in current.GetFormats(autoConvert: false))
            {
                try
                {
                    if (current.GetData(format, autoConvert: false) is { } value)
                        copy.SetData(format, value);
                }
                catch { /* some formats (e.g. delayed-render) cannot be read; skip them */ }
            }
            return copy;
        }
        catch (Exception e)
        {
            Log.Warn($"Could not save clipboard: {e.Message}");
            return null;
        }
    }

    static async Task<bool> Retry(Action action)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try { action(); return true; }
            catch (COMException) { await Task.Delay(40); }
            catch (ExternalException) { await Task.Delay(40); }
        }
        return false;
    }

    static uint SendCtrlV() => NativeInput.Keys(
        (NativeInput.VK_CONTROL, false), (NativeInput.VK_V, false), (NativeInput.VK_V, true), (NativeInput.VK_CONTROL, true));
}
