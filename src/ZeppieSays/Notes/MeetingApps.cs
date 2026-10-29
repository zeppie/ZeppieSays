using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace ZeppieSays.Notes;

/// <summary>
/// Finds the output device a meeting app plays on, so the call can be recorded from there: Teams often uses the
/// laptop speakers or a headset while Windows' default device is another one.
/// </summary>
public static class MeetingApps
{
    /// <summary>Process names per app, as <see cref="MeetingDetector"/> names the apps.</summary>
    static readonly Dictionary<string, string[]> Processes = new()
    {
        ["Teams"] = ["ms-teams", "Teams"],
        ["Zoom"] = ["Zoom"],
        ["Webex"] = ["CiscoCollabHost", "webexmta", "atmgr"],
        ["Slack"] = ["slack"],
        ["Discord"] = ["Discord"],
    };

    /// <summary>A meeting app that is playing sound right now, for a recording started by hand; null when none is.</summary>
    public static string? Playing()
    {
        try
        {
            using var devices = new MMDeviceEnumerator();
            foreach (var device in devices.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                if (Sessions(device).FirstOrDefault(s => s.Active).App is { } app) return app;
        }
        catch { /* a sound check is a nicety: record from the default device instead */ }
        return null;
    }

    /// <summary>
    /// The output device the app plays on: where it has sound right now, or else where it last had a stream (at
    /// the very start of a call nothing may be playing yet); null when the app has no stream anywhere.
    /// </summary>
    public static MMDevice? DeviceOf(string app, MMDeviceEnumerator devices)
    {
        try
        {
            var all = devices.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
            return all.FirstOrDefault(d => Sessions(d).Any(s => s.App == app && s.Active))
                ?? all.FirstOrDefault(d => Sessions(d).Any(s => s.App == app));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The meeting apps with a sound stream on this device, and whether it is playing.</summary>
    static IEnumerable<(string App, bool Active)> Sessions(MMDevice device)
    {
        var sessions = device.AudioSessionManager.Sessions;
        for (var i = 0; i < sessions.Count; i++)
        {
            string name;
            try { name = Process.GetProcessById((int)sessions[i].GetProcessID).ProcessName; }
            catch { continue; }
            var app = App(name);
            if (app is not null) yield return (app, sessions[i].State == AudioSessionState.AudioSessionStateActive);
        }
    }

    /// <summary>The meeting app a process belongs to ("ms-teams" is Teams), or null.</summary>
    public static string? App(string processName) =>
        Processes.FirstOrDefault(p => p.Value.Contains(processName, StringComparer.OrdinalIgnoreCase)).Key;
}
