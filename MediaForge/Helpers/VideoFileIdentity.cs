using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MediaForge.Helpers;

internal static class VideoFileIdentity
{
    internal sealed record Snapshot(string Identity, long Size, long ModifiedUtcTicks);

    public static Snapshot Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return Read(stream);
    }

    public static Snapshot Read(FileStream stream)
    {
        if (!GetFileInformationByHandle(stream.SafeFileHandle, out var info))
            throw new IOException("Nelze zjistit identitu souboru.", new Win32Exception(Marshal.GetLastWin32Error()));
        long modified = ((long)info.WriteHigh << 32) | info.WriteLow;
        return new($"{info.Volume:X8}-{info.IndexHigh:X8}{info.IndexLow:X8}",
            ((long)info.SizeHigh << 32) | info.SizeLow, DateTime.FromFileTimeUtc(modified).Ticks);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
}
