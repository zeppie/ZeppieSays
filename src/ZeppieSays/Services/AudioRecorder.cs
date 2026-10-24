using NAudio.Wave;
using ZeppieSays.Core;
using ZeppieSays.Engines;

namespace ZeppieSays.Services;

/// <summary>Records 16 kHz mono audio from a microphone into memory.</summary>
public sealed class AudioRecorder : IDisposable
{
    readonly object _lock = new();
    readonly List<float> _samples = new(Audio.SampleRate * 60);
    WaveIn? _waveIn;

    /// <summary>Peak level of the latest buffer, 0..1. Raised on a background thread.</summary>
    public event Action<float>? Level;

    public bool IsRecording => _waveIn is not null || _replay is not null;

    public static IReadOnlyList<string> Devices() =>
        Enumerable.Range(0, WaveIn.DeviceCount).Select(i => WaveIn.GetCapabilities(i).ProductName).ToList();

    public void Start(int device)
    {
        Stop();
        lock (_lock) _samples.Clear();
        if (Environment.GetEnvironmentVariable("ZEPPIE_TEST_WAV") is { Length: > 0 } wav)
        {
            StartReplay(wav);
            return;
        }
        _waveIn = new WaveIn
        {
            DeviceNumber = device,
            WaveFormat = new WaveFormat(Audio.SampleRate, 16, 1),
            BufferMilliseconds = 40,
            NumberOfBuffers = 4,
        };
        _waveIn.DataAvailable += OnData;
        _waveIn.StartRecording();
        Log.Info($"Recording started on device {device}");
    }

    void OnData(object? sender, WaveInEventArgs e)
    {
        var count = e.BytesRecorded / 2;
        var peak = 0f;
        lock (_lock)
        {
            for (var i = 0; i < count; i++)
            {
                var sample = BitConverter.ToInt16(e.Buffer, i * 2) / 32768f;
                _samples.Add(sample);
                peak = Math.Max(peak, Math.Abs(sample));
            }
        }
        Level?.Invoke(peak);
    }

    public int SampleCount
    {
        get { lock (_lock) return _samples.Count; }
    }

    public float[] Snapshot()
    {
        lock (_lock) return [.. _samples];
    }

    CancellationTokenSource? _replay;

    /// <summary>
    /// Developer aid: plays a 16 kHz mono WAV in real time as if it came from the microphone,
    /// so the full key → overlay → paste flow can be tested without speaking.
    /// </summary>
    void StartReplay(string path)
    {
        var replay = _replay = new CancellationTokenSource();
        var source = new WaveFileReader(path).ToSampleProvider();
        Log.Info($"Replaying {path} instead of the microphone");
        _ = Task.Run(async () =>
        {
            var chunk = new float[Audio.SampleRate / 25]; // 40 ms, like the microphone buffers
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(40));
            while (!replay.IsCancellationRequested && await timer.WaitForNextTickAsync())
            {
                var read = source.Read(chunk.AsSpan());
                if (read == 0) Array.Clear(chunk); // keep "recording" silence after the clip ends
                var count = read == 0 ? chunk.Length : read;
                var peak = 0f;
                lock (_lock)
                    for (var i = 0; i < count; i++)
                    {
                        _samples.Add(chunk[i]);
                        peak = Math.Max(peak, Math.Abs(chunk[i]));
                    }
                Level?.Invoke(peak);
            }
        });
    }

    /// <summary>Stops recording and returns everything that was captured.</summary>
    public float[] Stop()
    {
        _replay?.Cancel();
        _replay = null;
        var waveIn = _waveIn;
        _waveIn = null;
        if (waveIn is not null)
        {
            waveIn.DataAvailable -= OnData;
            waveIn.StopRecording();
            waveIn.Dispose();
        }
        return Snapshot();
    }

    public void Dispose() => Stop();
}
