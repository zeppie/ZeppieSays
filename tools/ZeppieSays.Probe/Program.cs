using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Windows.Media.SpeechSynthesis;
using ZeppieSays.Core;
using ZeppieSays.Engines;
using ZeppieSays.Services;

// Usage:
//   probe install <model-id> [<model-id> ...]
//   probe run <model-id> <language> "<sentence to speak>" [dictionary terms separated by ;]
//   probe speak <language> "<sentence>" <out.wav>
var settings = SettingsStore.Load();
var models = new ModelManager(settings);

switch (args.FirstOrDefault())
{
    case "install":
        foreach (var id in args.Skip(1))
        {
            var info = ModelCatalog.BuiltIn.Single(m => m.Id == id);
            if (models.IsInstalled(id)) { Console.WriteLine($"{id}: already installed"); continue; }
            var last = -1;
            var watch = Stopwatch.StartNew();
            await models.InstallAsync(info, new Progress<double>(p =>
            {
                var pct = (int)(p * 100);
                if (pct / 10 != last / 10) Console.WriteLine($"{id}: {pct}%");
                last = pct;
            }), CancellationToken.None);
            Console.WriteLine($"{id}: installed in {watch.Elapsed.TotalSeconds:F0}s");
        }
        break;

    case "run":
        var (modelId, language, sentence) = (args[1], args[2], args[3]);
        var terms = args.Length > 4 ? args[4].Split(';', StringSplitOptions.RemoveEmptyEntries) : [];
        settings.UseGpu = true;
        settings.Dictionary = terms.Select(t => new DictionaryEntry { Term = t }).ToList();

        var samples = await SpeakAsync(sentence, language);
        Console.WriteLine($"audio: {samples.Length / (double)Audio.SampleRate:F1}s");

        using (var cache = new EngineCache(models, settings))
        {
            var load = Stopwatch.StartNew();
            var engine = await cache.GetAsync(modelId);
            Console.WriteLine($"engine: {engine.Name} on {engine.Backend}, loaded in {load.ElapsedMilliseconds} ms");

            // ZS_SCREEN: text to treat as "what's on screen" (as if dictating into a code editor).
            // ZS_GAIN: volume factor, to mimic a quieter real microphone.
            var screen = Environment.GetEnvironmentVariable("ZS_SCREEN") is { Length: > 0 } s
                ? ScreenContext.Empty with { IsCodeEditor = true, Identifiers = ContextCapture.ExtractIdentifiers(File.ReadAllText(s)) }
                : ScreenContext.Empty;
            // ZS_BEFORE: text to treat as what precedes the cursor.
            if (Environment.GetEnvironmentVariable("ZS_BEFORE") is { Length: > 0 } b)
                screen = screen with { BeforeText = File.ReadAllText(b) };
            if (float.TryParse(Environment.GetEnvironmentVariable("ZS_GAIN"), System.Globalization.CultureInfo.InvariantCulture, out var gain))
                for (var i = 0; i < samples.Length; i++) samples[i] *= gain;
            var prompt = engine is WhisperEngine ? TextShaping.BuildPrompt(settings.Dictionary, screen) : "";
            Console.WriteLine($"prompt ({prompt.Length} chars): {prompt}");
            var keyTerms = engine is ElevenLabsEngine ? TextShaping.KeyTerms(settings.Dictionary, screen) : null;
            if (keyTerms is not null) Console.WriteLine($"key terms: {string.Join("; ", keyTerms)}");
            for (var run = 1; run <= 2; run++) // the first run includes GPU warm-up
            {
                var watch = Stopwatch.StartNew();
                var raw = await engine.TranscribeAsync(new(samples, language, prompt, keyTerms), CancellationToken.None);
                Console.WriteLine($"run {run}: {watch.ElapsedMilliseconds} ms");
                if (run == 2)
                {
                    Console.WriteLine($"raw:   {raw}");
                    Console.WriteLine($"clean: {TextShaping.Clean(raw, settings, ScreenContext.Empty)}");
                }
            }
        }
        break;

    case "speak": // probe speak <lang> "<sentence>" <out.wav>  — for ZEPPIE_TEST_WAV
    {
        var speech = await SpeakAsync(args[2], args[1]);
        using var writer = new WaveFileWriter(args[3], new WaveFormat(Audio.SampleRate, 16, 1));
        writer.WriteSamples(speech, 0, speech.Length);
        Console.WriteLine($"wrote {args[3]} ({speech.Length / (double)Audio.SampleRate:F1}s)");
        break;
    }

    case "ask": // probe ask "<question>" ["<selected text>"]  — answered as an Ask key would, by the first Ask key's model
    {
        // ZS_PROFILE: answer with another profile's models, in memory only (nothing is saved).
        if (Environment.GetEnvironmentVariable("ZS_PROFILE") is { Length: > 0 } profileName)
            settings.Profiles.First(p => p.Name == profileName).ApplyTo(settings);
        var answerer = new ZeppieSays.Llm.Answerer(settings);
        var key = settings.Keys.FirstOrDefault(k => k.Asks) ?? new KeyBinding { Action = KeyAction.Ask };
        var (provider, model) = answerer.Resolve(key);
        if (answerer.Create(provider, model) is not { } answerModel)
        {
            Console.WriteLine($"No API key for {provider}");
            break;
        }
        var context = ScreenContext.Empty with { SelectedText = args.Length > 2 ? args[2] : null };
        var progress = new ZeppieSays.Llm.AnswerProgress();
        string? lastStatus = null;
        var shown = 0;
        progress.Changed += () =>
        {
            lock (progress)
            {
                if (progress.Status is { } status && status != lastStatus) Console.WriteLine($"[{status}]");
                lastStatus = progress.Status;
                var text = progress.Text;
                if (text.Length > shown) { Console.Write(text[shown..]); shown = text.Length; }
            }
        };
        var watch = Stopwatch.StartNew();
        await answerModel.AnswerAsync(ZeppieSays.Llm.Answerer.SystemPrompt(answerModel.SearchesWeb, null, DateTime.Now),
            ZeppieSays.Llm.Answerer.UserMessage(args[1], context), progress, CancellationToken.None);
        Console.WriteLine();
        Console.WriteLine($"-- {provider} {model} in {watch.ElapsedMilliseconds} ms");
        foreach (var source in progress.Sources) Console.WriteLine($"   {source.Site}: {source.Title} {source.Url}");
        break;
    }

    case "card": // probe card "<question>" "<selected text>" <out.png>  — the answer card as the app shows it, saved as an image
    {
        if (Environment.GetEnvironmentVariable("ZS_PROFILE") is { Length: > 0 } profileName)
            settings.Profiles.First(p => p.Name == profileName).ApplyTo(settings);
        var key = settings.Keys.FirstOrDefault(k => k.Asks) ?? new KeyBinding { Key = "F10", Action = KeyAction.Ask };
        var context = ScreenContext.Empty with { SelectedText = args[2].Length > 0 ? args[2] : null };
        var ui = new Thread(() =>
        {
            var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
            var card = new ZeppieSays.UI.AnswerWindow(new ZeppieSays.Llm.Answerer(settings));
            // From inside the message loop, as the app does, so the card's awaits come back to the UI thread.
            app.Dispatcher.BeginInvoke(() => card.Ask(args[1], context, key));
            // Long enough for a web search; then a picture of the card, drawn on its own (no desktop behind it).
            var done = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
            done.Tick += (_, _) =>
            {
                done.Stop();
                var content = (System.Windows.FrameworkElement)card.Content;
                var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(content);
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    (int)(content.ActualWidth * dpi.DpiScaleX), (int)(content.ActualHeight * dpi.DpiScaleY),
                    dpi.PixelsPerInchX, dpi.PixelsPerInchY, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(content);
                var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
                png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using (var file = File.Create(args[3])) png.Save(file);
                Console.WriteLine($"saved {args[3]}");
                app.Shutdown();
            };
            done.Start();
            app.Run();
        });
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
        ui.Join();
        break;
    }

    case "rewrite": // probe rewrite <process> "<window title>" "<dictated text>" ["<text before cursor>"]
    {
        var context = ScreenContext.Empty with
        {
            ProcessName = args[1], Title = args[2], BeforeText = args.Length > 4 ? args[4] : null,
        };
        var rewriter = new ZeppieSays.Llm.TextRewriter(settings);
        // ZS_TARGET: a language code to translate into, as a key with that output language would.
        var target = Environment.GetEnvironmentVariable("ZS_TARGET") is { Length: > 0 } t ? t : null;
        var style = rewriter.ResolveStyle(context, new KeyBinding { OutputLanguage = target ?? KeyBinding.SameLanguage });
        // ZS_MODEL: try another model with the same style, for comparisons.
        if (Environment.GetEnvironmentVariable("ZS_MODEL") is { Length: > 0 } otherModel) style.Model = otherModel;
        settings.Llm.TimeoutSeconds = 60; // measure, don't fall back
        var watch = Stopwatch.StartNew();
        var cleaned = TextShaping.Clean(args[3], settings, context).Trim(); // same order as the app: rules first
        var result = await rewriter.RewriteAsync(cleaned, style, context, "auto", CancellationToken.None, target);
        Console.WriteLine($"style: {style.Name} ({style.Provider} {style.Model}, {style.Strength}) in {watch.ElapsedMilliseconds} ms");
        var shown = result.Text.Replace("\r", "").Replace("\n", " ⏎ ");
        Console.WriteLine(result.Rewritten ? $"out:   {shown}" : $"KEPT:  {shown}  ({result.FallbackReason})");
        break;
    }

    case "context": // probe context <process>  — what the app sees in that process's window, and the style it picks
    {
        var process = Process.GetProcessesByName(args[1]).First(p => p.MainWindowHandle != IntPtr.Zero);
        var basic = ScreenContext.Empty with { Window = process.MainWindowHandle, ProcessName = process.ProcessName, Title = process.MainWindowTitle };
        var watch = Stopwatch.StartNew();
        var context = await ContextCapture.AddBrowserPageAsync(basic);
        Console.WriteLine($"window: {basic.Title}");
        Console.WriteLine($"title:  {context.Title} ({watch.ElapsedMilliseconds} ms)");
        Console.WriteLine($"style:  {new ZeppieSays.Llm.TextRewriter(settings).ResolveStyle(context).Name}");
        break;
    }

    case "diarize": // probe diarize <audio file> [threshold | nN for N speakers ...]  — who speaks when
    {
        var audio = ZeppieSays.Notes.AudioFile.Load(args[1]);
        // ZS_MINUTES: only use the start of a long recording, to compare settings quickly.
        if (int.TryParse(Environment.GetEnvironmentVariable("ZS_MINUTES"), out var minutes) && minutes * 60 * Audio.SampleRate < audio.Length)
            audio = audio[..(minutes * 60 * Audio.SampleRate)];
        // ZS_VOICE_MODEL: the voice model to use (campplus, wespeaker, titanet); the one in Settings otherwise.
        var voiceModel = Environment.GetEnvironmentVariable("ZS_VOICE_MODEL") ?? settings.Notetaker.VoiceModel;
        await ZeppieSays.Notes.SpeakerDiarizer.EnsureInstalledAsync(voiceModel, new Progress<double>(), CancellationToken.None);
        var runs = args.Length > 2 ? args.Skip(2) : ["default"];
        foreach (var run in runs)
        {
            var watch = Stopwatch.StartNew();
            var options = settings.Notetaker.SpeakerDetection.Copy();
            if (float.TryParse(run, System.Globalization.CultureInfo.InvariantCulture, out var threshold)) options.Threshold = threshold;
            var turns = ZeppieSays.Notes.SpeakerDiarizer.Run(audio, new Progress<double>(), voiceModel, options,
                run.StartsWith('n') ? int.Parse(run[1..]) : 0);
            Console.WriteLine($"{run}: {turns.Select(t => t.Speaker).Distinct().Count()} speakers in {watch.ElapsedMilliseconds} ms");
            foreach (var g in turns.GroupBy(t => t.Speaker))
                Console.WriteLine($"  speaker {g.Key}: {g.Count()} turns, {g.Sum(t => t.End - t.Start) / (double)Audio.SampleRate:F0} s");
            if (turns.Count <= 40)
                foreach (var t in turns)
                    Console.WriteLine($"  {t.Start / (double)Audio.SampleRate,6:F1}-{t.End / (double)Audio.SampleRate,6:F1}  speaker {t.Speaker}");
        }
        break;
    }

    case "teams-call": // probe teams-call  — what the recorder reads from Teams' call window (counts only, no names)
    {
        for (var attempt = 1; attempt <= 2; attempt++) // the first read may only wake up Teams' accessibility tree
        {
            var call = ZeppieSays.Notes.TeamsWindow.ReadCall();
            Console.WriteLine(call is null ? $"read {attempt}: no call window"
                : $"read {attempt}: call between two people: {call.OneToOne}, other people: {call.Others.Count}, " +
                  $"name matches the window title: {call.Others.Contains(ZeppieSays.Notes.TeamsWindow.CallTitle() ?? "")}");
            await Task.Delay(3000);
        }
        break;
    }

    case "teams-tree": // probe teams-tree <out file>  — every Teams window's accessibility tree, to learn how a call shows its people
    {
        var ids = Process.GetProcessesByName("ms-teams").Concat(Process.GetProcessesByName("Teams")).Select(p => p.Id).ToHashSet();
        var desktop = System.Windows.Automation.AutomationElement.RootElement;
        var condition = System.Windows.Automation.Condition.TrueCondition;
        List<System.Windows.Automation.AutomationElement> Windows() =>
            desktop.FindAll(System.Windows.Automation.TreeScope.Children, condition).Cast<System.Windows.Automation.AutomationElement>()
                .Where(w => ids.Contains(w.Current.ProcessId)).ToList();
        // Teams (a web view) only fills in its tree once something asks for it: ask, wait, then read.
        foreach (var w in Windows()) w.FindAll(System.Windows.Automation.TreeScope.Descendants, condition);
        await Task.Delay(3000);

        var walker = System.Windows.Automation.TreeWalker.ControlViewWalker;
        var lines = new List<string>();
        void Walk(System.Windows.Automation.AutomationElement element, int depth)
        {
            if (depth > 60 || lines.Count > 20000) return;
            var c = element.Current;
            var name = c.Name.Replace('\n', ' ');
            if (name.Length > 160) name = name[..160] + "…";
            lines.Add($"{new string(' ', depth * 2)}{c.ControlType.ProgrammaticName.Replace("ControlType.", "")} \"{name}\"" +
                      (c.AutomationId.Length > 0 ? $" id={c.AutomationId}" : "") + (c.ClassName.Length > 0 ? $" class={c.ClassName}" : ""));
            for (var child = walker.GetFirstChild(element); child is not null; child = walker.GetNextSibling(child))
                Walk(child, depth + 1);
        }
        foreach (var w in Windows())
        {
            lines.Add($"===== window \"{w.Current.Name}\"");
            Walk(w, 0);
        }
        File.WriteAllLines(args[1], lines);
        Console.WriteLine($"{Windows().Count} Teams windows, {lines.Count} lines written to {args[1]}");
        break;
    }

    case "devices": // probe devices  — output devices, the defaults, and which programs have sound on each
    {
        using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
        var console = enumerator.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Console).ID;
        var comms = enumerator.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Communications).ID;
        foreach (var device in enumerator.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.DeviceState.Active))
        {
            Console.WriteLine($"{device.FriendlyName}{(device.ID == console ? "  [default]" : "")}{(device.ID == comms ? "  [default communications]" : "")}");
            var sessions = device.AudioSessionManager.Sessions;
            for (var i = 0; i < sessions.Count; i++)
            {
                var s = sessions[i];
                string name;
                try { name = Process.GetProcessById((int)s.GetProcessID).ProcessName; } catch { name = $"pid {s.GetProcessID}"; }
                Console.WriteLine($"    {name,-24} {s.State}");
            }
        }
        break;
    }

    case "record": // probe record <app, e.g. Teams> <seconds> <folder>  — a meeting recording as the app makes it
    {
        using var recorder = new ZeppieSays.Notes.MeetingRecorder();
        recorder.Saved += path => Console.WriteLine($"saved: {path}");
        recorder.Start(args[1]);
        await Task.Delay(TimeSpan.FromSeconds(double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture)));
        recorder.Stop(args[3], wait: true);
        break;
    }

    case "levels": // probe levels <meeting recording>  — how loud each track is, per 10 seconds (left: mic, right: call)
    {
        var tracks = ZeppieSays.Notes.AudioFile.LoadChannels(args[1], 2);
        static string Db(float[] t, int from, int to)
        {
            double sum = 0; float peak = 0;
            for (var i = from; i < Math.Min(to, t.Length); i++) { sum += t[i] * t[i]; peak = Math.Max(peak, Math.Abs(t[i])); }
            var rms = Math.Sqrt(sum / Math.Max(1, Math.Min(to, t.Length) - from));
            return $"rms {20 * Math.Log10(rms + 1e-9),6:F1} dB, peak {peak:F3}";
        }
        var step = 10 * Audio.SampleRate;
        Console.WriteLine($"whole:  mic {Db(tracks[0], 0, tracks[0].Length)} | call {Db(tracks[1], 0, tracks[1].Length)}");
        for (var at = 0; at < tracks[0].Length; at += step)
            Console.WriteLine($"{at / Audio.SampleRate,4}s:  mic {Db(tracks[0], at, at + step)} | call {Db(tracks[1], at, at + step)}");
        break;
    }

    case "paths": // probe paths  — where the app keeps its data and settings
        Console.WriteLine($"data: {AppPaths.Data}");
        Console.WriteLine($"settings: {AppPaths.Settings}");
        break;

    case "words": // probe words <model-id> <language> <audio file> [from-seconds to-seconds]  — each word and when it was said
    {
        var audio = ZeppieSays.Notes.AudioFile.Load(args[3]);
        if (args.Length > 5)
            audio = audio[(int)(double.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture) * Audio.SampleRate)..
                          (int)(double.Parse(args[5], System.Globalization.CultureInfo.InvariantCulture) * Audio.SampleRate)];
        settings.UseGpu = true;
        using var cache = new EngineCache(models, settings);
        var engine = await cache.GetAsync(args[1]);
        var (text, words) = await engine.TranscribeWordsAsync(new(audio, args[2], ""), CancellationToken.None);
        Console.WriteLine(text);
        foreach (var w in words) Console.WriteLine($"  {w.Start,6:F2}-{w.End,6:F2}  {w.Text}");
        break;
    }

    case "diarize-raw": // probe diarize-raw <audio file> <embedding .onnx> [threshold | nN ...]  — sherpa's own segments, before Zeppie's clean-up
    {
        var audio = ZeppieSays.Notes.AudioFile.Load(args[1]);
        var segmentation = Directory.GetFiles(Path.Combine(AppPaths.Models, "speakers", "segmentation"), "*.onnx");
        foreach (var run in args.Skip(3))
        {
            var config = new SherpaOnnx.OfflineSpeakerDiarizationConfig();
            config.Segmentation.Pyannote.Model = segmentation.FirstOrDefault(f => f.Contains("int8")) ?? segmentation[0];
            config.Segmentation.NumThreads = 4;
            config.Embedding.Model = args[2];
            config.Embedding.NumThreads = 4;
            config.Clustering.NumClusters = run.StartsWith('n') ? int.Parse(run[1..]) : -1;
            config.Clustering.Threshold = run.StartsWith('n') ? 0.5f : float.Parse(run, System.Globalization.CultureInfo.InvariantCulture);
            config.MinDurationOn = 0.3f;
            config.MinDurationOff = 0.5f;
            using var diarizer = new SherpaOnnx.OfflineSpeakerDiarization(config);
            var watch = Stopwatch.StartNew();
            var segments = diarizer.Process(audio).OrderBy(s => s.Start).ToList();
            Console.WriteLine($"{run}: {segments.Select(s => s.Speaker).Distinct().Count()} clusters in {watch.ElapsedMilliseconds} ms");
            foreach (var s in segments)
                Console.WriteLine($"  {s.Start,6:F1}-{s.End,6:F1}  cluster {s.Speaker}");
        }
        break;
    }

    case "models": // probe models  — the cloud models the saved API keys can use
    {
        if (ZeppieSays.Llm.SecretStore.Unprotect(settings.Llm.AnthropicKeyProtected) is { Length: > 0 } anthropic)
            Console.WriteLine("Anthropic: " + string.Join(", ", await ZeppieSays.Llm.AnthropicChat.ListModelsAsync(anthropic)));
        else Console.WriteLine("Anthropic: no key saved");
        if (ZeppieSays.Llm.SecretStore.Unprotect(settings.Llm.OpenAiKeyProtected) is { Length: > 0 } openAi)
            Console.WriteLine("OpenAI: " + string.Join(", ", await ZeppieSays.Llm.OpenAiChat.ListModelsAsync(openAi)));
        else Console.WriteLine("OpenAI: no key saved");
        break;
    }

    default:
        Console.WriteLine("probe install <ids> | probe run <model> <lang> \"<sentence>\" [terms;...] | probe speak <lang> \"<sentence>\" <out.wav>");
        break;
}

/// <summary>Speaks the sentence with a local Windows voice and returns 16 kHz mono samples.</summary>
static async Task<float[]> SpeakAsync(string text, string language)
{
    using var synth = new SpeechSynthesizer();
    var culture = language == "nl" ? "nl-NL" : "en-US";
    synth.Voice = SpeechSynthesizer.AllVoices.FirstOrDefault(v => v.Language == culture)
        ?? SpeechSynthesizer.AllVoices.First(v => v.Language.StartsWith(language));
    using var stream = await synth.SynthesizeTextToStreamAsync(text);
    var bytes = new byte[stream.Size];
    await stream.ReadAsync(bytes.AsBuffer(), (uint)stream.Size, Windows.Storage.Streams.InputStreamOptions.None);

    using var wav = new WaveFileReader(new MemoryStream(bytes));
    ISampleProvider provider = wav.ToSampleProvider();
    if (provider.WaveFormat.Channels > 1) provider = provider.ToMono();
    if (provider.WaveFormat.SampleRate != Audio.SampleRate)
        provider = new WdlResamplingSampleProvider(provider, Audio.SampleRate);

    var output = new List<float>();
    var buffer = new float[Audio.SampleRate];
    int read;
    while ((read = provider.Read(buffer.AsSpan())) > 0)
        output.AddRange(buffer.AsSpan(0, read).ToArray());
    return [.. output];
}
