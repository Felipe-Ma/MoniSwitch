using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using ScreenShift.Models;
using ScreenShift.Native;

namespace ScreenShift.Services;

/// <summary>
/// Turns QueryDisplayConfig output into <see cref="MonitorInfo"/> objects.
/// </summary>
/// <remarks>
/// The awkward part of the CCD API is that a "monitor" is not a record it returns. What it returns
/// is <em>paths</em>: every legal pairing of a desktop surface (source) with a connector (target).
/// A three-monitor machine typically produces dozens of them, most inactive. So the work here is:
/// group paths by target, pick the one path per target that represents reality, and resolve the
/// mode records it points at.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class DisplayService : IDisplayService
{
    /// <summary>Sentinel Windows uses in modeInfoIdx when a path has no mode record.</summary>
    private const uint ModeIndexInvalid = 0xFFFFFFFF;

    private readonly WindowsDisplayApi _api;
    private readonly IAppLogger _logger;

    public DisplayService(IAppLogger logger)
    {
        _logger = logger;
        _api = new WindowsDisplayApi(logger);
    }

    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var stopwatch = Stopwatch.StartNew();

        // ALL_PATHS rather than ONLY_ACTIVE_PATHS: a monitor the user has switched off still needs
        // to appear in the list, otherwise there is no way to build a profile that turns it back on.
        var snapshot = _api.Query(NativeConstants.QDC_ALL_PATHS);

        var cloneGroupSizes = CountTargetsPerActiveSource(snapshot);
        var adapterPaths = new Dictionary<LUID, string?>();
        var monitors = new List<MonitorInfo>();

        foreach (var group in GroupPathsByTarget(snapshot))
        {
            var monitor = BuildMonitor(group, snapshot, cloneGroupSizes, adapterPaths);
            if (monitor is not null)
            {
                monitors.Add(monitor);
            }
        }

        var ordered = OrderForDisplay(monitors);

        stopwatch.Stop();
        _logger.Info($"Enumerated {ordered.Count} monitor(s) from {snapshot.Paths.Length} path(s) in {stopwatch.ElapsedMilliseconds} ms.");
        foreach (var monitor in ordered)
        {
            _logger.Debug($"  {monitor} pos={monitor.Position?.ToString() ?? "—"} primary={monitor.IsPrimary} conn={monitor.Connection} path={monitor.DevicePath}");
        }

        return ordered;
    }

    /// <summary>
    /// Collapses the path list to one entry per physical target, preferring the active path.
    /// Grouping on (adapter, target) rather than on the friendly name is what keeps two identical
    /// monitors apart — they report the same name and EDID product code, but never the same target id.
    /// </summary>
    private static IEnumerable<DISPLAYCONFIG_PATH_INFO> GroupPathsByTarget(DisplayConfigSnapshot snapshot)
    {
        var chosen = new Dictionary<(LUID Adapter, uint Target), DISPLAYCONFIG_PATH_INFO>();

        foreach (var path in snapshot.Paths)
        {
            // Targets that are not plugged in are stale entries from Windows' persistence
            // database — a monitor that used to be on this port. Showing them would be noise.
            if (!path.targetInfo.IsConnected)
            {
                continue;
            }

            var key = (path.targetInfo.adapterId, path.targetInfo.id);

            if (!chosen.TryGetValue(key, out var existing))
            {
                chosen[key] = path;
                continue;
            }

            // An active path always wins: it is the only one carrying real mode information.
            if (path.IsActive && !existing.IsActive)
            {
                chosen[key] = path;
            }
        }

        return chosen.Values;
    }

    /// <summary>
    /// Counts how many active targets each source drives. More than one means those targets are
    /// cloned — they share a desktop surface, so they also share position and resolution.
    /// </summary>
    private static Dictionary<(LUID Adapter, uint Source), int> CountTargetsPerActiveSource(DisplayConfigSnapshot snapshot)
    {
        var counts = new Dictionary<(LUID, uint), int>();

        foreach (var path in snapshot.Paths)
        {
            if (!path.IsActive)
            {
                continue;
            }

            var key = (path.sourceInfo.adapterId, path.sourceInfo.id);
            counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;
        }

        return counts;
    }

    private MonitorInfo? BuildMonitor(
        DISPLAYCONFIG_PATH_INFO path,
        DisplayConfigSnapshot snapshot,
        Dictionary<(LUID Adapter, uint Source), int> cloneGroupSizes,
        Dictionary<LUID, string?> adapterPathCache)
    {
        var adapterId = path.targetInfo.adapterId;
        var targetId = path.targetInfo.id;
        var isActive = path.IsActive;

        string devicePath = string.Empty;
        string? edidVendor = null;
        ushort? edidProduct = null;
        string? edidName = null;

        if (_api.TryGetTargetDeviceName(adapterId, targetId, out var targetName))
        {
            devicePath = targetName.monitorDevicePath ?? string.Empty;

            var reported = targetName.monitorFriendlyDeviceName;
            if (!string.IsNullOrWhiteSpace(reported))
            {
                edidName = reported.Trim();
            }

            if (targetName.EdidIdsValid)
            {
                edidVendor = DecodeEdidVendor(targetName.edidManufactureId);
                edidProduct = targetName.edidProductCodeId;
            }
        }

        _api.TryGetSourceDeviceName(adapterId, path.sourceInfo.id, out var gdiName);
        var displayNumber = ParseDisplayNumber(gdiName);

        if (!adapterPathCache.TryGetValue(adapterId, out var adapterPath))
        {
            adapterPath = _api.TryGetAdapterDevicePath(adapterId, out var resolved) ? resolved : null;
            adapterPathCache[adapterId] = adapterPath;
        }

        DisplayResolution? resolution = null;
        DisplayResolution? signalResolution = null;
        DisplayPosition? position = null;
        var refreshRate = RefreshRate.Unknown;
        var interlaced = false;

        if (isActive)
        {
            if (TryResolveSourceMode(snapshot, path, out var sourceMode))
            {
                resolution = new DisplayResolution((int)sourceMode.width, (int)sourceMode.height);
                position = new DisplayPosition(sourceMode.position.x, sourceMode.position.y);
            }
            else
            {
                _logger.Warn($"Active path for target {targetId} on adapter {adapterId} has no usable source mode (modeInfoIdx={path.sourceInfo.modeInfoIdx}); resolution and position will show as unknown.");
            }

            if (TryResolveTargetMode(snapshot, path, out var targetMode))
            {
                var signal = targetMode.targetVideoSignalInfo;
                signalResolution = new DisplayResolution((int)signal.activeSize.cx, (int)signal.activeSize.cy);
                interlaced = signal.scanLineOrdering is DISPLAYCONFIG_SCANLINE_ORDERING.Interlaced
                    or DISPLAYCONFIG_SCANLINE_ORDERING.InterlacedLowerFieldFirst;

                if (signal.VSyncFreqDivider > 1)
                {
                    _logger.Debug($"Target {targetId} reports vSyncFreqDivider={signal.VSyncFreqDivider}; the signal rate and the reported refresh rate may differ.");
                }
            }

            refreshRate = ResolveRefreshRate(path, snapshot);
        }

        var friendlyName = edidName
            ?? (displayNumber is { } n ? $"Display {n}" : $"Monitor on target {targetId}");

        var isCloned = isActive
            && cloneGroupSizes.TryGetValue((path.sourceInfo.adapterId, path.sourceInfo.id), out var siblings)
            && siblings > 1;

        return new MonitorInfo
        {
            DevicePath = devicePath,
            FriendlyName = friendlyName,
            HasEdidName = edidName is not null,
            GdiDeviceName = string.IsNullOrEmpty(gdiName) ? null : gdiName,
            DisplayNumber = displayNumber,
            AdapterId = adapterId.ToString(),
            AdapterDevicePath = adapterPath,
            TargetId = targetId,
            SourceId = path.sourceInfo.id,
            EdidVendorCode = edidVendor,
            EdidProductCode = edidProduct,
            IsConnected = true,
            IsEnabled = isActive,
            // Windows anchors the primary display at the desktop origin. Every other monitor is
            // positioned relative to it, which is also why coordinates can be negative.
            IsPrimary = isActive && position is { X: 0, Y: 0 },
            Resolution = resolution,
            SignalResolution = signalResolution,
            RefreshRate = refreshRate,
            Orientation = MapOrientation(path.targetInfo.rotation),
            Position = position,
            Connection = MapConnection(path.targetInfo.outputTechnology),
            IsInterlaced = interlaced,
            IsCloned = isCloned,
        };
    }

    /// <summary>
    /// The path's own refresh rate is the authoritative one. The target mode's vertical sync
    /// frequency is only consulted when the path leaves it blank, which happens on some paths
    /// reported through QDC_ALL_PATHS.
    /// </summary>
    private RefreshRate ResolveRefreshRate(DISPLAYCONFIG_PATH_INFO path, DisplayConfigSnapshot snapshot)
    {
        var fromPath = path.targetInfo.refreshRate;
        if (fromPath.Denominator != 0 && fromPath.Numerator != 0)
        {
            return new RefreshRate(fromPath.Numerator, fromPath.Denominator);
        }

        if (TryResolveTargetMode(snapshot, path, out var targetMode))
        {
            var vSync = targetMode.targetVideoSignalInfo.vSyncFreq;
            if (vSync.Denominator != 0 && vSync.Numerator != 0)
            {
                return new RefreshRate(vSync.Numerator, vSync.Denominator);
            }
        }

        _logger.Warn($"No refresh rate available for target {path.targetInfo.id} on adapter {path.targetInfo.adapterId}.");
        return RefreshRate.Unknown;
    }

    /// <summary>
    /// Dereferences a path's source mode index, checking that the record found really belongs to
    /// this path. Some drivers leave a stale index behind on inactive paths, and following it blindly
    /// would attribute another monitor's resolution to this one.
    /// </summary>
    private static bool TryResolveSourceMode(
        DisplayConfigSnapshot snapshot,
        DISPLAYCONFIG_PATH_INFO path,
        out DISPLAYCONFIG_SOURCE_MODE mode)
    {
        mode = default;
        var index = path.sourceInfo.modeInfoIdx;

        if (index == ModeIndexInvalid || index >= snapshot.Modes.Length)
        {
            return false;
        }

        var candidate = snapshot.Modes[index];
        if (candidate.infoType != DISPLAYCONFIG_MODE_INFO_TYPE.Source
            || candidate.id != path.sourceInfo.id
            || !candidate.adapterId.Equals(path.sourceInfo.adapterId))
        {
            return false;
        }

        mode = candidate.mode.sourceMode;
        return true;
    }

    /// <summary>Target-mode counterpart of <see cref="TryResolveSourceMode"/>, with the same validation.</summary>
    private static bool TryResolveTargetMode(
        DisplayConfigSnapshot snapshot,
        DISPLAYCONFIG_PATH_INFO path,
        out DISPLAYCONFIG_TARGET_MODE mode)
    {
        mode = default;
        var index = path.targetInfo.modeInfoIdx;

        if (index == ModeIndexInvalid || index >= snapshot.Modes.Length)
        {
            return false;
        }

        var candidate = snapshot.Modes[index];
        if (candidate.infoType != DISPLAYCONFIG_MODE_INFO_TYPE.Target
            || candidate.id != path.targetInfo.id
            || !candidate.adapterId.Equals(path.targetInfo.adapterId))
        {
            return false;
        }

        mode = candidate.mode.targetMode;
        return true;
    }

    /// <summary>Left to right, then top to bottom, with disabled monitors collected at the end.</summary>
    private static List<MonitorInfo> OrderForDisplay(List<MonitorInfo> monitors) =>
        monitors
            .OrderByDescending(m => m.IsEnabled)
            .ThenBy(m => m.Position?.X ?? int.MaxValue)
            .ThenBy(m => m.Position?.Y ?? int.MaxValue)
            .ThenBy(m => m.DisplayNumber ?? int.MaxValue)
            .ThenBy(m => m.FriendlyName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    private static DisplayOrientation MapOrientation(DISPLAYCONFIG_ROTATION rotation) => rotation switch
    {
        DISPLAYCONFIG_ROTATION.Rotate90 => DisplayOrientation.Portrait,
        DISPLAYCONFIG_ROTATION.Rotate180 => DisplayOrientation.LandscapeFlipped,
        DISPLAYCONFIG_ROTATION.Rotate270 => DisplayOrientation.PortraitFlipped,
        _ => DisplayOrientation.Landscape,
    };

    private static MonitorConnection MapConnection(DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY tech) => tech switch
    {
        DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.Internal => MonitorConnection.Internal,
        DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.Lvds => MonitorConnection.Internal,
        DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.Hd15 => MonitorConnection.Vga,
        DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.Dvi => MonitorConnection.Dvi,
        DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.Hdmi => MonitorConnection.Hdmi,
        DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DisplayPortExternal => MonitorConnection.DisplayPort,
        DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DisplayPortEmbedded => MonitorConnection.Internal,
        // A USB-C dock in DisplayPort alt mode shows up here rather than as DisplayPortExternal.
        DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DisplayPortUsbTunnel => MonitorConnection.UsbC,
        DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.UdiExternal => MonitorConnection.Other,
        DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.UdiEmbedded => MonitorConnection.Internal,
        DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.Miracast => MonitorConnection.Wireless,
        DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.IndirectWired => MonitorConnection.Virtual,
        DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.IndirectVirtual => MonitorConnection.Virtual,
        DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.SVideo => MonitorConnection.Composite,
        DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.CompositeVideo => MonitorConnection.Composite,
        DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.ComponentVideo => MonitorConnection.Composite,
        DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.Sdi => MonitorConnection.Other,
        DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.SdtvDongle => MonitorConnection.Other,
        DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DJpn => MonitorConnection.Other,
        _ => MonitorConnection.Unknown,
    };

    /// <summary>Pulls the 1 out of <c>\\.\DISPLAY1</c>.</summary>
    private static int? ParseDisplayNumber(string? gdiDeviceName)
    {
        if (string.IsNullOrEmpty(gdiDeviceName))
        {
            return null;
        }

        var digits = new StringBuilder();
        for (var i = gdiDeviceName.Length - 1; i >= 0 && char.IsAsciiDigit(gdiDeviceName[i]); i--)
        {
            digits.Insert(0, gdiDeviceName[i]);
        }

        return digits.Length > 0 && int.TryParse(digits.ToString(), out var number) ? number : null;
    }

    /// <summary>
    /// Unpacks the three-letter PNP vendor code from an EDID manufacturer id. EDID packs it as five
    /// bits per letter into a big-endian word, so the bytes are swapped before unpacking. Returns
    /// null rather than mojibake if the result is not three plausible letters.
    /// </summary>
    private static string? DecodeEdidVendor(ushort manufactureId)
    {
        var swapped = (ushort)((manufactureId >> 8) | (manufactureId << 8));

        var c1 = (char)('A' + ((swapped >> 10) & 0x1F) - 1);
        var c2 = (char)('A' + ((swapped >> 5) & 0x1F) - 1);
        var c3 = (char)('A' + (swapped & 0x1F) - 1);

        return char.IsAsciiLetterUpper(c1) && char.IsAsciiLetterUpper(c2) && char.IsAsciiLetterUpper(c3)
            ? new string([c1, c2, c3])
            : null;
    }
}
