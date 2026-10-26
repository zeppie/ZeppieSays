using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using ZeppieSays.Core;
using ZeppieSays.Engines;
using ZeppieSays.Llm;
using ZeppieSays.Services;

namespace ZeppieSays.UI;

/// <summary>
/// The "In memory now" panel of the Models page: which speech and text models are loaded, whether they run on the
/// graphics card or the processor, and buttons to load and unload them. Refreshes itself only while it's on screen.
/// </summary>
public sealed class LoadedModelsViewModel : ObservableObject
{
    static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    readonly AppSettings _settings;
    readonly ModelManager _models;
    readonly EngineCache _engines;
    readonly DispatcherTimer _timer;
    bool _refreshing;

    public ObservableCollection<LoadedModelRow> SpeechRows { get; } = [];
    public ObservableCollection<LoadedModelRow> TextRows { get; } = [];
    public RelayCommand UnloadAll { get; }
    public RelayCommand Refresh { get; }

    public LoadedModelsViewModel(AppSettings settings, ModelManager models, EngineCache engines)
    {
        _settings = settings;
        _models = models;
        _engines = engines;
        _timer = new DispatcherTimer { Interval = Interval };
        _timer.Tick += (_, _) => _ = RefreshAsync();
        UnloadAll = new RelayCommand(_ => _ = UnloadAllAsync());
        Refresh = new RelayCommand(_ => _ = RefreshAsync());
        _ = RefreshAsync();
    }

    /// <summary>Polls while the panel is visible and stops otherwise, so a hidden window never asks Ollama anything.</summary>
    public void SetActive(bool active)
    {
        if (active == _timer.IsEnabled) return;
        if (active)
        {
            _timer.Start();
            _ = RefreshAsync();
        }
        else _timer.Stop();
    }

    public bool HasLoaded => SpeechRows.Concat(TextRows).Any(r => r.IsLoaded);

    public bool OllamaDown
    {
        get => _ollamaDown;
        private set => Set(ref _ollamaDown, value);
    }
    bool _ollamaDown;

    public string GpuText
    {
        get => _gpuText;
        private set => Set(ref _gpuText, value);
    }
    string _gpuText = "";

    /// <summary>0 to 1: how much of the graphics memory all programs together use.</summary>
    public double GpuFraction
    {
        get => _gpuFraction;
        private set => Set(ref _gpuFraction, value);
    }
    double _gpuFraction;

    public bool HasGpuInfo
    {
        get => _hasGpuInfo;
        private set => Set(ref _hasGpuInfo, value);
    }
    bool _hasGpuInfo;

    async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var loaded = OllamaChat.ListLoadedAsync(_settings.Llm.OllamaUrl);
            var gpu = GpuMemory.QueryAsync();
            var ollama = await loaded;
            var usage = await gpu;

            Sync(SpeechRows, SpeechSnapshots());
            Sync(TextRows, TextSnapshots(ollama));
            OllamaDown = ollama is null;
            ShowGpu(usage, ollama);
            Raise(nameof(HasLoaded));
        }
        catch (Exception e)
        {
            Log.Warn($"Refreshing the loaded models failed: {e.Message}");
        }
        finally
        {
            _refreshing = false;
        }
    }

    void ShowGpu(GpuMemory.Usage? usage, IReadOnlyList<OllamaLoadedModel>? ollama)
    {
        HasGpuInfo = usage is not null;
        if (usage is null) return;
        var held = ollama?.Sum(m => m.VramBytes) ?? 0;
        GpuFraction = (double)usage.UsedBytes / usage.TotalBytes;
        GpuText = $"Graphics memory: {Gb(usage.UsedBytes)} of {Gb(usage.TotalBytes)} in use" +
                  (held > 0 ? $" · {Gb(held)} of it by Ollama models" : "");
    }

    List<Snapshot> SpeechSnapshots()
    {
        var engines = _engines.Loaded().ToDictionary(e => e.ModelId);
        return _models.Installed().Select(m =>
        {
            var size = m.Info.ApproxBytes >= 1_000_000_000 ? $"{m.Info.ApproxBytes / 1e9:0.0} GB" : $"{m.Info.ApproxBytes / 1e6:0} MB";
            var detail = $"{m.Info.Languages} · {size} on disk";
            if (!engines.TryGetValue(m.Info.Id, out var engine))
                return new Snapshot(m.Info.Id, m.Info.Name, State.NotLoaded, "Not loaded", detail,
                    Unload: null, Load: () => LoadSpeechAsync(m.Info.Id));
            if (!engine.IsReady)
                return new Snapshot(m.Info.Id, m.Info.Name, State.Loading, "Loading…", detail, null, null);
            var where = Where(engine.Backend);
            // Whisper tries the graphics card first; landing on the processor usually means a missing or old driver.
            if (m.Info.Engine == EngineKind.Whisper && _settings.UseGpu && where == "CPU")
                detail += " · fell back to the processor; is the NVIDIA driver installed?";
            return new Snapshot(m.Info.Id, m.Info.Name, State.Loaded, where, detail,
                Unload: () => Task.FromResult<string?>(_engines.Unload(m.Info.Id) ? null : "In use right now"), Load: null);
        }).OrderByDescending(s => s.State == State.Loaded).ThenBy(s => s.Name).ToList();
    }

    List<Snapshot> TextSnapshots(IReadOnlyList<OllamaLoadedModel>? ollama)
    {
        // Models the styles use are listed even when they're not loaded, so they can be loaded from here.
        var configured = _settings.Styles
            .Where(s => s.Provider == LlmProvider.Ollama && s.Model.Length > 0).Select(s => s.Model)
            .Append(_settings.Llm.DefaultProvider == LlmProvider.Ollama ? _settings.Llm.DefaultModel : "")
            .Where(n => n.Length > 0)
            .Select(Tag)
            .Distinct();
        var url = _settings.Llm.OllamaUrl;
        var keep = _settings.Llm.KeepLoaded;
        var snapshots = new List<Snapshot>();
        foreach (var m in ollama ?? [])
        {
            var detail = $"{Gb(m.VramBytes)} graphics memory" +
                         (m.VramBytes < m.SizeBytes ? $" + {Gb(m.SizeBytes - m.VramBytes)} in RAM" : "") +
                         (m.StaysLoaded ? " · stays loaded" : m.ExpiresAt is { } at ? $" · unloads {In(at - DateTimeOffset.Now)}" : "");
            snapshots.Add(new Snapshot(m.Name, m.Name, State.Loaded, Split(m), detail,
                Unload: async () => { await OllamaChat.UnloadAsync(url, m.Name); return null; }, Load: null));
        }
        foreach (var name in configured.Where(n => ollama?.Any(m => m.Name == n) != true))
            snapshots.Add(ollama is null
                ? new Snapshot(name, name, State.Unavailable, "Ollama isn't running", "", null, null)
                : new Snapshot(name, name, State.NotLoaded, "Not loaded", "Used by your styles",
                    Unload: null, Load: async () =>
                    {
                        await OllamaChat.WarmUpAsync(url, name, keep);
                        return null;
                    }));
        return snapshots.OrderByDescending(s => s.State == State.Loaded).ThenBy(s => s.Name).ToList();
    }

    async Task<string?> LoadSpeechAsync(string id)
    {
        try
        {
            await _engines.GetAsync(id);
            return null;
        }
        catch (Exception e)
        {
            Log.Error($"Loading {id} failed", e);
            return "Loading failed. See the log.";
        }
    }

    async Task UnloadAllAsync()
    {
        foreach (var row in SpeechRows.Concat(TextRows).Where(r => r.IsLoaded).ToList())
            await row.RunAsync();
        await RefreshAsync();
    }

    /// <summary>Updates the rows in place, so a row being clicked isn't replaced under the mouse.</summary>
    void Sync(ObservableCollection<LoadedModelRow> rows, List<Snapshot> snapshots)
    {
        for (var i = rows.Count - 1; i >= 0; i--)
            if (snapshots.All(s => s.Key != rows[i].Key)) rows.RemoveAt(i);
        for (var index = 0; index < snapshots.Count; index++)
        {
            var snapshot = snapshots[index];
            var current = rows.ToList().FindIndex(r => r.Key == snapshot.Key);
            if (current < 0) rows.Insert(index, new LoadedModelRow(snapshot, RefreshAsync));
            else
            {
                if (current != index) rows.Move(current, index);
                rows[index].Apply(snapshot);
            }
        }
    }

    static string Where(string backend) => backend.ToLowerInvariant() switch
    {
        "cuda" => "GPU (CUDA)",
        "vulkan" => "GPU (Vulkan)",
        "cpu" => "CPU",
        _ => backend,
    };

    /// <summary>Ollama puts as many layers as fit on the graphics card and runs the rest on the processor.</summary>
    static string Split(OllamaLoadedModel m)
    {
        if (m.SizeBytes <= 0) return "GPU";
        var gpu = (double)m.VramBytes / m.SizeBytes;
        return gpu >= 0.99 ? "GPU" : gpu <= 0.01 ? "CPU" : $"GPU {gpu:P0} · CPU {1 - gpu:P0}";
    }

    static string In(TimeSpan span) =>
        span.TotalMinutes < 1 ? "in under a minute"
        : span.TotalMinutes < 90 ? $"in {Math.Round(span.TotalMinutes)} min"
        : $"in {span.TotalHours.ToString("0.#", CultureInfo.InvariantCulture)} h";

    static string Gb(long bytes) => $"{bytes / 1e9:0.0} GB";

    static string Tag(string model) => model.Contains(':') ? model : model + ":latest";

    public enum State { NotLoaded, Loading, Loaded, Unavailable }

    /// <summary>What one poll found out about a model, and what its button does. Unload returns an error message, or null.</summary>
    public sealed record Snapshot(string Key, string Name, State State, string Where, string Detail,
        Func<Task<string?>>? Unload, Func<Task<string?>>? Load);
}

public sealed class LoadedModelRow : ObservableObject
{
    readonly Func<Task> _refresh;
    LoadedModelsViewModel.Snapshot _snapshot;
    bool _busy;

    public LoadedModelRow(LoadedModelsViewModel.Snapshot snapshot, Func<Task> refresh)
    {
        _snapshot = snapshot;
        _refresh = refresh;
        Action = new RelayCommand(_ => _ = RunAsync());
    }

    public string Key => _snapshot.Key;
    public string Name => _snapshot.Name;
    public RelayCommand Action { get; }

    public bool IsLoaded => _snapshot.State == LoadedModelsViewModel.State.Loaded;
    public string Where => _busy ? (IsLoaded ? "Unloading…" : "Loading…") : _snapshot.Where;
    public string Detail => _snapshot.Detail;
    public double Dim => _snapshot.State == LoadedModelsViewModel.State.Loaded ? 1 : 0.6;
    public string ActionLabel => IsLoaded ? "Unload" : "Load";
    public bool CanAct => !_busy && (IsLoaded ? _snapshot.Unload is not null : _snapshot.Load is not null);

    public string? Error
    {
        get => _error;
        private set => Set(ref _error, value);
    }
    string? _error;

    public void Apply(LoadedModelsViewModel.Snapshot snapshot)
    {
        _snapshot = snapshot;
        RaiseAll();
    }

    /// <summary>Unloads a loaded model, or loads one that isn't.</summary>
    public async Task RunAsync()
    {
        var work = IsLoaded ? _snapshot.Unload : _snapshot.Load;
        if (_busy || work is null) return;
        _busy = true;
        Error = null;
        RaiseAll();
        try
        {
            Error = await work();
        }
        finally
        {
            _busy = false;
            RaiseAll();
            await _refresh();
        }
    }

    void RaiseAll()
    {
        Raise(nameof(IsLoaded));
        Raise(nameof(Where));
        Raise(nameof(Detail));
        Raise(nameof(Dim));
        Raise(nameof(ActionLabel));
        Raise(nameof(CanAct));
    }
}
