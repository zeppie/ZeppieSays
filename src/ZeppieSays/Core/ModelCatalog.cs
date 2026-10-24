namespace ZeppieSays.Core;

public enum EngineKind
{
    /// <summary>whisper.cpp ggml model, run through Whisper.net (CUDA / Vulkan / CPU).</summary>
    Whisper,
    /// <summary>NeMo transducer exported to ONNX, run through sherpa-onnx (CPU).</summary>
    SherpaTransducer,
}

public sealed record ModelInfo(
    string Id,
    string Name,
    EngineKind Engine,
    string Description,
    string Languages,
    long ApproxBytes,
    string DownloadUrl,
    /// <summary>True when the model detects the spoken language itself and has no language setting.</summary>
    bool AutoLanguageOnly = false,
    /// <summary>True when the model can be steered with a text prompt (dictionary and screen context).</summary>
    bool SupportsPrompt = false);

public static class ModelCatalog
{
    const string Ggml = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/";
    const string Sherpa = "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/";

    public static IReadOnlyList<ModelInfo> BuiltIn { get; } =
    [
        new("whisper-large-v3-turbo", "Whisper large-v3 turbo", EngineKind.Whisper,
            "Fast and accurate in 99 languages. Good default for Dutch.", "Multilingual",
            1_620_000_000, Ggml + "ggml-large-v3-turbo.bin", SupportsPrompt: true),
        new("whisper-large-v3", "Whisper large-v3", EngineKind.Whisper,
            "Most accurate Whisper, strongest on Dutch. Slower and uses ~3 GB of GPU memory.", "Multilingual",
            3_100_000_000, Ggml + "ggml-large-v3.bin", SupportsPrompt: true),
        new("whisper-large-v3-turbo-q5", "Whisper large-v3 turbo (compact)", EngineKind.Whisper,
            "Quantized turbo: a third of the size, slightly less accurate.", "Multilingual",
            574_000_000, Ggml + "ggml-large-v3-turbo-q5_0.bin", SupportsPrompt: true),
        new("whisper-medium-en", "Whisper medium (English)", EngineKind.Whisper,
            "English-only Whisper, lighter than large.", "English",
            1_530_000_000, Ggml + "ggml-medium.en.bin", SupportsPrompt: true),
        new("whisper-small", "Whisper small", EngineKind.Whisper,
            "Small and quick, for older hardware.", "Multilingual",
            488_000_000, Ggml + "ggml-small.bin", SupportsPrompt: true),
        new("parakeet-tdt-0.6b-v2", "Parakeet TDT 0.6B v2", EngineKind.SherpaTransducer,
            "NVIDIA's English model: very fast and very accurate, with punctuation.", "English",
            660_000_000, Sherpa + "sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8.tar.bz2", AutoLanguageOnly: true),
        new("parakeet-tdt-0.6b-v3", "Parakeet TDT 0.6B v3", EngineKind.SherpaTransducer,
            "NVIDIA's European model (25 languages incl. Dutch), detects the language itself.", "25 European languages",
            670_000_000, Sherpa + "sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8.tar.bz2", AutoLanguageOnly: true),
    ];

    public static readonly (string Code, string Name)[] Languages =
    [
        ("auto", "Detect"), ("nl", "Dutch"), ("en", "English"), ("de", "German"), ("fr", "French"),
        ("es", "Spanish"), ("it", "Italian"), ("pt", "Portuguese"), ("pl", "Polish"), ("sv", "Swedish"),
    ];
}
