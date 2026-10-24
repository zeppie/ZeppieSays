using System.IO;
using SherpaOnnx;
using ZeppieSays.Core;

namespace ZeppieSays.Engines;

/// <summary>NeMo transducer models (Parakeet) through sherpa-onnx, on the CPU.</summary>
public sealed class SherpaTransducerEngine : ITranscriptionEngine
{
    readonly OfflineRecognizer _recognizer;
    readonly SemaphoreSlim _gate = new(1, 1);

    public string Name { get; }
    public string Backend => "CPU";

    public SherpaTransducerEngine(string name, string modelDir)
    {
        Name = name;
        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = Audio.SampleRate;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Transducer.Encoder = Pick(modelDir, "encoder");
        config.ModelConfig.Transducer.Decoder = Pick(modelDir, "decoder");
        config.ModelConfig.Transducer.Joiner = Pick(modelDir, "joiner");
        config.ModelConfig.Tokens = Path.Combine(modelDir, "tokens.txt");
        config.ModelConfig.ModelType = "nemo_transducer";
        config.ModelConfig.NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
        config.ModelConfig.Provider = "cpu";
        config.DecodingMethod = "greedy_search";
        _recognizer = new OfflineRecognizer(config);
        Log.Info($"Sherpa '{name}' loaded from {modelDir}");
    }

    /// <summary>Prefers the int8 file when a release ships both precisions.</summary>
    static string Pick(string dir, string part)
    {
        var files = Directory.GetFiles(dir, $"{part}*.onnx");
        return files.FirstOrDefault(f => f.Contains("int8")) ?? files.FirstOrDefault()
            ?? throw new FileNotFoundException($"No {part}*.onnx in {dir}");
    }

    public async Task<string> TranscribeAsync(TranscriptionRequest request, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await Task.Run(() =>
            {
                using var stream = _recognizer.CreateStream();
                stream.AcceptWaveform(Audio.SampleRate, request.Samples);
                _recognizer.Decode(stream);
                return stream.Result.Text.Trim();
            }, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Wait();
        _recognizer.Dispose();
    }
}
