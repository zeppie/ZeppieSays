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
            for (var run = 1; run <= 2; run++) // the first run includes GPU warm-up
            {
                var watch = Stopwatch.StartNew();
                var raw = await engine.TranscribeAsync(new(samples, language, prompt), CancellationToken.None);
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
