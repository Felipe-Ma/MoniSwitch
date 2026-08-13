using ScreenShift.Native;
using ScreenShift.Services;

namespace ScreenShift.Probe;

/// <summary>
/// Works directly on the CCD path table, below the level of <see cref="DisplayService"/>'s
/// monitor list.
/// </summary>
/// <remarks>
/// Exists because a monitor that the monitor list drops — through a filter being wrong, say — still
/// needs to be diagnosable and still needs to be switchable back on. Operating on raw paths keeps
/// that possible without depending on the layer under suspicion.
/// </remarks>
internal static class PathDump
{
    public static int Dump()
    {
        var logger = new ConsoleLogger();
        var api = new WindowsDisplayApi(logger);
        var snapshot = api.Query(NativeConstants.QDC_ALL_PATHS);

        var byTarget = snapshot.Paths
            .GroupBy(p => (p.targetInfo.adapterId.ToString(), p.targetInfo.id))
            .OrderBy(g => g.Key.Item2);

        Console.WriteLine($"{snapshot.Paths.Length} paths across {byTarget.Count()} targets");
        Console.WriteLine();

        foreach (var group in byTarget)
        {
            var first = group.First();
            var name = api.TryGetTargetDeviceName(first.targetInfo.adapterId, first.targetInfo.id, out var target)
                       && !string.IsNullOrWhiteSpace(target.monitorFriendlyDeviceName)
                ? target.monitorFriendlyDeviceName.Trim()
                : "(no name)";

            var anyActive = group.Any(p => p.IsActive);

            Console.WriteLine($"target {group.Key.Item2,-6} {name,-16} {group.Count(),3} path(s)  active={anyActive}");

            foreach (var path in group.Take(3))
            {
                Console.WriteLine($"    source {path.sourceInfo.id,-3} pathFlags=0x{path.flags:X8} "
                                  + $"targetStatus=0x{path.targetInfo.statusFlags:X8} "
                                  + $"available={path.targetInfo.targetAvailable} "
                                  + $"connected={path.targetInfo.IsConnected} "
                                  + $"tech={path.targetInfo.outputTechnology}");
            }
        }

        return 0;
    }

    /// <summary>
    /// Asks Windows to apply one of its four named topologies — extend, clone, internal or external
    /// — using the arrangement it has remembered for the currently connected monitors.
    /// </summary>
    /// <remarks>
    /// The escape hatch when the path table has been left in a state that is awkward to edit back
    /// into shape, such as two monitors accidentally cloned onto one surface. Windows already knows
    /// what an extended desktop should look like here, so asking for that by name is more reliable
    /// than reconstructing it path by path.
    /// </remarks>
    public static int ApplyTopology(string name)
    {
        var flag = name.ToLowerInvariant() switch
        {
            "extend" => NativeConstants.SDC_TOPOLOGY_EXTEND,
            "clone" => NativeConstants.SDC_TOPOLOGY_CLONE,
            "internal" => NativeConstants.SDC_TOPOLOGY_INTERNAL,
            "external" => NativeConstants.SDC_TOPOLOGY_EXTERNAL,
            _ => 0u,
        };

        if (flag == 0)
        {
            Console.Error.WriteLine($"Unknown topology '{name}'. Use extend, clone, internal or external.");
            return 2;
        }

        var logger = new ConsoleLogger();
        var api = new WindowsDisplayApi(logger);

        // The topology flags name a stored configuration, so no path or mode arrays are supplied.
        var result = NativeMethods.SetDisplayConfig(0, null, 0, null, NativeConstants.SDC_APPLY | flag);

        if (result != NativeConstants.ERROR_SUCCESS)
        {
            Console.Error.WriteLine($"Failed: {DisplayConfigException.DescribeError(result)}");
            return 1;
        }

        Console.WriteLine($"Applied the {name} topology.");
        return 0;
    }

    /// <summary>
    /// Switches a target on by target id, going straight to the path table.
    /// </summary>
    public static int ForceEnable(uint targetId)
    {
        var logger = new ConsoleLogger();
        var api = new WindowsDisplayApi(logger);

        var snapshot = api.Query(NativeConstants.QDC_ALL_PATHS);
        var paths = snapshot.Paths;

        var index = -1;
        for (var i = 0; i < paths.Length; i++)
        {
            if (paths[i].targetInfo.id != targetId)
            {
                continue;
            }

            // Prefer an inactive path — that is the one to light up.
            if (!paths[i].IsActive)
            {
                index = i;
                break;
            }

            index = i;
        }

        if (index < 0)
        {
            Console.Error.WriteLine($"No path found for target {targetId}.");
            return 2;
        }

        if (paths[index].IsActive)
        {
            Console.WriteLine($"Target {targetId} is already active.");
            return 0;
        }

        var path = paths[index];
        path.flags |= NativeConstants.DISPLAYCONFIG_PATH_ACTIVE;
        path.sourceInfo.modeInfoIdx = 0xFFFFFFFF;
        path.targetInfo.modeInfoIdx = 0xFFFFFFFF;
        paths[index] = path;

        const uint Flags = NativeConstants.SDC_APPLY
            | NativeConstants.SDC_USE_SUPPLIED_DISPLAY_CONFIG
            | NativeConstants.SDC_ALLOW_CHANGES
            | NativeConstants.SDC_SAVE_TO_DATABASE;

        var result = api.SetConfiguration(snapshot, Flags);

        if (result != NativeConstants.ERROR_SUCCESS)
        {
            Console.Error.WriteLine($"Failed: {DisplayConfigException.DescribeError(result)}");
            return 1;
        }

        Console.WriteLine($"Target {targetId} switched on.");
        return 0;
    }
}
