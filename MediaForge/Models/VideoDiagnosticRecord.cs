using System.Text.Json.Serialization;

namespace MediaForge.Models;

public sealed class VideoStreamInfo
{
    public int Index { get; init; }
    public string Type { get; init; } = "";
    public string Codec { get; init; } = "";
    public string Profile { get; init; } = "";
    public string Language { get; init; } = "";
    public string Title { get; init; } = "";
    public bool IsDefault { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public double? FrameRate { get; init; }
    public string PixelFormat { get; init; } = "";
    public int? BitDepth { get; init; }
    public long? BitRate { get; init; }
    public int? Channels { get; init; }
    public int? SampleRate { get; init; }
    public bool IsAttachedPicture { get; init; }
}

public sealed record VideoDiagnosticRecord
{
    public string FilePath { get; init; } = "";
    public long FileSize { get; init; }
    public long ModifiedUtcTicks { get; init; }
    public string FileIdentity { get; init; } = "";
    public DateTime AnalyzedUtc { get; init; }
    public string ProbeVersion { get; init; } = "";
    public string Container { get; init; } = "";
    public string Title { get; init; } = "";
    public double? DurationSeconds { get; init; }
    public long? BitRate { get; init; }
    public string VideoCodec { get; init; } = "";
    public int? Width { get; init; }
    public int? Height { get; init; }
    public double? FrameRate { get; init; }
    public string PixelFormat { get; init; } = "";
    public int? BitDepth { get; init; }
    public List<VideoStreamInfo> Streams { get; init; } = [];
    [JsonIgnore] public string RawJson { get; init; } = "";
    [JsonIgnore] public string Name => Path.GetFileName(FilePath);
    [JsonIgnore] public int AudioTracks => Streams.Count(s => s.Type == "audio");
    [JsonIgnore] public int SubtitleTracks => Streams.Count(s => s.Type == "subtitle");
    [JsonIgnore] public string Summary => $"{VideoCodec} • {Width?.ToString() ?? "?"} × {Height?.ToString() ?? "?"}"
        + $" • {(FrameRate is { } fps ? $"{fps:F3} fps" : "? fps")}"
        + $" • {(DurationSeconds is { } seconds ? $"{seconds:F1} s" : "? s")} • zvuk: {AudioTracks} • titulky: {SubtitleTracks}";
    [JsonIgnore] public string Details =>
        $"{FilePath}\n\n{Summary}\nKontejner: {Container}\nVelikost: {FileSize:N0} B"
        + $"\nDatový tok: {(BitRate is { } rate ? $"{rate / 1000d:F0} kb/s" : "nezjištěn")}"
        + $"\nPixelový formát: {PixelFormat} • bitová hloubka: {BitDepth?.ToString() ?? "nezjištěna"}"
        + $"\nZměna souboru (UTC): {new DateTime(ModifiedUtcTicks, DateTimeKind.Utc):u}"
        + $"\nDiagnostika: {AnalyzedUtc.ToLocalTime():g} • FFprobe {ProbeVersion}\n\n"
        + string.Join("\n", Streams.Select(s =>
            $"#{s.Index} {s.Type}: {s.Codec} {s.Profile} {s.Language}"
            + (s.Width.HasValue ? $" • {s.Width} × {s.Height}" : "")
            + (s.Channels.HasValue ? $" • {s.Channels} kanálů" : "")
            + (s.SampleRate.HasValue ? $" • {s.SampleRate} Hz" : "")
            + (s.IsAttachedPicture ? " • obalový obrázek" : "")))
        + "\n\nMetadata platí pro uloženou identitu, velikost a čas změny souboru."
        + "\nFFprobe neověřuje úplné dekódování ani neporušenost všech snímků.";
}

public sealed record VideoDiagnosticProgress(
    string Stage, string Detail, int Completed, int Total, int Saved, int Failed, int Skipped,
    bool Discovering = false, VideoDiagnosticRecord? Record = null, bool WriteLog = true);

public sealed record VideoDiagnosticSummary(int Completed, int Total, int Saved, int Failed, int Skipped);
