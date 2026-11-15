using System;
using System.Runtime.InteropServices;

namespace MediaForge.Services;

internal static class KnownFolderService
{
    public static string? GetDownloadsPath()
    {
        Guid id = new("374DE290-123F-4565-9164-39C4925E467B");
        IntPtr path = IntPtr.Zero;
        try
        {
            int result = SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out path);
            return result >= 0 ? Marshal.PtrToStringUni(path) : null;
        }
        finally { if (path != IntPtr.Zero) Marshal.FreeCoTaskMem(path); }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);
}
