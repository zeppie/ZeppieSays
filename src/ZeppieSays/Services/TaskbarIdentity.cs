using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using ZeppieSays.Core;

namespace ZeppieSays.Services;

/// <summary>
/// Gives a window its own taskbar identity. The installed app runs under Velopack's app ID, and for that ID the
/// taskbar showed a blank window icon instead of ours: it takes the icon from the exe through the shortcut, and
/// Windows came up blank for it (the shortcut and the exe do have the right icon). With an ID of its own and no
/// relaunch icon the taskbar uses the window's own icon.
/// </summary>
public static class TaskbarIdentity
{
    const string AppId = "Zeppie.ZeppieSays.Window";

    /// <summary>Call before the window is shown (from SourceInitialized): Windows reads these once.</summary>
    public static void Apply(Window window)
    {
        try
        {
            var iid = typeof(IPropertyStore).GUID;
            Marshal.ThrowExceptionForHR(SHGetPropertyStoreForWindow(new WindowInteropHelper(window).Handle, ref iid, out var store));
            try
            {
                // No relaunch icon: pointing it at the exe brings the blank icon back.
                Set(store, Id, AppId);
                store.Commit();
            }
            finally
            {
                Marshal.ReleaseComObject(store);
            }
        }
        catch (Exception e)
        {
            Log.Warn($"Could not set the taskbar identity: {e.Message}");
        }
    }

    static void Set(IPropertyStore store, PropertyKey key, string value)
    {
        // Not freed: the window's property store keeps pointing at the string, and freeing it crashed the app a few
        // seconds later. One short string per window.
        var variant = new PropVariant { Type = 31 /* VT_LPWSTR */, Pointer = Marshal.StringToCoTaskMemUni(value) };
        Marshal.ThrowExceptionForHR(store.SetValue(ref key, ref variant));
    }

    // System.AppUserModel.ID
    static PropertyKey Id => new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    readonly struct PropertyKey(Guid formatId, uint propertyId)
    {
        readonly Guid _formatId = formatId;
        readonly uint _propertyId = propertyId;
    }

    [StructLayout(LayoutKind.Explicit)]
    struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Pointer;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [DllImport("shell32.dll")]
    static extern int SHGetPropertyStoreForWindow(IntPtr window, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
}
