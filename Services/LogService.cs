using System.IO;

namespace ZDesk.Services;

public static class LogService
{
    private static readonly object Gate = new();
    private static string _logDirectory = AppDataPathService.LogDirectory;
    private static DateOnly _lastPrunedDate;

    public static string CurrentLogFile => Path.Combine(_logDirectory, $"zdesk-{DateTime.Now:yyyyMMdd}.log");
    public static void Configure(string directory) => _logDirectory = AppDataPathService.Normalize(directory);

    public static void Info(string message) => Write("INFO", message, null);
    public static void Warning(string message, Exception? exception = null) => Write("WARN", message, exception);
    public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private static void Write(string level, string message, Exception? exception)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(_logDirectory);
                var detail = exception is null ? string.Empty : $"{Environment.NewLine}{exception}";
                File.AppendAllText(CurrentLogFile, $"{DateTimeOffset.Now:O} [{level}] {message}{detail}{Environment.NewLine}");
                var today = DateOnly.FromDateTime(DateTime.Today);
                if (_lastPrunedDate != today)
                {
                    _lastPrunedDate = today;
                    PruneOldLogs();
                }
            }
        }
        catch
        {
            // Logging must never destabilize the desktop process.
        }
    }

    private static void PruneOldLogs()
    {
        foreach (var file in Directory.EnumerateFiles(_logDirectory, "zdesk-*.log")
                     .OrderByDescending(File.GetLastWriteTimeUtc)
                     .Skip(14))
        {
            File.Delete(file);
        }
    }
}
