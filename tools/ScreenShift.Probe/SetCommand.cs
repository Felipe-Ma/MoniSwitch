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

        var target = service.GetMonitors().FirstOrDefault(m => m.IsEnabled && m.DisplayNumber == displayNumber);
        if (target is null)
        {
            Console.Error.WriteLine($"No enabled display numbered {displayNumber}.");
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

        var request = new MonitorChangeRequest
        {
            Monitor = target,
            Resolution = resolution,
            RefreshHz = refresh,
            MakePrimary = makePrimary,
        };

        if (!request.ChangesAnything)
        {
            Console.Error.WriteLine("Nothing to change. Pass --resolution, --refresh and/or --primary.");
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
        foreach (var monitor in service.GetMonitors().Where(m => m.IsEnabled))
        {
            Console.WriteLine($"  {monitor.GdiDeviceName,-14} {monitor.FriendlyName,-14} {monitor.Resolution} @ {monitor.RefreshRate} "
                              + $"at ({monitor.Position?.X},{monitor.Position?.Y}){(monitor.IsPrimary ? "  PRIMARY" : string.Empty)}");
        }

        return 0;
    }
}
