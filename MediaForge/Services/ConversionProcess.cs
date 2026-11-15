using System.Diagnostics;
using System.Text;

namespace MediaForge.Services;

internal static class ConversionProcess
{
    internal static Process Create(string executable, IEnumerable<string> arguments)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(executable)!
            }
        };
        foreach (string argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        return process;
    }

    internal static async Task<int> RunAsync(Process process, Action<string> stdout, Action<string> stderr,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var job = new ConversionProcessJob();
        if (!process.Start()) throw new IOException("Proces nástroje nelze spustit.");
        try { job.Attach(process); }
        catch
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
                await process.WaitForExitAsync(CancellationToken.None);
            }
            throw;
        }
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        { Trace.TraceWarning(ex.Message); }
        using var stop = token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            { Trace.TraceWarning(ex.Message); }
        });
        // Read bounded chunks: malformed output must not allocate an unbounded line.
        async Task ReadAsync(StreamReader reader, Action<string> receive)
        {
            char[] buffer = new char[4096];
            var line = new StringBuilder();
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
            {
                for (int i = 0; i < count; i++)
                {
                    char c = buffer[i];
                    if (c is '\r' or '\n')
                    {
                        if (line.Length > 0) { receive(line.ToString()); line.Clear(); }
                    }
                    else
                    {
                        line.Append(c);
                        if (line.Length >= 8192) { receive(line.ToString()); line.Clear(); }
                    }
                }
            }
            if (line.Length > 0) receive(line.ToString());
        }
        Task output = ReadAsync(process.StandardOutput, stdout);
        Task error = ReadAsync(process.StandardError, stderr);
        // A failed log writer/reader must stop FFmpeg, rather than block a full pipe.
        Task exit = process.WaitForExitAsync(token);
        try
        {
            var pending = new List<Task> { output, error, exit };
            while (pending.Count > 0)
            {
                Task completed = await Task.WhenAny(pending);
                await completed;
                pending.Remove(completed);
            }
            token.ThrowIfCancellationRequested();
            return process.ExitCode;
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
                await process.WaitForExitAsync(CancellationToken.None);
            }
            try { await Task.WhenAll(output, error, exit); }
            catch { /* The original failure is reported to the caller. */ }
        }
    }
}
