using System.IO;
using System.Text;

namespace ScreenShift.Services;

/// <summary>
/// Appends to %APPDATA%\ScreenShift\logs\screenshift-yyyyMMdd.log and mirrors to the debugger.
/// Deliberately dumb: a lock and a file handle, no background queue, no third-party framework.
/// If logging itself fails it stays silent rather than taking the app down with it.
/// </summary>
public sealed class FileLogger : IAppLogger
{
    private const int RetainedLogDays = 7;

    private readonly object _gate = new();
    private readonly string? _filePath;
    private readonly LogLevel _minimumLevel;

    public FileLogger(LogLevel minimumLevel = LogLevel.Debug)
    {
        _minimumLevel = minimumLevel;

        try
        {
            AppPaths.EnsureCreated();
            _filePath = Path.Combine(AppPaths.LogDirectory, $"screenshift-{DateTime.Now:yyyyMMdd}.log");
            PruneOldLogs();
        }
        catch (Exception ex)
        {
            // No disk logging available (roaming profile locked down, disk full, ...).
            // The debugger mirror below still works.
            System.Diagnostics.Debug.WriteLine($"[ScreenShift] could not open log file: {ex}");
            _filePath = null;
        }
    }

    public string? FilePath => _filePath;

    public void Log(LogLevel level, string message, Exception? exception = null)
    {
        if (level < _minimumLevel)
        {
            return;
        }

        var line = new StringBuilder()
            .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
            .Append(" [").Append(level.ToString().ToUpperInvariant().PadRight(5)).Append("] ")
            .Append(message);

        if (exception is not null)
        {
            line.AppendLine().Append(exception);
        }

        var text = line.ToString();
        System.Diagnostics.Debug.WriteLine($"[ScreenShift] {text}");

        if (_filePath is null)
        {
            return;
        }

        lock (_gate)
        {
            try
            {
                File.AppendAllText(_filePath, text + Environment.NewLine, Encoding.UTF8);
            }
            catch (IOException)
            {
                // Another instance holds the handle, or the disk went away. Not worth crashing over.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static void PruneOldLogs()
    {
        var cutoff = DateTime.Now.AddDays(-RetainedLogDays);

        foreach (var file in Directory.EnumerateFiles(AppPaths.LogDirectory, "screenshift-*.log"))
        {
            try
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
