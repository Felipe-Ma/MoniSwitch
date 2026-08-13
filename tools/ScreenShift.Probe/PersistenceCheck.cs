using ScreenShift.Native;
using ScreenShift.Services;

namespace ScreenShift.Probe;

/// <summary>
/// Compares what is on screen now against what Windows has stored for this set of monitors.
/// </summary>
/// <remarks>
/// Answers "will this survive a reboot?" without rebooting. Windows keeps a configuration per
/// connected monitor set in the CCD database and restores it at boot, so a mismatch between the
/// active configuration and the stored one is exactly the bug that made changes evaporate.
/// Read-only.
/// </remarks>
internal static class PersistenceCheck
{
    public static int Run()
    {
        var logger = new ConsoleLogger();
        var api = new WindowsDisplayApi(logger);

        var active = api.Query(NativeConstants.QDC_ONLY_ACTIVE_PATHS);
        var live = Summarise(api, active);

        Dictionary<string, string> stored;
        try
        {
            var database = api.QueryDatabase(out var topologyId);
            stored = Summarise(api, database);
            Console.WriteLine($"Stored topology id: {topologyId}");
        }
        catch (DisplayConfigException)
        {
            // DISPLAYCONFIG_TOPOLOGY_ID only has values for internal, clone, extend and external.
            // A layout Windows cannot label as one of those — anything with rotation or a custom
            // arrangement — has no topology id to return, and the query fails rather than
            // describing it. That is a limitation of the read-back, not a sign of trouble.
            Console.WriteLine("Windows will not report a stored configuration for this layout, which");
            Console.WriteLine("happens when the arrangement does not match one of its four named");
            Console.WriteLine("topologies. Use --persist and check its result instead.");
            Console.WriteLine();
            Print("ON SCREEN NOW", live);
            return 0;
        }

        Console.WriteLine();
        Print("ON SCREEN NOW", live);
        Print("STORED FOR NEXT BOOT", stored);

        var differences = new List<string>();

        foreach (var (key, value) in live)
        {
            if (!stored.TryGetValue(key, out var saved))
            {
                differences.Add($"{key} is active but not in the stored configuration");
            }
            else if (saved != value)
            {
                differences.Add($"{key}: now '{value}', stored '{saved}'");
            }
        }

        foreach (var key in stored.Keys.Where(k => !live.ContainsKey(k)))
        {
            differences.Add($"{key} is stored but not currently active");
        }

        if (differences.Count == 0)
        {
            Console.WriteLine("PERSISTED — the stored configuration matches what is on screen.");
            return 0;
        }

        Console.WriteLine("NOT PERSISTED — a reboot would change these:");
        foreach (var difference in differences)
        {
            Console.WriteLine($"  {difference}");
        }

        return 1;
    }

    private static Dictionary<string, string> Summarise(WindowsDisplayApi api, DisplayConfigSnapshot snapshot)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in snapshot.Paths)
        {
            if (!path.IsActive)
            {
                continue;
            }

            var name = api.TryGetTargetDeviceName(path.targetInfo.adapterId, path.targetInfo.id, out var target)
                       && !string.IsNullOrWhiteSpace(target.monitorFriendlyDeviceName)
                ? $"{target.monitorFriendlyDeviceName.Trim()} #{path.targetInfo.id}"
                : $"target {path.targetInfo.id}";

            var size = "size unknown";
            var position = "position unknown";

            var sourceIndex = path.sourceInfo.modeInfoIdx;
            if (sourceIndex != 0xFFFFFFFF && sourceIndex < snapshot.Modes.Length)
            {
                var candidate = snapshot.Modes[sourceIndex];
                if (candidate.infoType == DISPLAYCONFIG_MODE_INFO_TYPE.Source)
                {
                    var mode = candidate.mode.sourceMode;
                    size = $"{mode.width}x{mode.height}";
                    position = $"({mode.position.x},{mode.position.y})";
                }
            }

            var rate = path.targetInfo.refreshRate.Denominator == 0
                ? "?"
                : ((double)path.targetInfo.refreshRate.Numerator / path.targetInfo.refreshRate.Denominator).ToString("0.###");

            result[name] = $"{size} @ {rate} Hz at {position} rot={path.targetInfo.rotation}";
        }

        return result;
    }

    private static void Print(string label, Dictionary<string, string> state)
    {
        Console.WriteLine($"--- {label} ---");
        foreach (var (key, value) in state.OrderBy(kv => kv.Key))
        {
            Console.WriteLine($"  {key,-22} {value}");
        }

        Console.WriteLine();
    }
}
