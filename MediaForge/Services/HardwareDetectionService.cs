using MediaForge.Models;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text;

namespace MediaForge.Services;

public sealed class HardwareDetectionService
{
    public Task<HardwareCapabilities> DetectAsync(FfmpegInstallation installation, CancellationToken token) =>
        Task.Run(async () =>
        {
            var log = new StringBuilder();
            string cpu = "Nezjištěno";
            var gpus = new HashSet<string>();
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                cpu = key?.GetValue("ProcessorNameString")?.ToString()?.Trim() ?? cpu;
                using var video = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
                foreach (string name in video?.GetSubKeyNames() ?? [])
                {
                    using var adapter = video!.OpenSubKey(name);
                    if (adapter?.GetValue("DriverDesc") is string description)
                        gpus.Add($"{description} • ovladač {adapter.GetValue("DriverVersion")} ({adapter.GetValue("DriverDate")})");
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            { log.AppendLine("Inventář: " + ex.Message); }
            var memory = MachinePerformanceService.ReadMemory();
            var encoders = new List<EncoderOption>();
            EncoderOption[] candidates =
            [
                new("CPU – H.264", "libx264", "h264"),
                new("CPU – HEVC", "libx265", "hevc"),
                new("NVIDIA NVENC – H.264", "h264_nvenc", "h264"),
                new("NVIDIA NVENC – HEVC", "hevc_nvenc", "hevc"),
                new("Intel Quick Sync – H.264", "h264_qsv", "h264"),
                new("Intel Quick Sync – HEVC", "hevc_qsv", "hevc"),
                new("AMD AMF – H.264", "h264_amf", "h264"),
                new("AMD AMF – HEVC", "hevc_amf", "hevc")
            ];
            foreach (var option in candidates)
            {
                token.ThrowIfCancellationRequested();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(12));
                var tail = new StringBuilder();
                using var process = ConversionProcess.Create(installation.FfmpegPath,
                (string[])[
                    "-hide_banner", "-nostdin", "-v", "error", "-f", "lavfi", "-i",
                    "color=c=black:s=320x240:r=25", "-frames:v", "3", "-an", "-vf", "format=yuv420p",
                    "-c:v", option.Encoder, "-threads", "2", "-f", "null", "-"
                ]);
                try
                {
                    int code = await ConversionProcess.RunAsync(process, _ => { }, line =>
                    {
                        if (tail.Length < 4096) tail.AppendLine(line);
                    }, timeout.Token);
                    if (code == 0) encoders.Add(option);
                    log.AppendLine($"{option.Name}: {(code == 0 ? "krátký test úspěšný" : $"nedostupný ({code})")}");
                    if (code != 0) log.AppendLine(tail.ToString());
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                { log.AppendLine($"{option.Name}: vypršel limit testu."); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { log.AppendLine($"{option.Name}: {ex.Message}"); }
            }
            return new HardwareCapabilities(
                $"CPU: {cpu}\nLogických procesorů: {Environment.ProcessorCount} • {RuntimeInformation.ProcessArchitecture}"
                + $"\nRAM: {memory.TotalGiB:F1} GiB\nGrafické adaptéry (registr): {string.Join(", ", gpus)}"
                + "\nAkcelerace byla zkoušena krátkým kódováním, nikoli jen seznamem enkodérů."
                + "\nTest enkodérů negarantuje podporu každého rozlišení ani GPU dekódování."
                + "\nAutomatické učení porovná cesty na konkrétním vstupu při spuštění převodu; samotný test dostupnosti neurčuje rychlost.",
                encoders, log.ToString());
        }, token);
}
