using System.IO;
using System.Text.Json;

namespace ScreenShift.Services;

/// <summary>Application settings, as stored in settings.json.</summary>
public sealed class AppSettings
{
    /// <summary>The tray icon is the point of the app once profiles exist, so it defaults on.</summary>
    public bool ShowTrayIcon { get; set; } = true;

    /// <summary>
    /// Closing the window hides to the tray instead of exiting. Matters beyond convenience:
    /// global hotkeys only work while the process is alive.
    /// </summary>
    public bool CloseToTray { get; set; } = true;
}

/// <summary>Loads and saves settings.json. Settings are small and non-critical, so a failure to
/// read or write them degrades to defaults rather than surfacing as an error.</summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IAppLogger _logger;

    public SettingsService(IAppLogger logger)
    {
        _logger = logger;
        Current = Load();
    }

    public AppSettings Current { get; }

    public void Save()
    {
        try
        {
            AppPaths.EnsureCreated();

            var temp = AppPaths.SettingsFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Current, JsonOptions));
            File.Move(temp, AppPaths.SettingsFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warn($"Could not save settings: {ex.Message}");
        }
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), JsonOptions)
                    ?? new AppSettings();
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.Warn($"Could not read settings; using defaults: {ex.Message}");
        }

        return new AppSettings();
    }
}
