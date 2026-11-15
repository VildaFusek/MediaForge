using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MediaForge.Services;

internal sealed record HardwareRoute(string Encoder, string Decoder);
internal sealed record HardwareMeasurement(HardwareRoute Route, double Seconds, double CpuSeconds, int Frames);
internal sealed record HardwareLearningEntry(string Key, DateTime MeasuredUtc, HardwareMeasurement[] Measurements,
    HardwareRoute Recommended);

// Empirical selection, not an image-quality or energy-consumption model.
internal static class HardwareLearningService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MediaForge");
    private static readonly string CachePath = Path.Combine(DirectoryPath, "hardware-learning.json");

    internal static async Task<HardwareRoute> SelectAsync(string context, HardwareRoute[] routes,
        HardwareRoute fallback, Func<HardwareRoute, CancellationToken, Task<HardwareMeasurement?>> measure,
        Action<string> report, Action<string> log, CancellationToken token)
    {
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(context)));
        await Gate.WaitAsync(token);
        try
        {
            var entries = await LoadAsync(log, token);
            var cached = entries.FirstOrDefault(e => e.Key == key && routes.Contains(e.Recommended)
                && e.MeasuredUtc <= DateTime.UtcNow && DateTime.UtcNow - e.MeasuredUtc < TimeSpan.FromDays(7));
            if (cached is not null)
            {
                report($"Doporučení z uloženého měření: {Describe(cached.Recommended)}. "
                    + "Podobný vstup a stejné nastavení; platnost nejvýše 7 dní.");
                return cached.Recommended;
            }

            var results = new List<HardwareMeasurement>();
            foreach (var route in routes.Distinct())
            {
                token.ThrowIfCancellationRequested();
                log($"Porovnání hardwaru: {Describe(route)}.");
                var result = await measure(route, token);
                if (result is not null && result.Frames == 48 && result.Seconds > 0
                    && double.IsFinite(result.Seconds) && double.IsFinite(result.CpuSeconds))
                {
                    results.Add(result);
                    log($"Vzorek: {result.Frames} snímků • {result.Seconds:F2} s • CPU čas {result.CpuSeconds:F2} s.");
                }
            }
            token.ThrowIfCancellationRequested();
            if (results.Count == 0)
            {
                report($"Vzorek neposkytl dost dat pro doporučení; ponechávám {Describe(fallback)}. "
                    + "Nedostatečné měření se neukládá.");
                return fallback;
            }

            // Compare only samples that actually produced the same number of frames.
            int frames = results.Max(r => r.Frames);
            var comparable = results.Where(r => r.Frames == frames).ToArray();
            var cpu = comparable.Where(r => r.Route.Encoder.StartsWith("lib", StringComparison.Ordinal)
                && r.Route.Decoder == "cpu").MinBy(r => r.Seconds);
            var fastest = comparable.MinBy(r => r.Seconds)!;
            var gpu = comparable.Where(r => !r.Route.Encoder.StartsWith("lib", StringComparison.Ordinal)
                    || r.Route.Decoder != "cpu")
                .Where(r => cpu is null || r.Seconds <= cpu.Seconds * 0.85
                    || r.Seconds <= cpu.Seconds * 1.10 && r.CpuSeconds <= cpu.CpuSeconds * 0.75)
                .MinBy(r => r.Seconds);
            var best = gpu ?? cpu ?? fastest;
            string comparison = cpu is not null && gpu is not null
                ? $"CPU vzorek {cpu.Seconds:F2} s / CPU čas {cpu.CpuSeconds:F2} s; "
                    + $"GPU cesta {gpu.Seconds:F2} s / CPU čas {gpu.CpuSeconds:F2} s."
                : cpu is not null ? "GPU v tomto vzorku nepřinesla prokázané zlepšení nebo nebyla dostupná."
                : "CPU referenční měření není dostupné; volím nejrychlejší úspěšnou cestu bez tvrzení o výhodě oproti CPU.";
            report($"Doporučeno: {Describe(best.Route)}. {comparison}");
            entries.RemoveAll(e => e.Key == key);
            entries.Add(new(key, DateTime.UtcNow, comparable, best.Route));
            await SaveAsync(entries.OrderByDescending(e => e.MeasuredUtc).Take(100).ToList(), log, token);
            return best.Route;
        }
        finally { Gate.Release(); }
    }

    internal static string Describe(HardwareRoute route) => $"enkodér {route.Encoder}, dekódování {route.Decoder}";

    internal static async Task ForgetAsync(string context, Action<string> log)
    {
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(context)));
        await Gate.WaitAsync();
        try
        {
            var entries = await LoadAsync(log, CancellationToken.None);
            if (entries.RemoveAll(e => e.Key == key) > 0)
                await SaveAsync(entries, log, CancellationToken.None);
        }
        finally { Gate.Release(); }
    }

    private static async Task<List<HardwareLearningEntry>> LoadAsync(Action<string> log, CancellationToken token)
    {
        try
        {
            VideoConversionService.EnsureSafePath(CachePath);
            if (!File.Exists(CachePath)) return [];
            if (new FileInfo(CachePath).Length > 512 * 1024) throw new IOException("Soubor měření je příliš velký.");
            var entries = JsonSerializer.Deserialize<List<HardwareLearningEntry>>(
                await File.ReadAllTextAsync(CachePath, token));
            return entries?.Where(e => e is not null && e.Recommended is not null).Take(100).ToList() ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            log("Uložené měření nelze načíst; proběhne nové porovnání: " + ex.Message);
            return [];
        }
    }

    private static async Task SaveAsync(List<HardwareLearningEntry> entries, Action<string> log, CancellationToken token)
    {
        string temporary = Path.Combine(DirectoryPath, $"hardware-learning-{Guid.NewGuid():N}.tmp");
        try
        {
            VideoConversionService.EnsureSafePath(DirectoryPath);
            Directory.CreateDirectory(DirectoryPath);
            VideoConversionService.EnsureSafePath(DirectoryPath);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous))
                await JsonSerializer.SerializeAsync(stream, entries, cancellationToken: token);
            token.ThrowIfCancellationRequested();
            VideoConversionService.EnsureSafePath(CachePath);
            // Only the application's own disposable measurements are replaced.
            File.Move(temporary, CachePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        { log("Doporučení platí pro tento převod, ale měření nelze uložit: " + ex.Message); }
        finally
        {
            try
            {
                VideoConversionService.EnsureSafePath(temporary);
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { log("Nelze uklidit dočasný soubor měření: " + ex.Message); }
        }
    }
}
