// Explicit: the WPF SDK drops System.IO from implicit usings, because System.Windows.Shapes.Path
// would otherwise collide with System.IO.Path.
using System.IO;

namespace ScreenShift.Services;

/// <summary>
/// Everything ScreenShift writes lives under %APPDATA%\ScreenShift. Profiles land here in
/// Phase 4; for now it is just the log directory.
/// </summary>
public static class AppPaths
{
    public static string RootDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ScreenShift");

    public static string LogDirectory { get; } = Path.Combine(RootDirectory, "logs");

    public static string ProfilesFile { get; } = Path.Combine(RootDirectory, "profiles.json");

    public static string SettingsFile { get; } = Path.Combine(RootDirectory, "settings.json");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(LogDirectory);
    }
}
