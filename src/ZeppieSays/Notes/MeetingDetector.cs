using Microsoft.Win32;
using ZeppieSays.Core;

namespace ZeppieSays.Notes;

/// <summary>
/// Notices when a meeting app starts or stops using the microphone. Windows keeps this per app in the registry
/// (the same data behind the microphone icon in the taskbar): an app is using the mic while its LastUsedTimeStop is 0.
/// That is a better sign of "in a call" than the app merely running.
/// </summary>
public sealed class MeetingDetector : IDisposable
{
    const string Store = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    /// <summary>Registry key name fragments (package family or exe path) and the name shown to the user.</summary>
    static readonly (string Match, string App)[] MeetingApps =
    [
        ("MSTeams_", "Teams"), ("#ms-teams.exe", "Teams"), ("#Teams.exe", "Teams"),
        ("#Zoom.exe", "Zoom"), ("#CiscoCollabHost.exe", "Webex"), ("#webexmta.exe", "Webex"), ("#atmgr.exe", "Webex"),
        ("#slack.exe", "Slack"), ("#Discord.exe", "Discord"),
    ];

    readonly Timer _timer;
    string? _active;

    /// <summary>Raised on a worker thread with the app's name when it starts using the microphone.</summary>
    public event Action<string>? Started;
    /// <summary>Raised on a worker thread when that app has stopped using the microphone.</summary>
    public event Action<string>? Ended;

    public MeetingDetector() => _timer = new Timer(_ => Poll(), null, 2000, 2000);

    void Poll()
    {
        try
        {
            var app = AppUsingMicrophone();
            if (app == _active) return;
            if (_active is not null) Ended?.Invoke(_active);
            _active = app;
            if (app is not null) Started?.Invoke(app);
        }
        catch (Exception e)
        {
            Log.Warn($"Meeting detection failed: {e.Message}");
        }
    }

    static string? AppUsingMicrophone()
    {
        using var store = Registry.CurrentUser.OpenSubKey(Store);
        if (store is null) return null;
        foreach (var (name, key) in Keys(store))
        {
            var app = MeetingApps.FirstOrDefault(m => name.Contains(m.Match, StringComparison.OrdinalIgnoreCase)).App;
            if (app is null) continue;
            if (key.GetValue("LastUsedTimeStop") is long stop && stop == 0 && key.GetValue("LastUsedTimeStart") is long start && start != 0)
                return app;
        }
        return null;
    }

    /// <summary>Packaged apps sit directly under the store; desktop apps under NonPackaged, keyed by exe path.</summary>
    static IEnumerable<(string Name, RegistryKey Key)> Keys(RegistryKey store)
    {
        foreach (var name in store.GetSubKeyNames().Where(n => n != "NonPackaged"))
            using (var key = store.OpenSubKey(name))
                if (key is not null) yield return (name, key);
        using var desktop = store.OpenSubKey("NonPackaged");
        if (desktop is null) yield break;
        foreach (var name in desktop.GetSubKeyNames())
            using (var key = desktop.OpenSubKey(name))
                if (key is not null) yield return (name, key);
    }

    public void Dispose() => _timer.Dispose();
}
