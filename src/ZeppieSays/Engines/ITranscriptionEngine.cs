namespace ZeppieSays.Engines;

public sealed record TranscriptionRequest(
    float[] Samples,
    /// <summary>ISO code or "auto".</summary>
    string Language,
    /// <summary>Vocabulary and preceding text to steer models that accept a prompt. May be empty.</summary>
    string Prompt);

public interface ITranscriptionEngine : IDisposable
{
    string Name { get; }
    /// <summary>Which backend actually runs the model, e.g. "CUDA" or "CPU".</summary>
    string Backend { get; }
    Task<string> TranscribeAsync(TranscriptionRequest request, CancellationToken ct);
}

public static class Audio
{
    public const int SampleRate = 16_000;
}
