using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using ZeppieSays.Core;

namespace ZeppieSays.Services;

/// <summary>What was on screen when dictation started. Everything stays on this machine.</summary>
public sealed record ScreenContext(
    IntPtr Window,
    string ProcessName,
    string Title,
    bool IsCodeEditor,
    /// <summary>Text just before the cursor, or null when the app does not expose it.</summary>
    string? BeforeText,
    string? AfterText,
    IReadOnlyList<string> Identifiers)
{
    public static readonly ScreenContext Empty = new(IntPtr.Zero, "", "", false, null, null, []);

    /// <summary>The selection in the focused control, or null when nothing is selected or the app doesn't expose it.</summary>
    public string? SelectedText { get; init; }

    /// <summary>All the text OCR read in the window, for a question about what's on screen.</summary>
    public string? WindowText { get; init; }
}

public static partial class ContextCapture
{
    static readonly HashSet<string> CodeEditors = new(StringComparer.OrdinalIgnoreCase)
    {
        "code", "code - insiders", "cursor", "windsurf", "devenv", "rider64", "idea64", "pycharm64",
        "webstorm64", "notepad++", "sublime_text", "zed",
    };

    static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "msedge", "chrome", "firefox", "brave", "opera", "vivaldi", "arc", "zen",
    };

    const int MaxSurroundingChars = 500;
    const int MaxSelectedChars = 6000;

    /// <summary>Only the window and process; cheap enough to run on every key press.</summary>
    public static ScreenContext Foreground()
    {
        var hwnd = GetForegroundWindow();
        GetWindowThreadProcessId(hwnd, out var pid);
        var process = "";
        try { process = Process.GetProcessById((int)pid).ProcessName; } catch { }
        var title = new StringBuilder(512);
        GetWindowText(hwnd, title, title.Capacity);
        return ScreenContext.Empty with
        {
            Window = hwnd,
            ProcessName = process,
            Title = title.ToString(),
            IsCodeEditor = CodeEditors.Contains(process),
        };
    }

    /// <summary>
    /// For a browser, adds the open tab's title and site to the window title. A browser window's title doesn't always
    /// name the page (Edge workspaces show just "Work"), and website rules match on it. Takes a few hundred ms.
    /// </summary>
    public static async Task<ScreenContext> AddBrowserPageAsync(ScreenContext basic)
    {
        if (!Browsers.Contains(basic.ProcessName) || basic.Window == IntPtr.Zero) return basic;
        var (name, url) = await WithTimeout(Task.Run(() => ReadBrowserPage(basic.Window)), TimeSpan.FromSeconds(2), (null, null));
        return basic with { Title = WithPage(basic.Title, name, url) };
    }

    /// <summary>"Inbox - Outlook (outlook.office.com) - Work": the page title and site, then the window title if it adds anything.</summary>
    public static string WithPage(string windowTitle, string? pageName, string? url)
    {
        var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.Host : null;
        var page = string.Join(' ', new[] { pageName?.Trim(), host is null ? null : $"({host})" }.Where(s => !string.IsNullOrEmpty(s)));
        if (page.Length == 0) return windowTitle;
        if (string.IsNullOrWhiteSpace(pageName) || !windowTitle.Contains(pageName.Trim(), StringComparison.OrdinalIgnoreCase))
            return windowTitle.Length == 0 ? page : $"{page} - {windowTitle}";
        return host is null ? windowTitle : $"{windowTitle} ({host})";
    }

    /// <summary>The web page in a browser window: its title and address, through UI Automation.</summary>
    static (string?, string?) ReadBrowserPage(IntPtr window)
    {
        var document = AutomationElement.FromHandle(window).FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));
        if (document is null) return (null, null);
        var url = document.TryGetCurrentPattern(ValuePattern.Pattern, out var v) && v is ValuePattern value ? value.Current.Value : null;
        return (document.Current.Name, url);
    }

    /// <summary>Adds the text around the cursor and the identifiers visible in the window.</summary>
    public static async Task<ScreenContext> EnrichAsync(ScreenContext basic, CancellationToken ct)
    {
        var surrounding = WithTimeout(Task.Run(ReadAroundCursor, ct), TimeSpan.FromMilliseconds(800), (null, null, null));
        var ocr = WithTimeout(OcrWindowAsync(basic.Window), TimeSpan.FromSeconds(3), "");
        await Task.WhenAll(surrounding, ocr);

        var (before, after, selected) = surrounding.Result;
        var source = string.Join('\n', basic.Title, ocr.Result, before, after);
        return basic with
        {
            BeforeText = before, AfterText = after, Identifiers = ExtractIdentifiers(source),
            SelectedText = selected, WindowText = ocr.Result,
        };
    }

    static async Task<T> WithTimeout<T>(Task<T> task, TimeSpan timeout, T fallback)
    {
        try
        {
            var done = await Task.WhenAny(task, Task.Delay(timeout));
            return done == task ? await task : fallback;
        }
        catch (Exception e)
        {
            Log.Warn($"Context capture step failed: {e.Message}");
            return fallback;
        }
    }

    /// <summary>
    /// Reads text before and after the caret, and what is selected, through UI Automation, if the focused control
    /// supports it.
    /// </summary>
    static (string?, string?, string?) ReadAroundCursor()
    {
        var element = AutomationElement.FocusedElement;
        if (element is null) return (null, null, null);
        var selected = ReadSelection(element);

        if (element.TryGetCurrentPattern(TextPattern.Pattern, out var p) && p is TextPattern text)
        {
            var selection = text.GetSelection();
            if (selection.Length == 0) return (null, null, selected);
            var caret = selection[0];

            var before = caret.Clone();
            before.MoveEndpointByRange(TextPatternRangeEndpoint.End, caret, TextPatternRangeEndpoint.Start);
            before.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -MaxSurroundingChars);

            var after = caret.Clone();
            after.MoveEndpointByRange(TextPatternRangeEndpoint.Start, caret, TextPatternRangeEndpoint.End);
            after.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, MaxSurroundingChars);

            return (before.GetText(MaxSurroundingChars), after.GetText(MaxSurroundingChars), selected);
        }

        // Plain inputs without a text pattern: we know the content but not the caret, so assume the end.
        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var v) && v is ValuePattern value
            && !element.Current.IsPassword)
        {
            var content = value.Current.Value ?? "";
            return (content.Length > MaxSurroundingChars ? content[^MaxSurroundingChars..] : content, "", selected);
        }
        return (null, null, selected);
    }

    /// <summary>
    /// The selected text: in the focused control, or else in the page or document around it. A word selected in a
    /// Teams message or on a web page is selected on the page while the focus stays in, say, the message box.
    /// </summary>
    static string? ReadSelection(AutomationElement focused)
    {
        var walker = TreeWalker.ControlViewWalker;
        for (var element = focused; element is not null; element = walker.GetParent(element))
        {
            try
            {
                if (element.TryGetCurrentPattern(TextPattern.Pattern, out var p) && p is TextPattern text)
                    foreach (var range in text.GetSelection())
                        if (range.GetText(MaxSelectedChars) is { } selected && !string.IsNullOrWhiteSpace(selected))
                            return selected;
            }
            catch (Exception e) when (e is InvalidOperationException or ElementNotAvailableException)
            {
                // this control can't report a selection; its container may
            }
            if (element.Current.ControlType == ControlType.Window) break; // the app's window: no further
        }
        return null;
    }

    /// <summary>Runs the built-in Windows OCR on the foreground window.</summary>
    static async Task<string> OcrWindowAsync(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || DwmGetWindowAttribute(hwnd, 9 /* extended frame bounds */, out RECT r, Marshal.SizeOf<RECT>()) != 0)
            return "";
        var width = r.Right - r.Left;
        var height = r.Bottom - r.Top;
        if (width < 50 || height < 50) return "";

        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null) return "";

        using var bitmap = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(bitmap))
            g.CopyFromScreen(r.Left, r.Top, 0, 0, bitmap.Size);

        using var png = new MemoryStream();
        bitmap.Save(png, System.Drawing.Imaging.ImageFormat.Png);
        png.Position = 0;
        var decoder = await BitmapDecoder.CreateAsync(png.AsRandomAccessStream());
        var scale = Math.Min(1.0, (double)OcrEngine.MaxImageDimension / Math.Max(width, height));
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)(width * scale),
            ScaledHeight = (uint)(height * scale),
        };
        using var software = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        var result = await engine.RecognizeAsync(software);
        return string.Join('\n', result.Lines.Select(l => l.Text));
    }

    /// <summary>
    /// Picks out words that look like code: camelCase, PascalCase with several humps, snake_case,
    /// and file names. Ordinary prose words are left out so they do not crowd the prompt.
    /// </summary>
    public static IReadOnlyList<string> ExtractIdentifiers(string text) =>
        IdentifierRegex().Matches(text)
            .Select(m => m.Value.Trim('.', '_'))
            .Where(w => w.Length is >= 4 and <= 40)
            .GroupBy(w => w)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .Take(120)
            .ToList();

    [GeneratedRegex(@"\b(?:[a-z]+(?:[A-Z][a-z0-9]*)+|[A-Z][a-z0-9]+(?:[A-Z][a-z0-9]*)+|[A-Za-z][A-Za-z0-9]*(?:_[A-Za-z0-9]+)+|[\w-]+\.(?:cs|ts|tsx|js|jsx|py|go|rs|java|kt|json|md|yml|yaml|xaml|sql|css|html))\b")]
    private static partial Regex IdentifierRegex();

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);
}
