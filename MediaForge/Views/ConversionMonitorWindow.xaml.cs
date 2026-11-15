using MediaForge.Services;
using MediaForge.ViewModels;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace MediaForge.Views;

public sealed partial class ConversionMonitorWindow : Window
{
    private readonly MachinePerformanceService _performance = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _sampling, _closed;

    public ConversionMonitorWindow()
    {
        InitializeComponent();
        Surface.DataContext = ConversionViewModel.Instance;
        AppWindow.Resize(new SizeInt32(980, 820));
        _timer.Tick += Sample_Tick;
        Closed += (_, _) =>
        {
            _closed = true;
            _timer.Stop();
            _performance.Dispose();
        };
        _timer.Start();
    }

    private async void Sample_Tick(object? sender, object e)
    {
        if (_sampling || _closed) return;
        _sampling = true;
        try
        {
            var sample = await Task.Run(_performance.Sample);
            if (_closed) return;
            CpuText.Text = sample.Cpu is { } cpu ? $"CPU: {cpu:F1} %" : "CPU: nezjištěno";
            CpuBar.IsIndeterminate = sample.Cpu is null;
            CpuBar.Value = sample.Cpu ?? 0;
            MemoryText.Text = sample.TotalGiB > 0
                ? $"RAM: {sample.UsedGiB:F1} / {sample.TotalGiB:F1} GiB ({sample.MemoryPercent:F0} %)"
                : "RAM: nezjištěno";
            MemoryBar.Value = sample.MemoryPercent;
            EncodeText.Text = sample.GpuEncode is { } encode ? $"GPU encode: {encode:F1} %" : "GPU encode: čítač nedostupný";
            EncodeBar.IsIndeterminate = sample.GpuEncode is null;
            EncodeBar.Value = sample.GpuEncode ?? 0;
            DecodeText.Text = sample.GpuDecode is { } decode ? $"GPU decode: {decode:F1} %" : "GPU decode: čítač nedostupný";
            DecodeBar.IsIndeterminate = sample.GpuDecode is null;
            DecodeBar.Value = sample.GpuDecode ?? 0;
        }
        catch (Exception ex)
        {
            if (!_closed)
            {
                CpuText.Text = "Měření nedostupné: " + ex.Message;
                _timer.Stop();
            }
        }
        finally { _sampling = false; }
    }

    private void Controls_Click(object sender, RoutedEventArgs e) => ConversionWindows.ShowControls();
}
