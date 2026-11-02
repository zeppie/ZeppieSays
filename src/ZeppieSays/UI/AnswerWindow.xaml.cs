using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ZeppieSays.Core;
using ZeppieSays.Llm;
using ZeppieSays.Services;

namespace ZeppieSays.UI;

/// <summary>
/// The card that answers an Ask key's question, where the pill was. It never takes focus, so "Paste at cursor" types
/// into the app you asked from. It stays until Esc, the close button, or the next question.
/// </summary>
public partial class AnswerWindow : Window
{
    static readonly Brush TextBrush = new SolidColorBrush(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF));
    static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x8A));
    static readonly Brush LinkBrush = new SolidColorBrush(Color.FromRgb(0xB9, 0xA8, 0xFF));

    readonly Answerer _answerer;
    readonly TextRewriter _rewriter;
    readonly TodoClient _todo;
    // The answer streams in faster than it's worth redrawing; the screen catches up a few times a second.
    readonly DispatcherTimer _redraw = new() { Interval = TimeSpan.FromMilliseconds(80) };
    // A task's card goes by itself, unless the mouse is on it.
    readonly DispatcherTimer _fade = new() { Interval = TimeSpan.FromSeconds(8) };
    CancellationTokenSource? _asking;
    AnswerProgress? _progress;
    CreatedTask? _task;
    bool _taskMode;
    volatile bool _dirty;
    volatile bool _isOpen;

    public AnswerWindow(Answerer answerer, TextRewriter rewriter, TodoClient todo)
    {
        _answerer = answerer;
        _rewriter = rewriter;
        _todo = todo;
        InitializeComponent();
        _redraw.Tick += (_, _) => { if (_dirty) Draw(); };
        _fade.Tick += (_, _) => { if (!IsMouseOver) CloseCard(); };
        SizeChanged += (_, _) => OverlayWindow.PlaceBottomCentre(this);
    }

    /// <summary>The card's look for a question (Copy, Paste) or for a task (Open in To Do, Undo).</summary>
    void Begin(bool taskMode, KeyBinding key, string spoken, string status)
    {
        _asking?.Cancel();
        _fade.Stop();
        _taskMode = taskMode;
        _task = null;
        _progress = null;
        KeyLabel.Text = key.Key.Replace("+", " + ");
        Question.Text = spoken;
        Answer.Children.Clear();
        Sources.Children.Clear();
        CopyButton.Content = taskMode ? "Open in To Do" : "Copy";
        PasteButton.Content = taskMode ? "Undo" : "Paste at cursor";
        PasteButton.ToolTip = taskMode ? "Removes the task from Microsoft To Do again" : "Types the answer where your cursor was, without the formatting";
        CopyButton.IsEnabled = PasteButton.IsEnabled = false;
        Status.Foreground = LinkBrush;
        Status.Text = status;
        Status.Visibility = Visibility.Visible;
        Scroller.MaxHeight = Math.Max(160, SystemParameters.WorkArea.Height * 0.5);
        _isOpen = true;
        Show();
        OverlayWindow.PlaceBottomCentre(this);
    }

    /// <summary>Reads a spoken task with the key's model and adds it to Microsoft To Do.</summary>
    public async void AddTask(string spoken, KeyBinding key)
    {
        Begin(taskMode: true, key, spoken, "Reading the task…");
        var asking = _asking = new CancellationTokenSource();
        var (provider, model) = _answerer.Resolve(key);
        ModelLabel.Text = provider == LlmProvider.Ollama ? $"read by {model} on this PC" : $"read by {model} · {provider}";
        var watch = Stopwatch.StartNew();
        try
        {
            if (_rewriter.CreateModel(new Core.Style { Provider = provider, Model = model }) is not { } chat)
            {
                ShowError($"Add your {provider} API key in Settings, under AI providers.");
                return;
            }
            var lists = (await _todo.ListsAsync(asking.Token)).Select(l => l.Name).ToList(); // also checks the sign-in
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(asking.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var now = DateTime.Now;
            var reply = await Task.Run(() => chat.CompleteAsync(
                TaskReader.SystemPrompt(now, lists, key.Translates ? key.OutputLanguage : null), TaskReader.UserMessage(spoken), timeout.Token));
            var draft = TaskReader.Parse(reply, spoken, lists, now);
            if (asking.IsCancellationRequested) return;
            ShowTask(draft, null);
            Status.Text = "Adding it to To Do…";

            var created = await _todo.CreateAsync(draft, asking.Token);
            if (asking.IsCancellationRequested) return;
            _task = created;
            Log.Info($"Task added with {provider} {model} in {watch.ElapsedMilliseconds} ms: " +
                     $"{TaskReader.Describe(draft, created.ListName)}, {draft.StepList.Count} steps");
            ShowTask(draft, created.ListName);
            Status.Text = $"✓ Added to {created.ListName}";
            CopyButton.IsEnabled = PasteButton.IsEnabled = true;
            _fade.Start();
        }
        catch (OperationCanceledException) when (asking.IsCancellationRequested) { }
        catch (Exception e)
        {
            if (asking.IsCancellationRequested) return;
            Log.Warn($"Adding a task failed: {e.Message}");
            ShowError(e is TodoException or LlmException ? e.Message
                : e is OperationCanceledException ? "The model took too long to read the task."
                : $"Couldn't add the task: {e.Message}");
        }
    }

    /// <summary>The task as it will be (or was) made: its title, then where and when, notes and steps.</summary>
    void ShowTask(TaskDraft task, string? listName)
    {
        Answer.Children.Clear();
        Answer.Children.Add(new TextBlock(new Bold(new Run(task.Title)))
            { Foreground = TextBrush, FontSize = 15, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) });
        Answer.Children.Add(new TextBlock(new Run(TaskReader.Describe(task, listName)))
            { Foreground = LinkBrush, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
        if (task.Notes is { } notes)
            Answer.Children.Add(new TextBlock(new Run(notes))
                { Foreground = TextBrush, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
        foreach (var step in task.StepList)
            Answer.Children.Add(new TextBlock(new Run($"○  {step}"))
                { Foreground = TextBrush, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(14, 0, 0, 2) });
    }

    /// <summary>Read from the key hook's thread, so Esc can close the card without waiting for the UI.</summary>
    public bool IsOpen => _isOpen;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Clickable, unlike the pill, but never activated: the app you asked from keeps the focus.
        var hwnd = new WindowInteropHelper(this).Handle;
        const int GWL_EXSTYLE = -20, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x80;
        SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    public async void Ask(string question, ScreenContext context, KeyBinding key)
    {
        Begin(taskMode: false, key, question, "Thinking…");
        var asking = _asking = new CancellationTokenSource();
        var progress = _progress = new AnswerProgress();
        progress.Changed += () => _dirty = true;
        var (provider, model) = _answerer.Resolve(key);
        var answerModel = _answerer.Create(provider, model);
        ModelLabel.Text = provider == LlmProvider.Ollama ? $"{model} on this PC" : $"{model} · {provider}" + (answerModel?.SearchesWeb == true ? " · web" : "");
        _redraw.Start();

        if (answerModel is null)
        {
            ShowError($"Add your {provider} API key in Settings, under AI providers.");
            return;
        }

        var target = key.Translates ? key.OutputLanguage : null;
        var system = Answerer.SystemPrompt(answerModel.SearchesWeb, target, DateTime.Now);
        var user = Answerer.UserMessage(question, context);
        var watch = Stopwatch.StartNew();
        try
        {
            await Task.Run(() => answerModel.AnswerAsync(system, user, progress, asking.Token), asking.Token);
            if (asking.IsCancellationRequested) return;
            Log.Info($"Answered with {provider} {model} in {watch.ElapsedMilliseconds} ms: {progress.Text.Length} chars, " +
                     $"{progress.Sources.Count} sources, {user.Length} chars asked");
            Draw();
            if (progress.Text.Trim().Length == 0) ShowError("The model gave no answer.");
        }
        catch (OperationCanceledException) when (asking.IsCancellationRequested) { }
        catch (Exception e)
        {
            if (asking.IsCancellationRequested) return;
            Log.Warn($"Answering with {provider} {model} failed: {e.Message}");
            ShowError(e is LlmException ? e.Message : $"Couldn't get an answer: {e.Message}");
        }
        finally
        {
            if (_asking == asking) _redraw.Stop();
        }
    }

    void Draw()
    {
        _dirty = false;
        if (_progress is not { } progress) return;
        var text = progress.Text;
        Status.Text = progress.Status ?? "";
        Status.Visibility = progress.Status is null ? Visibility.Collapsed : Visibility.Visible;

        Answer.Children.Clear();
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;
            var bullet = line.TrimStart().StartsWith("- ") || line.TrimStart().StartsWith("* ");
            var block = new TextBlock
            {
                Foreground = TextBrush, FontSize = 14, LineHeight = 21, TextWrapping = TextWrapping.Wrap,
                Margin = bullet ? new Thickness(14, 0, 0, 4) : new Thickness(0, 0, 0, 8),
            };
            var content = line.TrimStart().TrimStart('#').Trim();
            if (bullet) { block.Inlines.Add(new Run("•  ")); content = line.TrimStart()[2..]; }
            block.Inlines.AddRange(Markdown.Spans(content));
            Answer.Children.Add(block);
        }
        Scroller.ScrollToEnd();

        var hasText = text.Trim().Length > 0;
        CopyButton.IsEnabled = PasteButton.IsEnabled = hasText;

        Sources.Children.Clear();
        foreach (var source in progress.Sources.Take(5))
        {
            var link = new Hyperlink(new Run(source.Site)) { Foreground = LinkBrush, ToolTip = source.Title.Length > 0 ? source.Title : source.Url };
            link.Click += (_, _) => Open(source.Url);
            Sources.Children.Add(new TextBlock(link) { FontSize = 12, Margin = new Thickness(0, 0, 14, 2) });
        }
    }

    void ShowError(string message)
    {
        _redraw.Stop();
        if (!_taskMode) Draw(); // a task has no streamed answer to show
        Status.Text = message;
        Status.Foreground = ErrorBrush;
        Status.Visibility = Visibility.Visible;
    }

    static void Open(string url)
    {
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception e) { Log.Warn($"Could not open {url}: {e.Message}"); }
    }

    void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_taskMode)
        {
            Open(TodoClient.WebUrl);
            CloseCard();
            return;
        }
        if (_progress is null) return;
        try
        {
            Clipboard.SetText(Answerer.PlainText(_progress.Text));
            CopyButton.Content = "Copied";
        }
        catch (Exception ex) // another program can hold the clipboard for a moment
        {
            Log.Warn($"Could not copy the answer: {ex.Message}");
            CopyButton.Content = "Try again";
        }
    }

    async void Paste_Click(object sender, RoutedEventArgs e)
    {
        if (_taskMode)
        {
            await UndoTaskAsync();
            return;
        }
        if (_progress is null) return;
        var text = Answerer.PlainText(_progress.Text);
        CloseCard();
        await TextInserter.InsertAsync(text);
    }

    async Task UndoTaskAsync()
    {
        if (_task is not { } task) return;
        _fade.Stop();
        CopyButton.IsEnabled = PasteButton.IsEnabled = false;
        Status.Text = "Removing it…";
        try
        {
            await _todo.DeleteAsync(task, CancellationToken.None);
            _task = null;
            Log.Info("Task removed again (undo)");
            Status.Text = "Removed from To Do";
            await Task.Delay(1200);
            CloseCard();
        }
        catch (Exception ex)
        {
            Log.Warn($"Undoing the task failed: {ex.Message}");
            ShowError(ex is TodoException ? ex.Message : $"Couldn't remove it: {ex.Message}");
            PasteButton.IsEnabled = true;
        }
    }

    void Close_Click(object sender, RoutedEventArgs e) => CloseCard();

    /// <summary>Stops an answer still coming in, and hides the card.</summary>
    public void CloseCard()
    {
        _asking?.Cancel();
        _redraw.Stop();
        _fade.Stop();
        _isOpen = false;
        Hide();
    }

    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd, int index, int value);
}
