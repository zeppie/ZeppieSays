using System.IO;
using System.Windows.Threading;
using ZeppieSays.Core;

namespace ZeppieSays.Services;

/// <summary>One dictionary text file and what reading it gave.</summary>
public sealed record DictionaryFile(string StoredPath, string FullPath, int Words, string? Problem)
{
    public string Name => Path.GetFileName(FullPath);
}

/// <summary>
/// Reads the dictionary text files (one word per line, "Term = sounds like, …", "#" comments) into
/// <see cref="AppSettings.FileWords"/>, and reads them again when one changes on disk, so a file that syncs in
/// from OneDrive or a shared team folder takes effect without restarting.
/// </summary>
public sealed class DictionaryFiles : IDisposable
{
    readonly AppSettings _settings;
    readonly Dispatcher _ui;
    readonly List<FileSystemWatcher> _watchers = [];
    readonly DispatcherTimer _debounce;

    public IReadOnlyList<DictionaryFile> Files { get; private set; } = [];

    /// <summary>Raised on the UI thread after the files were (re)read.</summary>
    public event Action? Changed;

    public DictionaryFiles(AppSettings settings, Dispatcher ui)
    {
        _settings = settings;
        _ui = ui;
        // Editors and OneDrive write a file in several steps; read it once things have settled.
        _debounce = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMilliseconds(500) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Reload(); };
        Reload();
    }

    /// <summary>A path as stored in the settings: relative when the file is inside the settings folder.</summary>
    public static string ToStored(string fullPath)
    {
        var folder = Path.GetFullPath(AppPaths.Settings).TrimEnd('\\') + "\\";
        var full = Path.GetFullPath(fullPath);
        return full.StartsWith(folder, StringComparison.OrdinalIgnoreCase) ? full[folder.Length..] : full;
    }

    public static string ToFull(string stored) =>
        Path.IsPathRooted(stored) ? stored : Path.GetFullPath(Path.Combine(AppPaths.Settings, stored));

    public static IReadOnlyList<DictionaryEntry> Read(string fullPath)
    {
        var source = Path.GetFileNameWithoutExtension(fullPath);
        return File.ReadAllLines(fullPath)
            .Select(DictionaryEntry.Parse)
            .OfType<DictionaryEntry>()
            .Select(e => { e.Source = source; return e; })
            .ToList();
    }

    public static void Write(string fullPath, IEnumerable<DictionaryEntry> entries, string header)
    {
        var lines = header.Split('\n').Select(l => "# " + l.TrimEnd()).Append("").Concat(entries.Select(e => e.ToString()));
        File.WriteAllLines(fullPath, lines);
    }

    public void Add(string fullPath)
    {
        var stored = ToStored(fullPath);
        if (_settings.DictionaryFiles.Any(p => ToFull(p).Equals(Path.GetFullPath(fullPath), StringComparison.OrdinalIgnoreCase)))
            return;
        _settings.DictionaryFiles.Add(stored);
        SettingsStore.Save(_settings);
        Reload();
    }

    public void Remove(DictionaryFile file)
    {
        _settings.DictionaryFiles.Remove(file.StoredPath);
        SettingsStore.Save(_settings);
        Reload();
    }

    public void Reload()
    {
        var files = new List<DictionaryFile>();
        var words = new List<DictionaryEntry>();
        foreach (var stored in _settings.DictionaryFiles)
        {
            var full = ToFull(stored);
            try
            {
                var entries = Read(full);
                words.AddRange(entries);
                files.Add(new DictionaryFile(stored, full, entries.Count, null));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A file on a OneDrive that isn't synced yet, or a removed share: keep it listed, say why.
                files.Add(new DictionaryFile(stored, full, 0, e is FileNotFoundException or DirectoryNotFoundException
                    ? "File not found" : "Can't read this file right now"));
                Log.Warn($"Dictionary file {full}: {e.Message}");
            }
        }
        _settings.FileWords = words; // a new list, so a transcription reading the old one isn't disturbed
        Files = files;
        Watch(files.Select(f => f.FullPath));
        Changed?.Invoke();
    }

    void Watch(IEnumerable<string> paths)
    {
        foreach (var watcher in _watchers) watcher.Dispose();
        _watchers.Clear();
        var watched = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in watched.Select(Path.GetDirectoryName).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(dir)) continue;
            var watcher = new FileSystemWatcher(dir) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
            FileSystemEventHandler changed = (_, e) =>
            {
                if (watched.Contains(e.FullPath)) _ui.BeginInvoke(() => { _debounce.Stop(); _debounce.Start(); });
            };
            watcher.Changed += changed;
            watcher.Created += changed;
            watcher.Deleted += changed;
            watcher.Renamed += (_, e) =>
            {
                if (watched.Contains(e.FullPath) || watched.Contains(e.OldFullPath))
                    _ui.BeginInvoke(() => { _debounce.Stop(); _debounce.Start(); });
            };
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
    }

    public void Dispose()
    {
        foreach (var watcher in _watchers) watcher.Dispose();
        _debounce.Stop();
    }
}
