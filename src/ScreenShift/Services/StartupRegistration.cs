using Microsoft.Win32;

namespace ScreenShift.Services;

/// <summary>
/// Registers ScreenShift in the per-user Run key so it launches at sign-in.
/// </summary>
/// <remarks>
/// The registry entry is the single source of truth — there is deliberately no copy of this in
/// settings.json. Task Manager's Startup page manages the same entry, so a user disabling it
/// there and the checkbox here can never disagree about what will actually happen at sign-in.
/// The entry points at whatever executable is currently running and passes --minimized, so a
/// sign-in launch goes straight to the tray instead of opening the window.
/// </remarks>
public static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ScreenShift";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(ValueName) is string;
    }

    /// <summary>What the entry currently holds, for diagnostics. Null when not registered.</summary>
    public static string? RegisteredCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(ValueName) as string;
    }

    public static void Enable()
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("The path of the running executable could not be determined.");

        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath)
            ?? throw new InvalidOperationException("The startup registry key could not be opened.");

        key.SetValue(ValueName, $"\"{executable}\" --minimized");
    }

    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
