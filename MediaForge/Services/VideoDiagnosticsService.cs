using MediaForge.Models;
using MediaForge.Helpers;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MediaForge.Services;

public sealed class VideoDiagnosticsService
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".flv", ".m4v", ".ts", ".mpg",
        ".mpeg", ".mts", ".m2ts", ".m2v", ".3gp", ".3g2", ".mxf", ".vob", ".ogv", ".asf", ".divx"
    };
    private readonly VideoDiagnosticsRepository _repository = new();
    public static bool IsVideoFile(string path) => !ConversionTemporaryFile.IsInternalPath(path)
        && VideoExtensions.Contains(Path.GetExtension(path));

    public Task<VideoDiagnosticSummary> DiagnoseAsync(
        IReadOnlyList<string> inputs, bool recursive, FfmpegInstallation installation,
        IProgress<VideoDiagnosticProgress> progress, CancellationToken token) =>
        Task.Run(async () =>
        {
            int saved = 0, failed = 0, skipped = 0, completed = 0;
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Report(string stage, string detail, bool discovering = false,
                VideoDiagnosticRecord? record = null, bool writeLog = true)
            {
                if (stage == "Chyba") AppLog.Information($"Analýza videa: {detail}");
                progress.Report(new(stage, detail, completed, files.Count, saved, failed, skipped, discovering, record, writeLog));
            }
            Report("Vyhledávání", "Načítám vstupy. Ve složkách hledám běžné video přípony; odkazy a junction se nepoužívají.", true);
            var pending = new Stack<string>(inputs.Reverse());
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long lastReport = Environment.TickCount64;
            while (pending.TryPop(out string? input))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    string path = Path.GetFullPath(input);
                    if (ConversionTemporaryFile.IsInternalPath(path)) continue;
                    EnsureLocalPath(path);
                    var attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if (!visited.Add(path)) continue;
                        // Enumerate one level at a time so links are rejected before descent.
                        foreach (string entry in Directory.EnumerateFileSystemEntries(path))
                        {
                            token.ThrowIfCancellationRequested();
                            try
                            {
                                var entryAttributes = File.GetAttributes(entry);
                                if ((entryAttributes & FileAttributes.ReparsePoint) != 0)
                                {
                                    skipped++;
                                    Report("Přeskočeno", $"Odkaz / junction: {entry}", true);
                                }
                                else if ((entryAttributes & FileAttributes.Directory) != 0)
                                {
                                    if (recursive) pending.Push(entry);
                                }
                                else if (IsVideoFile(entry)) files.Add(entry);
                            }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                            {
                                skipped++;
                                Report("Přeskočeno", $"{entry}: {ex.Message}", true);
                            }
                            if (files.Count + pending.Count > 100000)
                                throw new InvalidOperationException("Dávka překročila limit 100 000 položek. Vyberte menší rozsah.");
                            if (Environment.TickCount64 - lastReport >= 150)
                            {
                                lastReport = Environment.TickCount64;
                                Report("Vyhledávání", $"Nalezeno {files.Count} videí • {path}", true, writeLog: false);
                            }
                        }
                    }
                    else files.Add(path); // Explicitly selected files are probed regardless of extension.
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    skipped++;
                    Report("Přeskočeno", $"{input}: {ex.Message}", true);
                }
            }
            Report("Diagnostika", $"Nalezeno {files.Count} unikátních souborů. Zpracování probíhá postupně.");
            foreach (string path in files)
            {
                token.ThrowIfCancellationRequested();
                using var catalogLock = _repository.AcquireMutationLock();
                Report("FFprobe", path);
                VideoDiagnosticRecord record;
                try
                {
                    EnsureLocalPath(path);
                    if (_repository.TryReadCurrent(path, token) is { } cached)
                    {
                        saved++;
                        completed++;
                        Report("Z databáze", path, record: cached);
                        continue;
                    }
                    record = await ProbeAsync(path, installation, detail =>
                        Report("FFprobe", detail, writeLog: false), token);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    failed++;
                    completed++;
                    Report("Chyba", $"{path}: FFprobe překročil limit 120 sekund.");
                    continue;
                }
                catch (Microsoft.Data.Sqlite.SqliteException) { throw; }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed++;
                    completed++;
                    Report("Chyba", $"{path}: {ex.Message}");
                    continue;
                }
                token.ThrowIfCancellationRequested();
                // Database failures stop the batch. A file is successful only after its transaction commits.
                _repository.Save(record, token);
                saved++;
                completed++;
                Report("Uloženo do databáze", $"{path} • {record.Summary}", record: record with { RawJson = "" });
            }
            return new VideoDiagnosticSummary(completed, files.Count, saved, failed, skipped);
        }, token);

    internal static async Task<VideoDiagnosticRecord> ProbeAsync(
        string path, FfmpegInstallation installation, Action<string> heartbeat, CancellationToken token,
        int? frameStreamIndex = null)
    {
        // Windows sharing denies writes/deletion while FFprobe reads the file.
        using var guard = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var before = VideoFileIdentity.Read(guard);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(installation.FfprobePath)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(installation.FfprobePath)!
            }
        };
        foreach (string argument in new[]
        {
            "-v", "error", "-protocol_whitelist", "file", "-probesize", "50000000",
            "-analyzeduration", "20000000", "-show_format", "-show_streams", "-show_error", "-of", "json", "-i", path
        }) process.StartInfo.ArgumentList.Add(argument);
        if (frameStreamIndex is { } streamIndex)
            foreach (string argument in new[] { "-select_streams", streamIndex.ToString(CultureInfo.InvariantCulture),
                "-read_intervals", "%+#64", "-show_frames" })
                process.StartInfo.ArgumentList.Add(argument);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(120));
        using var lifetime = new ConversionProcessJob();
        if (!process.Start()) throw new IOException("FFprobe se nepodařilo spustit.");
        try { lifetime.Attach(process); }
        catch
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
                await process.WaitForExitAsync(CancellationToken.None);
            }
            throw;
        }
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        { Trace.TraceWarning($"Nelze snížit prioritu FFprobe: {ex.Message}"); }
        var watch = Stopwatch.StartNew();
        Task<string> stdout = ReadLimitedAsync(process.StandardOutput, 4 * 1024 * 1024, timeout);
        Task<string> stderr = ReadLimitedAsync(process.StandardError, 65536, timeout);
        Task all = Task.WhenAll(process.WaitForExitAsync(timeout.Token), stdout, stderr);
        try
        {
            while (!all.IsCompleted)
            {
                await Task.WhenAny(all, Task.Delay(500, timeout.Token));
                if (stdout.IsFaulted) await stdout;
                if (stderr.IsFaulted) await stderr;
                if (all.IsCompleted) break;
                timeout.Token.ThrowIfCancellationRequested();
                if (!all.IsCompleted) heartbeat($"{path} • načítání údajů {watch.Elapsed.TotalSeconds:F0} s");
            }
            await all;
            if (process.ExitCode != 0)
                throw new InvalidDataException($"FFprobe: {(await stderr).Trim()} (kód {process.ExitCode}).");
            var after = VideoFileIdentity.Read(guard);
            if (after != before)
                throw new IOException("Soubor se během diagnostiky změnil; údaje nebyly uloženy.");
            return Parse(await stdout, path, before.Size, before.ModifiedUtcTicks, installation.Version)
                with { FileIdentity = before.Identity };
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
            try { await all; }
            catch { /* Observe canceled readers after terminating the child process. */ }
        }
    }

    private static VideoDiagnosticRecord Parse(string json, string path, long size, long modified, string probeVersion)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("error", out var error))
            throw new InvalidDataException("FFprobe oznámil chybu: " + Text(error, "string"));
        if (!root.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("FFprobe nevrátil žádné stopy.");
        if (streams.GetArrayLength() > 256) throw new InvalidDataException("Soubor obsahuje více než 256 stop.");
        var tracks = streams.EnumerateArray().Select(stream =>
        {
            bool attached = stream.TryGetProperty("disposition", out var disposition)
                && Number(disposition, "attached_pic") == 1;
            string language = stream.TryGetProperty("tags", out var tags) ? Text(tags, "language") : "";
            return new VideoStreamInfo
            {
                Index = Integer(stream, "index") ?? 0,
                Type = Text(stream, "codec_type"), Codec = Text(stream, "codec_name"),
                Profile = Text(stream, "profile"), Language = language,
                Width = Integer(stream, "width"), Height = Integer(stream, "height"),
                Title = stream.TryGetProperty("tags", out var trackTags) ? Text(trackTags, "title") : "",
                IsDefault = stream.TryGetProperty("disposition", out var trackDisposition)
                    && Number(trackDisposition, "default") == 1,
                FrameRate = Rational(Text(stream, "avg_frame_rate")) ?? Rational(Text(stream, "r_frame_rate")),
                PixelFormat = Text(stream, "pix_fmt"),
                BitDepth = Integer(stream, "bits_per_raw_sample") is > 0 ? Integer(stream, "bits_per_raw_sample") : null,
                BitRate = Long(stream, "bit_rate"), Channels = Integer(stream, "channels"),
                SampleRate = Integer(stream, "sample_rate"), IsAttachedPicture = attached
            };
        }).ToList();
        var video = tracks.FirstOrDefault(s => s.Type == "video" && !s.IsAttachedPicture)
            ?? throw new InvalidDataException("Soubor nemá video stopu (obalový obrázek se nepočítá).");
        root.TryGetProperty("format", out var format);
        return new()
        {
            FilePath = path, FileSize = size, ModifiedUtcTicks = modified, AnalyzedUtc = DateTime.UtcNow,
            ProbeVersion = probeVersion, Container = Text(format, "format_name"),
            Title = format.ValueKind == JsonValueKind.Object && format.TryGetProperty("tags", out var formatTags)
                ? Text(formatTags, "title") : "",
            DurationSeconds = Number(format, "duration"), BitRate = Long(format, "bit_rate"),
            VideoCodec = video.Codec, Width = video.Width, Height = video.Height,
            FrameRate = video.FrameRate, PixelFormat = video.PixelFormat, BitDepth = video.BitDepth,
            Streams = tracks, RawJson = json
        };
    }

    private static string Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString() : "";
    private static double? Number(JsonElement element, string property) =>
        double.TryParse(Text(element, property), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            && double.IsFinite(value) && value >= 0 ? value : null;
    private static long? Long(JsonElement element, string property) =>
        long.TryParse(Text(element, property), NumberStyles.Integer, CultureInfo.InvariantCulture, out long value)
            && value >= 0 ? value : null;
    private static int? Integer(JsonElement element, string property) =>
        int.TryParse(Text(element, property), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            && value >= 0 ? value : null;
    private static double? Rational(string text)
    {
        string[] parts = text.Split('/');
        if (parts.Length != 2 || !double.TryParse(parts[0], CultureInfo.InvariantCulture, out double numerator)
            || !double.TryParse(parts[1], CultureInfo.InvariantCulture, out double denominator) || denominator <= 0) return null;
        double value = numerator / denominator;
        return double.IsFinite(value) && value > 0 ? value : null;
    }

    private static void EnsureLocalPath(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Symbolické odkazy a junction se při diagnostice nepoužívají.");
    }

    private static async Task<string> ReadLimitedAsync(StreamReader reader, int limit, CancellationTokenSource cancellation)
    {
        var text = new StringBuilder();
        char[] buffer = new char[4096];
        try
        {
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellation.Token)) > 0)
            {
                if (text.Length + count > limit) throw new InvalidDataException("Výstup FFprobe překročil povolenou velikost.");
                text.Append(buffer, 0, count);
            }
            return text.ToString();
        }
        catch { cancellation.Cancel(); throw; }
    }
}
