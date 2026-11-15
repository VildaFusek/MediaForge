using MediaForge.ViewModels;

namespace MediaForge.Views;

public static class ConversionWindows
{
    private static ConversionMonitorWindow? _monitor;
    public static Action? ActivateControls { get; set; }

    static ConversionWindows()
    {
        ConversionViewModel.Instance.QueueStarted += ShowMonitor;
        ConversionViewModel.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ConversionViewModel.IsRunning) && !ConversionViewModel.Instance.IsRunning)
                _monitor?.Close();
        };
    }

    public static void ShowControls()
    {
        ActivateControls?.Invoke();
    }

    public static void ShowMonitor()
    {
        if (!ConversionViewModel.Instance.IsRunning) return;
        if (_monitor is null)
        {
            _monitor = new ConversionMonitorWindow();
            _monitor.Closed += (_, _) => _monitor = null;
        }
        _monitor.Activate();
    }

    public static void CloseAll()
    {
        _monitor?.Close();
    }
}
