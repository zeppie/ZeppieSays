using System.Windows;
using ZeppieSays.Core;
using ZeppieSays.Engines;
using ZeppieSays.Llm;
using ZeppieSays.Services;
using ZeppieSays.UI;
using Forms = System.Windows.Forms;

namespace ZeppieSays;

public partial class App : Application
{
    Mutex? _singleInstance;
    AppSettings _settings = null!;
    KeyboardHook? _hook;
    AudioRecorder? _recorder;
    EngineCache? _engines;
    DictationController? _controller;
    Notes.Notetaker? _notetaker;
    MeetingController? _meetings;
    Forms.NotifyIcon? _tray;
    MainWindow? _window;
    OverlayWindow? _overlay;
    ModelManager _models = null!;

    public bool IsQuitting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _singleInstance = new Mutex(true, "ZeppieSays.SingleInstance", out var first);
        if (!first)
        {
            MessageBox.Show("ZeppieSays is already running. Find it in the system tray.", "ZeppieSays");
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Unhandled UI exception", args.Exception);
            args.Handled = true;
        };
        Log.Info("ZeppieSays starting");

        _settings = SettingsStore.Load();
        _models = new ModelManager(_settings);
        _engines = new EngineCache(_models, _settings);
        _recorder = new AudioRecorder();
        _hook = new KeyboardHook(Dispatcher);
        var rewriter = new TextRewriter(_settings);
        rewriter.WarmUpDefault(); // a model load takes seconds; do it now rather than on the first dictation
        var usage = UsageStore.Load();
        _controller = new DictationController(_settings, _recorder, _engines, rewriter, usage, _hook, Dispatcher);

        var summarizer = new Notes.Summarizer(rewriter);
        var voices = Notes.VoiceLibrary.Load();
        var controller = _controller;
        _notetaker = new Notes.Notetaker(_settings, _engines, _models, summarizer, voices,
            () => controller.State != DictationState.Idle, Dispatcher);
        _meetings = new MeetingController(_settings, Dispatcher);

        var model = new MainViewModel(_settings, _models, _engines, _hook, rewriter, usage, _notetaker, summarizer, voices, _meetings);
        model.Notetaker.Start();
        _window = new MainWindow(model);
        _overlay = new OverlayWindow();
        new System.Windows.Interop.WindowInteropHelper(_overlay).EnsureHandle(); // so the first dictation shows it instantly

        _controller.StateChanged += (state, binding) => Dispatcher.BeginInvoke(() =>
            _overlay.ShowState(state, binding, binding is null ? null : _models.Find(binding.ModelId)?.Info));
        _controller.LevelChanged += _overlay.OnLevel;
        _controller.Failed += message => Dispatcher.BeginInvoke(() => _overlay.ShowError(message));
        _controller.Rewriting += style => Dispatcher.BeginInvoke(() => _overlay.ShowRewriting(style));

        CreateTray();

        var background = e.Args.Contains("--background");
        if (!background || model.HasNoModels)
            ShowMainWindow();
    }

    void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open ZeppieSays", null, (_, _) => ShowMainWindow());
        var copyUnedited = menu.Items.Add("Copy last dictation (unedited)", null, (_, _) =>
        {
            if (_controller?.LastUnedited is { Length: > 0 } text) Clipboard.SetText(text.Trim());
        });
        menu.Opening += (_, _) => copyUnedited.Enabled = _controller?.LastUnedited is { Length: > 0 };
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Quit());

        var iconStream = GetResourceStream(new Uri("pack://application:,,,/Assets/zeppie.ico"))!.Stream;
        _tray = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(iconStream, Forms.SystemInformation.SmallIconSize), // the tray's own size, not a scaled 32px
            Text = "ZeppieSays",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left) ShowMainWindow();
        };
    }

    void ShowMainWindow()
    {
        if (_window is null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    void Quit()
    {
        IsQuitting = true;
        _window?.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_tray is not null) _tray.Visible = false;
        _tray?.Dispose();
        _controller?.Dispose();
        _meetings?.Dispose(); // saves a running recording first
        _notetaker?.Dispose();
        _hook?.Dispose();
        _recorder?.Dispose();
        _engines?.Dispose();
        _singleInstance?.Dispose();
        Log.Info("ZeppieSays stopped");
        base.OnExit(e);
    }
}
