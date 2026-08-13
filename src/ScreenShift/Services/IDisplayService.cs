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
}
