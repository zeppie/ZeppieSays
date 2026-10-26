using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using ZeppieSays.Core;
using ZeppieSays.Engines;
using ZeppieSays.Services;

namespace ZeppieSays.UI;

public sealed record Choice(string Value, string Label);

public sealed class MainViewModel : ObservableObject
{
    readonly AppSettings _settings;
    readonly ModelManager _models;
    readonly EngineCache _engines;
    readonly KeyboardHook _hook;

    public StylesViewModel Styles { get; }
    public InsightsViewModel Insights { get; }
    public LoadedModelsViewModel Loaded { get; }
    public NotetakerViewModel Notetaker { get; }
    /// <summary>The dictation keys, in the order of <see cref="AppSettings.Keys"/>.</summary>
    public ObservableCollection<BindingViewModel> Keys { get; } = [];
    public ObservableCollection<Choice> InstalledModels { get; } = [];
    /// <summary>"As spoken" plus every language a key can type in.</summary>
    public IReadOnlyList<Choice> OutputLanguages { get; } =
        ModelCatalog.Languages.Where(l => l.Code != "auto").Select(l => new Choice(l.Code, l.Name))
            .Prepend(new Choice(KeyBinding.SameLanguage, "As spoken")).ToList();
    public ObservableCollection<ModelRowViewModel> Catalog { get; } = [];
    public IReadOnlyList<Choice> Languages { get; } =
        ModelCatalog.Languages.Select(l => new Choice(l.Code, l.Name)).ToList();
    public IReadOnlyList<Choice> Microphones { get; }

    public RelayCommand AddCustomModel { get; }
    public RelayCommand AddKey { get; }
    public RelayCommand RemoveKey { get; }
    public RelayCommand RemoveWord { get; }
    public RelayCommand OpenDataFolder { get; }

    public MainViewModel(AppSettings settings, ModelManager models, EngineCache engines, KeyboardHook hook, Llm.TextRewriter rewriter,
        UsageStore usage, Notes.Notetaker notetaker, Notes.Summarizer summarizer, Notes.VoiceLibrary voices,
        MeetingController meetings, DictionaryFiles dictionaryFiles)
    {
        SetUpDictionaryFiles(dictionaryFiles);
        SetUpSettingsFolder();
        Styles = new StylesViewModel(settings, rewriter, Save);
        Insights = new InsightsViewModel(usage);
        Loaded = new LoadedModelsViewModel(settings, models, engines);
        Notetaker = new NotetakerViewModel(settings, notetaker, summarizer, voices, meetings, Save);
        _settings = settings;
        _models = models;
        _engines = engines;
        _hook = hook;

        foreach (var key in settings.Keys)
            Keys.Add(new BindingViewModel(key, this));
        RenumberKeys();
        AddKey = new RelayCommand(_ =>
        {
            // A new key starts as a copy of the last one, minus the hotkey, which the user presses next.
            var last = _settings.Keys[^1];
            var key = new KeyBinding { Key = "", ModelId = last.ModelId, Language = last.Language, OutputLanguage = last.OutputLanguage };
            _settings.Keys.Add(key);
            var model = new BindingViewModel(key, this);
            Keys.Add(model);
            RenumberKeys();
            ApplyBindings();
            model.RecordKey.Execute(null); // straight to "Press keys…", as if the field was clicked
        });
        RemoveKey = new RelayCommand(p =>
        {
            if (p is not BindingViewModel key || Keys.Count <= 1) return;
            if (_recording == key) { _hook.StopRecording(); _recording = null; }
            _settings.Keys.Remove(key.Binding);
            Keys.Remove(key);
            RenumberKeys();
            ApplyBindings();
        });
        Microphones = AudioRecorder.Devices().Select((name, i) => new Choice(i.ToString(), name))
            .Prepend(new Choice("-1", "Windows default")).ToList();
        AddCustomModel = new RelayCommand(_ => PickCustomModel());
        RemoveWord = new RelayCommand(p =>
        {
            if (p is not DictionaryEntry entry || SelectedDictionaryFile is not { } file) return;
            WriteToFile(() => _dictionaryFiles.RemoveWord(file, entry.Term));
        });
        OpenDataFolder = new RelayCommand(_ =>
            Process.Start(new ProcessStartInfo(AppPaths.Data) { UseShellExecute = true }));

        foreach (var info in ModelCatalog.BuiltIn)
            Catalog.Add(new ModelRowViewModel(info, models));
        models.Changed += () => System.Windows.Application.Current.Dispatcher.BeginInvoke(RefreshModels);
        RefreshModels();
    }

    void RefreshModels()
    {
        var installed = _models.Installed();
        InstalledModels.Clear();
        foreach (var m in installed) InstalledModels.Add(new Choice(m.Info.Id, m.Info.Name));
        foreach (var row in Catalog) row.Refresh();
        foreach (var custom in Catalog.Where(r => r.IsCustom && !_models.IsInstalled(r.Info.Id)).ToList())
            Catalog.Remove(custom);
        foreach (var custom in _settings.CustomModels.Where(c => Catalog.All(r => r.Info.Id != c.Id)))
            Catalog.Add(new ModelRowViewModel(ModelManager.CustomInfo(custom), _models, isCustom: true));

        // A key whose model was just installed (or removed) needs its picker and the loaded engines updated.
        foreach (var key in Keys) key.Refresh();
        ApplyBindings();
        Raise(nameof(Status));
        Raise(nameof(HasNoModels));
    }

    public bool HasNoModels => InstalledModels.Count == 0;

    public string Status
    {
        get
        {
            string Describe(KeyBinding b) =>
                _models.Find(b.ModelId) is { } m ? $"{b.Key} → {m.Info.Name}" : $"{b.Key} → no model";
            return string.Join("   ·   ", _settings.Keys.Where(k => k.Key.Length > 0).Select(Describe));
        }
    }

    void RenumberKeys()
    {
        for (var i = 0; i < Keys.Count; i++) Keys[i].Title = $"Key {i + 1}";
        Raise(nameof(CanRemoveKeys));
    }

    public bool CanRemoveKeys => Keys.Count > 1;

    internal void ApplyBindings()
    {
        _hook.Watch(_settings.Keys.Select(k => k.Key).ToArray());
        _engines.Sync();
        Save();
        Raise(nameof(Status));
    }

    internal ModelInfo? ModelInfo(string id) => _models.Find(id)?.Info;

    BindingViewModel? _recording;

    /// <summary>Lets the user press a new key combination for one binding.</summary>
    internal void RecordHotkey(BindingViewModel target)
    {
        if (_recording is not null)
        {
            // Clicking the field again cancels.
            _hook.StopRecording();
            var was = _recording;
            _recording = null;
            was.EndRecording(null);
            ApplyBindings();
            if (was == target) return;
        }
        _recording = target;
        _hook.Watch(); // no dictation while recording
        _hook.Record(hotkey =>
        {
            _recording = null;
            var other = hotkey is null ? null : Keys.FirstOrDefault(k => k != target && k.Key == hotkey.Text);
            var problem = hotkey is null ? null
                : hotkey.Problem()
                  ?? (other is not null ? $"{hotkey.Text} is already used by {other.Title}." : null);
            target.EndRecording(hotkey is not null && problem is null ? hotkey.Text : null, problem);
            ApplyBindings();
        });
    }

    void PickCustomModel()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Add a whisper.cpp model",
            Filter = "Whisper ggml model (*.bin)|*.bin",
        };
        if (dialog.ShowDialog() == true)
        {
            _models.AddCustom(dialog.FileName);
            Save();
        }
    }

    public string NewWord
    {
        get => _newWord;
        set => Set(ref _newWord, value);
    }
    string _newWord = "";

    /// <summary>"Betabit" or "Betabit = beta bit, beta bid", into the selected dictionary file.</summary>
    public void AddWord()
    {
        if (DictionaryEntry.Parse(NewWord) is not { } entry || SelectedDictionaryFile is not { } file) return;
        if (WriteToFile(() => _dictionaryFiles.AddWord(file, entry))) NewWord = "";
    }

    /// <summary>Edits a dictionary file, saying so when it can't be written (read-only, or a share that's gone).</summary>
    static bool WriteToFile(Action edit)
    {
        try
        {
            edit();
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Error("Could not edit a dictionary file", e);
            System.Windows.MessageBox.Show($"Could not change this dictionary file: {e.Message}", "Zeppie Says",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return false;
        }
    }

    public bool UseScreenContext
    {
        get => _settings.UseScreenContext;
        set { _settings.UseScreenContext = value; Save(); Raise(); }
    }

    public bool RemoveFillerWords
    {
        get => _settings.RemoveFillerWords;
        set { _settings.RemoveFillerWords = value; Save(); Raise(); }
    }

    public bool UseGpu
    {
        get => _settings.UseGpu;
        set { _settings.UseGpu = value; Save(); Raise(); }
    }

    public string Microphone
    {
        get => _settings.MicrophoneDevice.ToString();
        set { _settings.MicrophoneDevice = int.TryParse(value, out var d) ? d : -1; Save(); Raise(); }
    }

    public bool StartWithWindows
    {
        get => _settings.StartWithWindows;
        set
        {
            _settings.StartWithWindows = value;
            Autostart.Apply(value);
            Save();
            Raise();
        }
    }

    void Save() => SettingsStore.Save(_settings);

    // ---- Dictionary files -----------------------------------------------------------------------

    public ObservableCollection<DictionaryFile> DictionaryFileList { get; } = [];
    public RelayCommand AddDictionaryFile { get; private set; } = null!;
    public RelayCommand NewDictionaryFile { get; private set; } = null!;
    public RelayCommand RemoveDictionaryFile { get; private set; } = null!;
    public RelayCommand OpenDictionaryFile { get; private set; } = null!;
    DictionaryFiles _dictionaryFiles = null!;

    const string FileHeader =
        "Zeppie Says dictionary: one word per line.\n" +
        "To fix a word the model keeps getting wrong: Betabit = beta bit, beta bid\n" +
        "Lines starting with # are ignored. Changes are picked up as soon as the file is saved.";

    /// <summary>The file whose words the left side shows and edits; the default file at first.</summary>
    public DictionaryFile? SelectedDictionaryFile
    {
        get => _selectedDictionaryFile;
        set
        {
            if (value is null || value == _selectedDictionaryFile) return; // the list briefly clears while it refreshes
            _selectedDictionaryFile = value;
            Raise();
            ShowWords();
        }
    }
    DictionaryFile? _selectedDictionaryFile;

    /// <summary>The words of the selected file, newest (last line) first.</summary>
    public ObservableCollection<DictionaryEntry> Dictionary { get; } = [];

    void ShowWords()
    {
        Dictionary.Clear();
        if (SelectedDictionaryFile is { } file)
            foreach (var entry in DictionaryFiles.WordsOf(file).Reverse()) Dictionary.Add(entry);
    }

    void SetUpDictionaryFiles(DictionaryFiles files)
    {
        _dictionaryFiles = files;
        void Show()
        {
            // Keep the same file selected across a reload (its record changes when its word count does).
            var selected = _selectedDictionaryFile?.FullPath;
            DictionaryFileList.Clear();
            foreach (var file in files.Files) DictionaryFileList.Add(file);
            _selectedDictionaryFile = DictionaryFileList.FirstOrDefault(f => f.FullPath.Equals(selected, StringComparison.OrdinalIgnoreCase))
                                      ?? DictionaryFileList.FirstOrDefault();
            Raise(nameof(SelectedDictionaryFile));
            ShowWords();
            Raise(nameof(FileWordCount));
        }
        files.Changed += Show;
        Show();

        AddDictionaryFile = new RelayCommand(_ =>
        {
            var dialog = new OpenFileDialog
            {
                Title = "Add dictionary files", Filter = "Text files (*.txt)|*.txt", Multiselect = true,
                InitialDirectory = AppPaths.Settings,
            };
            if (dialog.ShowDialog() != true) return;
            foreach (var path in dialog.FileNames) files.Add(path);
        });
        NewDictionaryFile = new RelayCommand(_ =>
        {
            var dialog = new SaveFileDialog
            {
                Title = "New dictionary file", Filter = "Text files (*.txt)|*.txt", FileName = "Dictionary.txt",
                InitialDirectory = AppPaths.Settings,
            };
            if (dialog.ShowDialog() != true) return;
            if (!File.Exists(dialog.FileName)) DictionaryFiles.Write(dialog.FileName, [], FileHeader);
            _selectedDictionaryFile = null;
            files.Add(dialog.FileName);
            // Open the new file on the left, ready for its first word.
            SelectedDictionaryFile = DictionaryFileList.FirstOrDefault(f =>
                f.FullPath.Equals(Path.GetFullPath(dialog.FileName), StringComparison.OrdinalIgnoreCase));
        });
        RemoveDictionaryFile = new RelayCommand(p => { if (p is DictionaryFile file) files.Remove(file); });
        OpenDictionaryFile = new RelayCommand(p => { if (p is DictionaryFile file) OpenFile(file.FullPath); });
    }

    /// <summary>How many words the dictionary files add, for the page's caption.</summary>
    public int FileWordCount => _settings.FileWords.Count;

    static void OpenFile(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception e) { Log.Error($"Could not open {path}", e); }
    }

    // ---- Settings folder ------------------------------------------------------------------------

    /// <summary>Where settings, voices and stats are kept (see <see cref="AppPaths.Settings"/>).</summary>
    public string SettingsFolder => AppPaths.Settings;
    public bool SettingsAreLocal => AppPaths.Settings.TrimEnd('\\').Equals(AppPaths.Data.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    public RelayCommand ChooseSettingsFolder { get; private set; } = null!;
    public RelayCommand OpenSettingsFolder { get; private set; } = null!;
    public RelayCommand UseLocalSettings { get; private set; } = null!;

    void SetUpSettingsFolder()
    {
        ChooseSettingsFolder = new RelayCommand(_ =>
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Folder for your Zeppie Says settings (for example in OneDrive)",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            };
            if (dialog.ShowDialog() == true) MoveSettingsTo(dialog.FolderName);
        });
        OpenSettingsFolder = new RelayCommand(_ => OpenFile(AppPaths.Settings));
        UseLocalSettings = new RelayCommand(_ => MoveSettingsTo(AppPaths.Data));
    }

    /// <summary>
    /// Points Zeppie Says at another settings folder and restarts. If that folder already holds Zeppie Says settings
    /// (a reinstall, or another PC), the user picks: use those, or replace them with this PC's.
    /// </summary>
    void MoveSettingsTo(string folder)
    {
        var from = Path.GetFullPath(AppPaths.Settings).TrimEnd('\\');
        var to = Path.GetFullPath(folder).TrimEnd('\\');
        if (from.Equals(to, StringComparison.OrdinalIgnoreCase)) return;

        var theirs = Path.Combine(to, "settings.json");
        var copyOurs = true;
        if (File.Exists(theirs))
        {
            var answer = System.Windows.MessageBox.Show(
                $"This folder already has Zeppie Says settings, last saved {File.GetLastWriteTime(theirs):d MMM yyyy, HH:mm}.\n\n" +
                "Yes: use those settings (your dictionary, styles, keys and voices from there).\n" +
                "No: replace them with the settings from this PC.\n\n" +
                "Zeppie Says restarts either way. API keys stay on this PC.",
                "Zeppie Says settings", System.Windows.MessageBoxButton.YesNoCancel, System.Windows.MessageBoxImage.Question);
            if (answer == System.Windows.MessageBoxResult.Cancel) return;
            copyOurs = answer == System.Windows.MessageBoxResult.No;
        }

        try
        {
            if (copyOurs)
            {
                Save(); // make sure the files in the current folder are up to date
                Directory.CreateDirectory(to);
                foreach (var name in AppPaths.SettingsFiles)
                    if (File.Exists(Path.Combine(from, name)))
                        File.Copy(Path.Combine(from, name), Path.Combine(to, name), overwrite: true);
                // Dictionary files kept inside the settings folder belong to it: they move along.
                foreach (var stored in _settings.DictionaryFiles.Where(p => !Path.IsPathRooted(p)))
                {
                    var source = Path.Combine(from, stored);
                    var target = Path.Combine(to, stored);
                    if (!File.Exists(source) || File.Exists(target)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(source, target);
                }
            }
            AppPaths.SetSettingsFolder(to);
            Log.Info($"Settings folder is now {to} ({(copyOurs ? "copied from this PC" : "using what was there")})");
        }
        catch (Exception e)
        {
            Log.Error($"Could not move the settings to {to}", e);
            System.Windows.MessageBox.Show($"Could not use that folder: {e.Message}", "Zeppie Says",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }
        ((App)System.Windows.Application.Current).Restart();
    }
}

public sealed class BindingViewModel : ObservableObject
{
    readonly KeyBinding _binding;
    readonly MainViewModel _owner;
    bool _isRecording;
    string? _keyProblem;

    public BindingViewModel(KeyBinding binding, MainViewModel owner)
    {
        _binding = binding;
        _owner = owner;
        RecordKey = new RelayCommand(_ => { _owner.RecordHotkey(this); IsRecording = true; KeyProblem = null; });
    }

    internal KeyBinding Binding => _binding;

    /// <summary>"Key 1", "Key 2", … by position.</summary>
    public string Title
    {
        get => _title;
        set => Set(ref _title, value);
    }
    string _title = "";

    /// <summary>The hotkey as text, e.g. "F8" or "Ctrl+Win".</summary>
    public string Key => _binding.Key;

    /// <summary>What the key field shows: the hotkey, or a prompt while waiting for keys.</summary>
    public string KeyLabel => IsRecording ? "Press keys…" : Key.Length == 0 ? "Set a key" : Key.Replace("+", " + ");

    public RelayCommand RecordKey { get; }

    public bool IsRecording
    {
        get => _isRecording;
        private set { if (Set(ref _isRecording, value)) Raise(nameof(KeyLabel)); }
    }

    /// <summary>Why the last pressed combination was refused, shown under the field.</summary>
    public string? KeyProblem
    {
        get => _keyProblem;
        private set => Set(ref _keyProblem, value);
    }

    /// <summary>Ends a recording; a null hotkey keeps the current one.</summary>
    internal void EndRecording(string? hotkey, string? problem = null)
    {
        if (hotkey is not null) _binding.Key = hotkey;
        IsRecording = false;
        KeyProblem = problem;
        Raise(nameof(Key));
        Raise(nameof(KeyLabel));
    }

    public string ModelId
    {
        get => _binding.ModelId;
        set
        {
            if (value is null) return; // the picker briefly clears while its list refreshes
            _binding.ModelId = value;
            _owner.ApplyBindings();
            Raise();
            Raise(nameof(LanguageEnabled));
            Raise(nameof(ModelName));
        }
    }

    public string Language
    {
        get => _binding.Language;
        set { if (value is null) return; _binding.Language = value; _owner.ApplyBindings(); Raise(); }
    }

    /// <summary>"same" to type what was said, or a language to translate into (by the style model).</summary>
    public string OutputLanguage
    {
        get => _binding.OutputLanguage;
        set { if (value is null) return; _binding.OutputLanguage = value; _owner.ApplyBindings(); Raise(); Raise(nameof(OutputLabel)); }
    }

    /// <summary>"→ English" for a key that translates, else empty.</summary>
    public string OutputLabel => _binding.Translates
        ? "→ " + (ModelCatalog.Languages.FirstOrDefault(l => l.Code == _binding.OutputLanguage).Name ?? _binding.OutputLanguage)
        : "";

    /// <summary>Parakeet picks the language itself, so its language picker is greyed out.</summary>
    public bool LanguageEnabled => _owner.ModelInfo(_binding.ModelId) is not { AutoLanguageOnly: true };

    public string ModelName => _owner.ModelInfo(_binding.ModelId)?.Name ?? "no model yet";

    public void Refresh()
    {
        Raise(nameof(ModelId));
        Raise(nameof(LanguageEnabled));
        Raise(nameof(ModelName));
    }
}

public sealed class ModelRowViewModel : ObservableObject
{
    readonly ModelManager _models;
    CancellationTokenSource? _download;

    public ModelInfo Info { get; }
    public bool IsCustom { get; }
    public RelayCommand Action { get; }

    public ModelRowViewModel(ModelInfo info, ModelManager models, bool isCustom = false)
    {
        Info = info;
        IsCustom = isCustom;
        _models = models;
        Action = new RelayCommand(_ => _ = RunAsync());
    }

    public string Size => Info.ApproxBytes >= 1_000_000_000
        ? $"{Info.ApproxBytes / 1e9:0.0} GB"
        : $"{Info.ApproxBytes / 1e6:0} MB";

    public string Details => $"{Info.Languages} · {Size} · {(Info.Engine == EngineKind.Whisper ? "GPU" : "CPU")}";

    public bool IsInstalled => _models.IsInstalled(Info.Id);
    public bool IsBusy => _download is not null;

    public double Progress
    {
        get => _progress;
        private set => Set(ref _progress, value);
    }
    double _progress;

    public string ActionLabel => IsBusy ? "Cancel" : IsInstalled ? "Remove" : "Install";

    public string? Error
    {
        get => _error;
        private set => Set(ref _error, value);
    }
    string? _error;

    async Task RunAsync()
    {
        if (_download is not null)
        {
            _download.Cancel();
            return;
        }
        if (IsInstalled)
        {
            _models.Uninstall(Info.Id);
            return;
        }

        Error = null;
        _download = new CancellationTokenSource();
        Refresh();
        try
        {
            await _models.InstallAsync(Info, new Progress<double>(p => Progress = p), _download.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Log.Error($"Installing {Info.Id} failed", e);
            Error = "Download failed. Check your connection and try again.";
        }
        finally
        {
            _download = null;
            Progress = 0;
            Refresh();
        }
    }

    public void Refresh()
    {
        Raise(nameof(IsInstalled));
        Raise(nameof(IsBusy));
        Raise(nameof(ActionLabel));
    }
}

public static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void Apply(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key is null) return;
        if (enabled) key.SetValue("ZeppieSays", $"\"{Environment.ProcessPath}\" --background");
        else key.DeleteValue("ZeppieSays", throwOnMissingValue: false);
    }
}
