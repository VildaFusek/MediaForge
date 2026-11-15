using MediaForge.Helpers;
using MediaForge.Models;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace MediaForge.Services;

public sealed class VideoConversionService
{
    public static string LogDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MediaForge", "ConversionLogs");

    public Task ConvertAsync(ConversionJob job, FfmpegInstallation installation,
        IProgress<ConversionUpdate> progress, CancellationToken token) =>
        Task.Run(() => ConvertCoreAsync(job, installation, progress, token), token);

    private static async Task ConvertCoreAsync(ConversionJob job, FfmpegInstallation installation,
        IProgress<ConversionUpdate> progress, CancellationToken token)
    {
        string? temporary = null;
        ConversionTemporaryFile? work = null;
        bool published = false;
        StreamWriter? log = null;
        var logGate = new object();
        var tail = new Queue<string>();
        string logPath = "";
        string outputPath = "";
        void Write(string text)
        {
            lock (logGate)
            {
                log?.WriteLine($"{DateTime.Now:O} {text}");
                tail.Enqueue(text.Length > 2048 ? text[..2048] : text);
                while (tail.Count > 60) tail.Dequeue();
            }
        }
        string Tail() { lock (logGate) return string.Join(Environment.NewLine, tail); }
        void Report(string stage, string detail, double? percent = null) =>
            progress.Report(new(stage, detail, percent, Tail(), logPath, outputPath));
        try
        {
            EnsureSafePath(LogDirectory);
            Directory.CreateDirectory(LogDirectory);
            EnsureSafePath(LogDirectory);
            logPath = Path.Combine(LogDirectory, $"{DateTime.Now:yyyyMMdd-HHmmss}-{job.Id:N}.log");
            log = new StreamWriter(new FileStream(logPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            { AutoFlush = true };
            Write(job.Description);
            Write($"FFmpeg {installation.Version}: {installation.FfmpegPath}");
            Report("Kontrola", "Ověřuji vstup, výstup a parametry.");
            string input = Path.GetFullPath(job.InputPath);
            string directory = Path.GetFullPath(job.Settings.OutputDirectory);
            EnsureSafePath(input);
            EnsureSafePath(directory);
            if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("Cílová složka neexistuje.");
            var settings = job.Settings;
            ValidateSettings(settings);
            // Keep the source open for the whole operation; Windows denies concurrent writers/deletion.
            using var source = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read);
            var identity = VideoFileIdentity.Read(source);
            var info = await VideoDiagnosticsService.ProbeAsync(input, installation, detail => Report("FFprobe", detail), token);
            if (settings.ExpectedSource is { } expected
                && (expected.FileIdentity != info.FileIdentity || expected.FileSize != info.FileSize
                    || expected.ModifiedUtcTicks != info.ModifiedUtcTicks))
                throw new IOException("Vstup se od výběru stop změnil. Načtěte stopy znovu a vytvořte novou úlohu.");
            var video = settings.VideoStreamIndex is { } videoIndex
                ? info.Streams.FirstOrDefault(s => s.Index == videoIndex && s.Type == "video" && !s.IsAttachedPicture)
                    ?? throw new InvalidDataException($"Vybraná video stopa #{videoIndex} ve vstupu neexistuje.")
                : info.Streams.First(s => s.Type == "video" && !s.IsAttachedPicture);
            var selectedAudio = settings.AudioDisabled ? null
                : settings.AudioStreamIndex is { } audioIndex
                    ? info.Streams.FirstOrDefault(s => s.Index == audioIndex && s.Type == "audio")
                        ?? throw new InvalidDataException($"Vybraná zvuková stopa #{audioIndex} ve vstupu neexistuje.")
                    : MediaLanguage.SelectAudio(info.Streams, settings.AudioLanguage);
            Write($"Vybrané video: #{video.Index} • {video.Codec} • {video.Width} × {video.Height}");
            Write(selectedAudio is null ? "Zvuk: bez zvuku."
                : $"Vybraný zvuk: #{selectedAudio.Index} • {MediaLanguage.Display(selectedAudio.Language)}"
                    + $" • {selectedAudio.Codec} • {selectedAudio.Title}");
            if (!settings.AudioDisabled && settings.AudioStreamIndex is null && selectedAudio is not null
                && MediaLanguage.Normalize(selectedAudio.Language) != MediaLanguage.Normalize(settings.AudioLanguage))
                Write("Preferovaný jazyk nebyl nalezen; použita výchozí nebo první zvuková stopa.");
            var colorProbe = await VideoDiagnosticsService.ProbeAsync(input, installation,
                detail => Report("Kontrola HDR", detail), token, video.Index);
            var color = ReadColorInfo(colorProbe.RawJson, video.Index, settings.PreserveHdr);
            bool preserveHdr = settings.PreserveHdr && color.IsHdr;
            Write("Kontrola HDR metadat vychází z hlavičky a úvodních 64 video paketů, nikoli z celého souboru."
                + " Zachování pozdějších dynamických změn metadat se neověřuje.");
            Write(color.IsHdr
                ? preserveHdr ? $"Zachovávám HDR: {color.Transfer}, HEVC 10 bitů, libx265."
                    : $"HDR {color.Transfer} → SDR BT.709; tone mapping Mobius, referenční bílá 100 nitů."
                : "SDR vstup; tone mapping se nepoužije.");
            Write("Vstup: " + info.Summary);
            if (settings.Decoder != "cpu" || color.IsHdr)
            {
                Report("Ověření převodu", $"Ověřuji {settings.DecoderDescription}, filtry a enkodér krátkým průchodem (8 výstupních snímků).");
                var probeArgs = BuildArguments(input, "-", video, null, settings, color, preflight: true);
                Write("Kontrola cesty převodu – argumenty (JSON pole): " + JsonSerializer.Serialize(probeArgs));
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                int timeoutSeconds = color.IsHdr ? 120 : 30;
                timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                using var probe = ConversionProcess.Create(installation.FfmpegPath, probeArgs);
                int probeCode;
                try
                {
                    probeCode = await ConversionProcess.RunAsync(probe, _ => { }, Write, timeout.Token);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    throw new IOException($"Ověření dekódování, filtrů a enkodéru překročilo {timeoutSeconds} sekund. Podrobnosti jsou v protokolu.");
                }
                if (probeCode != 0)
                    throw new IOException($"Zvolená cesta převodu pro tento vstup selhala (kód {probeCode})."
                        + " Podrobnosti jsou v protokolu; HDR → SDR vyžaduje filtry zscale a tonemap.");
                Write("Krátká kontrola dekódování, CPU filtrů a zvoleného enkodéru dokončena."
                    + " Neověřuje celý vstup; automatické přepnutí na CPU není povoleno.");
            }
            // A unique name is reserved only at final publication; File.Move never overwrites.
            outputPath = Path.Combine(directory, Path.GetFileNameWithoutExtension(input) + ".mp4");
            work = ConversionTemporaryFile.Create(directory);
            temporary = work.Path;
            var args = BuildArguments(input, temporary, video, selectedAudio, settings, color);
            Write("Argumenty (JSON pole): " + JsonSerializer.Serialize(args));
            Report("Převod", $"Spouštím FFmpeg; dekódování: {settings.DecoderDescription}, filtry CPU, {settings.Encoder}");
            using var process = ConversionProcess.Create(installation.FfmpegPath, args);
            var watch = Stopwatch.StartNew();
            long lastReport = -500;
            double? percent = null;
            double seconds = 0;
            string speed = "?", fps = "?";
            long lastLog = -5000;
            int code = await ConversionProcess.RunAsync(process, line =>
            {
                int separator = line.IndexOf('=');
                if (separator < 0) return;
                string key = line[..separator], value = line[(separator + 1)..];
                switch (key)
                {
                    case "out_time_us":
                        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double us)
                            && double.IsFinite(us))
                        {
                            seconds = Math.Max(0, us / 1_000_000);
                            if (info.DurationSeconds is > 0)
                                percent = Math.Clamp(seconds * 100 / info.DurationSeconds.Value, 0, 99.9);
                        }
                        break;
                    case "speed": speed = value; break;
                    case "fps": fps = value; break;
                    case "progress":
                        if (watch.ElapsedMilliseconds - lastReport < 400 && value != "end") break;
                        lastReport = watch.ElapsedMilliseconds;
                        double remaining = info.DurationSeconds is > 0 && seconds > 0
                            ? Math.Max(0, (info.DurationSeconds.Value - seconds) * watch.Elapsed.TotalSeconds / seconds) : -1;
                        string detail = $"Zpracováno {seconds:F1} s • {fps} fps • rychlost {speed}"
                            + $" • uplynulo {watch.Elapsed:hh\\:mm\\:ss}"
                            + (remaining >= 0 ? $" • odhad zbývá {TimeSpan.FromSeconds(Math.Min(remaining, 31536000)):d\\.hh\\:mm\\:ss}" : "");
                        if (watch.ElapsedMilliseconds - lastLog >= 5000 || value == "end")
                        {
                            Write(detail);
                            lastLog = watch.ElapsedMilliseconds;
                        }
                        Report("Převod", detail, percent);
                        break;
                }
            }, Write, token);
            Write($"FFmpeg skončil s kódem {code}.");
            if (code != 0) throw new IOException($"FFmpeg selhal (kód {code}). Podrobnosti jsou v protokolu.");
            token.ThrowIfCancellationRequested();
            Report("Kontrola výstupu", "Ověřuji kontejner a video stopu pomocí FFprobe.", 99.9);
            var result = await VideoDiagnosticsService.ProbeAsync(temporary, installation, _ => { }, token);
            if (new FileInfo(temporary).Length == 0 || result.Width is not > 0 || result.Height is not > 0)
                throw new InvalidDataException("Výstup neobsahuje platnou video stopu.");
            var profile = settings.Profile;
            if (result.VideoCodec != profile.Codec
                || Math.Max(result.Width.Value, result.Height.Value) > Math.Max(profile.Width, profile.Height)
                || Math.Min(result.Width.Value, result.Height.Value) > Math.Min(profile.Width, profile.Height)
                || (result.FrameRate is { } outputFps && outputFps > profile.Fps + 0.1))
                throw new InvalidDataException("Výstup neodpovídá kodeku nebo limitům zvoleného profilu.");
            var audio = result.Streams.Where(s => s.Type == "audio").ToArray();
            if (audio.Length != (selectedAudio is not null ? 1 : 0)
                || audio.Any(s => s.Codec != "aac" || s.Channels != 2))
                throw new InvalidDataException("Výstupní zvuk neodpovídá zvolenému AAC stereo profilu.");
            if (color.IsHdr)
            {
                var outputVideo = result.Streams.First(s => s.Type == "video" && !s.IsAttachedPicture);
                var outputProbe = await VideoDiagnosticsService.ProbeAsync(temporary, installation,
                    detail => Report("Kontrola HDR výstupu", detail, 99.9), token, outputVideo.Index);
                var outputColor = ReadColorInfo(outputProbe.RawJson, outputVideo.Index, preserveHdr);
                if (preserveHdr
                    ? outputVideo.PixelFormat != "yuv420p10le" || outputColor != color
                    : outputVideo.PixelFormat != "yuv420p" || outputColor.Transfer != "bt709"
                        || outputColor.Primaries != "bt709" || outputColor.Matrix != "bt709"
                        || outputColor.Range != "tv" || outputColor.MasterDisplay != "" || outputColor.ContentLight != "")
                    throw new InvalidDataException("Výstup neodpovídá zvolenému HDR/SDR režimu nebo ztratil HDR metadata.");
            }
            Write("Výstup: " + result.Summary);
            if (VideoFileIdentity.Read(source) != identity)
                throw new IOException("Vstup se během převodu změnil; výstup nebyl publikován.");
            EnsureSafePath(directory);
            EnsureSafePath(temporary);
            token.ThrowIfCancellationRequested();
            string? smartName = null;
            int suffix = 2;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (!File.Exists(outputPath) && !Directory.Exists(outputPath))
                {
                    try
                    {
                        Write("Publikuji bez přepsání: " + outputPath);
                        File.Move(temporary, outputPath, overwrite: false);
                        break;
                    }
                    catch (IOException ex) when ((ex.HResult & 0xFFFF) is 80 or 183)
                    {
                        // Another process created the destination after the existence check.
                    }
                }
                if (smartName is null)
                {
                    smartName = SmartVideoNameService.Suggest(result with { Title = info.Title, FilePath = input },
                        ".mp4", audio: result.Streams.FirstOrDefault(s => s.Type == "audio"));
                    outputPath = Path.Combine(directory, smartName);
                }
                else
                {
                    outputPath = Path.Combine(directory,
                        $"{Path.GetFileNameWithoutExtension(smartName)} ({suffix++}).mp4");
                }
                Write("Kolize názvu, nový název výstupu: " + outputPath);
            }
            published = true;
            Write("Dokončeno. FFprobe ověřil metadata, nikoli úplnou dekódovatelnost výstupu.");
            Report("Dokončeno", outputPath, 100);
        }
        catch (Exception ex)
        {
            // Once published, a logging problem must not claim the output was rolled back.
            string stage = published ? "Dokončeno s upozorněním"
                : ex is OperationCanceledException ? "Zrušeno" : "Chyba";
            try { Write(stage + ": " + ex.Message); }
            catch (Exception logError) { progress.Report(new(stage, $"Zápis protokolu selhal: {logError.Message}")); }
            Report(stage, (published ? $"Výstup zachován: {outputPath}\n" : "") + ex.Message, published ? 100 : null);
            if (!published) throw;
        }
        finally
        {
            if (work is not null)
            {
                try
                {
                    await work.CompleteAsync(published);
                }
                catch (Exception ex)
                {
                    string warning = $"Úklid převodu selhal ({temporary}). Záznam zůstává pro další spuštění: {ex.Message}";
                    try { Write(warning); }
                    catch { /* A full/unavailable disk is also reported through the UI below. */ }
                    progress.Report(new("Úklid selhal", warning, LogTail: Tail(), LogPath: logPath));
                }
            }
            try { log?.Dispose(); }
            catch (Exception ex)
            { progress.Report(new("Protokol", $"Uzavření protokolu selhalo: {ex.Message}", LogPath: logPath)); }
        }
    }

    private static void ValidateSettings(ConversionSettings settings)
    {
        var p = settings.Profile;
        if (p.Width is < 128 or > 7680 || p.Height is < 128 or > 4320 || p.Fps is < 1 or > 120
            || p.VideoKbps is < 250 or > 100000 || p.AudioKbps is < 64 or > 512)
            throw new ArgumentException("Parametry převodu jsou mimo povolené meze.");
        string[] allowed = p.Codec == "h264"
            ? ["libx264", "h264_nvenc", "h264_qsv", "h264_amf"]
            : p.Codec == "hevc" ? ["libx265", "hevc_nvenc", "hevc_qsv", "hevc_amf"] : [];
        if (!allowed.Contains(settings.Encoder)) throw new ArgumentException("Enkodér neodpovídá kodeku profilu.");
        if (settings.Decoder is not ("cpu" or "cuda" or "d3d11va"))
            throw new ArgumentException("Neplatný způsob dekódování.");
        if (settings.PreserveHdr && (p.Codec != "hevc" || settings.Encoder != "libx265"))
            throw new ArgumentException("Zachování HDR vyžaduje HEVC a CPU enkodér libx265.");
    }

    private static List<string> BuildArguments(string input, string output, VideoStreamInfo video,
        VideoStreamInfo? audio, ConversionSettings settings, ColorInfo color, bool preflight = false)
    {
        var p = settings.Profile;
        // Orientation-independent bounding box: portrait mobile clips retain their orientation and size.
        int maxSide = Math.Max(p.Width, p.Height), minSide = Math.Min(p.Width, p.Height);
        string landscape = $"gte(iw,ih)";
        string width = $"min(iw,if({landscape},{maxSide},{minSide}))";
        string height = $"min(ih,if({landscape},{minSide},{maxSide}))";
        bool preserveHdr = settings.PreserveHdr && color.IsHdr;
        string pixelFormat = preserveHdr ? "yuv420p10le" : "yuv420p";
        string filter = $"scale=w='{width}':h='{height}':force_original_aspect_ratio=decrease:force_divisible_by=2:reset_sar=1";
        if (video.FrameRate is { } rate && rate > p.Fps) filter += $",fps={p.Fps}";
        filter += $",format={pixelFormat}";
        if (color.IsHdr && !preserveHdr)
            filter = "zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,"
                + "tonemap=tonemap=mobius:desat=2,zscale=t=bt709:m=bt709:r=tv,format=yuv420p,"
                + "sidedata=mode=delete:type=MASTERING_DISPLAY_METADATA,"
                + "sidedata=mode=delete:type=CONTENT_LIGHT_LEVEL,"
                + "sidedata=mode=delete:type=DYNAMIC_HDR_PLUS," + filter;
        if (settings.Decoder != "cpu")
        {
            string downloadFormat = video.PixelFormat switch
            {
                "yuv420p" or "yuvj420p" or "nv12" => "nv12",
                "yuv420p10le" or "p010le" => "p010le",
                "yuv420p12le" or "p016le" => "p016le",
                _ => throw new NotSupportedException(
                    $"GPU cesta zatím nepodporuje vstupní formát {video.PixelFormat}. Zvolte CPU dekódování.")
            };
            // Hardware frames must be explicitly downloaded before the existing software filters.
            filter = $"hwdownload,format={downloadFormat}," + filter;
        }
        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-y", "-loglevel", "info", "-nostats", "-stats_period", "0.5",
            "-progress", "pipe:1", "-protocol_whitelist", "file", "-threads",
            Math.Clamp(Environment.ProcessorCount - 1, 1, 16).ToString(CultureInfo.InvariantCulture),
            "-filter_threads", Math.Clamp(Environment.ProcessorCount - 1, 1, 16).ToString(CultureInfo.InvariantCulture)
        };
        if (settings.Decoder != "cpu")
            args.AddRange((string[])["-hwaccel", settings.Decoder, "-hwaccel_output_format",
                settings.Decoder == "cuda" ? "cuda" : "d3d11"]);
        args.AddRange((string[])
        [
            "-i", input, "-map", $"0:{video.Index}", "-sn", "-dn",
            "-map_metadata", "-1", "-map_chapters", "-1", "-vf", filter,
            "-c:v", settings.Encoder, "-b:v", $"{p.VideoKbps}k", "-maxrate", $"{p.VideoKbps}k",
            "-bufsize", $"{p.VideoKbps * 2}k", "-pix_fmt", pixelFormat,
            "-fps_mode:v", "cfr", "-fpsmax:v", p.Fps.ToString(CultureInfo.InvariantCulture),
            "-threads", Math.Clamp(Environment.ProcessorCount - 1, 1, 16).ToString(CultureInfo.InvariantCulture)
        ]);
        if (color.IsHdr)
        {
            args.AddRange((string[])["-color_trc", preserveHdr ? color.Transfer : "bt709",
                "-color_primaries", preserveHdr ? color.Primaries : "bt709",
                "-colorspace", preserveHdr ? color.Matrix : "bt709",
                "-color_range", preserveHdr ? color.Range : "tv"]);
            if (preserveHdr)
            {
                args.AddRange((string[])["-profile:v", "main10"]);
                var parameters = new List<string>
                {
                    "repeat-headers=1", "colorprim=bt2020", $"transfer={color.Transfer}",
                    "colormatrix=bt2020nc", $"range={(color.Range == "pc" ? "full" : "limited")}"
                };
                if (color.Transfer == "smpte2084") parameters.Add("hdr10=1");
                if (color.MasterDisplay != "") parameters.Add("master-display=" + color.MasterDisplay);
                if (color.ContentLight != "") parameters.Add("max-cll=" + color.ContentLight);
                args.AddRange((string[])["-x265-params", string.Join(":", parameters)]);
            }
        }
        if (preflight) args.AddRange((string[])["-frames:v", "8", "-f", "null"]);
        else args.AddRange((string[])["-movflags", "+faststart", "-f", "mp4"]);
        if (audio is not null)
        {
            args.AddRange((string[])["-map", $"0:{audio.Index}", "-c:a", "aac", "-b:a", $"{p.AudioKbps}k",
                "-ac", "2", "-ar", "48000", "-disposition:a:0", "default"]);
            if (!string.IsNullOrWhiteSpace(audio.Language))
                args.AddRange((string[])["-metadata:s:a:0", $"language={audio.Language}"]);
            if (!string.IsNullOrWhiteSpace(audio.Title))
                args.AddRange((string[])["-metadata:s:a:0", $"title={audio.Title}"]);
        }
        else args.Add("-an");
        if (settings.Encoder is "libx264" or "libx265") args.AddRange((string[])["-preset", "medium"]);
        if (settings.Encoder.EndsWith("_nvenc", StringComparison.Ordinal)) args.AddRange((string[])["-preset", "p4"]);
        if (p.Codec == "h264") args.AddRange((string[])["-profile:v", "high"]);
        if (p.Codec == "hevc") args.AddRange((string[])["-tag:v", "hvc1"]);
        args.Add(output);
        return args;
    }

    private sealed record ColorInfo(string Transfer, string Primaries, string Matrix, string Range,
        string MasterDisplay, string ContentLight)
    {
        public bool IsHdr => Transfer is "smpte2084" or "arib-std-b67";
    }

    private static ColorInfo ReadColorInfo(string rawJson, int streamIndex, bool preserve)
    {
        using var json = JsonDocument.Parse(rawJson);
        var stream = json.RootElement.GetProperty("streams").EnumerateArray()
            .First(s => s.GetProperty("index").GetInt32() == streamIndex);
        static string Text(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? "" : "";
        var sideData = new List<JsonElement>();
        void AddSideData(JsonElement element)
        {
            if (element.TryGetProperty("side_data_list", out var list))
                sideData.AddRange(list.EnumerateArray());
        }
        AddSideData(stream);
        if (json.RootElement.TryGetProperty("frames", out var frames))
            foreach (var frame in frames.EnumerateArray()) AddSideData(frame);
        string master = "", light = "";
        foreach (var data in sideData)
        {
            string type = Text(data, "side_data_type");
            if (type.Contains("DOVI", StringComparison.OrdinalIgnoreCase)
                || type.Contains("Dolby Vision", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("Dolby Vision zatím není podporované; převod nebyl spuštěn.");
            if (preserve && (type.Contains("2094", StringComparison.OrdinalIgnoreCase)
                || type.Contains("HDR10+", StringComparison.OrdinalIgnoreCase)))
                throw new NotSupportedException("Zachování dynamických HDR10+ metadat zatím není podporované. Lze zvolit HDR → SDR.");
            string value;
            if (type == "Mastering display metadata")
            {
                string Coordinate(string name) => MetadataNumber(data, name, 50000, 50000);
                string Luminance(string name) => MetadataNumber(data, name, 10000, 100000000);
                value = $"G({Coordinate("green_x")},{Coordinate("green_y")})"
                    + $"B({Coordinate("blue_x")},{Coordinate("blue_y")})"
                    + $"R({Coordinate("red_x")},{Coordinate("red_y")})"
                    + $"WP({Coordinate("white_point_x")},{Coordinate("white_point_y")})"
                    + $"L({Luminance("max_luminance")},{Luminance("min_luminance")})";
                if (master != "" && master != value)
                    throw new NotSupportedException("Proměnlivá mastering HDR metadata zatím nejsou podporovaná.");
                master = value;
            }
            if (type == "Content light level metadata")
            {
                value = $"{MetadataNumber(data, "max_content", 1, 65535)},"
                    + MetadataNumber(data, "max_average", 1, 65535);
                if (light != "" && light != value)
                    throw new NotSupportedException("Proměnlivá jasová HDR metadata zatím nejsou podporovaná.");
                light = value == "0,0" ? "" : value;
            }
        }
        var result = new ColorInfo(Text(stream, "color_transfer"), Text(stream, "color_primaries"),
            Text(stream, "color_space"), Text(stream, "color_range"), master, light);
        if (result.IsHdr && (result.Primaries != "bt2020" || result.Matrix != "bt2020nc"
            || result.Range is not ("tv" or "pc")))
            throw new NotSupportedException("HDR vyžaduje jednoznačné údaje BT.2020, BT.2020 non-constant a rozsah tv/pc. Chybějící údaje se neodhadují.");
        if (preserve && result.Transfer == "smpte2084" && master == "")
            throw new NotSupportedException("V úvodním vzorku HDR10 nebyla nalezena mastering metadata. Bez nich nelze bezpečně potvrdit zachování HDR10.");
        if (!result.IsHdr && (master != "" || light != ""))
            throw new NotSupportedException("Vstup obsahuje HDR metadata, ale nemá jednoznačně označenou PQ/HLG přenosovou funkci.");
        return result;
    }

    private static string MetadataNumber(JsonElement data, string name, decimal scale, decimal maximum)
    {
        if (!data.TryGetProperty(name, out var element))
            throw new InvalidDataException("Chybí údaj HDR metadat: " + name);
        string text = element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : element.GetRawText();
        string[] parts = text.Split('/');
        if (parts.Length is < 1 or > 2
            || !decimal.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out decimal numerator)
            || numerator < 0)
            throw new InvalidDataException("Neplatný údaj HDR metadat: " + name);
        decimal denominator = 1;
        if (parts.Length == 2 && (!decimal.TryParse(parts[1], NumberStyles.Float,
            CultureInfo.InvariantCulture, out denominator) || denominator <= 0))
            throw new InvalidDataException("Neplatný zlomek HDR metadat: " + name);
        decimal value = decimal.Round(numerator / denominator * scale, 0, MidpointRounding.AwayFromZero);
        if (value < 0 || value > maximum)
            throw new InvalidDataException("Údaj HDR metadat je mimo rozsah: " + name);
        return value.ToString(CultureInfo.InvariantCulture);
    }

    internal static void EnsureSafePath(string path)
    {
        string full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\", StringComparison.Ordinal) || full.Length < 3 || full[1] != ':'
            || full[2..].Contains(':') || new DriveInfo(Path.GetPathRoot(full)!).DriveType == DriveType.Network)
            throw new NotSupportedException("Převod podporuje pouze místní diskové cesty bez odkazů.");
        for (string? current = full; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Odkazy a junction nejsou pro převod povoleny: " + current);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
