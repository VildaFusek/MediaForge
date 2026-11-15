using CommunityToolkit.Mvvm.ComponentModel;
using MediaForge.Models;
using MediaForge.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using System.Collections.Concurrent;

namespace MediaForge.ViewModels;

public partial class FileExplorerViewModel
{
    private readonly VideoDiagnosticsRepository _catalog = new();
    private readonly VideoDiagnosticsService _diagnostics = new();
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private CancellationTokenSource? _analysisCancellation;
    private Task _analysisTask = Task.CompletedTask;
    private int _analysisGeneration;
    private int _analysisRestartRequest;
    private FileSystemWatcher? _directoryWatcher;
    private DispatcherQueueTimer? _refreshTimer;
    private int _watchGeneration;
    private readonly List<(string Source, string? Destination)> _externalChanges = [];
    private bool _interactionActive;
    private bool _refreshRequested;
    public bool IsInteractionActive
    {
        get => _interactionActive;
        set
        {
            _interactionActive = value;
            if (!value && _refreshRequested) _refreshTimer?.Start();
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AnalysisVisibility))]
    public partial bool IsAnalyzing { get; set; }
    [ObservableProperty] public partial string AnalysisStatus { get; set; } = "Analýza čeká na FFprobe";
    [ObservableProperty] public partial double AnalysisProgress { get; set; }
    [ObservableProperty] public partial bool AnalysisIndeterminate { get; set; }
    public Visibility AnalysisVisibility => IsAnalyzing ? Visibility.Visible : Visibility.Collapsed;
    private bool _analysisPaused;

    public async Task StopBackgroundAnalysisAsync()
    {
        ++_analysisRestartRequest;
        await StopAnalysisCoreAsync();
    }

    private async Task StopAnalysisCoreAsync()
    {
        _metadataFilterTimer?.Stop();
        int generation = ++_analysisGeneration;
        var cancellation = _analysisCancellation;
        var task = _analysisTask;
        _analysisCancellation = null;
        cancellation?.Cancel();
        await task;
        if (generation == _analysisGeneration) IsAnalyzing = false;
    }

    public async Task RestartBackgroundAnalysisAsync()
    {
        int request = ++_analysisRestartRequest;
        await StopAnalysisCoreAsync();
        NotifyState();
        if (request != _analysisRestartRequest || _analysisPaused || !IsIdle || string.IsNullOrEmpty(CurrentPath)) return;
        var cancellation = new CancellationTokenSource();
        _analysisCancellation = cancellation;
        int generation = ++_analysisGeneration;
        string folder = CurrentPath;
        var snapshot = _allItems.Where(item => !item.IsDirectory && !item.IsLink).ToArray();
        _analysisTask = AnalyzeDirectoryAsync(folder, snapshot, generation, cancellation);
    }

    public async Task PauseBackgroundAnalysisAsync()
    {
        _analysisPaused = true;
        await StopBackgroundAnalysisAsync();
    }

    public async Task ResumeBackgroundAnalysisAsync()
    {
        _analysisPaused = false;
        await RestartBackgroundAnalysisAsync();
    }

    private async Task AnalyzeDirectoryAsync(
        string folder, FileSystemItem[] snapshot, int generation, CancellationTokenSource cancellation)
    {
        var pending = new ConcurrentQueue<VideoDiagnosticProgress>();
        var displayedByPath = snapshot.ToDictionary(item => item.FullPath, StringComparer.OrdinalIgnoreCase);
        bool IsCurrent() => generation == _analysisGeneration && !cancellation.IsCancellationRequested;
        void Drain()
        {
            bool metadataChanged = false;
            while (pending.TryDequeue(out var update))
            {
                if (!IsCurrent()) continue;
                AnalysisStatus = $"Analýza: {update.Completed}/{update.Total} • chyby {update.Failed} • {update.Detail}";
                AnalysisProgress = update.Total > 0 ? update.Completed * 100d / update.Total : 0;
                AnalysisIndeterminate = update.Discovering;
                if (update.Record is { } record)
                {
                    var item = displayedByPath.GetValueOrDefault(record.FilePath);
                    if (item is not null) item.Analysis = record;
                    metadataChanged = true;
                    OnPropertyChanged(nameof(DetailsText));
                }
            }
            if (metadataChanged) ScheduleMetadataFilterRefresh();
        }
        var dispatch = new Progress<bool>(_ => { if (IsCurrent()) Drain(); });
        var progress = new InlineProgress<VideoDiagnosticProgress>(update =>
        {
            pending.Enqueue(update);
            ((IProgress<bool>)dispatch).Report(true);
        });
        try
        {
            IsAnalyzing = true;
            AnalysisIndeterminate = true;
            AnalysisStatus = "Ověřuji uloženou analýzu…";
            var cached = await Task.Run(() => _catalog.LoadDirectory(folder, cancellation.Token), cancellation.Token);
            if (!IsCurrent()) return;
            var byPath = cached.ToDictionary(item => item.FilePath, StringComparer.OrdinalIgnoreCase);
            foreach (var item in snapshot)
                item.Analysis = byPath.GetValueOrDefault(item.FullPath);
            ApplyView(incremental: true);
            OnPropertyChanged(nameof(DetailsText));
            var inputs = snapshot.Where(item => !item.IsAnalyzed).Select(item => item.FullPath).ToArray();
            var installation = FfmpegService.Instance.Current;
            if (installation is null)
            {
                AnalysisStatus = inputs.Length > 0 ? "Automatická analýza čeká na instalaci FFprobe."
                    : ShowThumbnails ? "Miniatury čekají na instalaci FFmpeg." : "Videa v této složce jsou analyzována.";
                return;
            }
            // Give navigation and foreground file operations priority.
            await Task.Delay(400, cancellation.Token);
            if (!IsCurrent()) return;
            if (inputs.Length > 0)
            {
                var summary = await _diagnostics.DiagnoseAsync(inputs, false, installation, progress, cancellation.Token);
                Drain();
                if (IsCurrent())
                    AnalysisStatus = $"Analýza dokončena: {summary.Saved}/{summary.Total} • chyby {summary.Failed}.";
                await Task.Run(() => AppLog.Information(
                    $"Analýza {folder}: připraveno {summary.Saved}/{summary.Total}, chyby {summary.Failed}."));
            }
            else AnalysisStatus = "Videa v této složce jsou analyzována.";
            if (ShowThumbnails && IsCurrent())
                await LoadThumbnailsAsync(snapshot, installation, generation, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (IsCurrent()) AnalysisStatus = "Analýza: " + ex.Message;
            await Task.Run(() => AppLog.Error("Automatická analýza " + folder, ex));
        }
        finally
        {
            Drain();
            if (generation == _analysisGeneration)
            {
                IsAnalyzing = false;
                _analysisCancellation = null;
            }
            cancellation.Dispose();
        }
    }

    private async Task RunCatalogMutationAsync(
        string source, string? destination, Func<Task> action, Action? onFileCompleted = null)
    {
        await StopPreviewAsync(source);
        await StopBackgroundAnalysisAsync();
        using var mutationLock = await Task.Run(() => _catalog.AcquireMutationLock());
        var change = await Task.Run(() => _catalog.PrepareChange(source, destination));
        Exception? fileError = null;
        try { await action(); }
        catch (Exception ex) { fileError = ex; }
        if (fileError is null) onFileCompleted?.Invoke();
        try { await Task.Run(() => _catalog.FinishChange(change, fileError is null)); }
        catch (Exception ex)
        {
            throw new IOException(
                (fileError is null ? "Souborová akce byla provedena, ale aktualizace DB selhala. "
                    : $"Souborová akce nebyla dokončena ({fileError.Message}) a aktualizace DB také selhala. ")
                + "Propojení metadat není dokončeno; "
                + "záměr operace zůstal uložen pro opravu při dalším načtení složky.",
                fileError is null ? ex : new AggregateException(fileError, ex));
        }
        if (fileError is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(fileError).Throw();
    }

    private void WatchDirectory(string folder)
    {
        StopDirectoryWatcher();
        int generation = _watchGeneration;
        try
        {
            _refreshTimer ??= _dispatcher.CreateTimer();
            _refreshTimer.Interval = TimeSpan.FromMilliseconds(700);
            _refreshTimer.IsRepeating = false;
            _refreshTimer.Tick -= RefreshTimer_Tick;
            _refreshTimer.Tick += RefreshTimer_Tick;
            _directoryWatcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                    | NotifyFilters.LastWrite | NotifyFilters.Size
            };
            void Changed(string? path = null, string? source = null, string? destination = null)
            {
                if (path is not null && ConversionTemporaryFile.IsInternalPath(path)
                    && (source is null || ConversionTemporaryFile.IsInternalPath(source))) return;
                if (source is not null && ConversionTemporaryFile.IsInternalPath(source)) source = null;
                bool relevant = path is null || VideoDiagnosticsService.IsVideoFile(path)
                    || (source is not null && VideoDiagnosticsService.IsVideoFile(source))
                    || Directory.Exists(path);
                _dispatcher.TryEnqueue(() =>
                {
                    if (generation != _watchGeneration || !IsIdle) return;
                    if (!relevant && !_allItems.Any(item => item.IsDirectory
                        && (string.Equals(item.FullPath, path, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(item.FullPath, source, StringComparison.OrdinalIgnoreCase)))) return;
                    if (source is not null) _externalChanges.Add((source, destination));
                    _refreshRequested = true;
                    _refreshTimer?.Start();
                });
            }
            _directoryWatcher.Created += (_, e) => Changed(e.FullPath);
            _directoryWatcher.Changed += (_, e) => Changed(e.FullPath);
            _directoryWatcher.Deleted += (_, e) => Changed(e.FullPath, e.FullPath);
            _directoryWatcher.Renamed += (_, e) => Changed(e.FullPath, e.OldFullPath, e.FullPath);
            _directoryWatcher.Error += (_, e) =>
            {
                _ = Task.Run(() => AppLog.Error("Sledování složky", e.GetException()));
                Changed();
            };
            _directoryWatcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) { _ = Task.Run(() => AppLog.Error("Sledování složky " + folder, ex)); }
    }

    private async void RefreshTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (IsIdle && !_analysisPaused && !IsInteractionActive)
        {
            _refreshRequested = false;
            await RefreshAsync();
        }
    }

    private void StopDirectoryWatcher()
    {
        ++_watchGeneration;
        _refreshTimer?.Stop();
        _directoryWatcher?.Dispose();
        _directoryWatcher = null;
    }

    private async Task ReconcileExternalChangesAsync()
    {
        var changes = _externalChanges.ToArray();
        _externalChanges.Clear();
        if (changes.Length == 0) return;
        try
        {
            await Task.Run(() =>
            {
                using var mutationLock = _catalog.AcquireMutationLock();
                foreach (var (source, destination) in changes)
                {
                    try
                    {
                        if (destination is null) _catalog.RemoveMissingTree(source);
                        else _catalog.ReconcileExternalRename(source, destination);
                    }
                    catch (Exception ex) { AppLog.Error("Změna souboru mimo MediaForge " + source, ex); }
                }
            });
        }
        catch (Exception ex)
        {
            _externalChanges.InsertRange(0, changes);
            await Task.Run(() => AppLog.Error("Synchronizace změn složky", ex));
        }
    }

    public async Task ShutdownAnalysisAsync()
    {
        _isShuttingDown = true;
        NotifyState();
        _analysisPaused = true;
        StopDirectoryWatcher();
        try
        {
            await StopBackgroundAnalysisAsync();
            await StopPreviewAsync();
        }
        catch
        {
            _isShuttingDown = false;
            _analysisPaused = false;
            if (!string.IsNullOrEmpty(CurrentPath)) WatchDirectory(CurrentPath);
            await RestartBackgroundAnalysisAsync();
            throw;
        }
    }
}
