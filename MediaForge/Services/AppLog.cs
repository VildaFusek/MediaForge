using System;
using System.Diagnostics;
using System.IO;

namespace MediaForge.Services;

internal static class AppLog
{
    private static readonly object Gate = new();
    public static void Error(string context, Exception exception)
    {
        Trace.TraceError($"{context}: {exception}");
        Write($"{context}: {exception}");
    }

    public static void Information(string message)
    {
        Trace.TraceInformation(message);
        Write(message);
    }

    private static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MediaForge", "Logs");
                Directory.CreateDirectory(folder);
                File.AppendAllText(Path.Combine(folder, $"{DateTime.Today:yyyy-MM-dd}.log"),
                    $"{DateTime.Now:O} {message}{Environment.NewLine}");
            }
        }
        catch (Exception loggingError) { Trace.TraceError($"Nelze zapsat log: {loggingError}"); }
    }
}
