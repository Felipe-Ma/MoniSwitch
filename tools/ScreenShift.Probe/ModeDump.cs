using ScreenShift.Native;
using ScreenShift.Services;

namespace ScreenShift.Probe;

/// <summary>
/// Dumps raw DEVMODE data per display: what GDI thinks the current mode is, and the full list of
/// modes the driver will accept. Read-only — nothing here changes the display configuration.
/// </summary>
internal static class ModeDump
{
    public static int Run()
    {
        var logger = new ConsoleLogger();
        var api = new WindowsDisplayApi(logger);
        var service = new DisplayService(logger);

        foreach (var monitor in service.GetMonitors())
        {
            Console.WriteLine();
            Console.WriteLine($"=== {monitor.FriendlyName}  ({monitor.GdiDeviceName ?? "no GDI name"}) ===");
            Console.WriteLine($"    CCD says: {monitor.Resolution} @ {monitor.RefreshRate}, {monitor.Orientation}, panel mode {monitor.PanelResolution}");

            if (monitor.GdiDeviceName is not { } device)
            {
                Console.WriteLine("    (disabled — GDI has no mode information)");
                continue;
            }

            if (api.TryGetCurrentMode(device, out var current))
            {
                Console.WriteLine($"    GDI current: {current.dmPelsWidth} x {current.dmPelsHeight} @ {current.dmDisplayFrequency} Hz, "
                                  + $"{current.dmBitsPerPel} bpp, pos=({current.dmPosition.x},{current.dmPosition.y}), "
                                  + $"dmDisplayOrientation={current.dmDisplayOrientation}, dmFields=0x{current.dmFields:X}");
            }
            else
            {
                Console.WriteLine("    GDI current: <failed>");
            }

            // What Windows has saved, i.e. what this display would come back as after a reboot.
            // Divergence from the line above means the live mode was applied but never persisted.
            var registry = DEVMODE.Create();
            if (NativeMethods.EnumDisplaySettingsEx(device, DeviceModeConstants.ENUM_REGISTRY_SETTINGS, ref registry, 0))
            {
                Console.WriteLine($"    GDI saved:   {registry.dmPelsWidth} x {registry.dmPelsHeight} @ {registry.dmDisplayFrequency} Hz, "
                                  + $"pos=({registry.dmPosition.x},{registry.dmPosition.y}), "
                                  + $"dmDisplayOrientation={registry.dmDisplayOrientation}");
            }
            else
            {
                Console.WriteLine("    GDI saved:   <failed>");
            }

            var modes = api.EnumerateModes(device);

            var grouped = modes
                .Where(m => m.dmBitsPerPel == 32)
                .GroupBy(m => (Width: m.dmPelsWidth, Height: m.dmPelsHeight))
                .OrderByDescending(g => (long)g.Key.Width * g.Key.Height)
                .ToList();

            Console.WriteLine($"    {modes.Count} raw modes, {grouped.Count} distinct 32-bpp resolutions:");

            foreach (var group in grouped)
            {
                var rates = group
                    .Select(m => m.dmDisplayFrequency)
                    .Distinct()
                    .OrderByDescending(r => r);

                Console.WriteLine($"      {group.Key.Width,5} x {group.Key.Height,-5}  {string.Join(", ", rates)} Hz");
            }

            var orientations = modes.Select(m => m.dmDisplayOrientation).Distinct().OrderBy(o => o);
            Console.WriteLine($"    dmDisplayOrientation values present in the list: {string.Join(", ", orientations)}");

            var nonStandardDepths = modes.Select(m => m.dmBitsPerPel).Distinct().Where(b => b != 32).ToList();
            if (nonStandardDepths.Count > 0)
            {
                Console.WriteLine($"    other colour depths present: {string.Join(", ", nonStandardDepths)} bpp");
            }
        }

        return 0;
    }
}
