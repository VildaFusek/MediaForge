using CommunityToolkit.Mvvm.ComponentModel;
using MediaForge.Models;
using MediaForge.Services;

namespace MediaForge.ViewModels;

public partial class FileExplorerViewModel
{
    private readonly VideoThumbnailService _thumbnails = new();
    [ObservableProperty] public partial bool ShowThumbnails { get; set; }

    partial void OnShowThumbnailsChanged(bool value) => _ = RefreshThumbnailModeAsync();

    private async Task RefreshThumbnailModeAsync()
    {
        try { await RestartBackgroundAnalysisAsync(); }
        catch (Exception ex) { await Task.Run(() => AppLog.Error("Změna režimu miniatur", ex)); }
    }

    private async Task LoadThumbnailsAsync(
        FileSystemItem[] snapshot, FfmpegInstallation installation, int generation, CancellationToken token)
    {
        var videos = snapshot.Where(item => item.Analysis is not null).ToArray();
        int completed = 0, failures = 0;
        AnalysisIndeterminate = false;
        foreach (var item in videos)
        {
            token.ThrowIfCancellationRequested();
            AnalysisProgress = videos.Length > 0 ? completed * 100d / videos.Length : 0;
            AnalysisStatus = $"Miniatury: {completed}/{videos.Length} • {item.Name}";
            try
            {
                string uri = await _thumbnails.GetThumbnailAsync(item.Analysis!, installation, token);
                if (generation != _analysisGeneration || !ShowThumbnails) return;
                item.ThumbnailUri = uri;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                failures++;
                await Task.Run(() => AppLog.Error("Miniatura " + item.FullPath, ex));
            }
            completed++;
        }
        token.ThrowIfCancellationRequested();
        AnalysisProgress = 100;
        AnalysisStatus = $"Miniatury: {completed - failures}/{videos.Length} připraveno • chyby {failures}.";
    }
}
