using ScreenShift.Models;
using ScreenShift.Services;

namespace ScreenShift.Probe;

/// <summary>
/// Exercises one kind of display change end to end: apply it, hold it long enough to be visible,
/// put it back, then check that what came back matches what was there before.
/// </summary>
/// <remarks>
/// This is the automated stand-in for the 15-second confirmation prompt — same apply and restore
/// path, minus the human. It always targets a non-primary display so a failure cannot take the
/// primary down with it, and it always restores, including when the change fails partway.
/// </remarks>
internal static class ApplyTest
{
    private static readonly TimeSpan HoldDuration = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SettleDuration = TimeSpan.FromSeconds(2);

    public static int Run(string what)
    {
        var logger = new ConsoleLogger();
        var service = new DisplayService(logger);

        var target = service.GetMonitors().FirstOrDefault(m => m.IsEnabled && !m.IsPrimary);
        if (target is null)
        {
            Console.Error.WriteLine("Need at least one enabled non-primary display to test against.");
            return 2;
        }

        Console.WriteLine($"Target: {target.FriendlyName} ({target.GdiDeviceName})");

        var request = BuildRequest(service, target, what);
        if (request is null)
        {
            return 2;
        }

        Console.WriteLine($"Change: {request}");
        Console.WriteLine();

        var before = Describe(service);
        Print("BEFORE", before);

        var snapshot = service.CaptureSnapshot();

        var applied = service.Apply([request]);
        if (!applied.Succeeded)
        {
            Console.Error.WriteLine($"Apply failed: {applied.Message}");
            Console.Error.WriteLine(applied.RolledBack ? "The previous settings were restored." : "Nothing was changed.");
            return 1;
        }

        Thread.Sleep(SettleDuration);
        Print("AFTER", Describe(service));

        Console.WriteLine($"Holding for {HoldDuration.TotalSeconds:0} seconds, then restoring...");
        Thread.Sleep(HoldDuration);

        var restored = service.Restore(snapshot);
        Thread.Sleep(SettleDuration);

        var after = Describe(service);
        Print("RESTORED", after);

        if (!restored.Succeeded)
        {
            Console.Error.WriteLine($"Restore reported a problem: {restored.Message}");
            return 1;
        }

        var differences = Compare(before, after);
        if (differences.Count == 0)
        {
            Console.WriteLine("PASS — the restored configuration matches the original exactly.");
            return 0;
        }

        Console.Error.WriteLine("FAIL — the restored configuration differs from the original:");
        foreach (var difference in differences)
        {
            Console.Error.WriteLine($"  {difference}");
        }

        return 1;
    }

    private static MonitorChangeRequest? BuildRequest(DisplayService service, MonitorInfo target, string what)
    {
        var modes = service.GetSupportedModes(target);
        var current = service.GetCurrentMode(target);

        if (current is null || modes.Count == 0)
        {
            Console.Error.WriteLine("Could not read the target's mode list.");
            return null;
        }

        switch (what.ToLowerInvariant())
        {
            case "refresh":
            {
                // Highest rate available at the resolution already in use, so only the rate moves.
                var candidate = modes
                    .Where(m => m.Resolution == current.Resolution && m.RefreshHz != current.RefreshHz)
                    .MaxBy(m => m.RefreshHz);

                if (candidate is null)
                {
                    Console.Error.WriteLine($"{target.FriendlyName} offers only one refresh rate at {current.Resolution}.");
                    return null;
                }

                return new MonitorChangeRequest { Monitor = target, RefreshHz = candidate.RefreshHz };
            }

            case "resolution":
            {
                // The next size down, keeping a rate that size actually supports.
                var candidate = modes
                    .Where(m => m.Resolution != current.Resolution)
                    .OrderByDescending(m => m.PixelCount)
                    .ThenByDescending(m => m.RefreshHz)
                    .FirstOrDefault();

                if (candidate is null)
                {
                    Console.Error.WriteLine($"{target.FriendlyName} offers only one resolution.");
                    return null;
                }

                return new MonitorChangeRequest
                {
                    Monitor = target,
                    Resolution = candidate.Resolution,
                    RefreshHz = candidate.RefreshHz,
                };
            }

            case "primary":
                return new MonitorChangeRequest { Monitor = target, MakePrimary = true };

            default:
                Console.Error.WriteLine($"Unknown test '{what}'. Use refresh, resolution or primary.");
                return null;
        }
    }

    private static Dictionary<string, string> Describe(DisplayService service) =>
        service.GetMonitors()
            .Where(m => m.IsEnabled)
            .ToDictionary(
                m => m.StableKey,
                m => $"{m.Resolution} @ {m.RefreshRate} at ({m.Position?.X},{m.Position?.Y}){(m.IsPrimary ? " PRIMARY" : string.Empty)} [{m.Orientation}]");

    private static void Print(string label, Dictionary<string, string> state)
    {
        Console.WriteLine($"--- {label} ---");
        foreach (var (key, value) in state.OrderBy(kv => kv.Key))
        {
            Console.WriteLine($"  {Shorten(key),-28} {value}");
        }

        Console.WriteLine();
    }

    private static List<string> Compare(Dictionary<string, string> before, Dictionary<string, string> after)
    {
        var differences = new List<string>();

        foreach (var (key, value) in before)
        {
            if (!after.TryGetValue(key, out var now))
            {
                differences.Add($"{Shorten(key)} disappeared");
            }
            else if (now != value)
            {
                differences.Add($"{Shorten(key)}: was '{value}', now '{now}'");
            }
        }

        foreach (var key in after.Keys.Where(k => !before.ContainsKey(k)))
        {
            differences.Add($"{Shorten(key)} appeared");
        }

        return differences;
    }

    /// <summary>Device paths are unreadable in a table; the UID segment is the part that differs.</summary>
    private static string Shorten(string stableKey)
    {
        var parts = stableKey.Split('#');
        return parts.Length >= 3 ? $"{parts[1]}/{parts[2].Split('&').Last()}" : stableKey;
    }
}
