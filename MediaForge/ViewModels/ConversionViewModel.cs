using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaForge.Models;
using MediaForge.Helpers;
using MediaForge.Services;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;

namespace MediaForge.ViewModels;

public partial class ConversionViewModel : ObservableObject
{
    public static ConversionViewModel Instance { get; } = new();
    private readonly VideoConversionService _service = new();
    private CancellationTokenSource? _runCancellation;
    private CancellationTokenSource? _detectCancellation;
    private CancellationTokenSource? _trackCancellation;
    private ConversionJob? _activeJob;
    private HardwareCapabilities? _hardware;
    private string? _detectedInstallation;
    private bool _stopAfterCurrent;
    private Task? _recoveryTask;
    private bool _updatingSettings;
    public event Action? QueueStarted;

    public IReadOnlyList<DevicePreset> Presets => DevicePreset.All;
    public IReadOnlyList<string> Codecs { get; } = (string[])["h264", "hevc"];
    public IReadOnlyList<DecoderOption> Decoders => DecoderOption.All;
    public IReadOnlyList<HdrOption> HdrOptions => HdrOption.All;
    public ObservableCollection<EncoderOption> Encoders { get; } = [];
    public ObservableCollection<ConversionJob> Jobs { get; } = [];
    public ObservableCollection<ConversionInput> Inputs { get; } = [];
    public string SystemAudioLanguage { get; } = MediaLanguage.SystemLanguage;
    public string AudioSelectionHint => $"Předvolený jazyk Windows: {MediaLanguage.Display(SystemAudioLanguage)}."
        + " Pokud chybí, použije se výchozí zvuková stopa souboru, jinak první. Jiný zvuk lze zvolit pro každý vstup.";

    [ObservableProperty] public partial DevicePreset SelectedPreset { get; set; } = DevicePreset.All[0];
    [ObservableProperty] public partial EncoderOption? SelectedEncoder { get; set; }
    [ObservableProperty] public partial DecoderOption SelectedDecoder { get; set; } = DecoderOption.All[0];
    [ObservableProperty] public partial HdrOption SelectedHdr { get; set; } = HdrOption.All[0];
    [ObservableProperty] public partial bool IsManual { get; set; }
    [ObservableProperty] public partial string ManualCodec { get; set; } = "h264";
    [ObservableProperty] public partial double Width { get; set; } = 1920;
    [ObservableProperty] public partial double Height { get; set; } = 1080;
    [ObservableProperty] public partial double FrameRate { get; set; } = 30;
    [ObservableProperty] public partial double VideoKbps { get; set; } = 6000;
    [ObservableProperty] public partial double AudioKbps { get; set; } = 192;
    [ObservableProperty] public partial string OutputDirectory { get; set; } = "";
    [ObservableProperty] public partial bool UseSmartOutputNames { get; set; }
    [ObservableProperty] public partial ConversionInput? SelectedInput { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial bool IsLoadingTracks { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SettingsScopeDescription))]
    public partial bool EditSelectedOnly { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyPropertyChangedFor(nameof(HasSettingsError))]
    [NotifyPropertyChangedFor(nameof(NextStep))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial string SettingsError { get; set; } = "";
    public bool HasSettingsError => !string.IsNullOrEmpty(SettingsError);
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UseCustomDirectory))]
    public partial bool UseSourceDirectory { get; set; } = true;
    public bool UseCustomDirectory => !UseSourceDirectory;
    [ObservableProperty] public partial ConversionJob? SelectedJob { get; set; }
    [ObservableProperty] public partial string Message { get; set; } = "";
    [ObservableProperty] public partial string HardwareSummary { get; set; } = "Hardware zatím nebyl zjištěn.";
    [ObservableProperty] public partial string HardwareLog { get; set; } = "";
    [ObservableProperty] public partial string QueueSummary { get; set; } = "Fronta je prázdná.";
    [ObservableProperty] public partial string CurrentTask { get; set; } = "Žádný převod neprobíhá.";
    [ObservableProperty] public partial string CurrentDetail { get; set; } = "";
    [ObservableProperty] public partial string CurrentLogTail { get; set; } = "";
    [ObservableProperty] public partial double CurrentPercent { get; set; }
    [ObservableProperty] public partial bool CurrentIndeterminate { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyPropertyChangedFor(nameof(CanDetect))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(DetectCommand))]
    public partial bool IsShuttingDown { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyPropertyChangedFor(nameof(CanDetect))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(DetectCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopAfterCurrentCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyPropertyChangedFor(nameof(CanDetect))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(DetectCommand))]
    public partial bool IsDetecting { get; set; }

    public bool CanStart => !IsRunning && !IsDetecting && !IsLoadingTracks && !IsShuttingDown
        && string.IsNullOrEmpty(SettingsError) && Jobs.Any(j => j.Status == "Čeká");
    public bool CanDetect => !IsRunning && !IsDetecting && !IsShuttingDown;
    public bool CanEditSelected => SelectedJob?.Status == "Čeká";
    public bool CanEditSettings => !EditSelectedOnly || CanEditSelected;
    public string SettingsScopeDescription => !EditSelectedOnly ? "Změny platí pro všechna čekající videa."
        : CanEditSelected ? $"Upravujete pouze: {SelectedJob!.Name}" : "Pro úpravu vyberte čekající video.";
    public string NextStep => IsRunning ? "Převod běží. Další videa můžete přidat do fronty."
        : HasSettingsError ? "Nejprve opravte zvýrazněné nastavení."
        : IsDetecting ? "Připravuji automatickou akceleraci. Videa můžete mezitím přidávat."
        : IsLoadingTracks ? "Načítám stopy vybraného videa. Jazyk Windows předvyberu automaticky."
        : Jobs.Any(j => j.Status == "Čeká") ? "Videa jsou ve frontě. Stačí spustit, případně změnit cílové zařízení."
        : "Vyberte videa — do fronty je zařadím automaticky. Potom stačí spustit převod.";
    public string ProfileDescription => IsManual
        ? "Ruční profil: MP4, AAC stereo; HDR podle zvolené možnosti. Rozměry a fps jsou horní limity."
        : SelectedPreset.Description;

    public ConversionViewModel()
    {
        UpdateEncoders();
        Jobs.CollectionChanged += (_, _) => UpdateSummary();
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SelectedPreset) or nameof(SelectedEncoder) or nameof(SelectedDecoder)
                or nameof(IsManual) or nameof(ManualCodec) or nameof(Width) or nameof(Height)
                or nameof(FrameRate) or nameof(VideoKbps) or nameof(AudioKbps)
                or nameof(OutputDirectory) or nameof(UseSourceDirectory) or nameof(UseSmartOutputNames) or nameof(SelectedHdr))
                ApplySettingsToWaiting();
            if (e.PropertyName is nameof(IsRunning) or nameof(IsDetecting) or nameof(IsLoadingTracks))
                OnPropertyChanged(nameof(NextStep));
        };
    }

    partial void OnSelectedPresetChanged(DevicePreset value)
    {
        if (!_updatingSettings) IsManual = false;
        UpdateEncoders();
        OnPropertyChanged(nameof(ProfileDescription));
    }
    partial void OnIsManualChanged(bool value) { UpdateEncoders(); OnPropertyChanged(nameof(ProfileDescription)); }
    partial void OnManualCodecChanged(string value) => UpdateEncoders();
    partial void OnSelectedInputChanged(ConversionInput? value)
    {
        if (UseSourceDirectory) OutputDirectory = value is null ? "" : Path.GetDirectoryName(value.FullPath) ?? "";
    }
    partial void OnUseSourceDirectoryChanged(bool value)
    {
        if (value) OutputDirectory = SelectedInput is null ? "" : Path.GetDirectoryName(SelectedInput.FullPath) ?? "";
    }
    partial void OnSelectedJobChanged(ConversionJob? value)
    {
        _updatingSettings = true;
        try
        {
            SelectedInput = value?.Input;
            if (EditSelectedOnly && value is not null) { LoadEditor(value.Settings); SettingsError = ""; }
        }
        finally { _updatingSettings = false; }
        OnPropertyChanged(nameof(CanEditSelected));
        OnPropertyChanged(nameof(CanEditSettings));
        OnPropertyChanged(nameof(SettingsScopeDescription));
    }
    partial void OnEditSelectedOnlyChanged(bool value)
    {
        if (value && SelectedJob is { } job)
        {
            _updatingSettings = true;
            try { LoadEditor(job.Settings); SettingsError = ""; }
            finally { _updatingSettings = false; }
        }
        // Returning to shared editing does not overwrite individual choices until a setting is changed.
        OnPropertyChanged(nameof(CanEditSettings));
        OnPropertyChanged(nameof(SettingsScopeDescription));
    }

    private void LoadEditor(ConversionSettings settings)
    {
        IsManual = !Presets.Contains(settings.Profile);
        if (!IsManual) SelectedPreset = settings.Profile;
        ManualCodec = settings.Profile.Codec;
        Width = settings.Profile.Width; Height = settings.Profile.Height; FrameRate = settings.Profile.Fps;
        VideoKbps = settings.Profile.VideoKbps; AudioKbps = settings.Profile.AudioKbps;
        UpdateEncoders();
        SelectedEncoder = Encoders.FirstOrDefault(e => e.Encoder == settings.Encoder) ?? Encoders[0];
        SelectedDecoder = Decoders.First(d => d.HwAccel == settings.Decoder);
        UseSourceDirectory = string.Equals(settings.OutputDirectory, Path.GetDirectoryName(SelectedInput?.FullPath),
            StringComparison.OrdinalIgnoreCase);
        OutputDirectory = settings.OutputDirectory;
        UseSmartOutputNames = settings.UseSmartName;
        SelectedHdr = HdrOptions.First(h => h.Preserve == settings.PreserveHdr);
    }

    private void UpdateEncoders()
    {
        bool wasUpdating = _updatingSettings;
        _updatingSettings = true;
        try
        {
            string codec = IsManual ? ManualCodec : SelectedPreset.Codec;
            string? previous = SelectedEncoder?.Encoder;
            Encoders.Clear();
            Encoders.Add(new("Automaticky – ověřená GPU, jinak CPU", "auto", codec));
            foreach (var encoder in _hardware?.Encoders.Where(e => e.Codec == codec) ?? [])
                Encoders.Add(encoder);
            SelectedEncoder = Encoders.FirstOrDefault(e => e.Encoder == previous) ?? Encoders[0];
        }
        finally { _updatingSettings = wasUpdating; }
    }

    public async Task InitializeAsync()
    {
        await RecoverTemporaryFilesAsync();
        if (CanDetect && FfmpegService.Instance.Current is { } installation
            && _detectedInstallation != installation.FfmpegPath)
            await DetectAsync();
    }

    public Task RecoverTemporaryFilesAsync() => _recoveryTask ??= RecoverCoreAsync();
    private async Task RecoverCoreAsync()
    {
        try
        {
            var warnings = await ConversionTemporaryFile.RecoverAsync();
            if (warnings.Count > 0) Message = string.Join("\n", warnings);
        }
        catch (Exception ex) { Message = "Obnova úklidu nedokončených převodů selhala: " + ex.Message; }
    }

    [RelayCommand(CanExecute = nameof(CanDetect))]
    private async Task DetectAsync()
    {
        if (!CanDetect) return;
        if (FfmpegService.Instance.Current is not { } installation)
        {
            Message = "Nejprve připravte soukromou instalaci FFmpeg v hlavním okně.";
            return;
        }
        IsDetecting = true;
        using var cancellation = new CancellationTokenSource();
        _detectCancellation = cancellation;
        HardwareSummary = "Zjišťuji CPU, RAM, GPU a ověřuji enkodéry krátkým převodem (nejvýše 12 s na enkodér)…";
        try
        {
            _hardware = await new HardwareDetectionService().DetectAsync(installation, cancellation.Token);
            _detectedInstallation = installation.FfmpegPath;
            HardwareSummary = _hardware.Summary;
            HardwareLog = _hardware.ProbeLog;
            UpdateEncoders();
        }
        catch (OperationCanceledException) { HardwareSummary = "Detekce hardwaru byla zrušena."; }
        catch (Exception ex) { HardwareSummary = "Detekce selhala: " + ex.Message; }
        finally { _detectCancellation = null; IsDetecting = false; }
    }

    public void CancelDetection()
    {
        _detectCancellation?.Cancel();
        _trackCancellation?.Cancel();
    }

    public void AddInputs(IEnumerable<string> paths)
    {
        if (IsShuttingDown) return;
        int added = 0;
        ConversionJob? lastAdded = null;
        string? issue = null;
        foreach (string path in paths)
        {
            try
            {
                if (Jobs.Count >= 5000) { issue = "Fronta má limit 5 000 úloh."; break; }
                string full = Path.GetFullPath(path);
                if (ConversionTemporaryFile.IsInternalPath(full)) continue;
                if (Jobs.Any(existing => existing.InputPath.Equals(full, StringComparison.OrdinalIgnoreCase)
                    && (existing.Status == "Čeká" || (IsRunning && ReferenceEquals(existing, _activeJob))))) continue;
                var input = new ConversionInput { FullPath = full };
                var settings = CreateSettings(input);
                input.PropertyChanged += Input_PropertyChanged;
                Inputs.Add(input);
                var job = new ConversionJob { InputPath = full, Input = input, Settings = settings };
                Jobs.Add(job);
                lastAdded = job;
                added++;
            }
            catch (Exception ex) { issue = "Video nelze zařadit: " + ex.Message; break; }
        }
        if (lastAdded is not null) SelectedJob = lastAdded;
        if (added > 0) Message = $"Automaticky zařazeno videí: {added}. Převod spustíte tlačítkem Spustit převod.";
        if (issue is not null) Message = (added > 0 ? $"Zařazeno videí: {added}.\n" : "") + issue;
    }

    private void Input_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is ConversionInput input
            && (e.PropertyName is nameof(ConversionInput.SelectedVideo) or nameof(ConversionInput.SelectedAudio)))
            UpdateInputSettings(input);
    }

    private void UpdateInputSettings(ConversionInput input)
    {
        if (input.Metadata is null || input.SelectedVideo?.Stream is null || input.SelectedAudio is null) return;
        foreach (var job in Jobs.Where(j => ReferenceEquals(j.Input, input) && j.Status == "Čeká"))
            job.Settings = job.Settings with
            {
                VideoStreamIndex = input.SelectedVideo.Stream.Index,
                AudioStreamIndex = input.SelectedAudio.Stream?.Index,
                AudioDisabled = input.SelectedAudio.Stream is null,
                ExpectedSource = input.Metadata
            };
    }

    public async Task LoadInputTracksAsync(ConversionInput? input, bool refresh = false)
    {
        _trackCancellation?.Cancel();
        if (input is null || IsShuttingDown || (!refresh && input.Metadata is not null)) return;
        if (FfmpegService.Instance.Current is not { } installation)
        {
            input.TrackStatus = "Pro načtení stop nejprve připravte FFmpeg.";
            return;
        }
        using var cancellation = new CancellationTokenSource();
        _trackCancellation = cancellation;
        IsLoadingTracks = true;
        input.TrackStatus = "Načítám video a zvukové stopy…";
        input.LoadError = "";
        try
        {
            var metadata = await Task.Run(async () =>
            {
                VideoConversionService.EnsureSafePath(input.FullPath);
                return await VideoDiagnosticsService.ProbeAsync(input.FullPath, installation, _ => { }, cancellation.Token);
            }, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_trackCancellation, cancellation) || !Inputs.Contains(input)) return;
            input.VideoTracks.Clear();
            input.AudioTracks.Clear();
            foreach (var track in metadata.Streams.Where(s => s.Type == "video" && !s.IsAttachedPicture))
                input.VideoTracks.Add(new($"#{track.Index} • {track.Codec} • {track.Width} × {track.Height}"
                    + (string.IsNullOrWhiteSpace(track.Title) ? "" : $" • {track.Title}"), track));
            input.AudioTracks.Add(new("Bez zvuku", null));
            foreach (var track in metadata.Streams.Where(s => s.Type == "audio"))
                input.AudioTracks.Add(new($"#{track.Index} • {MediaLanguage.Display(track.Language)} • {track.Codec}"
                    + $" • {track.Channels} kanálů"
                    + (track.IsDefault ? " • výchozí" : "")
                    + (string.IsNullOrWhiteSpace(track.Title) ? "" : $" • {track.Title}"), track));
            input.SelectedVideo = input.VideoTracks.FirstOrDefault();
            var audio = MediaLanguage.SelectAudio(metadata.Streams, SystemAudioLanguage);
            input.SelectedAudio = input.AudioTracks.FirstOrDefault(t => t.Stream?.Index == audio?.Index)
                ?? input.AudioTracks[0];
            input.Metadata = metadata with { RawJson = "" };
            UpdateInputSettings(input);
            bool matched = audio is not null && MediaLanguage.Normalize(audio.Language) == SystemAudioLanguage;
            input.TrackStatus = audio is null ? "Soubor nemá zvukovou stopu."
                : matched ? "Předvybrán zvuk v jazyce Windows; výběr lze změnit."
                : "Jazyk Windows nebyl nalezen; předvybrána výchozí nebo první zvuková stopa.";
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_trackCancellation, cancellation) || !ReferenceEquals(SelectedInput, input))
                input.TrackStatus = "Načítání stop bylo přerušeno; vyberte soubor znovu.";
        }
        catch (Exception ex)
        {
            input.LoadError = ex.Message;
            input.TrackStatus = "Načtení stop selhalo: " + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_trackCancellation, cancellation))
            {
                _trackCancellation = null;
                IsLoadingTracks = false;
            }
        }
    }

    [RelayCommand]
    private void ClearWaiting()
    {
        foreach (var job in Jobs.Where(j => j.Status == "Čeká").ToArray()) Remove(job);
    }

    private ConversionSettings CreateSettings(ConversionInput input)
    {
        if (input.Metadata is not null && (input.SelectedVideo?.Stream is null || input.SelectedAudio is null))
            throw new InvalidOperationException($"{input.Name}: počkejte na načtení stop, nebo vyberte video a zvuk.");
        if (!UseSourceDirectory && string.IsNullOrWhiteSpace(OutputDirectory))
            throw new InvalidOperationException("Vyberte cílovou složku, nebo zapněte ukládání ke zdroji.");
        return new(GetPreparedProfile(), SelectedEncoder?.Encoder ?? "auto",
            UseSourceDirectory ? Path.GetDirectoryName(input.FullPath)! : Path.GetFullPath(OutputDirectory),
            SystemAudioLanguage, input.SelectedVideo?.Stream?.Index, input.SelectedAudio?.Stream?.Index,
            input.Metadata is not null && input.SelectedAudio?.Stream is null, input.Metadata,
            Decoder: SelectedDecoder?.HwAccel ?? "cpu", UseSmartName: UseSmartOutputNames,
            PreserveHdr: SelectedHdr.Preserve);
    }

    private void ApplySettingsToWaiting()
    {
        if (_updatingSettings) return;
        try
        {
            // Validate even an empty queue, so invalid manual fields cannot be silently ignored.
            _ = GetPreparedProfile();
            if (SelectedHdr.Preserve && (GetPreparedProfile().Codec != "hevc"
                || SelectedEncoder?.Encoder is not (null or "auto" or "libx265")))
                throw new InvalidOperationException("Zachování HDR vyžaduje HEVC profil a automatický nebo CPU libx265 enkodér.");
            if (!UseSourceDirectory && string.IsNullOrWhiteSpace(OutputDirectory))
                throw new InvalidOperationException("Vyberte cílovou složku, nebo zapněte ukládání ke zdroji.");
            var changes = Jobs.Where(j => j.Status == "Čeká" && (!EditSelectedOnly || ReferenceEquals(j, SelectedJob)))
                .Select(j => (Job: j, Settings: CreateSettings(j.Input ?? new ConversionInput { FullPath = j.InputPath }))).ToArray();
            foreach (var change in changes) change.Job.Settings = change.Settings;
            SettingsError = "";
        }
        catch (Exception ex) { SettingsError = ex.Message; }
    }

    private string ResolveEncoder(ConversionSettings settings)
    {
        if (settings.PreserveHdr && settings.Encoder == "auto") return "libx265";
        if (settings.Encoder != "auto") return settings.Encoder;
        var compatible = _hardware?.Encoders.Where(e => e.Codec == settings.Profile.Codec).ToArray() ?? [];
        return compatible.FirstOrDefault(e => !e.Encoder.StartsWith("lib", StringComparison.Ordinal))?.Encoder
            ?? compatible.FirstOrDefault()?.Encoder
            ?? throw new InvalidOperationException("Pro tento profil není dostupný ověřený enkodér. Ověřte hardware v pokročilých možnostech.");
    }

    public DevicePreset GetPreparedProfile() => IsManual
        ? new("Ruční nastavení", Integer(Width, 128, 7680), Integer(Height, 128, 4320),
            Integer(FrameRate, 1, 120), Integer(VideoKbps, 250, 100000), Integer(AudioKbps, 64, 512), ManualCodec)
        : SelectedPreset;

    private static int Integer(double value, int minimum, int maximum)
    {
        if (!double.IsFinite(value) || value != Math.Truncate(value) || value < minimum || value > maximum)
            throw new ArgumentException($"Hodnota musí být celé číslo v rozsahu {minimum}–{maximum}.");
        return (int)value;
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (!CanStart) return;
        await InitializeAsync();
        if (!CanStart) return;
        if (FfmpegService.Instance.Current is not { } installation)
        { Message = "FFmpeg není připraven."; return; }
        IsRunning = true;
        _stopAfterCurrent = false;
        using var cancellation = new CancellationTokenSource();
        _runCancellation = cancellation;
        var updates = new ConcurrentQueue<ConversionUpdate>();
        var timer = new Microsoft.UI.Xaml.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        ConversionJob? active = null;
        void Drain()
        {
            while (updates.TryDequeue(out var update))
            {
                if (active is null) continue;
                if (update.Stage is "Úklid selhal" or "Protokol")
                {
                    if (update.Stage == "Úklid selhal")
                    {
                        _stopAfterCurrent = true;
                        Message = update.Detail;
                    }
                    active.Detail += "\n" + update.Detail;
                    CurrentDetail = active.Detail;
                    continue;
                }
                active.Status = update.Stage;
                active.Detail = update.Detail;
                if (update.LogPath is not null) active.LogPath = update.LogPath;
                if (update.OutputPath is not null) active.OutputPath = update.OutputPath;
                if (update.LogTail is not null) active.LogTail = update.LogTail;
                CurrentLogTail = active.LogTail;
                active.IsIndeterminate = update.Percent is null;
                if (update.Percent is { } percent) active.Percent = percent;
                CurrentTask = active.Name + " • " + active.Status;
                CurrentDetail = active.Detail;
                CurrentIndeterminate = active.IsIndeterminate;
                CurrentPercent = active.Percent;
            }
        }
        timer.Tick += (_, _) => Drain();
        timer.Start();
        try
        {
            QueueStarted?.Invoke();
            while (!_stopAfterCurrent && !cancellation.IsCancellationRequested)
            {
                active = Jobs.FirstOrDefault(j => j.Status == "Čeká");
                if (active is null) break;
                active.Status = "Příprava";
                _activeJob = active;
                active.IsIndeterminate = true;
                SelectedJob = active;
                OnPropertyChanged(nameof(CanEditSelected));
                OnPropertyChanged(nameof(CanEditSettings));
                OnPropertyChanged(nameof(SettingsScopeDescription));
                CurrentTask = active.Name;
                CurrentDetail = "Připravuji převod.";
                CurrentLogTail = "";
                CurrentPercent = 0;
                CurrentIndeterminate = true;
                try
                {
                    active.Settings = active.Settings with { Encoder = ResolveEncoder(active.Settings) };
                    await _service.ConvertAsync(active, installation,
                        new InlineProgress<ConversionUpdate>(updates.Enqueue), cancellation.Token);
                }
                catch (OperationCanceledException) { active.Status = "Zrušeno"; }
                catch (Exception ex) { active.Status = "Chyba"; active.Detail = ex.Message; }
                finally
                {
                    Drain(); // Synchronous producer has finished; no late updates can reach the next job.
                    active.IsIndeterminate = false;
                    CurrentIndeterminate = false;
                    CurrentTask = active.Name + " • " + active.Status;
                    CurrentDetail = active.Detail;
                    UpdateSummary();
                }
            }
        }
        catch (Exception ex) { Message = "Fronta byla zastavena: " + ex.Message; }
        finally
        {
            timer.Stop();
            Drain();
            _runCancellation = null;
            _activeJob = null;
            IsRunning = false;
            UpdateSummary();
        }
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void StopAfterCurrent()
    {
        _stopAfterCurrent = true;
        Message = "Fronta se zastaví po dokončení aktuální úlohy. Ostatní zůstanou čekat.";
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel()
    {
        _stopAfterCurrent = true;
        _runCancellation?.Cancel();
        Message = "Ruším aktuální převod a zastavuji frontu; čekající úlohy zůstanou zachované.";
    }

    [RelayCommand]
    private void Remove(ConversionJob? job)
    {
        if (job is null) return;
        if (ReferenceEquals(job, _activeJob)) { Message = "Běžící úlohu nelze odebrat."; return; }
        Jobs.Remove(job);
        if (job.Input is { } input)
        {
            input.PropertyChanged -= Input_PropertyChanged;
            Inputs.Remove(input);
        }
        if (ReferenceEquals(SelectedJob, job)) SelectedJob = Jobs.FirstOrDefault();
    }

    [RelayCommand]
    private void Retry(ConversionJob? job)
    {
        if (job is null || IsRunning || Jobs.Count >= 5000) return;
        var input = new ConversionInput
        {
            FullPath = job.InputPath, Metadata = job.Input?.Metadata,
            SelectedVideo = job.Input?.SelectedVideo, SelectedAudio = job.Input?.SelectedAudio,
            TrackStatus = job.Input?.TrackStatus ?? "Zvuk se zvolí automaticky podle jazyka Windows."
        };
        foreach (var track in job.Input?.VideoTracks ?? []) input.VideoTracks.Add(track);
        foreach (var track in job.Input?.AudioTracks ?? []) input.AudioTracks.Add(track);
        input.PropertyChanged += Input_PropertyChanged;
        Inputs.Add(input);
        var copy = new ConversionJob { InputPath = job.InputPath, Settings = job.Settings, Input = input };
        Jobs.Add(copy);
        SelectedJob = copy;
        Message = "Nová úloha byla přidána se stejným nastavením. Enkodér lze změnit při přípravě nové úlohy.";
    }

    private void UpdateSummary()
    {
        QueueSummary = $"Úloh: {Jobs.Count} • čeká: {Jobs.Count(j => j.Status == "Čeká")}"
            + $" • dokončeno: {Jobs.Count(j => j.Status.StartsWith("Dokončeno", StringComparison.Ordinal))}"
            + $" • chyb: {Jobs.Count(j => j.Status == "Chyba")} • zrušeno: {Jobs.Count(j => j.Status == "Zrušeno")}";
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(NextStep));
        OnPropertyChanged(nameof(CanEditSelected));
        OnPropertyChanged(nameof(CanEditSettings));
        OnPropertyChanged(nameof(SettingsScopeDescription));
        StartCommand.NotifyCanExecuteChanged();
    }
}
