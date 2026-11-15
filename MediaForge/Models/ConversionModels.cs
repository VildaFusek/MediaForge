using CommunityToolkit.Mvvm.ComponentModel;
using MediaForge.Helpers;
using System.Collections.ObjectModel;

namespace MediaForge.Models;

public sealed record DevicePreset(string Name, int Width, int Height, int Fps, int VideoKbps, int AudioKbps, string Codec)
{
    public override string ToString() => Name;
    public string Description => $"{Codec.ToUpperInvariant()} • nejvýše {Width} × {Height}, {Fps} fps"
        + $" • video {VideoKbps} kb/s • AAC stereo {AudioKbps} kb/s • MP4";

    public static IReadOnlyList<DevicePreset> All { get; } = (DevicePreset[])
    [
        new("Mobil – základní / úsporný", 1280, 720, 30, 2500, 128, "h264"),
        new("Mobil – výkonný", 1920, 1080, 60, 6000, 160, "h264"),
        new("Tablet – základní", 1920, 1080, 30, 4500, 160, "h264"),
        new("Tablet – výkonný HEVC", 2560, 1440, 60, 8000, 192, "hevc"),
        new("TV – starší / Full HD", 1920, 1080, 30, 7000, 192, "h264"),
        new("TV – 4K HEVC", 3840, 2160, 60, 18000, 192, "hevc"),
        new("PC – kompatibilní", 1920, 1080, 60, 8000, 192, "h264"),
        new("PC – výkonný / 4K HEVC", 3840, 2160, 60, 20000, 192, "hevc")
    ];
}

public sealed record EncoderOption(string Name, string Encoder, string Codec)
{
    public override string ToString() => Name;
}

public sealed record DecoderOption(string Name, string HwAccel)
{
    public override string ToString() => Name;
    public static IReadOnlyList<DecoderOption> All { get; } = (DecoderOption[])
    [
        new("Automaticky – podle měření CPU / GPU", "auto"),
        new("CPU – nejvyšší kompatibilita", "cpu"),
        new("NVIDIA – CUDA / NVDEC", "cuda"),
        new("Windows – D3D11VA (Intel / AMD / NVIDIA)", "d3d11va")
    ];
}

public sealed record HdrOption(string Name, bool Preserve)
{
    public override string ToString() => Name;
    public static IReadOnlyList<HdrOption> All { get; } = (HdrOption[])
    [
        new("HDR / Dolby Vision 7, 8.1 → SDR (BT.709)", false),
        new("Zachovat HDR / DV 7, 8.1 → HDR10 (HEVC, CPU)", true)
    ];
}

public sealed record ConversionLoadOption(string Name, string Value)
{
    public override string ToString() => Name;
    public static IReadOnlyList<ConversionLoadOption> All { get; } = (ConversionLoadOption[])
    [
        new("Úsporné – práce na pozadí", "eco"),
        new("Vyvážené – rezerva pro práci", "balanced"),
        new("Maximální – přednost převodu", "maximum")
    ];
}

public sealed record EncodingSpeedOption(string Name, string Value)
{
    public override string ToString() => Name;
    public static IReadOnlyList<EncodingSpeedOption> All { get; } = (EncodingSpeedOption[])
    [
        new("Rychlejší převod", "fast"),
        new("Vyvážená rychlost a kvalita", "balanced"),
        new("Důraz na kvalitu – pomalejší", "quality")
    ];
}

public sealed record ConversionSettings(DevicePreset Profile, string Encoder, string OutputDirectory,
    string AudioLanguage, int? VideoStreamIndex = null, int? AudioStreamIndex = null, bool AudioDisabled = false,
    VideoDiagnosticRecord? ExpectedSource = null, string Decoder = "cpu", bool UseSmartName = false,
    bool PreserveHdr = false, string Workload = "balanced", string EncodingSpeed = "balanced",
    bool AdaptiveHardware = true)
{
    public int CpuThreads => Workload switch
    {
        "eco" => Math.Max(1, Environment.ProcessorCount / 4),
        "maximum" => Environment.ProcessorCount,
        _ => Math.Max(1, Environment.ProcessorCount / 2)
    };
    public string DecoderDescription => Decoder switch
    {
        "auto" => "automaticky podle měření",
        "cuda" => "GPU – NVIDIA CUDA / NVDEC",
        "d3d11va" => "GPU – Windows D3D11VA",
        _ => "CPU"
    };
    public string Description => $"{Profile.Name}: {Profile.Description}\nEnkodér: {Encoder} • dekódování: {DecoderDescription} • filtry CPU"
        + $"\nZatížení: {ConversionLoadOption.All.FirstOrDefault(o => o.Value == Workload)?.Name} • {CpuThreads} CPU vláken na fázi"
        + $"\nEnkódování: {EncodingSpeedOption.All.FirstOrDefault(o => o.Value == EncodingSpeed)?.Name}"
        + $"\nHDR: {(PreserveHdr ? "zachovat HDR10/HLG, HEVC 10 bitů přes CPU; SDR vstup zůstane SDR" : "převést do SDR BT.709 pomocí tone mappingu")}"
        + "\nDolby Vision: pouze profily 7 a 8.1, převod HDR10 základní vrstvy; bez dynamických metadat a rozšiřující vrstvy."
        + $"\nVideo: {(VideoStreamIndex is { } video ? $"stopa #{video}" : "první video")}"
        + $"; zvuk: {(AudioDisabled ? "bez zvuku" : AudioStreamIndex is { } audio ? $"stopa #{audio}" : $"automaticky – {MediaLanguage.Display(AudioLanguage)}")}"
        + "\nPůvodní název s příponou .mp4; při kolizi chytrý název podle metadat, případně číselný dovětek."
        + "\nBez zvětšování rozlišení. Titulky a další stopy se nepřenášejí.";
}

public sealed record ConversionTrackOption(string Label, VideoStreamInfo? Stream)
{
    public override string ToString() => Label;
}

public partial class ConversionInput : ObservableObject
{
    public required string FullPath { get; init; }
    public string Name => Path.GetFileName(FullPath);
    public VideoDiagnosticRecord? Metadata { get; set; }
    public ObservableCollection<ConversionTrackOption> VideoTracks { get; } = [];
    public ObservableCollection<ConversionTrackOption> AudioTracks { get; } = [];
    [ObservableProperty] public partial ConversionTrackOption? SelectedVideo { get; set; }
    [ObservableProperty] public partial ConversionTrackOption? SelectedAudio { get; set; }
    [ObservableProperty] public partial string TrackStatus { get; set; } = "Vyberte soubor pro načtení stop; jinak se zvuk zvolí automaticky.";
    [ObservableProperty] public partial string LoadError { get; set; } = "";
}

public partial class ConversionJob : ObservableObject
{
    public Guid Id { get; } = Guid.NewGuid();
    public required string InputPath { get; init; }
    public ConversionInput? Input { get; init; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Description))]
    [NotifyPropertyChangedFor(nameof(Summary))]
    public partial ConversionSettings Settings { get; set; } = null!;
    public string Name => Path.GetFileName(InputPath);
    public string Description => $"{InputPath}\n{Settings.Description}\nCílová složka: {Settings.OutputDirectory}";
    public string Summary => $"{Settings.Profile.Name} • {(Settings.PreserveHdr ? "zachovat HDR" : "SDR")} • "
        + (Settings.AudioDisabled ? "bez zvuku"
            : Settings.ExpectedSource?.Streams.FirstOrDefault(s => s.Index == Settings.AudioStreamIndex) is { } audio
                ? $"zvuk: {MediaLanguage.Display(audio.Language)}" : $"zvuk automaticky: {MediaLanguage.Display(Settings.AudioLanguage)}")
        + $" • {Settings.OutputDirectory}";
    [ObservableProperty] public partial string Status { get; set; } = "Čeká";
    [ObservableProperty] public partial string Detail { get; set; } = "";
    [ObservableProperty] public partial double Percent { get; set; }
    [ObservableProperty] public partial bool IsIndeterminate { get; set; }
    [ObservableProperty] public partial string LogPath { get; set; } = "";
    [ObservableProperty] public partial string LogTail { get; set; } = "";
    [ObservableProperty] public partial string OutputPath { get; set; } = "";
}

public sealed record ConversionUpdate(string Stage, string Detail, double? Percent = null, string? LogTail = null,
    string? LogPath = null, string? OutputPath = null, ConversionSettings? AppliedSettings = null);

public sealed record HardwareCapabilities(string Summary, IReadOnlyList<EncoderOption> Encoders, string ProbeLog);
public sealed record MachinePerformance(double? Cpu, double MemoryPercent, double UsedGiB, double TotalGiB,
    double? GpuEncode, double? GpuDecode);
