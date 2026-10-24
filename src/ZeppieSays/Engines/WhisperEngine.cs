using System.Text;
using Whisper.net;
using Whisper.net.LibraryLoader;
using ZeppieSays.Core;

namespace ZeppieSays.Engines;

/// <summary>
/// whisper.cpp via Whisper.net. Tries CUDA, then CPU.
/// Whisper.net is pinned to 1.8.1 because it is built against CUDA 12; 1.9.x needs CUDA 13.
/// </summary>
public sealed class WhisperEngine : ITranscriptionEngine
{
    readonly WhisperFactory _factory;
    readonly SemaphoreSlim _gate = new(1, 1);

    public string Name { get; }
    public string Backend { get; }

    public WhisperEngine(string name, string modelPath, bool useGpu)
    {
        Name = name;
        RuntimeOptions.RuntimeLibraryOrder = useGpu
            ? [RuntimeLibrary.Cuda, RuntimeLibrary.Cpu]
            : [RuntimeLibrary.Cpu];
        _factory = WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions { UseGpu = useGpu });
        Backend = RuntimeOptions.LoadedLibrary?.ToString() ?? "unknown";
        Log.Info($"Whisper '{name}' loaded on {Backend}");
    }

    public async Task<string> TranscribeAsync(TranscriptionRequest request, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var samples = Normalize(request.Samples);
            var text = await RunAsync(samples, request.Language, request.Prompt, ct);
            // Safety net: if the hints put Whisper off, it is better to lose the hints than the dictation.
            if (text.Length == 0 && request.Prompt.Length > 0 && !ct.IsCancellationRequested)
                text = await RunAsync(samples, request.Language, "", ct);
            return text;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Laptop microphones often peak at a few percent of full scale. Whisper skips audio that quiet as
    /// silence, so it is boosted to a normal level first (Parakeet normalizes by itself).
    /// </summary>
    static float[] Normalize(float[] samples)
    {
        var peak = 0f;
        foreach (var s in samples) peak = Math.Max(peak, Math.Abs(s));
        if (peak is < 0.001f or >= 0.5f) return samples;
        var gain = Math.Min(0.5f / peak, 20f);
        var boosted = new float[samples.Length];
        for (var i = 0; i < samples.Length; i++) boosted[i] = samples[i] * gain;
        return boosted;
    }

    async Task<string> RunAsync(float[] samples, string language, string prompt, CancellationToken ct)
    {
        var builder = _factory.CreateBuilder()
            .WithThreads(Math.Max(2, Environment.ProcessorCount / 2))
            .WithNoContext()
            .WithSingleSegment()
            .WithNoSpeechThreshold(0.6f);
        builder = language == "auto"
            ? builder.WithLanguageDetection()
            : builder.WithLanguage(language);
        if (!string.IsNullOrWhiteSpace(prompt))
            builder = builder.WithPrompt(prompt);

        await using var processor = builder.Build();
        var text = new StringBuilder();
        var segments = new List<string>();
        await foreach (var segment in processor.ProcessAsync(samples, ct))
        {
            var dropped = segment.NoSpeechProbability > 0.8f;
            segments.Add($"{segment.Text.Length} chars, no-speech {segment.NoSpeechProbability:F2}{(dropped ? ", dropped" : "")}");
            if (!dropped) text.Append(segment.Text);
        }
        // Lengths and scores only: the dictated words themselves never go into the log.
        Log.Info($"Whisper segments [{string.Join("; ", segments)}], prompt {prompt.Length} chars");
        return text.ToString().Trim();
    }

    public void Dispose()
    {
        _gate.Wait();
        _factory.Dispose();
    }
}
