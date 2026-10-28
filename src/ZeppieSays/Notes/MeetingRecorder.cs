using System.Diagnostics;
using System.IO;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using ZeppieSays.Core;
using ZeppieSays.Engines;

namespace ZeppieSays.Notes;

/// <summary>What a meeting recording holds, saved next to it as "&lt;name&gt;.recording.json".</summary>
public sealed class RecordingInfo
{
    /// <summary>The meeting app, e.g. "Teams".</summary>
    public string App { get; set; } = "";
    public DateTime Started { get; set; }
    /// <summary>Left channel: the user's microphone. Right channel: what the computer played (the others).</summary>
    public bool MicLeftCallRight { get; set; } = true;
    /// <summary>What the Teams call window was called (a person, or a meeting's subject); null when unknown.</summary>
    public string? CallTitle { get; set; }

    public static string PathFor(string audioPath) => audioPath + ".recording.json";

    public static RecordingInfo? For(string audioPath)
    {
        try
        {
            var path = PathFor(audioPath);
            return File.Exists(path) ? JsonSerializer.Deserialize<RecordingInfo>(File.ReadAllText(path)) : null;
        }
        catch (Exception e)
        {
            Log.Warn($"Could not read {PathFor(audioPath)}: {e.Message}");
            return null;
        }
    }

    public void SaveFor(string audioPath) => File.WriteAllText(PathFor(audioPath), JsonSerializer.Serialize(this));
}

/// <summary>
/// Records a meeting as two tracks: the microphone (the user) on the left, and what the computer plays (the other
/// people) on the right, so "who is the user" never has to be guessed. Records to a WAV in the app's data folder;
/// on stop it is compressed to .m4a and moved into the Notetaker folder, where it's transcribed like any file.
/// </summary>
public sealed class MeetingRecorder : IDisposable
{
    const double Margin = 0.3; // seconds a stream may lag before silence is filled in

    sealed class Track(WaveFormat format)
    {
        public readonly BufferedWaveProvider Buffer = new(format, TimeSpan.FromSeconds(10)) { ReadFully = false, DiscardOnBufferOverflow = true };
        public ISampleProvider? Output;
        public readonly List<float> Pending = [];
        public long Written; // 16 kHz samples written to the file
    }

    readonly object _lock = new();
#pragma warning disable CS0618 // see Start
    WasapiCapture? _mic;
    WasapiLoopbackCapture? _loopback;
#pragma warning restore CS0618
    /// <summary>The meeting app's own sound (process loopback); when set, <see cref="_loopback"/> isn't used.</summary>
    WasapiRecorder? _appSound;
    Track? _micTrack, _callTrack;
    WaveFileWriter? _writer;
    Stopwatch? _clock;
    Timer? _flush;
    Timer? _callWatch;
    string? _wavPath;
    RecordingInfo? _info;

    public bool IsRecording => _writer is not null;
    public TimeSpan Elapsed => _clock?.Elapsed ?? TimeSpan.Zero;
    public string? App => _info?.App;

    /// <summary>Raised (on a worker thread) with the final file once a recording is saved, or null when it failed.</summary>
    public event Action<string?>? Saved;

    public void Start(string app)
    {
        lock (_lock)
        {
            if (IsRecording) return;
            using var devices = new MMDeviceEnumerator();
            var micDevice = devices.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            // The call is recorded from the meeting app itself when it can be: Windows' default device is often
            // not the one the call plays on (Teams on the laptop speakers while the default is the monitor).
            var source = app.Length > 0 ? app : MeetingApps.Playing();
            _appSound = source is null ? null : AppSound(source);
            var speakerDevice = (source is null ? null : MeetingApps.DeviceOf(source, devices))
                ?? devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Communications);

            _info = new RecordingInfo { App = app, Started = DateTime.Now };
            Directory.CreateDirectory(Recordings);
            _wavPath = Path.Combine(Recordings, $"{FileName(_info)}.wav");
            _writer = new WaveFileWriter(_wavPath, new WaveFormat(Audio.SampleRate, 16, 2));

#pragma warning disable CS0618 // NAudio 3's WasapiRecorderBuilder replaces these; they still work, a port can come later
            _mic = new WasapiCapture(micDevice);
            if (_appSound is null) _loopback = new WasapiLoopbackCapture(speakerDevice);
#pragma warning restore CS0618
            _micTrack = Prepare(_mic.WaveFormat);
            _callTrack = Prepare(_appSound?.WaveFormat ?? _loopback!.WaveFormat);
            _mic.DataAvailable += (_, e) => Add(_micTrack, e.Buffer.AsSpan(0, e.BytesRecorded));
            if (_appSound is not null) _appSound.DataAvailable += (buffer, _, _, _) => Add(_callTrack, buffer);
            else _loopback!.DataAvailable += (_, e) => Add(_callTrack, e.Buffer.AsSpan(0, e.BytesRecorded));

            _clock = Stopwatch.StartNew();
            _mic.StartRecording();
            if (_appSound is not null) _appSound.StartRecording();
            else _loopback!.StartRecording();
            _flush = new Timer(_ => Flush(final: false), null, 1000, 1000);
            // Who the call is with, for naming it afterwards. The call window may open a moment after the microphone.
            if (app == "Teams") _callWatch = new Timer(_ => NoteCallTitle(), null, 0, 5000);
            Log.Info($"Recording {FileName(_info)}: mic '{micDevice.FriendlyName}', call audio " +
                     (_appSound is not null ? $"from {source} itself" : $"from '{speakerDevice.FriendlyName}'"));
        }
    }

    void NoteCallTitle()
    {
        if (TeamsWindow.CallTitle() is not { } title) return;
        lock (_lock)
        {
            if (_info is null || !IsRecording || _info.CallTitle == title) return;
            _info.CallTitle = title;
            Log.Info($"Teams call window noted ({title.Length} chars)"); // not the name itself: the log stays free of who you talk to
        }
    }

    /// <summary>
    /// A recorder of everything the app (its top process and all its children) plays, on any device; null when
    /// the app isn't running or Windows can't (process loopback needs Windows 10 2004 or later).
    /// </summary>
    static WasapiRecorder? AppSound(string app)
    {
        if (MeetingApps.RootProcess(app) is not { } root) return null;
        try
        {
            // Activation is asynchronous and completes on a worker thread; don't wait for it on the caller's thread.
            var build = Task.Run(() => new WasapiRecorderBuilder()
                .WithProcessLoopback((uint)root, ProcessLoopbackMode.IncludeTargetProcessTree).BuildAsync());
            return build.Wait(TimeSpan.FromSeconds(5)) ? build.Result : null;
        }
        catch (Exception e)
        {
            Log.Warn($"Could not record {app}'s own sound, recording an output device instead: {e.GetBaseException().Message}");
            return null;
        }
    }

    static Track Prepare(WaveFormat format)
    {
        var track = new Track(format);
        ISampleProvider source = track.Buffer.ToSampleProvider();
        if (format.Channels > 1) source = new DownmixToMono(source);
        track.Output = format.SampleRate == Audio.SampleRate ? source : new WdlResamplingSampleProvider(source, Audio.SampleRate);
        return track;
    }

    void Add(Track track, ReadOnlySpan<byte> data)
    {
        lock (_lock)
        {
            if (_writer is null) return;
            var bytes = data.ToArray();
            track.Buffer.AddSamples(bytes, 0, bytes.Length);
            var buffer = new float[Audio.SampleRate / 10];
            int read;
            while ((read = track.Output!.Read(buffer.AsSpan())) > 0)
                track.Pending.AddRange(buffer.AsSpan(0, read));
        }
    }

    /// <summary>
    /// Writes what both tracks have in common. Loopback capture delivers nothing while the computer is silent,
    /// so a track that falls behind the clock is filled with silence to keep the two in step.
    /// </summary>
    void Flush(bool final)
    {
        lock (_lock)
        {
            if (_writer is null || _micTrack is null || _callTrack is null) return;
            var target = (long)((_clock!.Elapsed.TotalSeconds - (final ? 0 : Margin)) * Audio.SampleRate);
            foreach (var track in new[] { _micTrack, _callTrack })
            {
                var behind = target - (track.Written + track.Pending.Count);
                if (behind > 0) track.Pending.AddRange(new float[behind]);
            }
            var frames = Math.Min(_micTrack.Pending.Count, _callTrack.Pending.Count);
            if (frames <= 0) return;
            var interleaved = new float[frames * 2];
            for (var i = 0; i < frames; i++)
            {
                interleaved[i * 2] = _micTrack.Pending[i];
                interleaved[i * 2 + 1] = _callTrack.Pending[i];
            }
            _writer.WriteSamples(interleaved, 0, interleaved.Length);
            foreach (var track in new[] { _micTrack, _callTrack })
            {
                track.Pending.RemoveRange(0, frames);
                track.Written += frames;
            }
        }
    }

    /// <summary>Stops, then compresses and hands the recording to the Notetaker folder (in the background unless <paramref name="wait"/>).</summary>
    public void Stop(string notesFolder, bool wait = false)
    {
        string? wav;
        RecordingInfo? info;
        lock (_lock)
        {
            if (!IsRecording) return;
            _flush?.Dispose();
            _callWatch?.Dispose();
            _callWatch = null;
            _mic?.StopRecording();
            _loopback?.StopRecording();
            _appSound?.StopRecording();
        }
        // Stopping raises a last DataAvailable on the capture threads; flush after they're done.
        Thread.Sleep(150);
        lock (_lock)
        {
            Flush(final: true);
            _writer!.Dispose();
            _writer = null;
            _mic?.Dispose();
            _loopback?.Dispose();
            _appSound?.Dispose();
            _mic = null;
            _loopback = null;
            _appSound = null;
            wav = _wavPath;
            info = _info;
            _clock?.Stop();
        }
        Log.Info($"Recording stopped after {Elapsed.TotalMinutes:F1} min");
        var delivery = Task.Run(() => Saved?.Invoke(Deliver(wav!, info!, notesFolder)));
        if (wait) delivery.Wait();
    }

    /// <summary>Compresses to AAC (.m4a, about 40 MB an hour); keeps the WAV if that fails.</summary>
    static string? Deliver(string wav, RecordingInfo info, string notesFolder)
    {
        try
        {
            Directory.CreateDirectory(notesFolder);
            var name = FileName(info);
            var target = Path.Combine(notesFolder, name + ".m4a");
            for (var i = 2; File.Exists(target); i++) target = Path.Combine(notesFolder, $"{name} ({i}).m4a");
            var temp = Path.Combine(Path.GetDirectoryName(wav)!, Path.GetFileName(target));
            try
            {
                NAudio.MediaFoundation.MediaFoundationApi.Startup();
                using (var reader = new WaveFileReader(wav))
                using (var resampler = new MediaFoundationResampler(reader, new WaveFormat(48_000, 16, 2)))
                    MediaFoundationEncoder.EncodeToAac(resampler, temp, 96_000);
            }
            catch (Exception e)
            {
                Log.Warn($"Could not compress the recording, keeping the WAV: {e.Message}");
                temp = wav;
                target = Path.ChangeExtension(target, ".wav");
            }
            // The description goes first, so the Notetaker knows it's a two-track meeting when the audio appears.
            info.SaveFor(target);
            File.Move(temp, target);
            if (File.Exists(wav)) File.Delete(wav);
            Log.Info($"Recording saved as {target}");
            return target;
        }
        catch (Exception e)
        {
            Log.Error($"Could not save the recording {wav}", e);
            return null;
        }
    }

    static string Recordings => Path.Combine(AppPaths.Data, "recordings");

    /// <summary>"Teams meeting 2026-10-24 14.00", or "Meeting 2026-10-24 14.00" for a recording started by hand.</summary>
    static string FileName(RecordingInfo info) =>
        info.App.Length > 0 ? $"{info.App} meeting {info.Started:yyyy-MM-dd HH.mm}" : $"Meeting {info.Started:yyyy-MM-dd HH.mm}";

    public void Dispose()
    {
        lock (_lock)
        {
            _flush?.Dispose();
            _callWatch?.Dispose();
            _mic?.Dispose();
            _loopback?.Dispose();
            _appSound?.Dispose();
            _writer?.Dispose();
            _writer = null;
        }
    }

    /// <summary>Averages all channels into one.</summary>
    sealed class DownmixToMono(ISampleProvider source) : ISampleProvider
    {
        readonly int _channels = source.WaveFormat.Channels;
        float[] _buffer = [];

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);

        public int Read(Span<float> buffer)
        {
            var needed = buffer.Length * _channels;
            if (_buffer.Length < needed) _buffer = new float[needed];
            var read = source.Read(_buffer.AsSpan(0, needed));
            var frames = read / _channels;
            for (var f = 0; f < frames; f++)
            {
                float sum = 0;
                for (var c = 0; c < _channels; c++) sum += _buffer[f * _channels + c];
                buffer[f] = sum / _channels;
            }
            return frames;
        }
    }
}
