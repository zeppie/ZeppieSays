using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace ZeppieSays.Notes;

/// <summary>
/// Finds a meeting app's processes, so its own sound can be recorded (process loopback) whatever device it plays
/// through: Teams often uses the laptop speakers or a headset while Windows' default is another device.
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

    /// <summary>The app's top process: the one whose parent isn't the same app, so recording its tree catches every child.</summary>
    public static int? RootProcess(string app)
    {
        if (!Processes.TryGetValue(app, out var names)) return null;
        var processes = names.SelectMany(Process.GetProcessesByName).ToList();
        var ids = processes.Select(p => p.Id).ToHashSet();
        return Root(processes.Select(p => (p.Id, ParentOf(p.Id))), ids);
    }

    /// <summary>Of the given processes, the first whose parent is outside the set (the app's own top process).</summary>
    public static int? Root(IEnumerable<(int Id, int? Parent)> processes, IReadOnlySet<int> app)
    {
        foreach (var (id, parent) in processes)
            if (parent is not { } p || !app.Contains(p)) return id;
        return null;
    }

    /// <summary>A meeting app that is playing sound right now, for a recording started by hand; null when none is.</summary>
    public static string? Playing()
    {
        try
        {
            using var devices = new MMDeviceEnumerator();
            foreach (var device in devices.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                if (AppsWithSound(device).FirstOrDefault() is { } app) return app;
        }
        catch { /* a sound check is a nicety: record from the default device instead */ }
        return null;
    }

    /// <summary>The output device the app has sound on, so it can be recorded when its own sound can't be.</summary>
    public static MMDevice? DeviceOf(string app, MMDeviceEnumerator devices)
    {
        try
        {
            return devices.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).FirstOrDefault(d => AppsWithSound(d).Contains(app));
        }
        catch
        {
            return null;
        }
    }

    static IEnumerable<string> AppsWithSound(MMDevice device)
    {
        var sessions = device.AudioSessionManager.Sessions;
        for (var i = 0; i < sessions.Count; i++)
        {
            if (sessions[i].State != AudioSessionState.AudioSessionStateActive) continue;
            string name;
            try { name = Process.GetProcessById((int)sessions[i].GetProcessID).ProcessName; }
            catch { continue; }
            foreach (var (app, names) in Processes)
                if (names.Contains(name, StringComparer.OrdinalIgnoreCase)) yield return app;
        }
    }

    static int? ParentOf(int id)
    {
        var handle = OpenProcess(QueryLimitedInformation, false, id); // the least access that answers "who is your parent"
        if (handle == IntPtr.Zero) return null;
        try
        {
            var info = new ProcessBasicInformation();
            return NtQueryInformationProcess(handle, 0, ref info, Marshal.SizeOf(info), out _) == 0
                ? (int)info.InheritedFromUniqueProcessId
                : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    const uint QueryLimitedInformation = 0x1000;
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int id);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    struct ProcessBasicInformation
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    static extern int NtQueryInformationProcess(IntPtr process, int infoClass, ref ProcessBasicInformation info, int size, out int returned);
}
