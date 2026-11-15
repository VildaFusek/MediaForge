using CommunityToolkit.Mvvm.Input;
using MediaForge.Services;
using System.Diagnostics;
using System.Text;

namespace MediaForge.ViewModels;

public partial class FileExplorerViewModel
{
    private readonly SemaphoreSlim _previewGate = new(1, 1);
    private Process? _previewProcess;
    private string? _previewPath;
    private Task _previewMonitor = Task.CompletedTask;
    private bool _isShuttingDown;
    public bool CanPreview => !_isShuttingDown && HasSingleSelection && SelectedItem is { IsDirectory: false, IsLink: false }
        && FfmpegService.Instance.Current?.FfplayPath is not null;

    [RelayCommand]
    private async Task PreviewSelectedAsync()
    {
        if (!CanPreview || SelectedItem is not { } item
            || FfmpegService.Instance.Current?.FfplayPath is not { } player) return;
        string path = item.FullPath;
        await _previewGate.WaitAsync();
        try
        {
            if (!IsIdle || _isShuttingDown) return;
            await StopPreviewCoreAsync();
            var process = new Process
            {
                StartInfo = new ProcessStartInfo(player)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                    WorkingDirectory = Path.GetDirectoryName(player)!
                }
            };
            foreach (string argument in new[]
            {
                "-loglevel", "error", "-autoexit", "-window_title", "MediaForge – " + item.Name,
                "-protocol_whitelist", "file", "-i", path
            }) process.StartInfo.ArgumentList.Add(argument);
            try
            {
                await Task.Run(() =>
                {
                    FileSystemService.EnsureNoReparsePoints(path);
                    if (!process.Start()) throw new IOException("FFplay se nepodařilo spustit.");
                });
            }
            catch { process.Dispose(); throw; }
            _previewProcess = process;
            _previewPath = path;
            _previewMonitor = MonitorPreviewAsync(process, path);
            StatusText = "FFplay: náhled v samostatném okně • mezerník pozastaví • Esc zavře.";
        }
        catch (Exception ex) { ReportError("Náhled nelze otevřít", ex); }
        finally { _previewGate.Release(); }
    }

    private async Task MonitorPreviewAsync(Process process, string path)
    {
        try
        {
            var stdout = DrainPreviewLogAsync(process.StandardOutput);
            var stderr = DrainPreviewLogAsync(process.StandardError);
            await Task.WhenAll(process.WaitForExitAsync(), stdout, stderr);
            string errors = (await stderr).Trim();
            if (!string.IsNullOrEmpty(errors))
                await Task.Run(() => AppLog.Information($"FFplay {path}: {errors}"));
            if (ReferenceEquals(_previewProcess, process) && process.ExitCode != 0)
                StatusText = "FFplay byl ukončen s chybou; podrobnosti jsou v logu.";
        }
        catch (Exception ex) { await Task.Run(() => AppLog.Error("FFplay " + path, ex)); }
    }

    private async Task StopPreviewAsync(string? affectedPath = null)
    {
        await _previewGate.WaitAsync();
        try
        {
            if (affectedPath is not null && _previewPath is { } playing)
            {
                string source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(affectedPath));
                if (!string.Equals(source, playing, StringComparison.OrdinalIgnoreCase)
                    && !playing.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;
            }
            await StopPreviewCoreAsync();
        }
        finally { _previewGate.Release(); }
    }

    private async Task StopPreviewCoreAsync()
    {
        var process = _previewProcess;
        if (process is null) return;
        string? path = _previewPath;
        _previewProcess = null;
        _previewPath = null;
        try
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
                await process.WaitForExitAsync();
            }
            await _previewMonitor;
        }
        catch
        {
            _previewProcess = process;
            _previewPath = path;
            throw;
        }
        finally { if (!ReferenceEquals(_previewProcess, process)) process.Dispose(); }
    }

    private static async Task<string> DrainPreviewLogAsync(StreamReader reader)
    {
        var text = new StringBuilder();
        char[] buffer = new char[2048];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory())) > 0)
        {
            int retain = Math.Min(count, 16384 - text.Length);
            if (retain > 0) text.Append(buffer, 0, retain);
        }
        return text.ToString();
    }
}
