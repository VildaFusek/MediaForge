using MediaForge.Helpers;
using MediaForge.Models;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MediaForge.Services;

public sealed class VideoThumbnailService
{
    private static readonly string CacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MediaForge", "Cache", "Thumbnails");

    public Task<string> GetThumbnailAsync(
        VideoDiagnosticRecord record, FfmpegInstallation installation, CancellationToken token) =>
        Task.Run(async () =>
        {
            FileSystemService.EnsureNoReparsePoints(record.FilePath);
            using var input = new FileStream(record.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var before = VideoFileIdentity.Read(input);
            if (before.Identity != record.FileIdentity || before.Size != record.FileSize
                || before.ModifiedUtcTicks != record.ModifiedUtcTicks)
                throw new IOException("Video se změnilo; miniatura vyžaduje aktuální analýzu.");
            string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"thumbnail-v1|{record.FileIdentity}|{record.FileSize}|{record.ModifiedUtcTicks}")));
            Directory.CreateDirectory(CacheDirectory);
            FileSystemService.EnsureNoReparsePoints(CacheDirectory);
            string destination = Path.Combine(CacheDirectory, key + ".jpg");
            if (File.Exists(destination))
            {
                ValidateImage(destination);
                return new Uri(destination).AbsoluteUri;
            }
            string temporary = Path.Combine(CacheDirectory, key + "." + Guid.NewGuid().ToString("N") + ".tmp.jpg");
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(installation.FfmpegPath)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                    WorkingDirectory = Path.GetDirectoryName(installation.FfmpegPath)!
                }
            };
            double seek = record.DurationSeconds is > 1 ? Math.Min(record.DurationSeconds.Value * 0.1, 30) : 0;
            int videoStream = record.Streams.First(s => s.Type == "video" && !s.IsAttachedPicture).Index;
            foreach (string argument in new[]
            {
                "-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-filter_threads", "1",
                "-protocol_whitelist", "file", "-threads", "1", "-ss", seek.ToString("F3", CultureInfo.InvariantCulture),
                "-i", record.FilePath, "-map", $"0:{videoStream}", "-an", "-sn", "-dn",
                "-frames:v", "1", "-vf", "scale=480:270:force_original_aspect_ratio=decrease,setsar=1",
                "-threads", "1", "-q:v", "3", "-f", "image2", "-update", "1", temporary
            }) process.StartInfo.ArgumentList.Add(argument);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            // FFmpeg may overwrite only this newly reserved, private temporary file.
            using (var reserved = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            try
            {
                if (!process.Start()) throw new IOException("FFmpeg se nepodařilo spustit.");
                try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                { Trace.TraceWarning(ex.Message); }
                Task<string> stdout = ReadLogAsync(process.StandardOutput, timeout.Token);
                Task<string> stderr = ReadLogAsync(process.StandardError, timeout.Token);
                try
                {
                    await Task.WhenAll(process.WaitForExitAsync(timeout.Token), stdout, stderr);
                    if (process.ExitCode != 0)
                        throw new IOException($"FFmpeg: {(await stderr).Trim()} (kód {process.ExitCode}).");
                    token.ThrowIfCancellationRequested();
                    if (VideoFileIdentity.Read(input) != before)
                        throw new IOException("Video se během vytváření miniatury změnilo.");
                    ValidateImage(temporary);
                    try { File.Move(temporary, destination, overwrite: false); }
                    catch (IOException) when (File.Exists(destination)) { ValidateImage(destination); }
                    return new Uri(destination).AbsoluteUri;
                }
                finally
                {
                    timeout.Cancel();
                    if (!process.HasExited)
                    {
                        try { process.Kill(entireProcessTree: true); }
                        catch (InvalidOperationException) when (process.HasExited) { }
                        await process.WaitForExitAsync(CancellationToken.None);
                    }
                    try { await Task.WhenAll(stdout, stderr); }
                    catch (OperationCanceledException) { }
                }
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (Exception ex) { AppLog.Error("Úklid miniatury " + temporary, ex); }
            }
        }, token);

    private static void ValidateImage(string path)
    {
        FileSystemService.EnsureNoReparsePoints(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is < 4 or > 5242880 || file.ReadByte() != 0xFF || file.ReadByte() != 0xD8)
            throw new InvalidDataException("Neplatná miniatura v cache: " + path);
        file.Seek(-2, SeekOrigin.End);
        if (file.ReadByte() != 0xFF || file.ReadByte() != 0xD9)
            throw new InvalidDataException("Neúplná miniatura v cache: " + path);
    }

    private static async Task<string> ReadLogAsync(StreamReader reader, CancellationToken token)
    {
        var text = new StringBuilder();
        char[] buffer = new char[2048];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
        {
            // Continue draining output even after the in-memory log limit.
            int retain = Math.Min(count, 16384 - text.Length);
            if (retain > 0) text.Append(buffer, 0, retain);
        }
        return text.ToString();
    }
}
