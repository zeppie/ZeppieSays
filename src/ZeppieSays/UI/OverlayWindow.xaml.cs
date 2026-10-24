using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using ZeppieSays.Core;
using ZeppieSays.Services;

namespace ZeppieSays.UI;

/// <summary>
/// The small pill at the bottom of the screen. It never takes focus and lets clicks pass through,
/// so the cursor stays in the app you are dictating into.
/// </summary>
public partial class OverlayWindow : Window
{
    const int Bars = 26;
    const double BarWidth = 2.5, BarGap = 1.5;
    static readonly Brush ListeningBrush = new SolidColorBrush(Color.FromRgb(0xB9, 0xA8, 0xFF));
    static readonly Brush BusyBrush = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF));

    readonly Rectangle[] _bars = new Rectangle[Bars];
    readonly float[] _levels = new float[Bars];
    readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    readonly DispatcherTimer _busyTimer = new() { Interval = TimeSpan.FromMilliseconds(60) };
    float _pendingLevel;
    int _busyTick;
    DictationState _state;

    public OverlayWindow()
    {
        InitializeComponent();
        for (var i = 0; i < Bars; i++)
        {
            _bars[i] = new Rectangle { Width = BarWidth, RadiusX = 1.25, RadiusY = 1.25, Fill = ListeningBrush };
            Canvas.SetLeft(_bars[i], i * (BarWidth + BarGap));
            Wave.Children.Add(_bars[i]);
        }
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); _showingError = false; Hide(); };
        _busyTimer.Tick += (_, _) => AnimateBusy();
        CompositionTarget.Rendering += (_, _) => PushLevel();
        SizeChanged += (_, _) => PlaceOnScreen();
        Draw();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        const int GWL_EXSTYLE = -20;
        const int WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x80, WS_EX_TRANSPARENT = 0x20;
        SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT);
    }

    public void ShowState(DictationState state, KeyBinding? binding, ModelInfo? model)
    {
        _state = state;
        // An error shown at the end of a dictation stays until its timer runs out, even though the flow is now idle.
        if (state == DictationState.Idle && _showingError) return;
        _showingError = false;
        _hideTimer.Stop();
        switch (state)
        {
            case DictationState.Recording:
                KeyLabel.Text = binding?.Key ?? "";
                ModelLabel.Text = Describe(binding, model);
                ModelLabel.Foreground = new SolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF));
                Array.Clear(_levels);
                _busyTimer.Stop();
                SetBarBrush(ListeningBrush);
                PlaceOnScreen();
                Show();
                PlaceOnScreen(); // again, now that the window has its real size on this monitor
                break;
            case DictationState.Transcribing:
                ModelLabel.Text = "Writing…";
                SetBarBrush(BusyBrush);
                _busyTimer.Start();
                break;
            default:
                _busyTimer.Stop();
                Hide();
                break;
        }
    }

    /// <summary>
    /// "Dutch · Whisper large-v3 turbo", just the model name when the model picks the language itself, and
    /// "Dutch → English · …" for a key that translates.
    /// </summary>
    static string Describe(KeyBinding? binding, ModelInfo? model)
    {
        var language = model is { AutoLanguageOnly: true }
            ? null
            : ModelCatalog.Languages.FirstOrDefault(l => l.Code == binding?.Language).Name;
        if (language == "Detect") language = null;
        if (binding is { Translates: true })
        {
            var output = ModelCatalog.Languages.FirstOrDefault(l => l.Code == binding.OutputLanguage).Name ?? binding.OutputLanguage;
            language = language is null ? $"→ {output}" : $"{language} → {output}";
        }
        return string.Join(" · ", new[] { language, model?.Name }.Where(s => s is not null));
    }

    /// <summary>While an LLM rewrites the text: "Writing… · Formal".</summary>
    public void ShowRewriting(string style)
    {
        if (_state == DictationState.Transcribing) ModelLabel.Text = $"Writing… · {style}";
    }

    bool _showingError;

    public void ShowError(string message)
    {
        _showingError = true;
        _busyTimer.Stop();
        KeyLabel.Text = "!";
        ModelLabel.Text = message;
        ModelLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x8A));
        Array.Clear(_levels);
        Draw();
        PlaceOnScreen();
        Show();
        PlaceOnScreen();
        _hideTimer.Start();
    }

    /// <summary>Called from the audio thread; the value is picked up on the next frame.</summary>
    public void OnLevel(float level) => _pendingLevel = Math.Max(_pendingLevel, level);

    DateTime _lastPush;

    void PushLevel()
    {
        if (_state != DictationState.Recording || (DateTime.Now - _lastPush).TotalMilliseconds < 45) return;
        _lastPush = DateTime.Now;
        Array.Copy(_levels, 1, _levels, 0, Bars - 1);
        // Speech peaks are small; a square-root curve makes quiet talking visible.
        _levels[^1] = (float)Math.Min(1, Math.Sqrt(_pendingLevel * 2.2));
        _pendingLevel = 0;
        Draw();
    }

    void AnimateBusy()
    {
        _busyTick++;
        for (var i = 0; i < Bars; i++)
            _levels[i] = (float)(0.4 + 0.3 * Math.Sin((i * 0.45) - _busyTick * 0.35));
        Draw();
    }

    void Draw()
    {
        var height = Wave.Height;
        for (var i = 0; i < Bars; i++)
        {
            var h = Math.Max(3, _levels[i] * height);
            _bars[i].Height = h;
            Canvas.SetTop(_bars[i], (height - h) / 2);
        }
    }

    void SetBarBrush(Brush brush)
    {
        foreach (var bar in _bars) bar.Fill = brush;
    }

    /// <summary>
    /// Bottom centre of the screen the mouse is on, just above the taskbar. Done in physical pixels through
    /// Win32 because monitors can each have their own scaling; WPF rescales the window when it lands on a
    /// monitor with another DPI, which raises SizeChanged and re-centres it.
    /// </summary>
    void PlaceOnScreen()
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        var area = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea;
        if (!GetWindowRect(hwnd, out var rect)) return;
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        var margin = (int)(12 * GetDpiForWindow(hwnd) / 96.0);
        SetWindowPos(hwnd, new IntPtr(-1) /* HWND_TOPMOST */,
            area.Left + (area.Width - width) / 2, area.Bottom - height - margin, 0, 0,
            0x0001 /* NOSIZE */ | 0x0010 /* NOACTIVATE */);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
