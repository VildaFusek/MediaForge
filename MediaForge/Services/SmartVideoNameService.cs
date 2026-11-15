using MediaForge.Helpers;
using MediaForge.Models;
using System.Text.RegularExpressions;

namespace MediaForge.Services;

internal static class SmartVideoNameService
{
    public static string Suggest(VideoDiagnosticRecord info, string extension,
        VideoStreamInfo? video = null, VideoStreamInfo? audio = null)
    {
        video ??= info.Streams.First(s => s.Type == "video" && !s.IsAttachedPicture);
        string title = string.IsNullOrWhiteSpace(info.Title)
            ? Path.GetFileNameWithoutExtension(info.FilePath) : info.Title;
        title = Regex.Replace(title, @" \[[0-9]+x[0-9]+ [^\[\]]+\]$", "");
        title = Clean(title);
        if (string.IsNullOrWhiteSpace(title)) title = "Video";
        string language = audio is null ? "und" : MediaLanguage.Normalize(audio.Language);
        if (string.IsNullOrWhiteSpace(language)) language = "und";
        string sound = audio is null ? "bez zvuku" : $"{audio.Codec.ToUpperInvariant()} {language}";
        string technical = " [" + Clean($"{video.Width}x{video.Height} {video.Codec.ToUpperInvariant()} {sound}") + "]";
        int maximumTitle = Math.Max(1, 180 - technical.Length - extension.Length);
        if (title.Length > maximumTitle) title = title[..maximumTitle].TrimEnd();
        string name = title + technical + extension;
        FileSystemService.ValidateItemName(name);
        return name;
    }

    public static VideoDiagnosticRecord ForOutputPreview(VideoDiagnosticRecord input, DevicePreset profile,
        VideoStreamInfo video, VideoStreamInfo? audio)
    {
        int width = video.Width ?? profile.Width, height = video.Height ?? profile.Height;
        if (width <= 0 || height <= 0) throw new InvalidDataException("Nelze zjistit rozlišení vybrané video stopy.");
        double scale = Math.Min(1, Math.Min(
            Math.Max(profile.Width, profile.Height) / (double)Math.Max(width, height),
            Math.Min(profile.Width, profile.Height) / (double)Math.Min(width, height)));
        var outputVideo = new VideoStreamInfo
        {
            Type = "video", Codec = profile.Codec,
            Width = Math.Max(2, (int)(width * scale) / 2 * 2),
            Height = Math.Max(2, (int)(height * scale) / 2 * 2)
        };
        var streams = new List<VideoStreamInfo> { outputVideo };
        if (audio is not null) streams.Add(new() { Type = "audio", Codec = "aac", Language = audio.Language });
        return input with { Streams = streams };
    }

    private static string Clean(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        string clean = new(value.Select(c => invalid.Contains(c) || char.IsControl(c) ? '-' : c).ToArray());
        return Regex.Replace(clean, @"\s+", " ").Trim().TrimEnd('.', ' ');
    }
}
