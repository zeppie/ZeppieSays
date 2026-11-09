using System.IO;
using NAudio.Wave;
using ZeppieSays.Engines;

namespace ZeppieSays.Notes;

/// <summary>Reads audio and video files into 16 kHz mono samples, and cuts long audio into pieces a model can take.</summary>
public static class AudioFile
{
    /// <summary>What Windows Media Foundation can decode out of the box.</summary>
    public static readonly string[] Extensions = [".wav", ".mp3", ".m4a", ".aac", ".wma", ".mp4", ".flac", ".mov", ".webm"];

    public static bool IsAudio(string path) =>
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static float[] Load(string path) => LoadChannels(path, 1)[0];

    /// <summary>Reads the file as <paramref name="channels"/> separate 16 kHz tracks (1 mixes everything down).</summary>
    public static float[][] LoadChannels(string path, int channels)
    {
        using var reader = new MediaFoundationReader(path);
        using var resampler = new MediaFoundationResampler(reader, new WaveFormat(Audio.SampleRate, 16, channels)) { ResamplerQuality = 60 };
        var capacity = (int)Math.Min(int.MaxValue / 4, reader.TotalTime.TotalSeconds * Audio.SampleRate + 1);
        var tracks = Enumerable.Range(0, channels).Select(_ => new List<float>(capacity)).ToArray();
        var buffer = new byte[Audio.SampleRate * 2 * channels];
        int read;
        while ((read = resampler.Read(buffer.AsSpan())) > 0)
            for (var i = 0; i + 2 * channels <= read; i += 2 * channels)
                for (var c = 0; c < channels; c++)
                    tracks[c].Add(BitConverter.ToInt16(buffer, i + 2 * c) / 32768f);
        return tracks.Select(t => t.ToArray()).ToArray();
    }

    /// <summary>
    /// Where someone speaks in <paramref name="samples"/>, as sample ranges. With <paramref name="other"/> (the call
    /// track next to a microphone track), moments where the microphone is no louder than the call track count as echo
    /// of the speakers picked up by the microphone, not as speech: what comes out of the speakers is never the user.
    /// The call is judged over a few frames around each moment, as the echo arrives a little late and rings on, and
    /// how much the microphone picks up of it is measured on the recording itself (headphones: next to nothing; open
    /// speakers: a third of it or more), so the cut-off follows.
    /// </summary>
    public static IReadOnlyList<(int Start, int End)> SpeechRegions(float[] samples, float[]? other = null)
    {
        const int Frame = Audio.SampleRate * 3 / 100; // 30 ms
        var frames = samples.Length / Frame;
        var rms = new float[frames];
        var otherRms = new float[frames];
        for (var f = 0; f < frames; f++)
        {
            rms[f] = Rms(samples, f * Frame, Frame);
            if (other is not null) otherRms[f] = Rms(other, f * Frame, Math.Min(Frame, Math.Max(0, other.Length - f * Frame)));
        }
        if (frames == 0) return [];
        // Speech is well above the background noise of this track.
        var floor = rms.Order().ElementAt(frames / 10);
        var threshold = Math.Max(0.006f, floor * 3);
        var (callLevel, echoBelow) = other is null ? ([], 0f) : EchoLevels(rms, otherRms);
        var speech = new bool[frames];
        for (var f = 0; f < frames; f++)
            speech[f] = rms[f] > threshold && !(other is not null && callLevel[f] > 0.005f && rms[f] < callLevel[f] * echoBelow);

        // Bridge short pauses, drop blips, and pad a little so first and last syllables aren't cut.
        var regions = new List<(int, int)>();
        const int Bridge = 20, MinFrames = 10, Pad = 5; // 600 ms, 300 ms, 150 ms
        var startFrame = -1;
        var lastSpeech = -1;
        for (var f = 0; f <= frames; f++)
        {
            var isSpeech = f < frames && speech[f];
            if (isSpeech)
            {
                if (startFrame < 0) startFrame = f;
                lastSpeech = f;
            }
            else if (startFrame >= 0 && (f == frames || f - lastSpeech > Bridge))
            {
                if (lastSpeech - startFrame + 1 >= MinFrames)
                    regions.Add((Math.Max(0, startFrame - Pad) * Frame, Math.Min(frames, lastSpeech + 1 + Pad) * Frame));
                startFrame = -1;
            }
        }
        return regions;
    }

    /// <summary>
    /// The call's loudness around each frame (the loudest of 150 ms either side), and how loud the microphone may be
    /// next to it and still be echo: the median share of the call that reaches the microphone while the call is
    /// loud (mostly the call alone), times 2.5, kept between 0.6 and 1.5.
    /// </summary>
    static (float[] CallLevel, float EchoBelow) EchoLevels(float[] mic, float[] call)
    {
        const int Spread = 5;
        var level = new float[call.Length];
        for (var f = 0; f < call.Length; f++)
        {
            var max = 0f;
            for (var g = Math.Max(0, f - Spread); g <= Math.Min(call.Length - 1, f + Spread); g++) max = Math.Max(max, call[g]);
            level[f] = max;
        }
        var shares = new List<float>();
        for (var f = 0; f < call.Length; f++)
            if (call[f] > 0.02f) shares.Add(mic[f] / call[f]);
        var coupling = shares.Count >= 50 ? shares.Order().ElementAt(shares.Count / 2) : 0f;
        return (level, Math.Clamp(coupling * 2.5f, 0.6f, 1.5f));
    }

    static float Rms(float[] samples, int start, int count)
    {
        if (count <= 0) return 0;
        double sum = 0;
        for (var i = start; i < start + count; i++) sum += samples[i] * samples[i];
        return (float)Math.Sqrt(sum / count);
    }

    /// <summary>
    /// Splits [start, end) into pieces of at most <paramref name="maxSeconds"/>, cutting at the quietest moment
    /// in the last third of each piece so words aren't cut in half. Returns sample ranges.
    /// </summary>
    public static IReadOnlyList<(int Start, int End)> Split(float[] samples, int start, int end, double maxSeconds = 25)
    {
        var max = (int)(maxSeconds * Audio.SampleRate);
        var pieces = new List<(int, int)>();
        while (end - start > max)
        {
            var cut = QuietestPoint(samples, start + max * 2 / 3, start + max);
            pieces.Add((start, cut));
            start = cut;
        }
        if (end > start) pieces.Add((start, end));
        return pieces;
    }

    static int QuietestPoint(float[] samples, int from, int to)
    {
        const int Window = Audio.SampleRate / 5; // 200 ms
        const int Step = Audio.SampleRate / 50;  // 20 ms
        var best = to;
        var bestEnergy = double.MaxValue;
        for (var i = from; i + Window <= to; i += Step)
        {
            double energy = 0;
            for (var j = i; j < i + Window; j += 4) energy += samples[j] * samples[j];
            if (energy < bestEnergy) { bestEnergy = energy; best = i + Window / 2; }
        }
        return best;
    }

    /// <summary>A copy of a sample range, padded with silence to at least 1.1 s (Whisper drops shorter clips).</summary>
    public static float[] Slice(float[] samples, int start, int end)
    {
        var length = Math.Max(end - start, Audio.SampleRate * 11 / 10);
        var slice = new float[length];
        Array.Copy(samples, start, slice, 0, end - start);
        return slice;
    }

    public static bool IsSilent(float[] samples, int start, int end)
    {
        var peak = 0f;
        for (var i = start; i < end; i++) peak = Math.Max(peak, Math.Abs(samples[i]));
        return peak < 0.01f;
    }
}
