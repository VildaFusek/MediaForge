using MediaForge.Models;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MediaForge.Services;

public sealed class FfmpegService
{
    public static FfmpegService Instance { get; } = new();
    private const string Source = "https://www.gyan.dev/ffmpeg/builds/";
    private const string Package = "ffmpeg-release-essentials.zip";
    private const long MaxArchiveBytes = 512L * 1024 * 1024;
    private const long MaxExpandedBytes = 2L * 1024 * 1024 * 1024;
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };
    private readonly SemaphoreSlim _gate = new(1, 1);
    public string RuntimeDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "Tools", "FFmpeg");
    public FfmpegInstallation? Current { get; private set; }

    private sealed record InstallationManifest(
        string Version, string PackageHash, string Ffmpeg, string Ffprobe,
        string FfmpegHash, string FfprobeHash, string? Ffplay = null, string? FfplayHash = null);

    public async Task<FfmpegRelease> GetLatestAsync(CancellationToken cancellationToken)
    {
        EnsurePlatform();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        string version = (await GetTextAsync(Package + ".ver", timeout.Token)).Trim();
        string hash = (await GetTextAsync(Package + ".sha256", timeout.Token)).Trim();
        if (!Regex.IsMatch(version, @"^\d+\.\d+(?:\.\d+)?$") || !Regex.IsMatch(hash, @"^[a-fA-F0-9]{64}$"))
            throw new InvalidDataException("Zdroj vrátil neplatnou verzi nebo kontrolní součet.");
        return new(version, hash.ToUpperInvariant());
    }

    public async Task<FfmpegInstallation?> LoadInstalledAsync(
        IProgress<FfmpegSetupProgress> progress, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(async () =>
            {
                EnsurePlatform();
                Current = null;
                string pointer = Path.Combine(RuntimeDirectory, "current.txt");
                if (!File.Exists(pointer)) return null;
                progress.Report(new("Místní instalace", "Ověřuji soubory nástrojů pomocí SHA-256."));
                Current = await ReadInstallationAsync(pointer, cancellationToken);
                return Current;
            }, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    private async Task<FfmpegInstallation> ReadInstallationAsync(string pointer, CancellationToken token)
    {
        EnsureNoReparsePoints(RuntimeDirectory);
        EnsureNoReparsePoints(pointer);
        string id = (await File.ReadAllTextAsync(pointer, token)).Trim();
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Neplatný ukazatel instalace FFmpeg.");
        string directory = Path.Combine(RuntimeDirectory, "versions", id);
        EnsureNoReparsePoints(directory);
        string manifestPath = Path.Combine(directory, "installation.json");
        EnsureNoReparsePoints(manifestPath);
        var manifest = JsonSerializer.Deserialize<InstallationManifest>(await File.ReadAllTextAsync(manifestPath, token))
            ?? throw new InvalidDataException("Chybí informace o instalaci.");
        string ffmpeg = SafePath(directory, manifest.Ffmpeg);
        string ffprobe = SafePath(directory, manifest.Ffprobe);
        foreach (string file in new[] { ffmpeg, ffprobe }) EnsureNoReparsePoints(file);
        if (!string.Equals(await HashAsync(ffmpeg, token), manifest.FfmpegHash, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(await HashAsync(ffprobe, token), manifest.FfprobeHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Soubory místní instalace neodpovídají uloženým SHA-256. Je nutná nová instalace.");
        string? ffplay = null;
        if (manifest.Ffplay is not null && manifest.FfplayHash is not null)
        {
            ffplay = SafePath(directory, manifest.Ffplay);
            EnsureNoReparsePoints(ffplay);
            if (!string.Equals(await HashAsync(ffplay, token), manifest.FfplayHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("SHA-256 místního FFplay nesouhlasí. Je nutná nová instalace.");
        }
        return new(manifest.Version, ffmpeg, ffprobe, manifest.PackageHash, ffplay);
    }

    public async Task<FfmpegInstallation> InstallAsync(
        FfmpegRelease release, IProgress<FfmpegSetupProgress> progress, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() => InstallCoreAsync(release, progress, cancellationToken), cancellationToken);
        }
        finally { _gate.Release(); }
    }

    private async Task<FfmpegInstallation> InstallCoreAsync(
        FfmpegRelease release, IProgress<FfmpegSetupProgress> progress, CancellationToken cancellationToken)
    {
        EnsurePlatform();
        EnsureNoReparsePoints(RuntimeDirectory);
        Directory.CreateDirectory(RuntimeDirectory);
        EnsureNoReparsePoints(RuntimeDirectory);
        // A second MediaForge process must not activate a competing installation.
        string lockPath = Path.Combine(RuntimeDirectory, "setup.lock");
        EnsureNoReparsePoints(lockPath);
        using var installationLock = new FileStream(lockPath,
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        string id = Guid.NewGuid().ToString("N");
        string work = Path.Combine(RuntimeDirectory, "staging-" + id);
        string versions = Path.Combine(RuntimeDirectory, "versions");
        EnsureNoReparsePoints(versions);
        Directory.CreateDirectory(versions);
        string logs = Path.Combine(RuntimeDirectory, "logs");
        EnsureNoReparsePoints(logs);
        Directory.CreateDirectory(logs);
        string logPath = Path.Combine(logs, $"setup-{DateTime.Now:yyyyMMdd-HHmmss}-{id}.log");
        var log = new StreamWriter(new FileStream(logPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        { AutoFlush = true };
        bool logFailed = false;
        void Report(FfmpegSetupProgress update)
        {
            if (update.WriteLog && !logFailed)
            {
                try { log.WriteLine($"{DateTime.Now:HH:mm:ss} [{update.Stage}] {update.Detail}"); }
                catch (Exception ex)
                {
                    logFailed = true;
                    progress.Report(new("Protokol", $"Zápis úplného logu selhal: {ex.Message}"));
                }
            }
            progress.Report(update);
        }
        string? publishedDirectory = null;
        string pointerTemp = Path.Combine(RuntimeDirectory, "current-" + id + ".tmp");
        bool activated = false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));
        var token = timeout.Token;
        try
        {
            Directory.CreateDirectory(work);
            Report(new("Příprava", $"Stabilní FFmpeg {release.Version}, Windows x64, Gyan.dev Essentials, GPLv3."));
            Report(new("Protokol", logPath));
            Report(new("Kontrola verze", "Znovu ověřuji nabídnutou verzi a SHA-256."));
            var latest = await GetLatestAsync(token);
            if (latest != release) throw new IOException("Balíček se od kontroly změnil. Zkontrolujte verzi znovu.");
            string archive = Path.Combine(work, "package.zip");
            await DownloadAsync(archive, Report, token);
            Report(new("Ověření", $"Počítám SHA-256; očekáváno {release.Sha256}."));
            if (!string.Equals(await HashAsync(archive, token), release.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("SHA-256 staženého balíčku nesouhlasí. Balíček nebude použit.");
            Report(new("Ověření", "SHA-256 balíčku souhlasí."));
            string expanded = Path.Combine(work, "expanded");
            Directory.CreateDirectory(expanded);
            await ExtractAsync(archive, expanded, Report, token);
            string[] ffmpegs = Directory.GetFiles(expanded, "ffmpeg.exe", SearchOption.AllDirectories);
            string[] ffprobes = Directory.GetFiles(expanded, "ffprobe.exe", SearchOption.AllDirectories);
            string[] ffplays = Directory.GetFiles(expanded, "ffplay.exe", SearchOption.AllDirectories);
            if (ffmpegs.Length != 1 || ffprobes.Length != 1 || ffplays.Length != 1
                || Path.GetDirectoryName(ffmpegs[0]) != Path.GetDirectoryName(ffprobes[0])
                || Path.GetDirectoryName(ffmpegs[0]) != Path.GetDirectoryName(ffplays[0]))
                throw new InvalidDataException("Balíček neobsahuje jednoznačnou trojici FFmpeg, FFprobe a FFplay.");
            var manifest = new InstallationManifest(release.Version, release.Sha256,
                Path.GetRelativePath(expanded, ffmpegs[0]), Path.GetRelativePath(expanded, ffprobes[0]),
                await HashAsync(ffmpegs[0], token), await HashAsync(ffprobes[0], token),
                Path.GetRelativePath(expanded, ffplays[0]), await HashAsync(ffplays[0], token));
            await File.WriteAllTextAsync(Path.Combine(expanded, "installation.json"), JsonSerializer.Serialize(manifest), token);
            token.ThrowIfCancellationRequested();
            Report(new("Příprava instalace", "Přesouvám ověřený obsah balíčku do nové instalační složky."));
            publishedDirectory = Path.Combine(versions, id);
            // Move before launching tools: Windows may temporarily lock their directory after execution.
            Directory.Move(expanded, publishedDirectory);
            var installation = new FfmpegInstallation(release.Version,
                SafePath(publishedDirectory, manifest.Ffmpeg), SafePath(publishedDirectory, manifest.Ffprobe), release.Sha256,
                SafePath(publishedDirectory, manifest.Ffplay!));
            Report(new("Kontrola nástrojů", "Ověřuji spuštění a verzi FFmpeg, FFprobe a FFplay (-version)."));
            await CheckToolAsync(installation.FfmpegPath, "ffmpeg", release.Version, Report, token);
            await CheckToolAsync(installation.FfprobePath, "ffprobe", release.Version, Report, token);
            await CheckToolAsync(installation.FfplayPath!, "ffplay", release.Version, Report, token);
            Report(new("Aktivace", "Přepínám na novou instalaci; předchozí soubory zůstávají zachované."));
            await File.WriteAllTextAsync(pointerTemp, id, token);
            token.ThrowIfCancellationRequested();
            string pointer = Path.Combine(RuntimeDirectory, "current.txt");
            if (File.Exists(pointer))
            {
                EnsureNoReparsePoints(pointer);
                File.Replace(pointerTemp, pointer, Path.Combine(RuntimeDirectory, "previous-" + id + ".txt"));
            }
            else File.Move(pointerTemp, pointer);
            activated = true;
            Current = installation;
            Report(new("Dokončeno", $"FFmpeg, FFprobe a FFplay {release.Version} jsou připraveny.", 100, false));
            return installation;
        }
        catch (Exception ex)
        {
            Report(new(activated ? "Upozornění" : "Nedokončeno", ex.Message));
            // Once the pointer has been switched, cancellation/cleanup must not claim a rollback.
            if (activated && Current is not null) return Current;
            throw;
        }
        finally
        {
            Cleanup(work, Report);
            if (!activated && publishedDirectory is not null) Cleanup(publishedDirectory, Report);
            try { if (File.Exists(pointerTemp)) File.Delete(pointerTemp); }
            catch (Exception ex) { Report(new("Úklid", $"Nelze odstranit {pointerTemp}: {ex.Message}")); }
            try { log.Dispose(); }
            catch (Exception ex) { progress.Report(new("Protokol", $"Uzavření úplného logu selhalo: {ex.Message}")); }
        }
    }

    private static async Task DownloadAsync(string destination, Action<FfmpegSetupProgress> report, CancellationToken token)
    {
        report(new("Stahování", Source + Package));
        using var response = await GetAsync(new Uri(Source + Package), token);
        long? total = response.Content.Headers.ContentLength;
        if (total is > MaxArchiveBytes) throw new InvalidDataException("Archiv překračuje povolenou velikost 512 MiB.");
        await using var input = await response.Content.ReadAsStreamAsync(token);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true);
        byte[] buffer = new byte[131072];
        long received = 0;
        var watch = Stopwatch.StartNew();
        long lastReport = -500;
        int count;
        while ((count = await input.ReadAsync(buffer, token)) > 0)
        {
            received += count;
            if (received > MaxArchiveBytes) throw new InvalidDataException("Archiv překračuje povolenou velikost.");
            await output.WriteAsync(buffer.AsMemory(0, count), token);
            if (watch.ElapsedMilliseconds - lastReport < 150) continue;
            lastReport = watch.ElapsedMilliseconds;
            double speed = received / Math.Max(watch.Elapsed.TotalSeconds, 0.001);
            string detail = $"{received / 1048576d:F1} / {(total.HasValue ? $"{total.Value / 1048576d:F1}" : "?")} MiB"
                + $" • {speed / 1048576d:F1} MiB/s"
                + (total is > 0 ? $" • zbývá přibližně {Math.Max(0, (total.Value - received) / speed):F0} s" : "");
            report(new("Stahování", detail, total is > 0 ? received * 100d / total.Value : 0, total is not > 0, false));
        }
        if (total.HasValue && total.Value != received) throw new IOException("Stažený archiv je neúplný.");
        report(new("Stahování", $"Staženo {received / 1048576d:F1} MiB za {watch.Elapsed.TotalSeconds:F1} s.", 100, false));
    }

    private static async Task ExtractAsync(
        string archive, string destination, Action<FfmpegSetupProgress> report, CancellationToken token)
    {
        using var zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count is 0 or > 10000) throw new InvalidDataException("Neplatný počet položek archivu.");
        long total = 0;
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            string target = SafePath(destination, entry.FullName.TrimEnd('/', '\\'));
            int unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixType is not (0 or 0x8000 or 0x4000)
                || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0
                || !targets.Add(target))
                throw new InvalidDataException("Archiv obsahuje odkaz, speciální nebo duplicitní položku.");
            total = checked(total + entry.Length);
            if (total > MaxExpandedBytes) throw new InvalidDataException("Rozbalená data překračují limit 2 GiB.");
        }
        long extracted = 0;
        byte[] buffer = new byte[131072];
        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            string target = SafePath(destination, entry.FullName.TrimEnd('/', '\\'));
            report(new("Rozbalování", entry.FullName, total > 0 ? extracted * 100d / total : 0, false));
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                if (entry.Length != 0) throw new InvalidDataException("Neplatná složka v archivu.");
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = entry.Open();
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true);
            long written = 0;
            long lastReport = Environment.TickCount64;
            int count;
            while ((count = await input.ReadAsync(buffer, token)) > 0)
            {
                written += count;
                extracted += count;
                if (written > entry.Length || extracted > MaxExpandedBytes)
                    throw new InvalidDataException("Rozbalená data překračují deklarovanou velikost.");
                await output.WriteAsync(buffer.AsMemory(0, count), token);
                if (Environment.TickCount64 - lastReport < 150) continue;
                lastReport = Environment.TickCount64;
                report(new("Rozbalování", $"{entry.FullName} • {extracted / 1048576d:F1} / {total / 1048576d:F1} MiB",
                    total > 0 ? extracted * 100d / total : 0, false, false));
            }
            if (written != entry.Length) throw new InvalidDataException("Neúplná položka archivu.");
        }
        report(new("Rozbalování", "Archiv byl rozbalen včetně přiložených licencí a dokumentace.", 100, false));
    }

    private static async Task CheckToolAsync(
        string path, string tool, string version, Action<FfmpegSetupProgress> report, CancellationToken token)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(path)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(path)!
            }
        };
        process.StartInfo.ArgumentList.Add("-version");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        if (!process.Start()) throw new IOException($"Nelze spustit {tool}.");
        try
        {
            Task<string> stdout = ReadLimitedAsync(process.StandardOutput, timeout.Token);
            Task<string> stderr = ReadLimitedAsync(process.StandardError, timeout.Token);
            await Task.WhenAll(process.WaitForExitAsync(timeout.Token), stdout, stderr);
            string firstLine = ((await stdout) + "\n" + (await stderr)).Split('\n')
                .Select(line => line.Trim()).FirstOrDefault(line => line.StartsWith(tool + " version ", StringComparison.Ordinal))
                ?? "";
            if (process.ExitCode != 0 || !Regex.IsMatch(firstLine, $"^{tool} version {Regex.Escape(version)}(?:[-\\s]|$)"))
                throw new InvalidDataException($"Nesouhlasí verze {tool}: {firstLine}. {(await stderr).Trim()}");
            report(new("Kontrola nástrojů", firstLine));
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }

    private static async Task<string> ReadLimitedAsync(StreamReader reader, CancellationToken token)
    {
        char[] buffer = new char[4096];
        var text = new System.Text.StringBuilder();
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
        {
            if (text.Length + count > 65536) throw new InvalidDataException("Výstup nástroje je příliš dlouhý.");
            text.Append(buffer, 0, count);
        }
        return text.ToString();
    }

    private static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
    }

    private static string SafePath(string root, string relative)
    {
        string normalized = relative.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized) || Path.IsPathRooted(normalized)
            || normalized.Split('/').Any(part => part.Length == 0 || part is "." or ".."
                || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || part.EndsWith('.') || part.EndsWith(' ')
                || Regex.IsMatch(part, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase)))
            throw new InvalidDataException("Neplatná cesta v balíčku.");
        string result = Path.GetFullPath(Path.Combine(root, normalized));
        if (!result.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Cesta směřuje mimo instalační složku.");
        return result;
    }

    private static void EnsureNoReparsePoints(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Instalační cesta nesmí obsahovat symbolické odkazy nebo junction: {current}");
    }

    private static void Cleanup(string directory, Action<FfmpegSetupProgress> report)
    {
        try
        {
            if (!Directory.Exists(directory)) return;
            EnsureNoReparsePoints(directory);
            // Only our unique staging/unactivated version is removed, never an active installation.
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories))
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Pracovní složka obsahuje odkaz; automatický úklid byl zastaven.");
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) { report(new("Úklid", $"Pracovní data zůstala v {directory}: {ex.Message}")); }
    }

    private static void EnsurePlatform()
    {
        if (RuntimeInformation.OSArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("Tento balíček FFmpeg je určen pouze pro Windows x64.");
    }

    private static async Task<string> GetTextAsync(string name, CancellationToken token)
    {
        using var response = await GetAsync(new Uri(Source + name + "?t=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), token);
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var reader = new StreamReader(stream);
        char[] buffer = new char[1025];
        int count = await reader.ReadBlockAsync(buffer.AsMemory(), token);
        if (count > 1024) throw new InvalidDataException("Příliš dlouhá odpověď zdroje.");
        return new string(buffer, 0, count);
    }

    private static async Task<HttpResponseMessage> GetAsync(Uri uri, CancellationToken token)
    {
        for (int redirects = 0; redirects < 8; redirects++)
        {
            if (uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo)
                || !uri.IsDefaultPort || uri.Host is not ("www.gyan.dev" or "gyan.dev" or "github.com"
                    or "release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
                throw new IOException("Zdroj přesměroval požadavek mimo povolené HTTPS servery.");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("MediaForge/1.0");
            var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found
                or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                Uri? location = response.Headers.Location;
                response.Dispose();
                if (location is null) throw new IOException("Přesměrování neobsahuje adresu.");
                uri = new Uri(uri, location);
                continue;
            }
            try { response.EnsureSuccessStatusCode(); return response; }
            catch { response.Dispose(); throw; }
        }
        throw new IOException("Příliš mnoho přesměrování při stahování.");
    }
}
