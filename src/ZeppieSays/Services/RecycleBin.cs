using System.IO;
using System.Runtime.InteropServices;

namespace ZeppieSays.Services;

/// <summary>Moves files and folders to the Windows Recycle Bin, where they can be restored, instead of deleting them.</summary>
public static class RecycleBin
{
    /// <summary>Recycles everything that exists in one go (one undo in Explorer); returns false when Windows refused.</summary>
    public static bool Send(IEnumerable<string> paths)
    {
        var existing = paths.Where(p => File.Exists(p) || Directory.Exists(p)).Select(Path.GetFullPath).Distinct().ToList();
        if (existing.Count == 0) return true;
        var operation = new ShFileOperation
        {
            Func = FoDelete,
            From = string.Join('\0', existing) + "\0\0", // the API takes a double-null-terminated list
            Flags = FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi,
        };
        return SHFileOperation(ref operation) == 0 && !operation.AnyOperationsAborted;
    }

    const uint FoDelete = 3;
    const ushort FofSilent = 0x4, FofNoConfirmation = 0x10, FofAllowUndo = 0x40, FofNoErrorUi = 0x400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ShFileOperation
    {
        public IntPtr Window;
        public uint Func;
        [MarshalAs(UnmanagedType.LPWStr)] public string From;
        [MarshalAs(UnmanagedType.LPWStr)] public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool AnyOperationsAborted;
        public IntPtr NameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHFileOperation(ref ShFileOperation operation);
}
