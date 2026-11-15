using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaForge.Models;
using System.Collections.ObjectModel;

namespace MediaForge.ViewModels;

public sealed record MetadataTextOption(string Label, string Value);
public sealed record MetadataNumberOption(string Label, double Value);

public partial class FileExplorerViewModel
{
    private bool _resettingFilters;
    private bool _refreshingFilterOptions;
    public ObservableCollection<MetadataTextOption> VideoCodecOptions { get; } = [new("Všechny kodeky", "")];
    public ObservableCollection<MetadataTextOption> ContainerOptions { get; } = [new("Všechny kontejnery", "")];
    public ObservableCollection<MetadataNumberOption> HeightOptions { get; } = [new("Bez omezení", double.NaN)];
    public ObservableCollection<MetadataNumberOption> DurationOptions { get; } = [new("Bez omezení", double.NaN)];
    public ObservableCollection<MetadataNumberOption> BitRateOptions { get; } = [new("Bez omezení", double.NaN)];
    public ObservableCollection<MetadataNumberOption> FrameRateOptions { get; } = [new("Libovolné FPS", double.NaN)];
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
    [ObservableProperty] public partial double FrameRateFilter { get; set; } = double.NaN;
    [ObservableProperty] public partial int AudioFilterIndex { get; set; }
    [ObservableProperty] public partial int SubtitleFilterIndex { get; set; }

    public string MetadataFilterHint => InvalidRange(MinimumHeight, MaximumHeight)
        || InvalidRange(MinimumDurationMinutes, MaximumDurationMinutes)
        || InvalidRange(MinimumBitRateMbps, MaximumBitRateMbps)
        ? "Dolní mez nesmí být větší než horní mez."
        : "Nabídky se doplňují z analýzy videí v aktuální složce podle hledaného názvu a zobrazení skrytých položek. Další filtry nabídky nezužují. Složky zůstávají viditelné; videa bez potřebných údajů se při filtrování skryjí.";

    partial void OnAnalysisFilterIndexChanged(int value) => MetadataFiltersChanged();
    partial void OnVideoCodecFilterChanged(string value) => MetadataFiltersChanged();
    partial void OnContainerFilterChanged(string value) => MetadataFiltersChanged();
    partial void OnMinimumHeightChanged(double value) => MetadataFiltersChanged();
    partial void OnMaximumHeightChanged(double value) => MetadataFiltersChanged();
    partial void OnMinimumDurationMinutesChanged(double value) => MetadataFiltersChanged();
    partial void OnMaximumDurationMinutesChanged(double value) => MetadataFiltersChanged();
    partial void OnMinimumBitRateMbpsChanged(double value) => MetadataFiltersChanged();
    partial void OnMaximumBitRateMbpsChanged(double value) => MetadataFiltersChanged();
    partial void OnFrameRateFilterChanged(double value) => MetadataFiltersChanged();
    partial void OnAudioFilterIndexChanged(int value) => MetadataFiltersChanged();
    partial void OnSubtitleFilterIndexChanged(int value) => MetadataFiltersChanged();

    private void MetadataFiltersChanged()
    {
        if (_resettingFilters || _refreshingFilterOptions) return;
        OnPropertyChanged(nameof(MetadataFilterHint));
        ApplyView(incremental: true);
    }

    private void ScheduleMetadataFilterRefresh()
    {
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
            || double.IsFinite(FrameRateFilter) || AudioFilterIndex != 0 || SubtitleFilterIndex != 0;
        if (!needsMetadata) return true;
        if (item.Analysis is not { } data) return false;
        return (string.IsNullOrEmpty(VideoCodecFilter) || string.Equals(data.VideoCodec, VideoCodecFilter, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrEmpty(ContainerFilter) || string.Equals(data.Container, ContainerFilter, StringComparison.OrdinalIgnoreCase))
            && InRange(data.Height, MinimumHeight, MaximumHeight)
            && InRange(data.DurationSeconds / 60, MinimumDurationMinutes, MaximumDurationMinutes)
            && InRange(data.BitRate / 1000000d, MinimumBitRateMbps, MaximumBitRateMbps)
            && (!double.IsFinite(FrameRateFilter) || data.FrameRate is { } fps && Math.Round(fps, 3) == FrameRateFilter)
            && (AudioFilterIndex == 0 || (AudioFilterIndex == 1 ? data.AudioTracks > 0 : data.AudioTracks == 0))
            && (SubtitleFilterIndex == 0 || (SubtitleFilterIndex == 1 ? data.SubtitleTracks > 0 : data.SubtitleTracks == 0));
    }

    private static bool HasRange(double minimum, double maximum) => double.IsFinite(minimum) || double.IsFinite(maximum);

    private void RefreshMetadataFilterOptions()
    {
        if (_refreshingFilterOptions) return;
        _refreshingFilterOptions = true;
        try
        {
            // Do not derive choices from metadata-filtered Items: an active filter must
            // not hide the other available choices or disappear as analysis progresses.
            var records = _allItems.Where(i => !i.IsDirectory && !i.IsLink && (ShowHidden || !i.IsHidden)
                    && i.Name.Contains(SearchText.Trim(), StringComparison.CurrentCultureIgnoreCase))
                .Select(i => i.Analysis).OfType<VideoDiagnosticRecord>().ToArray();
            UpdateTextOptions(VideoCodecOptions, records.Select(r => r.VideoCodec), VideoCodecFilter, "Všechny kodeky");
            UpdateTextOptions(ContainerOptions, records.Select(r => r.Container), ContainerFilter, "Všechny kontejnery");
            UpdateNumberOptions(HeightOptions, records.Select(r => (double?)r.Height),
                [MinimumHeight, MaximumHeight], "Bez omezení", "0", " px");
            var durations = records.Where(r => r.DurationSeconds is { } n && double.IsFinite(n))
                .SelectMany(r => new double?[] { Math.Floor(r.DurationSeconds!.Value / 60), Math.Ceiling(r.DurationSeconds.Value / 60) });
            UpdateNumberOptions(DurationOptions, durations, [MinimumDurationMinutes, MaximumDurationMinutes],
                "Bez omezení", "0", " min");
            var bitRates = records.Where(r => r.BitRate is > 0)
                .SelectMany(r => new double?[] { Math.Floor(r.BitRate!.Value / 100000d) / 10,
                    Math.Ceiling(r.BitRate.Value / 100000d) / 10 });
            UpdateNumberOptions(BitRateOptions, bitRates, [MinimumBitRateMbps, MaximumBitRateMbps],
                "Bez omezení", "0.#", " Mb/s");
            UpdateNumberOptions(FrameRateOptions, records.Select(r => r.FrameRate is { } fps ? (double?)Math.Round(fps, 3) : null),
                [FrameRateFilter], "Libovolné FPS", "0.###", " fps");
        }
        finally { _refreshingFilterOptions = false; }
    }

    private static void UpdateTextOptions(ObservableCollection<MetadataTextOption> options,
        IEnumerable<string> values, string selected, string allLabel)
    {
        var desired = values.Append(selected).Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(v => v, StringComparer.CurrentCultureIgnoreCase)
            .Select(v => new MetadataTextOption(v, v)).Prepend(new(allLabel, "")).ToArray();
        UpdateOptions(options, desired);
    }

    private static void UpdateNumberOptions(ObservableCollection<MetadataNumberOption> options,
        IEnumerable<double?> values, double[] selected, string allLabel, string format, string unit)
    {
        var desired = values.Where(v => v.HasValue).Select(v => v!.Value).Concat(selected)
            .Where(double.IsFinite).Distinct().Order()
            .Select(v => new MetadataNumberOption(v.ToString(format) + unit, v))
            .Prepend(new(allLabel, double.NaN)).ToArray();
        UpdateOptions(options, desired);
    }

    private static void UpdateOptions<T>(ObservableCollection<T> options, T[] desired)
    {
        if (options.SequenceEqual(desired)) return;
        var retained = desired.ToHashSet();
        for (int i = options.Count - 1; i >= 0; i--)
            if (!retained.Contains(options[i])) options.RemoveAt(i);
        for (int i = 0; i < desired.Length; i++)
        {
            int current = options.IndexOf(desired[i]);
            if (current < 0) options.Insert(i, desired[i]);
            else if (current != i) options.Move(current, i);
        }
    }
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
            AnalysisFilterIndex = AudioFilterIndex = SubtitleFilterIndex = 0;
            FrameRateFilter = double.NaN;
            VideoCodecFilter = ContainerFilter = "";
            MinimumHeight = MaximumHeight = MinimumDurationMinutes = MaximumDurationMinutes =
                MinimumBitRateMbps = MaximumBitRateMbps = double.NaN;
        }
        finally { _resettingFilters = false; }
        MetadataFiltersChanged();
    }
}
