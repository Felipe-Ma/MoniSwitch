using System.Globalization;
using ScreenShift.Models;
using ScreenShift.Services;

namespace ScreenShift.Probe;

/// <summary>
/// Applies a change and leaves it applied — no hold, no automatic revert.
/// </summary>
/// <remarks>
/// The counterpart to <see cref="ApplyTest"/>: useful for putting a specific configuration in
/// place, and for repairing one by hand when something else has left it wrong.
/// <code>
///   --set 1 --refresh 180 --primary
///   --set 3 --resolution 2560x1440 --refresh 60
/// </code>
/// </remarks>
internal static class SetCommand
{
    public static int Run(string[] args, int displayNumberIndex)
    {
        if (!int.TryParse(args[displayNumberIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out var displayNumber))
        {
            Console.Error.WriteLine($"'{args[displayNumberIndex]}' is not a display number.");
            return 2;
        }

        var logger = new ConsoleLogger();
        var service = new DisplayService(logger);

        var monitors = service.GetMonitors();

        // A switched-off monitor has no GDI device and therefore no display number, so it can only
        // be named by its target id. Falling back to that is what makes it addressable at all.
        var target = monitors.FirstOrDefault(m => m.IsEnabled && m.DisplayNumber == displayNumber)
                     ?? monitors.FirstOrDefault(m => m.TargetId == (uint)displayNumber);

        if (target is null)
        {
            Console.Error.WriteLine($"No display numbered {displayNumber}, and no target with that id.");
            Console.Error.WriteLine("Connected displays:");
            foreach (var monitor in monitors)
            {
                Console.Error.WriteLine($"  {monitor.FriendlyName,-16} display {monitor.DisplayNumber?.ToString() ?? "—"}, target {monitor.TargetId}, {(monitor.IsEnabled ? "on" : "off")}");
            }

            return 2;
        }

        DisplayResolution? resolution = null;
        uint? refresh = null;
        var makePrimary = args.Any(a => string.Equals(a, "--primary", StringComparison.OrdinalIgnoreCase));

        var resolutionIndex = Array.FindIndex(args, a => string.Equals(a, "--resolution", StringComparison.OrdinalIgnoreCase));
        if (resolutionIndex >= 0 && resolutionIndex + 1 < args.Length)
        {
            var parts = args[resolutionIndex + 1].Split('x', 'X', '×');
            if (parts.Length != 2 || !int.TryParse(parts[0], out var width) || !int.TryParse(parts[1], out var height))
            {
                Console.Error.WriteLine("--resolution wants something like 2560x1440.");
                return 2;
            }

            resolution = new DisplayResolution(width, height);
        }

        var refreshIndex = Array.FindIndex(args, a => string.Equals(a, "--refresh", StringComparison.OrdinalIgnoreCase));
        if (refreshIndex >= 0 && refreshIndex + 1 < args.Length)
        {
            if (!uint.TryParse(args[refreshIndex + 1], out var hz))
            {
                Console.Error.WriteLine("--refresh wants a whole number of hertz.");
                return 2;
            }

            refresh = hz;
        }

        DisplayOrientation? orientation = null;
        var orientationIndex = Array.FindIndex(args, a => string.Equals(a, "--orientation", StringComparison.OrdinalIgnoreCase));
        if (orientationIndex >= 0 && orientationIndex + 1 < args.Length)
        {
            orientation = args[orientationIndex + 1].ToLowerInvariant() switch
            {
                "landscape" => DisplayOrientation.Landscape,
                "portrait" => DisplayOrientation.Portrait,
                "flipped" or "landscapeflipped" => DisplayOrientation.LandscapeFlipped,
                "portraitflipped" => DisplayOrientation.PortraitFlipped,
                _ => null,
            };

            if (orientation is null)
            {
                Console.Error.WriteLine("--orientation wants landscape, portrait, flipped or portraitflipped.");
                return 2;
            }
        }

        DisplayPosition? position = null;
        var positionIndex = Array.FindIndex(args, a => string.Equals(a, "--position", StringComparison.OrdinalIgnoreCase));
        if (positionIndex >= 0 && positionIndex + 1 < args.Length)
        {
            var parts = args[positionIndex + 1].Split(',');
            if (parts.Length != 2 || !int.TryParse(parts[0], out var x) || !int.TryParse(parts[1], out var y))
            {
                Console.Error.WriteLine("--position wants something like 0,-1440.");
                return 2;
            }

            position = new DisplayPosition(x, y);
        }

        bool? enabled = null;
        if (args.Any(a => string.Equals(a, "--enable", StringComparison.OrdinalIgnoreCase)))
        {
            enabled = true;
        }
        else if (args.Any(a => string.Equals(a, "--disable", StringComparison.OrdinalIgnoreCase)))
        {
            enabled = false;
        }

        var request = new MonitorChangeRequest
        {
            Monitor = target,
            Enabled = enabled,
            Resolution = resolution,
            RefreshHz = refresh,
            Orientation = orientation,
            Position = position,
            MakePrimary = makePrimary,
        };

        if (!request.ChangesAnything)
        {
            Console.Error.WriteLine("Nothing to change. Pass --resolution, --refresh, --primary, --enable or --disable.");
            return 2;
        }

        Console.WriteLine($"Target: {target.FriendlyName} ({target.GdiDeviceName})");
        Console.WriteLine($"Change: {request}");
        Console.WriteLine();

        var result = service.Apply([request]);

        if (!result.Succeeded)
        {
            Console.Error.WriteLine($"Failed: {result.Message}");
            Console.Error.WriteLine(result.RolledBack ? "The previous settings were restored." : "Nothing was changed.");
            return 1;
        }

        Thread.Sleep(TimeSpan.FromSeconds(2));

        Console.WriteLine("--- now ---");
        foreach (var monitor in service.GetMonitors())
        {
            Console.WriteLine(monitor.IsEnabled
                ? $"  {monitor.GdiDeviceName,-14} {monitor.FriendlyName,-14} {monitor.Resolution} @ {monitor.RefreshRate} "
                  + $"at ({monitor.Position?.X},{monitor.Position?.Y}){(monitor.IsPrimary ? "  PRIMARY" : string.Empty)}"
                : $"  {"(off)",-14} {monitor.FriendlyName,-14} target {monitor.TargetId}");
        }

        return 0;
    }
}
