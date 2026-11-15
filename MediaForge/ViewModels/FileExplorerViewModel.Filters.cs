using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaForge.Models;

namespace MediaForge.ViewModels;

public partial class FileExplorerViewModel
{
    private bool _resettingFilters;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _metadataFilterTimer;
    [ObservableProperty] public partial int AnalysisFilterIndex { get; set; }
    [ObservableProperty] public partial string VideoCodecFilter { get; set; } = "";
    [ObservableProperty] public partial string ContainerFilter { get; set; } = "";
    [ObservableProperty] public partial double MinimumHeight { get; set; } = double.NaN;
    [ObservableProperty] public partial double MaximumHeight { get; set; } = double.NaN;
    [ObservableProperty] public partial double MinimumDurationMinutes { get; set; } = double.NaN;
    [ObservableProperty] public partial double MaximumDurationMinutes { get; set; } = double.NaN;
    [ObservableProperty] public partial double MinimumBitRateMbps { get; set; } = double.NaN;
    [ObservableProperty] public partial double MaximumBitRateMbps { get; set; } = double.NaN;
    [ObservableProperty] public partial int FrameRateFilterIndex { get; set; }
    [ObservableProperty] public partial int AudioFilterIndex { get; set; }
    [ObservableProperty] public partial int SubtitleFilterIndex { get; set; }

    public string MetadataFilterHint => InvalidRange(MinimumHeight, MaximumHeight)
        || InvalidRange(MinimumDurationMinutes, MaximumDurationMinutes)
        || InvalidRange(MinimumBitRateMbps, MaximumBitRateMbps)
        ? "Dolní mez nesmí být větší než horní mez."
        : "Filtry platí pro otevřenou složku. Složky zůstávají viditelné; videa bez potřebných údajů se při filtrování skryjí a mohou přibývat během analýzy.";

    partial void OnAnalysisFilterIndexChanged(int value) => MetadataFiltersChanged();
    partial void OnVideoCodecFilterChanged(string value) => MetadataFiltersChanged();
    partial void OnContainerFilterChanged(string value) => MetadataFiltersChanged();
    partial void OnMinimumHeightChanged(double value) => MetadataFiltersChanged();
    partial void OnMaximumHeightChanged(double value) => MetadataFiltersChanged();
    partial void OnMinimumDurationMinutesChanged(double value) => MetadataFiltersChanged();
    partial void OnMaximumDurationMinutesChanged(double value) => MetadataFiltersChanged();
    partial void OnMinimumBitRateMbpsChanged(double value) => MetadataFiltersChanged();
    partial void OnMaximumBitRateMbpsChanged(double value) => MetadataFiltersChanged();
    partial void OnFrameRateFilterIndexChanged(int value) => MetadataFiltersChanged();
    partial void OnAudioFilterIndexChanged(int value) => MetadataFiltersChanged();
    partial void OnSubtitleFilterIndexChanged(int value) => MetadataFiltersChanged();

    private void MetadataFiltersChanged()
    {
        if (_resettingFilters) return;
        OnPropertyChanged(nameof(MetadataFilterHint));
        ApplyView(incremental: true);
    }

    private bool HasMetadataFilters => AnalysisFilterIndex != 0
        || !string.IsNullOrWhiteSpace(VideoCodecFilter) || !string.IsNullOrWhiteSpace(ContainerFilter)
        || HasRange(MinimumHeight, MaximumHeight) || HasRange(MinimumDurationMinutes, MaximumDurationMinutes)
        || HasRange(MinimumBitRateMbps, MaximumBitRateMbps)
        || FrameRateFilterIndex != 0 || AudioFilterIndex != 0 || SubtitleFilterIndex != 0;

    private void ScheduleMetadataFilterRefresh()
    {
        if (!HasMetadataFilters) return;
        if (_metadataFilterTimer is null)
        {
            _metadataFilterTimer = _dispatcher.CreateTimer();
            _metadataFilterTimer.Interval = TimeSpan.FromMilliseconds(200);
            _metadataFilterTimer.IsRepeating = false;
            _metadataFilterTimer.Tick += (_, _) => { if (IsIdle) ApplyView(incremental: true); };
        }
        if (!_metadataFilterTimer.IsRunning) _metadataFilterTimer.Start();
    }

    private bool MatchesMetadataFilters(FileSystemItem item)
    {
        if (item.IsDirectory) return true;
        if (AnalysisFilterIndex == 1 && !item.IsAnalyzed || AnalysisFilterIndex == 2 && item.IsAnalyzed) return false;
        bool needsMetadata = !string.IsNullOrWhiteSpace(VideoCodecFilter) || !string.IsNullOrWhiteSpace(ContainerFilter)
            || HasRange(MinimumHeight, MaximumHeight) || HasRange(MinimumDurationMinutes, MaximumDurationMinutes)
            || HasRange(MinimumBitRateMbps, MaximumBitRateMbps)
            || FrameRateFilterIndex != 0 || AudioFilterIndex != 0 || SubtitleFilterIndex != 0;
        if (!needsMetadata) return true;
        if (item.Analysis is not { } data) return false;
        return data.VideoCodec.Contains(VideoCodecFilter.Trim(), StringComparison.OrdinalIgnoreCase)
            && data.Container.Contains(ContainerFilter.Trim(), StringComparison.OrdinalIgnoreCase)
            && InRange(data.Height, MinimumHeight, MaximumHeight)
            && InRange(data.DurationSeconds / 60, MinimumDurationMinutes, MaximumDurationMinutes)
            && InRange(data.BitRate / 1000000d, MinimumBitRateMbps, MaximumBitRateMbps)
            && (FrameRateFilterIndex == 0 || data.FrameRate is { } fps && (FrameRateFilterIndex switch
            {
                1 => fps <= 30.01,
                2 => fps > 30.01,
                3 => fps >= 49.9,
                _ => false
            }))
            && (AudioFilterIndex == 0 || (AudioFilterIndex == 1 ? data.AudioTracks > 0 : data.AudioTracks == 0))
            && (SubtitleFilterIndex == 0 || (SubtitleFilterIndex == 1 ? data.SubtitleTracks > 0 : data.SubtitleTracks == 0));
    }

    private static bool HasRange(double minimum, double maximum) => double.IsFinite(minimum) || double.IsFinite(maximum);
    private static bool InvalidRange(double minimum, double maximum) =>
        double.IsFinite(minimum) && double.IsFinite(maximum) && minimum > maximum;
    private static bool InRange(double? value, double minimum, double maximum) =>
        !HasRange(minimum, maximum) || value is { } number
            && (!double.IsFinite(minimum) || number >= minimum)
            && (!double.IsFinite(maximum) || number <= maximum);

    [RelayCommand]
    private void ResetMetadataFilters()
    {
        _resettingFilters = true;
        try
        {
            AnalysisFilterIndex = FrameRateFilterIndex = AudioFilterIndex = SubtitleFilterIndex = 0;
            VideoCodecFilter = ContainerFilter = "";
            MinimumHeight = MaximumHeight = MinimumDurationMinutes = MaximumDurationMinutes =
                MinimumBitRateMbps = MaximumBitRateMbps = double.NaN;
        }
        finally { _resettingFilters = false; }
        MetadataFiltersChanged();
    }
}
