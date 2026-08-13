using ScreenShift.Models;

namespace ScreenShift.Services;

/// <summary>Reads the machine's display configuration. Phase 1 is read-only; writing arrives in Phase 2.</summary>
public interface IDisplayService
{
    /// <summary>
    /// Enumerates every connected monitor, enabled or not, ordered the way they sit on the desktop.
    /// </summary>
    /// <exception cref="Native.DisplayConfigException">The display configuration could not be read at all.</exception>
    IReadOnlyList<MonitorInfo> GetMonitors();

    /// <summary>
    /// Every mode the driver will accept for this monitor, largest first. Empty for a disabled
    /// monitor, which has no GDI device to ask.
    /// </summary>
    IReadOnlyList<DisplayMode> GetSupportedModes(MonitorInfo monitor);

    /// <summary>
    /// The mode GDI currently has applied, in the same terms as <see cref="GetSupportedModes"/>.
    /// Null for a disabled monitor.
    /// </summary>
    DisplayMode? GetCurrentMode(MonitorInfo monitor);

    /// <summary>
    /// Records the current configuration so it can be put back. Take one before applying anything.
    /// </summary>
    DisplaySnapshot CaptureSnapshot();

    /// <summary>
    /// Applies a batch of changes as a single switch. Validates every mode first and changes
    /// nothing if any of them would be rejected; if the commit itself fails, puts back what was
    /// there before rather than leaving a half-applied configuration.
    /// </summary>
    ApplyResult Apply(IReadOnlyList<MonitorChangeRequest> requests);

    /// <summary>Puts back a previously captured configuration.</summary>
    ApplyResult Restore(DisplaySnapshot snapshot);
}
