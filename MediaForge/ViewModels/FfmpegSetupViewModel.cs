using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaForge.Models;
using MediaForge.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using System.Collections.Concurrent;

namespace MediaForge.ViewModels;

public partial class FfmpegSetupViewModel : ObservableObject
{
    private readonly FfmpegService _service = FfmpegService.Instance;
    private CancellationTokenSource? _cancellation;
    private FfmpegRelease? _latest;
    private bool _initialized;
    private int _generation;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCheck))]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    [NotifyPropertyChangedFor(nameof(IndicatorText))]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IndicatorText))]
    [NotifyPropertyChangedFor(nameof(IndicatorBrush))]
    public partial bool IsReady { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IndicatorText))]
    [NotifyPropertyChangedFor(nameof(IndicatorBrush))]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    public partial bool HasUpdate { get; set; }

    [ObservableProperty] public partial string InstalledVersion { get; set; } = "Nenainstalováno";
    [ObservableProperty] public partial string LatestVersion { get; set; } = "Nezjištěna";
    [ObservableProperty] public partial string Stage { get; set; } = "Čeká na kontrolu";
    [ObservableProperty] public partial string Detail { get; set; } = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PercentText))]
    public partial double Percent { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PercentText))]
    public partial bool IsIndeterminate { get; set; }
    [ObservableProperty] public partial string LogText { get; set; } = "";
    public string RuntimeDirectory => _service.RuntimeDirectory;
    public string PercentText => IsIndeterminate ? "" : $"{Percent:F0} %";
    public bool CanCheck => !IsBusy;
    public bool CanInstall => !IsBusy && _latest is not null && (!IsReady || HasUpdate);
    public bool ShouldOfferSetup => !IsBusy && (!IsReady || HasUpdate);
    public string IndicatorText => IsBusy ? "FFmpeg: probíhá kontrola / instalace"
        : IsReady ? HasUpdate ? "FFmpeg: připraven • dostupná aktualizace" : "FFmpeg: připraven"
        : "FFmpeg: není připraven";
    public SolidColorBrush IndicatorBrush => new(IsReady ? HasUpdate ? Colors.Orange : Colors.ForestGreen : Colors.Gray);

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;
        await CheckAsync();
    }

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private async Task CheckAsync()
    {
        if (IsBusy) return;
        await RunAsync(async (progress, token) =>
        {
            _latest = null;
            HasUpdate = false;
            Stage = "Místní instalace";
            Detail = "Kontroluji dostupnost nástrojů.";
            try
            {
                var installed = await _service.LoadInstalledAsync(progress, token);
                IsReady = installed is not null;
                InstalledVersion = installed?.Version ?? "Nenainstalováno";
                AppendLog(installed is null ? "Místní instalace nebyla nalezena." : $"Místní verze {installed.Version}: SHA-256 souhlasí.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                IsReady = false;
                InstalledVersion = "Poškozená nebo nedostupná instalace";
                AppendLog("Místní instalace: " + ex.Message);
            }
            Stage = "Kontrola aktualizací";
            Detail = "Zjišťuji nejnovější stabilní verzi z Gyan.dev…";
            LatestVersion = "Zjišťuji…";
            _latest = await _service.GetLatestAsync(token);
            LatestVersion = _latest.Version;
            HasUpdate = IsReady && (_service.Current?.Version != _latest.Version
                || _service.Current?.FfplayPath is null
                || !string.Equals(_service.Current?.PackageSha256, _latest.Sha256, StringComparison.OrdinalIgnoreCase));
            Stage = IsReady ? HasUpdate ? "Dostupná aktualizace" : "Nástroje jsou připraveny" : "Nabídka instalace";
            Detail = CanOfferText();
            AppendLog($"Stabilní verze: {_latest.Version}. {Detail}");
        });
    }

    private string CanOfferText() => !IsReady
        ? "Zvolte „Stáhnout a nainstalovat“. Následující kroky proběhnou automaticky."
        : HasUpdate ? "Je dostupný nový balíček nebo doplnění FFplay. Při neúspěchu zůstane původní verze zachována."
        : "Je nainstalován aktuální stabilní balíček.";

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        if (!CanInstall || _latest is not { } release) return;
        await RunAsync(async (progress, token) =>
        {
            var installed = await _service.InstallAsync(release, progress, token);
            IsReady = true;
            InstalledVersion = installed.Version;
            HasUpdate = false;
            Stage = "Instalace úspěšně dokončena";
            Detail = $"FFmpeg, FFprobe a FFplay {installed.Version} jsou připraveny k použití.";
            Percent = 100;
            AppendLog(Detail);
        });
    }

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel()
    {
        _cancellation?.Cancel();
        Detail = "Ruším operaci a uklízím pracovní soubory…";
    }

    private async Task RunAsync(Func<IProgress<FfmpegSetupProgress>, CancellationToken, Task> operation)
    {
        if (IsBusy) return;
        IsBusy = true;
        IsIndeterminate = true;
        Percent = 0;
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        int generation = ++_generation;
        var pending = new ConcurrentQueue<FfmpegSetupProgress>();
        void Drain(bool updateState)
        {
            while (pending.TryDequeue(out var update))
            {
                if (updateState)
                {
                    Stage = update.Stage;
                    Detail = update.Detail;
                    Percent = update.Percent;
                    IsIndeterminate = update.Indeterminate;
                }
                if (update.WriteLog) AppendLog($"[{update.Stage}] {update.Detail}");
            }
        }
        var dispatch = new Progress<bool>(_ =>
        {
            if (generation == _generation && IsBusy) Drain(true);
        });
        var progress = new InlineProgress<FfmpegSetupProgress>(update =>
        {
            pending.Enqueue(update);
            ((IProgress<bool>)dispatch).Report(true);
        });
        try { await operation(progress, cancellation.Token); }
        catch (OperationCanceledException)
        {
            Stage = cancellation.IsCancellationRequested ? "Operace zrušena" : "Vypršel časový limit";
            Detail = IsReady ? "Původní instalace zůstává dostupná." : "Nástroje dosud nejsou připraveny.";
            AppendLog($"{Stage}. {Detail}");
        }
        catch (Exception ex)
        {
            Stage = "Operace se nezdařila";
            Detail = ex.Message;
            if (LatestVersion == "Zjišťuji…") LatestVersion = "Kontrola se nezdařila";
            AppendLog($"CHYBA: {ex.Message}");
            AppLog.Error("FFmpeg setup", ex);
        }
        finally
        {
            // Preserve cleanup warnings even if their UI callbacks arrived after completion.
            Drain(false);
            ++_generation;
            _cancellation = null;
            IsIndeterminate = false;
            IsBusy = false;
            OnPropertyChanged(nameof(CanInstall));
            InstallCommand.NotifyCanExecuteChanged();
        }
    }

    private void AppendLog(string message)
    {
        string text = LogText + $"{DateTime.Now:HH:mm:ss} {message}{Environment.NewLine}";
        // The complete installation log is on disk; keep the UI bounded.
        if (text.Length > 60000)
        {
            int start = text.IndexOf('\n', text.Length - 50000);
            text = start >= 0 ? text[(start + 1)..] : text[^50000..];
        }
        LogText = text;
    }
}
