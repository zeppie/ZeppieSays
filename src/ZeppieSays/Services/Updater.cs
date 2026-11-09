using Velopack;
using Velopack.Sources;
using ZeppieSays.Core;
using ZeppieSays.UI;

namespace ZeppieSays.Services;

/// <summary>
/// Keeps an installed ZeppieSays up to date from the GitHub releases: checks shortly after start and then every few
/// hours, downloads a new version in the background, and installs it when ZeppieSays quits (or right away on request).
/// A development build (not installed) never updates itself.
/// </summary>
public sealed class Updater : ObservableObject
{
    public const string ReleasesUrl = "https://github.com/zeppie/ZeppieSays";
    static readonly TimeSpan FirstCheck = TimeSpan.FromSeconds(30);
    static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    // ZEPPIESAYS_UPDATES: a local folder with releases (artifacts\releases), to try an update before publishing it.
    readonly UpdateManager _manager = Environment.GetEnvironmentVariable("ZEPPIESAYS_UPDATES") is { Length: > 0 } folder
        ? new(folder)
        : new(new GithubSource(ReleasesUrl, accessToken: null, prerelease: false));
    readonly Action _quit;
    UpdateInfo? _ready;
    bool _checking;
    string _status = "";

    /// <summary>Raised with the new version number once it is downloaded and waiting for a restart.</summary>
    public event Action<string>? Ready;

    public Updater(Action quit)
    {
        _quit = quit;
        CheckNow = new RelayCommand(_ => _ = CheckAsync());
        RestartToUpdate = new RelayCommand(_ => ApplyAndRestart());
        _status = IsInstalled ? "" : "This is a development build; only the installed app updates itself.";
    }

    public bool IsInstalled => _manager.IsInstalled;

    public string Version => _manager.CurrentVersion?.ToString() ??
                             $"{typeof(Updater).Assembly.GetName().Version?.ToString(3)} (development build)";

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public bool IsUpdateReady => _ready is not null;
    public bool CanCheck => IsInstalled && !_checking && _ready is null;

    public RelayCommand CheckNow { get; }
    public RelayCommand RestartToUpdate { get; }

    public void Start()
    {
        if (IsInstalled) _ = LoopAsync();
    }

    async Task LoopAsync()
    {
        await Task.Delay(FirstCheck); // don't compete with loading models at startup
        while (_ready is null)
        {
            await CheckAsync();
            await Task.Delay(Interval);
        }
    }

    async Task CheckAsync()
    {
        if (!CanCheck) return;
        _checking = true;
        Raise(nameof(CanCheck));
        Status = "Checking for updates…";
        try
        {
            var update = await _manager.CheckForUpdatesAsync();
            if (update is null)
            {
                Status = $"Up to date (checked at {DateTime.Now:HH:mm}).";
                return;
            }
            var version = update.TargetFullRelease.Version.ToString();
            Status = $"Downloading version {version}…";
            await _manager.DownloadUpdatesAsync(update);
            _ready = update;
            Log.Info($"Update {version} downloaded; it is installed when ZeppieSays quits");
            Status = $"Version {version} is ready. It is installed the next time ZeppieSays quits, or now with Restart.";
            Raise(nameof(IsUpdateReady));
            Ready?.Invoke(version);
        }
        catch (Exception e)
        {
            Log.Warn($"Update check failed: {e.Message}");
            Status = $"Couldn't check for updates ({e.Message}).";
        }
        finally
        {
            _checking = false;
            Raise(nameof(CanCheck));
        }
    }

    void ApplyAndRestart()
    {
        if (_ready is null) return;
        _manager.WaitExitThenApplyUpdates(_ready.TargetFullRelease, silent: true, restart: true);
        _ready = null; // so quitting doesn't start a second updater
        _quit();
    }

    /// <summary>Called as ZeppieSays exits: hands a downloaded update to Velopack, which installs it once we're gone.</summary>
    public void ApplyOnExit()
    {
        if (_ready is null) return;
        try
        {
            _manager.WaitExitThenApplyUpdates(_ready.TargetFullRelease, silent: true, restart: false);
        }
        catch (Exception e)
        {
            Log.Warn($"Could not start the update: {e.Message}");
        }
    }
}
