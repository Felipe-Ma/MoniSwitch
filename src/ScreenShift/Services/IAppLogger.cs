namespace ScreenShift.Services;

public enum LogLevel
{
    Debug,
    Info,
    Warn,
    Error,
}

/// <summary>
/// Display API failures are hard to reproduce, so every one of them gets written down with the
/// adapter/target ids and the Win32 code that caused it.
/// </summary>
public interface IAppLogger
{
    void Log(LogLevel level, string message, Exception? exception = null);

    void Debug(string message) => Log(LogLevel.Debug, message);
    void Info(string message) => Log(LogLevel.Info, message);
    void Warn(string message) => Log(LogLevel.Warn, message);
    void Error(string message, Exception? exception = null) => Log(LogLevel.Error, message, exception);
}

/// <summary>Drops everything. Handy for tests and for code paths that run before logging is up.</summary>
public sealed class NullLogger : IAppLogger
{
    public static readonly NullLogger Instance = new();

    public void Log(LogLevel level, string message, Exception? exception = null)
    {
    }
}
