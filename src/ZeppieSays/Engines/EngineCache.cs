using ZeppieSays.Core;
using ZeppieSays.Services;

namespace ZeppieSays.Engines;

/// <summary>Keeps the models bound to the dictation keys (and held ones) loaded so a key press does not wait for a model load.</summary>
public sealed class EngineCache(ModelManager models, AppSettings settings) : IDisposable
{
    readonly Dictionary<string, Task<ITranscriptionEngine>> _engines = [];
    readonly List<string> _held = [];
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

    public Task<ITranscriptionEngine> GetAsync(string modelId)
    {
        lock (_lock)
        {
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
                .Where(id => !string.IsNullOrEmpty(id) && models.IsInstalled(id))
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
