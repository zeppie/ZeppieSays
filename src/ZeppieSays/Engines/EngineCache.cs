using ZeppieSays.Core;
using ZeppieSays.Services;

namespace ZeppieSays.Engines;

/// <summary>Keeps the models bound to the dictation keys (and held ones) loaded so a key press does not wait for a model load.</summary>
public sealed class EngineCache(ModelManager models, AppSettings settings) : IDisposable
{
    readonly Dictionary<string, Task<ITranscriptionEngine>> _engines = [];
    readonly List<string> _held = [];
    /// <summary>Models the user unloaded: <see cref="Sync"/> doesn't preload them again until something asks for them.</summary>
    readonly HashSet<string> _paused = [];
    readonly object _lock = new();

    /// <summary>Keeps a model loaded while something besides the keys uses it (a Notetaker transcription).</summary>
    public IDisposable Hold(string modelId)
    {
        lock (_lock) _held.Add(modelId);
        return new Release(() =>
        {
            lock (_lock) _held.Remove(modelId);
            Sync();
        });
    }

    sealed class Release(Action release) : IDisposable
    {
        public void Dispose() => release();
    }

    /// <summary>A speech model in memory, or still loading.</summary>
    public sealed record LoadedEngine(string ModelId, bool IsReady, string Backend);

    /// <summary>The speech models held in memory right now. Backend is where each one actually runs ("Cuda", "Cpu").</summary>
    public IReadOnlyList<LoadedEngine> Loaded()
    {
        lock (_lock)
            return _engines
                .Where(e => !e.Value.IsFaulted && !e.Value.IsCanceled)
                .Select(e => new LoadedEngine(e.Key, e.Value.IsCompletedSuccessfully,
                    e.Value.IsCompletedSuccessfully ? e.Value.Result.Backend : ""))
                .ToList();
    }

    /// <summary>
    /// Frees a model's memory. It loads again on the next key press that uses it (or <see cref="GetAsync"/>), and
    /// not before. Returns false when something is using it right now.
    /// </summary>
    public bool Unload(string modelId)
    {
        Task<ITranscriptionEngine>? task;
        lock (_lock)
        {
            if (_held.Contains(modelId)) return false;
            _paused.Add(modelId);
            if (!_engines.Remove(modelId, out task)) return true;
        }
        task.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); });
        return true;
    }

    public Task<ITranscriptionEngine> GetAsync(string modelId)
    {
        lock (_lock)
        {
            _paused.Remove(modelId);
            if (_engines.TryGetValue(modelId, out var existing) && !existing.IsFaulted)
                return existing;
            var model = models.Find(modelId)
                ?? throw new InvalidOperationException("This key has no installed model. Open ZeppieSays to pick one.");
            var task = Task.Run(() => Create(model));
            _engines[modelId] = task;
            return task;
        }
    }

    ITranscriptionEngine Create(InstalledModel model) => model.Info.Engine switch
    {
        EngineKind.Whisper => new WhisperEngine(model.Info.Name, model.Path, settings.UseGpu),
        EngineKind.SherpaTransducer => new SherpaTransducerEngine(model.Info.Name, model.Path),
        _ => throw new NotSupportedException(model.Info.Engine.ToString()),
    };

    /// <summary>Loads the bound models in the background and frees the ones no key uses anymore.</summary>
    public void Sync()
    {
        List<Task<ITranscriptionEngine>> stale;
        HashSet<string> wanted;
        lock (_lock)
        {
            wanted = settings.Keys.Select(k => k.ModelId)
                .Concat(_held)
                .Where(id => !string.IsNullOrEmpty(id) && models.IsInstalled(id) && !_paused.Contains(id))
                .ToHashSet();
            stale = _engines.Where(e => !wanted.Contains(e.Key)).Select(e => e.Value).ToList();
            foreach (var id in _engines.Keys.Where(id => !wanted.Contains(id)).ToList())
                _engines.Remove(id);
        }
        foreach (var task in stale)
            task.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); });

        foreach (var id in wanted)
            GetAsync(id).ContinueWith(t => Log.Error($"Preloading {id} failed", t.Exception),
                TaskContinuationOptions.OnlyOnFaulted);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var task in _engines.Values.Where(t => t.IsCompletedSuccessfully))
                task.Result.Dispose();
            _engines.Clear();
        }
    }
}
