using MediaForge.Helpers;
using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace MediaForge.Services;

internal sealed class ConversionTemporaryFile
{
    private sealed record Entry(string Path, string Identity);
    private static string JournalDirectory => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MediaForge", "ConversionWork");
    private readonly FileStream _journal;
    private readonly string _journalPath;
    private readonly Entry _entry;
    public string Path => _entry.Path;

    private ConversionTemporaryFile(FileStream journal, string journalPath, Entry entry)
    {
        _journal = journal; _journalPath = journalPath; _entry = entry;
    }

    public static bool IsInternalPath(string path)
    {
        string name = System.IO.Path.GetFileName(path);
        const string prefix = ".mediaforge-";
        string suffix = name.EndsWith(".partial.mp4", StringComparison.OrdinalIgnoreCase) ? ".partial.mp4" : ".partial";
        return name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            && name.Length == prefix.Length + 32 + suffix.Length
            && Guid.TryParseExact(name.Substring(prefix.Length, 32), "N", out _);
    }

    public static ConversionTemporaryFile Create(string directory)
    {
        string id = Guid.NewGuid().ToString("N");
        string path = System.IO.Path.Combine(directory, $".mediaforge-{id}.partial");
        VideoConversionService.EnsureSafePath(JournalDirectory);
        Directory.CreateDirectory(JournalDirectory);
        string journalPath = System.IO.Path.Combine(JournalDirectory, id + ".json");
        var journal = new FileStream(journalPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        Entry? entry = null;
        try
        {
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            entry = new(path, VideoFileIdentity.Read(file).Identity);
            byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry));
            journal.Write(bytes);
            journal.Flush(flushToDisk: true); // FFmpeg cannot write video until its recovery record is durable.
            return new(journal, journalPath, entry);
        }
        catch
        {
            journal.Dispose();
            // Only a file whose successful creation and identity are known belongs to this operation.
            if (entry is not null) DeleteOwned(entry);
            File.Delete(journalPath);
            throw;
        }
    }

    public async Task CompleteAsync(bool published)
    {
        try
        {
            if (!published) await DeleteOwnedAsync(_entry);
            _journal.Dispose();
            File.Delete(_journalPath);
        }
        finally { _journal.Dispose(); }
    }

    public static Task<IReadOnlyList<string>> RecoverAsync() => Task.Run<IReadOnlyList<string>>(async () =>
    {
        var warnings = new List<string>();
        if (!Directory.Exists(JournalDirectory)) return warnings;
        VideoConversionService.EnsureSafePath(JournalDirectory);
        foreach (string record in Directory.EnumerateFiles(JournalDirectory, "*.json"))
        {
            try
            {
                VideoConversionService.EnsureSafePath(record);
                using (var journal = new FileStream(record, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    if (journal.Length > 32768) throw new InvalidDataException("Příliš velký záznam úklidu.");
                    var entry = JsonSerializer.Deserialize<Entry>(journal)
                        ?? throw new InvalidDataException("Neplatný záznam úklidu.");
                    string id = System.IO.Path.GetFileNameWithoutExtension(record);
                    if (!Guid.TryParseExact(id, "N", out _)
                        || System.IO.Path.GetFileName(entry.Path) != $".mediaforge-{id}.partial")
                        throw new InvalidDataException("Název dočasného souboru neodpovídá záznamu.");
                    await DeleteOwnedAsync(entry);
                }
                File.Delete(record);
            }
            catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33)
            { /* A different application instance or a still-exiting child owns this lease. */ }
            catch (Exception ex) { warnings.Add($"Nedokončený úklid {record}: {ex.Message}"); }
        }
        return warnings;
    });

    private static async Task DeleteOwnedAsync(Entry entry)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { DeleteOwned(entry); return; }
            catch (IOException ex) when (attempt < 4
                && ex.InnerException is Win32Exception { NativeErrorCode: 32 or 33 })
            {
                await Task.Delay(200 * (attempt + 1)); // Allow a just-exited process/scanner to release its handle.
            }
        }
    }

    private static void DeleteOwned(Entry entry)
    {
        VideoConversionService.EnsureSafePath(entry.Path);
        using SafeFileHandle handle = CreateFileW(@"\\?\" + System.IO.Path.GetFullPath(entry.Path), 0x80000000 | 0x00010000,
            0, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            if (error is 2 or 3) return;
            throw new IOException("Nelze odstranit rozpracovaný soubor: " + entry.Path, new Win32Exception(error));
        }
        using var stream = new FileStream(handle, FileAccess.Read);
        if (VideoFileIdentity.Read(stream).Identity != entry.Identity)
            throw new IOException("Identita souboru se změnila; cizí soubor nebude odstraněn: " + entry.Path);
        var disposition = new FileDisposition { Delete = 1 };
        if (!SetFileInformationByHandle(handle, 4, ref disposition, (uint)Marshal.SizeOf<FileDisposition>()))
            throw new IOException("Odstranění dočasného souboru selhalo: " + entry.Path,
                new Win32Exception(Marshal.GetLastWin32Error()));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDisposition { public byte Delete; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass,
        ref FileDisposition information, uint size);
}
