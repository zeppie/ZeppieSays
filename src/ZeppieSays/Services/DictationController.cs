using System.Diagnostics;
using System.Windows.Threading;
using ZeppieSays.Core;
using ZeppieSays.Engines;
using ZeppieSays.Llm;
using ZeppieSays.UI;

namespace ZeppieSays.Services;

public enum DictationState { Idle, Recording, Transcribing }

/// <summary>
/// Turns key presses into dictations. Holding a key records until release (push-to-talk);
/// a short tap keeps recording until the same key is pressed again (hands-free). Escape cancels.
/// </summary>
public sealed class DictationController : IDisposable
{
    static readonly TimeSpan TapThreshold = TimeSpan.FromMilliseconds(350);
    const float SilencePeak = 0.015f;

    readonly AppSettings _settings;
    readonly AudioRecorder _recorder;
    readonly EngineCache _engines;
    readonly TextRewriter _rewriter;
    readonly UsageStore _usage;
    readonly DictationHistory _history;
    readonly Dispatcher _ui;

    DictationState _state = DictationState.Idle;
    int _active = -1;
    bool _handsFree;
    uint _pressedAt;
    CancellationTokenSource? _session;
    Task<ScreenContext>? _context;
    float _peak;

    public event Action<DictationState, KeyBinding?>? StateChanged;
    public event Action<float>? LevelChanged;
    public event Action<string>? Failed;
    /// <summary>Raised with the style's name while an LLM rewrites the transcript.</summary>
    public event Action<string>? Rewriting;
    /// <summary>An Ask key's question, with what was on screen, for the answer card.</summary>
    public event Action<string, ScreenContext, KeyBinding>? Asked;

    /// <summary>
    /// Esc while no dictation runs, e.g. to close the answer card. Called on the key hook's thread, so it must return
    /// at once; true swallows the key.
    /// </summary>
    public Func<bool>? EscapeWhenIdle { get; set; }

    /// <summary>The last dictation before any style rewrote it, for "Paste last dictation unedited".</summary>
    public string? LastUnedited { get; private set; }

    public DictationController(AppSettings settings, AudioRecorder recorder, EngineCache engines, TextRewriter rewriter,
        UsageStore usage, DictationHistory history, KeyboardHook hook, Dispatcher ui)
    {
        _settings = settings;
        _recorder = recorder;
        _engines = engines;
        _rewriter = rewriter;
        _usage = usage;
        _history = history;
        _ui = ui;
        // The hook callback has to return fast, so the real work is posted to the UI queue.
        hook.KeyDown += (i, time) => _ui.BeginInvoke(() => OnKeyDown(i, time));
        hook.KeyUp += (i, time) => _ui.BeginInvoke(() => OnKeyUp(i, time));
        // Ctrl+Win+D is a Windows shortcut, not a dictation: drop the recording that Ctrl+Win started.
        hook.Interrupted += i => _ui.BeginInvoke(() =>
        {
            if (_state == DictationState.Recording && i == _active && !_handsFree) Cancel();
        });
        hook.Escape = () =>
        {
            if (_state == DictationState.Idle) return EscapeWhenIdle?.Invoke() ?? false;
            _ui.BeginInvoke(Cancel);
            return true;
        };
        _recorder.Level += level =>
        {
            _peak = Math.Max(_peak, level);
            LevelChanged?.Invoke(level);
        };
    }

    public DictationState State => _state;

    KeyBinding Binding(int index) => _settings.Keys[Math.Clamp(index, 0, _settings.Keys.Count - 1)];

    void OnKeyDown(int index, uint time)
    {
        if (_state == DictationState.Idle)
        {
            _pressedAt = time;
            Start(index);
        }
        else if (_state == DictationState.Recording && index == _active && _handsFree)
            _ = FinishAsync();
    }

    void OnKeyUp(int index, uint time)
    {
        if (_state != DictationState.Recording || index != _active || _handsFree) return;
        // Compare Windows' own timestamps: the UI thread may have been busy when these events arrive.
        if (unchecked(time - _pressedAt) < TapThreshold.TotalMilliseconds)
            _handsFree = true; // short tap: keep listening until the key is pressed again
        else
            _ = FinishAsync();
    }

    void Start(int index)
    {
        var binding = Binding(index);
        if (string.IsNullOrEmpty(binding.ModelId))
        {
            Failed?.Invoke("No model chosen for this key yet.");
            return;
        }

        try
        {
            _recorder.Start(_settings.MicrophoneDevice);
        }
        catch (Exception e)
        {
            Log.Error("Microphone failed", e);
            Failed?.Invoke("Could not open the microphone.");
            return;
        }

        _active = index;
        _handsFree = false;
        _peak = 0;
        _session = new CancellationTokenSource();
        var basic = ContextCapture.Foreground();
        var page = ContextCapture.AddBrowserPageAsync(basic);
        // A question is about what's on screen, so an Ask key reads it even with screen context turned off for dictation.
        _context = CaptureAsync(basic, page, _settings.UseScreenContext || binding.Asks, _session.Token);
        SetState(DictationState.Recording, binding);

        _ = _engines.GetAsync(binding.ModelId); // make sure the model is warming up
        _ = PrepareStyleAsync(page, binding); // and the style's LLM, while the user talks
    }

    static async Task<ScreenContext> CaptureAsync(ScreenContext basic, Task<ScreenContext> page, bool readScreen, CancellationToken ct)
    {
        var enriched = readScreen ? await ContextCapture.EnrichAsync(basic, ct) : basic;
        return enriched with { Title = (await page).Title };
    }

    /// <summary>
    /// Once the browser tab is known (a website rule may pick another style), warms up the style's model, or for an
    /// Ask key the model that answers.
    /// </summary>
    async Task PrepareStyleAsync(Task<ScreenContext> page, KeyBinding binding)
    {
        var context = await page;
        _rewriter.WarmUp(binding.Asks
            ? new Style { Provider = binding.AskProvider, Model = binding.AskModel }
            : _rewriter.ResolveStyle(context, binding));
        RememberApp(context);
    }

    async Task FinishAsync()
    {
        var binding = Binding(_active);
        var samples = _recorder.Stop();
        var session = _session!;
        SetState(DictationState.Transcribing, binding);

        try
        {
            var seconds = samples.Length / (double)Audio.SampleRate;
            if (seconds < 0.3 || _peak < SilencePeak)
            {
                Log.Info($"Nothing to transcribe ({seconds:F1}s, peak {_peak:F3})");
                return;
            }

            var engine = await _engines.GetAsync(binding.ModelId);
            var context = await _context!;
            // Teams, for one, doesn't always tell UI Automation what's selected: then copy it, now the hotkey is released.
            var copiedSelection = false;
            if (binding.Asks && context.SelectedText is null && await SelectionCopier.CopyAsync(context) is { } copied)
            {
                context = context with { SelectedText = copied };
                copiedSelection = true;
            }
            var watch = Stopwatch.StartNew();
            var raw = await engine.TranscribeAsync(new(Pad(samples), binding.Language, PromptFor(engine, binding, context),
                engine is ElevenLabsEngine ? TextShaping.KeyTerms(_settings.AllWords, context) : null), CancellationToken.None);
            if (session.IsCancellationRequested) return;

            var counts = new CleanupCounts();
            var text = TextShaping.Clean(raw, _settings, context, counts);
            var rewritten = false;
            Log.Info($"{engine.Name} on {engine.Backend}: {seconds:F1}s audio (peak {_peak:F2}) in {watch.ElapsedMilliseconds} ms, " +
                     $"{raw.Length} chars raw, {text.Length} after cleanup, {context.Identifiers.Count} screen words");
            LastUnedited = text;
            var spoken = text.Trim();

            if (binding.Asks)
            {
                // A question isn't typed anywhere: the answer card takes it from here.
                Log.Info($"Question from '{context.ProcessName}': {spoken.Length} chars, " +
                         $"{context.SelectedText?.Length ?? 0} selected{(copiedSelection ? " (copied)" : "")}, " +
                         $"{context.WindowText?.Length ?? 0} on screen");
                if (spoken.Length > 0) Asked?.Invoke(spoken, context, binding);
                return;
            }

            var style = _rewriter.ResolveStyle(context, binding);
            Log.Info($"Into '{context.ProcessName}' \"{context.Title}\": style '{style.Name}'");
            var target = binding.Translates ? binding.OutputLanguage : null;
            string? notTranslated = null;
            if (style.UsesLlm && text.Trim().Length > 0)
            {
                Rewriting?.Invoke(target is null ? style.Name : $"{style.Name} → {LanguageName(target)}");
                var rewriteWatch = Stopwatch.StartNew();
                // Whisper was told the language; Parakeet detects it itself, so the rewriter guesses it from the words.
                var language = engine is WhisperEngine ? binding.Language : "auto";
                var result = await _rewriter.RewriteAsync(text.Trim(), style, context, language, session.Token, target);
                if (session.IsCancellationRequested) return;
                Log.Info($"Style '{style.Name}'{(target is null ? "" : $" into {target}")} ({style.Provider} {style.Model}) in " +
                         $"{rewriteWatch.ElapsedMilliseconds} ms: " +
                         (result.Rewritten ? $"rewritten to {result.Text.Length} chars" : $"kept the transcript, {result.FallbackReason}"));
                if (result.Rewritten)
                    text = TextShaping.FitToCursor(result.Text, _settings, context);
                else if (target is not null)
                    notTranslated = result.FallbackReason;
                rewritten = result.Rewritten;
            }
            await TextInserter.InsertAsync(text);
            _history.Add(new DictationEntry(DateTime.Now, text.Trim(), rewritten ? spoken : null, context.ProcessName,
                !style.UsesLlm ? null : target is null ? style.Name : $"{style.Name} → {LanguageName(target)}"));
            // The text went in as spoken; say so, or it looks like the translation silently did nothing.
            if (notTranslated is not null)
                Failed?.Invoke($"Not translated ({notTranslated}), so it was typed as spoken.");
            _usage.Record(new DictationUsage(UsageStore.CountWords(text), seconds, context.ProcessName, context.Title,
                counts.FillerWords, counts.DictionaryFixes, counts.ScreenWords, rewritten));
        }
        catch (Exception e)
        {
            Log.Error("Transcription failed", e);
            Failed?.Invoke(e is InvalidOperationException ? e.Message : "Transcription failed. See the log for details.");
        }
        finally
        {
            session.Cancel();
            if (_session == session) SetState(DictationState.Idle, null);
        }
    }

    /// <summary>Keeps a short list of apps dictated into, so the Styles tab can offer them for new rules.</summary>
    void RememberApp(ScreenContext context)
    {
        if (string.IsNullOrEmpty(context.ProcessName) || context.ProcessName.Equals("ZeppieSays", StringComparison.OrdinalIgnoreCase))
            return;
        _settings.RecentApps.RemoveAll(a => a.ProcessName.Equals(context.ProcessName, StringComparison.OrdinalIgnoreCase));
        _settings.RecentApps.Insert(0, new SeenApp { ProcessName = context.ProcessName, Title = context.Title, LastUsed = DateTime.Now });
        if (_settings.RecentApps.Count > 15) _settings.RecentApps.RemoveRange(15, _settings.RecentApps.Count - 15);
        SettingsStore.Save(_settings);
    }

    static string LanguageName(string code) => ModelCatalog.Languages.FirstOrDefault(l => l.Code == code).Name ?? code;

    string PromptFor(ITranscriptionEngine engine, KeyBinding binding, ScreenContext context) =>
        engine is WhisperEngine ? TextShaping.BuildPrompt(_settings.AllWords, context) : "";

    /// <summary>Whisper drops clips shorter than a second, so short ones get trailing silence.</summary>
    static float[] Pad(float[] samples)
    {
        var minimum = Audio.SampleRate * 11 / 10;
        if (samples.Length >= minimum) return samples;
        var padded = new float[minimum];
        samples.CopyTo(padded, 0);
        return padded;
    }

    public void Cancel()
    {
        if (_state == DictationState.Idle) return;
        _recorder.Stop();
        _session?.Cancel();
        _session = null;
        SetState(DictationState.Idle, null);
        Log.Info("Dictation cancelled");
    }

    void SetState(DictationState state, KeyBinding? binding)
    {
        _state = state;
        StateChanged?.Invoke(state, binding);
    }

    public void Dispose() => Cancel();
}
