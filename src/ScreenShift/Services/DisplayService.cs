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

    // ------------------------------------------------------------------
    // Phase 2: reading the mode list, and writing changes back.
    // ------------------------------------------------------------------

    public IReadOnlyList<DisplayMode> GetSupportedModes(MonitorInfo monitor)
    {
        if (monitor.GdiDeviceName is not { } device)
        {
            _logger.Debug($"{monitor.FriendlyName} has no GDI device name (it is disabled), so it has no mode list.");
            return [];
        }

        var modes = _api.EnumerateModes(device)
            .Where(m => m.dmBitsPerPel == 32)
            // 0 and 1 are the driver's way of saying "hardware default"; they are not selectable rates.
            .Where(m => m.dmDisplayFrequency > 1)
            .Where(m => m.dmPelsWidth > 0 && m.dmPelsHeight > 0)
            .Select(m => new DisplayMode((int)m.dmPelsWidth, (int)m.dmPelsHeight, m.dmDisplayFrequency))
            .Distinct()
            .OrderByDescending(m => m.PixelCount)
            .ThenByDescending(m => m.RefreshHz)
            .ToList();

        _logger.Info($"{monitor.FriendlyName} ({device}) offers {modes.Count} distinct 32-bpp modes.");
        return modes;
    }

    /// <summary>
    /// The mode GDI reports as currently applied.
    /// </summary>
    /// <remarks>
    /// Worth asking GDI rather than deriving this from <see cref="MonitorInfo.RefreshRate"/>:
    /// DEVMODE truncates the rate, so a 59.94 Hz display reads as 59 here but rounds to 60 from the
    /// CCD value. Only the GDI number is guaranteed to appear in the GDI mode list, which is what a
    /// picker has to match against.
    /// </remarks>
    public DisplayMode? GetCurrentMode(MonitorInfo monitor)
    {
        if (monitor.GdiDeviceName is not { } device)
        {
            return null;
        }

        if (!_api.TryGetCurrentMode(device, out var mode))
        {
            return null;
        }

        return new DisplayMode((int)mode.dmPelsWidth, (int)mode.dmPelsHeight, mode.dmDisplayFrequency);
    }

    public DisplaySnapshot CaptureSnapshot()
    {
        var entries = new List<SavedDisplayMode>();

        foreach (var monitor in GetMonitors())
        {
            if (!monitor.IsEnabled || monitor.GdiDeviceName is not { } device)
            {
                continue;
            }

            if (!_api.TryGetCurrentMode(device, out var mode))
            {
                _logger.Warn($"Could not capture the current mode for {monitor.FriendlyName} ({device}); it will not be restorable.");
                continue;
            }

            entries.Add(new SavedDisplayMode(device, mode, monitor.IsPrimary));
        }

        // ALL_PATHS so the snapshot records which monitors were switched off, not just how the
        // switched-on ones were configured. Without this a topology change could not be undone.
        DisplayConfigSnapshot? configuration = null;
        try
        {
            configuration = _api.Query(NativeConstants.QDC_ALL_PATHS);
        }
        catch (DisplayConfigException ex)
        {
            _logger.Warn($"Could not capture the display path configuration; a topology change would not be undoable: {ex.Message}");
        }

        var snapshot = new DisplaySnapshot(entries, configuration);
        _logger.Info($"Captured display snapshot: {snapshot}.");
        return snapshot;
    }

    public ApplyResult Apply(IReadOnlyList<MonitorChangeRequest> requests)
    {
        var effective = requests.Where(r => r.ChangesAnything).ToList();
        if (effective.Count == 0)
        {
            return ApplyResult.Ok();
        }

        // Switching monitors on or off comes first and on its own. It is a topology change, so it
        // reshuffles source ids and GDI device names — which means anything resolved before it runs
        // is stale afterwards, and the mode stage has to re-resolve its targets.
        var topologyRequests = effective.Where(r => r.Enabled is not null).ToList();

        if (topologyRequests.Count > 0)
        {
            // The intended primary matters to the topology stage: if the current primary is being
            // switched off, the remaining displays have to be re-anchored around the future one.
            var futurePrimary = effective.FirstOrDefault(r => r.MakePrimary)?.Monitor;

            var topologyResult = ApplyTopology(topologyRequests, futurePrimary);
            if (!topologyResult.Succeeded)
            {
                return topologyResult;
            }

            effective = ReResolve(effective);

            if (effective.Count == 0)
            {
                return ApplyResult.Ok();
            }
        }

        var primaryRequests = effective.Where(r => r.MakePrimary).ToList();
        if (primaryRequests.Count > 1)
        {
            return ApplyResult.Fail("Only one display can be the primary.");
        }

        _logger.Info($"Applying: {string.Join(" | ", effective)}");

        // Read every enabled display, not just the ones being changed — a primary switch moves all
        // of them, because the primary is what defines the desktop origin.
        var monitors = GetMonitors().Where(m => m is { IsEnabled: true, GdiDeviceName: not null }).ToList();
        var working = new Dictionary<string, DEVMODE>(StringComparer.OrdinalIgnoreCase);

        foreach (var monitor in monitors)
        {
            if (!_api.TryGetCurrentMode(monitor.GdiDeviceName!, out var mode))
            {
                return ApplyResult.Fail($"Could not read the current mode for {monitor.FriendlyName}.");
            }

            working[monitor.GdiDeviceName!] = mode;
        }

        var original = new Dictionary<string, DEVMODE>(working, StringComparer.OrdinalIgnoreCase);
        var originalPrimary = monitors.FirstOrDefault(m => m.IsPrimary)?.GdiDeviceName;
        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // --- 1. resolution and refresh rate -------------------------------
        foreach (var request in effective)
        {
            if (request.Monitor.GdiDeviceName is not { } device || !working.TryGetValue(device, out var mode))
            {
                return ApplyResult.Fail($"{request.Monitor.FriendlyName} is not currently enabled, so its mode cannot be changed.");
            }

            if (!request.ChangesMode)
            {
                continue;
            }

            // Width, height, depth and rate are always named. They are carried over from the read,
            // so restating them changes nothing, and leaving any of them out invites the driver to
            // substitute a default.
            var fields = DeviceModeConstants.DM_BITSPERPEL
                | DeviceModeConstants.DM_PELSWIDTH
                | DeviceModeConstants.DM_PELSHEIGHT
                | DeviceModeConstants.DM_DISPLAYFREQUENCY;

            if (request.Orientation is { } orientation)
            {
                // DEVMODE's width and height are in desktop space, so a quarter turn transposes
                // them. Turning a display without swapping these asks for a 2560-wide desktop on a
                // panel that is now 1440 wide, which the driver rejects.
                var wasRotated = mode.dmDisplayOrientation is DeviceModeConstants.DMDO_90
                    or DeviceModeConstants.DMDO_270;
                var willBeRotated = orientation is DisplayOrientation.Portrait
                    or DisplayOrientation.PortraitFlipped;

                if (wasRotated != willBeRotated)
                {
                    (mode.dmPelsWidth, mode.dmPelsHeight) = (mode.dmPelsHeight, mode.dmPelsWidth);
                }

                mode.dmDisplayOrientation = ToDeviceOrientation(orientation);
                fields |= DeviceModeConstants.DM_DISPLAYORIENTATION;
            }

            // After the orientation swap, so an explicit resolution is read as being in the
            // orientation the display is going to end up in.
            if (request.Resolution is { } resolution)
            {
                mode.dmPelsWidth = (uint)resolution.Width;
                mode.dmPelsHeight = (uint)resolution.Height;
            }

            if (request.RefreshHz is { } hz)
            {
                mode.dmDisplayFrequency = hz;
            }

            if (request.Position is { } position)
            {
                mode.dmPosition.x = position.X;
                mode.dmPosition.y = position.Y;
                fields |= DeviceModeConstants.DM_POSITION;
            }

            mode.dmFields = fields;

            working[device] = mode;
            changed.Add(device);
        }

        // --- 2. validate before touching anything --------------------------
        // CDS_TEST asks the driver whether it would accept the mode. Doing this for the whole batch
        // up front means a bad mode is rejected while every display is still showing a picture.
        foreach (var device in changed)
        {
            var candidate = working[device];

            // Position is not a property of the graphics mode, and including it here makes the
            // test fail on transient overlaps that the real apply resolves. Only ask the driver
            // about the thing it is actually being asked to display.
            candidate.dmFields &= ~DeviceModeConstants.DM_POSITION;

            var testResult = _api.TestMode(device, ref candidate);

            if (testResult != DeviceModeConstants.DISP_CHANGE_SUCCESSFUL)
            {
                var description = DeviceModeConstants.DescribeChangeResult(testResult);
                _logger.Warn($"Rejected {candidate.dmPelsWidth} × {candidate.dmPelsHeight} @ {candidate.dmDisplayFrequency} Hz for {device}: {description}.");

                return ApplyResult.Fail(
                    $"{DescribeDevice(monitors, device)} will not accept {candidate.dmPelsWidth} × {candidate.dmPelsHeight} at {candidate.dmDisplayFrequency} Hz ({description}). Nothing was changed.");
            }
        }

        // --- 3. primary display ---------------------------------------------
        string? newPrimaryDevice = null;

        if (primaryRequests.Count == 1)
        {
            var target = primaryRequests[0].Monitor;

            if (target.GdiDeviceName is not { } device || !working.ContainsKey(device))
            {
                return ApplyResult.Fail($"{target.FriendlyName} is not currently enabled, so it cannot become the primary display.");
            }

            newPrimaryDevice = device;

            // Windows defines the primary as the display at (0,0), so making a different display
            // primary means translating the entire desktop by that display's current offset.
            var origin = working[device].dmPosition;

            foreach (var key in working.Keys.ToList())
            {
                var mode = working[key];

                mode.dmPosition.x -= origin.x;
                mode.dmPosition.y -= origin.y;
                mode.dmFields |= DeviceModeConstants.DM_POSITION;

                working[key] = mode;
                changed.Add(key);
            }

            _logger.Info($"Making {target.FriendlyName} primary; translating all displays by ({-origin.x}, {-origin.y}).");
        }

        // --- 4. write the changes out ----------------------------------------
        var push = PushChanges(changed, working, newPrimaryDevice);

        if (!push.Ok)
        {
            _logger.Error($"Apply failed ({push.Error}). Rolling back.");

            var rolledBack = RollBack(original, originalPrimary);
            return ApplyResult.Fail($"Windows refused the change ({push.Error}).", rolledBack);
        }

        // Applying and saving are separate operations that fail independently, so the save is a
        // deliberate second step. A change that applied but did not save is still a success from
        // the user's point of view — it just will not survive a reboot — so this never fails the apply.
        var persisted = PersistCurrentConfiguration();
        if (!persisted.Succeeded)
        {
            _logger.Warn($"Applied, but the configuration could not be saved: {persisted.Message} It will not survive a reboot.");
        }

        _logger.Info("Apply committed successfully.");
        return ApplyResult.Ok();
    }

    /// <summary>
    /// Switches monitors on and off by editing the CCD path table.
    /// </summary>
    /// <remarks>
    /// GDI has no honest way to express this — its trick for "off" is a zero-sized mode, which is a
    /// side effect rather than the operation. In CCD a monitor being on is literally a flag on the
    /// path connecting a desktop surface to a connector, so enabling and disabling is setting and
    /// clearing that flag and letting Windows work out the consequences.
    /// </remarks>
    private ApplyResult ApplyTopology(IReadOnlyList<MonitorChangeRequest> requests, MonitorInfo? futurePrimary)
    {
        // ALL_PATHS, not ONLY_ACTIVE_PATHS: a monitor that is currently off has no active path, so
        // querying only active ones would leave nothing to switch back on.
        var snapshot = _api.Query(NativeConstants.QDC_ALL_PATHS);
        var paths = snapshot.Paths;

        if (paths.Length == 0)
        {
            return ApplyResult.Fail("Windows reported no display paths at all.");
        }

        var activeCount = paths.Count(p => p.IsActive);
        var changed = 0;

        foreach (var request in requests)
        {
            var wantEnabled = request.Enabled!.Value;
            var monitor = request.Monitor;

            if (monitor.IsEnabled == wantEnabled)
            {
                continue;
            }

            var index = FindPathIndex(paths, monitor, wantEnabled);
            if (index < 0)
            {
                return ApplyResult.Fail(
                    $"Could not find a display path for {monitor.FriendlyName}, so it cannot be switched {(wantEnabled ? "on" : "off")}.");
            }

            if (!wantEnabled && activeCount <= 1)
            {
                return ApplyResult.Fail(
                    $"{monitor.FriendlyName} is the only display still switched on. Turning it off would leave you with no screen at all.");
            }

            var path = paths[index];

            if (wantEnabled)
            {
                path.flags |= NativeConstants.DISPLAYCONFIG_PATH_ACTIVE;
                activeCount++;
            }
            else
            {
                path.flags &= ~NativeConstants.DISPLAYCONFIG_PATH_ACTIVE;
                activeCount--;
            }

            // Drop the mode references on the path being changed. A monitor coming on has no mode
            // record yet, and one going off leaves a stale reference behind; invalidating both lets
            // Windows pick sensible modes and positions under SDC_ALLOW_CHANGES.
            path.sourceInfo.modeInfoIdx = ModeIndexInvalid;
            path.targetInfo.modeInfoIdx = ModeIndexInvalid;

            paths[index] = path;
            changed++;

            _logger.Info($"Switching {monitor.FriendlyName} {(wantEnabled ? "on" : "off")} (adapter {monitor.AdapterId}, target {monitor.TargetId}).");
        }

        if (changed == 0)
        {
            return ApplyResult.Ok();
        }

        EnsureOriginAnchored(snapshot, futurePrimary);

        // SDC_ALLOW_CHANGES is what makes this workable: it lets Windows lay out the desktop around
        // the monitors that are now on, rather than demanding a complete, self-consistent
        // configuration that we would otherwise have to compute by hand.
        const uint SharedFlags = NativeConstants.SDC_USE_SUPPLIED_DISPLAY_CONFIG
            | NativeConstants.SDC_ALLOW_CHANGES;

        var validate = _api.SetConfiguration(snapshot, NativeConstants.SDC_VALIDATE | SharedFlags);

        if (validate != NativeConstants.ERROR_SUCCESS)
        {
            _logger.Warn($"Topology change rejected at validation: {validate} ({DisplayConfigException.DescribeError(validate)}).");
            return ApplyResult.Fail(
                $"Windows will not accept that combination of displays ({DisplayConfigException.DescribeError(validate)}). Nothing was changed.");
        }

        var apply = _api.SetConfiguration(
            snapshot,
            NativeConstants.SDC_APPLY | NativeConstants.SDC_SAVE_TO_DATABASE | SharedFlags);

        if (apply != NativeConstants.ERROR_SUCCESS)
        {
            _logger.Error($"Topology change failed on apply: {apply} ({DisplayConfigException.DescribeError(apply)}).");
            return ApplyResult.Fail(
                $"Windows refused the change ({DisplayConfigException.DescribeError(apply)}).");
        }

        _logger.Info($"Topology change applied and saved ({changed} monitor(s) switched).");
        return ApplyResult.Ok();
    }

    /// <summary>
    /// Guarantees the supplied configuration still has a source at the desktop origin.
    /// </summary>
    /// <remarks>
    /// Windows rejects a supplied configuration with no source at (0,0) as ERROR_INVALID_PARAMETER,
    /// because the origin is what defines the primary display — and disabling the current primary
    /// rips the origin out. The remaining active sources are translated as a block until the
    /// intended future primary (or, without one, the first remaining display) sits at the origin.
    /// Only the supplied arrays are edited; the exact final positions are the mode stage's job,
    /// and its requests carry absolute coordinates that land on top of this unchanged.
    /// </remarks>
    private void EnsureOriginAnchored(DisplayConfigSnapshot snapshot, MonitorInfo? futurePrimary)
    {
        var paths = snapshot.Paths;
        var modes = snapshot.Modes;

        var sourceModeIndices = new List<int>();
        var seenSources = new HashSet<(string Adapter, uint Source)>();
        var anchorIndex = -1;

        foreach (var path in paths)
        {
            if (!path.IsActive)
            {
                continue;
            }

            var index = path.sourceInfo.modeInfoIdx;
            if (index == ModeIndexInvalid
                || index >= modes.Length
                || modes[index].infoType != DISPLAYCONFIG_MODE_INFO_TYPE.Source)
            {
                continue;
            }

            if (seenSources.Add((path.sourceInfo.adapterId.ToString(), path.sourceInfo.id)))
            {
                sourceModeIndices.Add((int)index);
            }

            if (futurePrimary is not null
                && path.targetInfo.id == futurePrimary.TargetId
                && path.targetInfo.adapterId.ToString() == futurePrimary.AdapterId)
            {
                anchorIndex = (int)index;
            }
        }

        if (sourceModeIndices.Count == 0
            || sourceModeIndices.Any(i => modes[i].mode.sourceMode.position is { x: 0, y: 0 }))
        {
            return;
        }

        if (anchorIndex < 0)
        {
            anchorIndex = sourceModeIndices[0];
        }

        var origin = modes[anchorIndex].mode.sourceMode.position;
        _logger.Info($"No display left at the desktop origin; translating {sourceModeIndices.Count} source(s) by ({-origin.x}, {-origin.y}).");

        foreach (var index in sourceModeIndices)
        {
            modes[index].mode.sourceMode.position.x -= origin.x;
            modes[index].mode.sourceMode.position.y -= origin.y;
        }
    }

    /// <summary>
    /// Finds the path to flip for a monitor. When switching one off, the active path is the one to
    /// clear. When switching one on, it has to be a path whose desktop surface is free.
    /// </summary>
    /// <remarks>
    /// The source is what makes this more than a lookup. Every target has a path for each source on
    /// the adapter, and picking one whose source is already driving another display does not extend
    /// the desktop — it clones the two monitors together onto one surface. Choosing a free source is
    /// the difference between "turn this monitor on" and "mirror that other monitor onto it".
    /// </remarks>
    private static int FindPathIndex(DISPLAYCONFIG_PATH_INFO[] paths, MonitorInfo monitor, bool wantEnabled)
    {
        var occupiedSources = new HashSet<(string Adapter, uint Source)>();

        foreach (var path in paths)
        {
            if (path.IsActive && path.targetInfo.id != monitor.TargetId)
            {
                occupiedSources.Add((path.sourceInfo.adapterId.ToString(), path.sourceInfo.id));
            }
        }

        var fallback = -1;

        for (var i = 0; i < paths.Length; i++)
        {
            var path = paths[i];

            if (path.targetInfo.id != monitor.TargetId
                || path.targetInfo.adapterId.ToString() != monitor.AdapterId)
            {
                continue;
            }

            if (path.IsActive == wantEnabled)
            {
                fallback = i;
                continue;
            }

            if (!wantEnabled)
            {
                return i;
            }

            if (!occupiedSources.Contains((path.sourceInfo.adapterId.ToString(), path.sourceInfo.id)))
            {
                return i;
            }

            // Right target, but its surface is taken; keep looking for one that is free.
            fallback = fallback < 0 ? i : fallback;
        }

        return fallback;
    }

    /// <summary>
    /// Rebuilds the outstanding requests against freshly read monitors after a topology change.
    /// Matching is by stable id, since display numbers and GDI names will have moved around.
    /// </summary>
    private List<MonitorChangeRequest> ReResolve(IEnumerable<MonitorChangeRequest> requests)
    {
        var current = GetMonitors().ToDictionary(m => m.StableKey, StringComparer.OrdinalIgnoreCase);
        var rebuilt = new List<MonitorChangeRequest>();

        foreach (var request in requests)
        {
            if (!request.ChangesMode && !request.MakePrimary)
            {
                continue;
            }

            if (!current.TryGetValue(request.Monitor.StableKey, out var monitor) || !monitor.IsEnabled)
            {
                _logger.Warn($"{request.Monitor.FriendlyName} is no longer available after the topology change; skipping the rest of its settings.");
                continue;
            }

            rebuilt.Add(new MonitorChangeRequest
            {
                Monitor = monitor,
                Resolution = request.Resolution,
                RefreshHz = request.RefreshHz,
                Orientation = request.Orientation,
                Position = request.Position,
                MakePrimary = request.MakePrimary,
            });
        }

        return rebuilt;
    }

    /// <summary>
    /// Writes a set of modes out, preferring one atomic switch and falling back to per-display
    /// application if the driver will not take the batch.
    /// </summary>
    /// <param name="allowDynamicFallback">
    /// False when the caller specifically needs the configuration written to the registry, so a
    /// change that applies but does not persist should still count as a failure.
    /// </param>
    private (bool Ok, string? Error) PushChanges(
        IReadOnlyCollection<string> devices,
        IReadOnlyDictionary<string, DEVMODE> modes,
        string? primaryDevice,
        bool allowDynamicFallback = true)
    {
        var staged = PushChangesOnce(devices, modes, primaryDevice, PushStrategy.StagedBatch);
        if (staged.Ok)
        {
            return staged;
        }

        // A staged batch asks the driver to validate a whole pending configuration at once, and it
        // refuses the entire batch if any intermediate state looks wrong. One display at a time is
        // uglier to watch, but each step stands on its own.
        _logger.Warn($"Staged application was refused ({staged.Error}); retrying one display at a time.");

        var immediate = PushChangesOnce(devices, modes, primaryDevice, PushStrategy.ImmediatePersisted);
        if (immediate.Ok || !allowDynamicFallback)
        {
            return immediate;
        }

        // Last resort: skip the registry write. Persisting the mode is a separate failure mode from
        // applying it, and a configuration that lasts until the next reboot is far better than
        // leaving the user staring at one that is wrong now.
        _logger.Warn($"Persisted application was refused ({immediate.Error}); retrying without writing to the registry.");

        var dynamicResult = PushChangesOnce(devices, modes, primaryDevice, PushStrategy.DynamicOnly);
        if (dynamicResult.Ok)
        {
            _logger.Warn("Applied dynamically. The configuration is active but was not persisted, so a reboot will undo it.");
        }

        return dynamicResult;
    }

    private enum PushStrategy
    {
        /// <summary>Stage every display, then commit them together.</summary>
        StagedBatch,

        /// <summary>Apply each display on its own, persisting to the registry.</summary>
        ImmediatePersisted,

        /// <summary>Apply each display on its own without persisting.</summary>
        DynamicOnly,
    }

    private (bool Ok, string? Error) PushChangesOnce(
        IReadOnlyCollection<string> devices,
        IReadOnlyDictionary<string, DEVMODE> modes,
        string? primaryDevice,
        PushStrategy strategy)
    {
        // The display that is to be primary goes first. Windows insists on a primary sitting at the
        // desktop origin, so moving any other display before that anchor exists leaves the
        // configuration momentarily without one — which the driver rejects.
        var ordered = devices
            .OrderByDescending(d => string.Equals(d, primaryDevice, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var device in ordered)
        {
            if (!modes.TryGetValue(device, out var mode))
            {
                continue;
            }

            var extraFlags = string.Equals(device, primaryDevice, StringComparison.OrdinalIgnoreCase)
                ? DeviceModeConstants.CDS_SET_PRIMARY
                : 0u;

            var result = strategy switch
            {
                PushStrategy.StagedBatch => _api.StageMode(device, ref mode, extraFlags),
                PushStrategy.ImmediatePersisted => _api.ApplyModeImmediately(device, ref mode, extraFlags),
                _ => _api.ApplyModeImmediately(device, ref mode, extraFlags, persist: false),
            };

            if (result != DeviceModeConstants.DISP_CHANGE_SUCCESSFUL)
            {
                return (false, $"{device}: {DeviceModeConstants.DescribeChangeResult(result)}");
            }
        }

        if (strategy != PushStrategy.StagedBatch)
        {
            return (true, null);
        }

        var commit = _api.CommitStagedChanges();

        return commit == DeviceModeConstants.DISP_CHANGE_SUCCESSFUL
            ? (true, null)
            : (false, $"commit: {DeviceModeConstants.DescribeChangeResult(commit)}");
    }

    /// <summary>
    /// Writes whatever is on screen right now into the registry, so it survives a reboot.
    /// </summary>
    /// <remarks>
    /// Applying a mode and persisting it are separate operations that can fail independently. When
    /// only the dynamic apply succeeds the desktop looks right but the saved configuration still
    /// describes the old one, and the next reboot brings it back. This re-commits the live state to
    /// close that gap, and reports failure rather than falling back, since not persisting is
    /// precisely the thing it exists to fix.
    /// </remarks>
    public ApplyResult PersistCurrentConfiguration()
    {
        // The CCD database is what Windows 11 actually reads when it builds the desktop at boot.
        // Re-applying the live configuration through SetDisplayConfig with SDC_SAVE_TO_DATABASE
        // writes it there. Applying a configuration identical to the current one is not a visible
        // change, so this costs nothing on screen.
        try
        {
            var active = _api.Query(NativeConstants.QDC_ONLY_ACTIVE_PATHS);

            if (active.Paths.Length > 0)
            {
                var flags = NativeConstants.SDC_APPLY
                    | NativeConstants.SDC_USE_SUPPLIED_DISPLAY_CONFIG
                    | NativeConstants.SDC_SAVE_TO_DATABASE;

                var rc = _api.SetConfiguration(active, flags);

                if (rc == NativeConstants.ERROR_SUCCESS)
                {
                    _logger.Info("Saved the current configuration to the CCD display database.");
                    return ApplyResult.Ok();
                }

                _logger.Warn($"SetDisplayConfig(SAVE_TO_DATABASE) failed with {rc} ({DisplayConfigException.DescribeError(rc)}); falling back to the legacy registry path.");
            }
        }
        catch (DisplayConfigException ex)
        {
            _logger.Warn($"Could not read the active configuration to persist it: {ex.Message}");
        }

        // Legacy fallback. Older Windows builds do keep display settings in the registry keys that
        // ChangeDisplaySettingsEx writes, so this is still worth trying when the CCD path refuses.
        var modes = new Dictionary<string, DEVMODE>(StringComparer.OrdinalIgnoreCase);
        string? primaryDevice = null;

        foreach (var monitor in GetMonitors())
        {
            if (!monitor.IsEnabled || monitor.GdiDeviceName is not { } device)
            {
                continue;
            }

            if (!_api.TryGetCurrentMode(device, out var mode))
            {
                return ApplyResult.Fail($"Could not read the current mode for {monitor.FriendlyName}.");
            }

            mode.dmFields = DeviceModeConstants.DM_BITSPERPEL
                | DeviceModeConstants.DM_PELSWIDTH
                | DeviceModeConstants.DM_PELSHEIGHT
                | DeviceModeConstants.DM_DISPLAYFREQUENCY
                | DeviceModeConstants.DM_POSITION
                | DeviceModeConstants.DM_DISPLAYORIENTATION;

            modes[device] = mode;

            if (monitor.IsPrimary)
            {
                primaryDevice = device;
            }
        }

        if (modes.Count == 0)
        {
            return ApplyResult.Fail("There are no enabled displays to persist.");
        }

        _logger.Info($"Persisting the current configuration for {modes.Count} display(s).");

        var push = PushChanges(modes.Keys, modes, primaryDevice, allowDynamicFallback: false);

        return push.Ok
            ? ApplyResult.Ok()
            : ApplyResult.Fail($"The current configuration could not be written to the registry ({push.Error}).");
    }

    public ApplyResult Restore(DisplaySnapshot snapshot)
    {
        if (snapshot.IsEmpty)
        {
            return ApplyResult.Fail("There is nothing to restore.");
        }

        _logger.Info($"Restoring {snapshot}.");

        // The CCD path table is the only thing that can put a switched-off monitor back on, so it
        // is tried before the mode-level restore. Exact first, then permitting adjustment: an exact
        // reinstatement is preferable, but a slightly adjusted one still beats leaving it wrong.
        if (snapshot.Configuration is { } configuration && configuration.Paths.Length > 0)
        {
            const uint BaseFlags = NativeConstants.SDC_APPLY
                | NativeConstants.SDC_USE_SUPPLIED_DISPLAY_CONFIG
                | NativeConstants.SDC_SAVE_TO_DATABASE;

            var exact = _api.SetConfiguration(configuration, BaseFlags);
            if (exact == NativeConstants.ERROR_SUCCESS)
            {
                _logger.Info("Restored the previous configuration through the CCD path table.");
                return ApplyResult.Ok();
            }

            var adjusted = _api.SetConfiguration(configuration, BaseFlags | NativeConstants.SDC_ALLOW_CHANGES);
            if (adjusted == NativeConstants.ERROR_SUCCESS)
            {
                _logger.Info("Restored the previous configuration, with Windows adjusting it slightly.");
                return ApplyResult.Ok();
            }

            _logger.Warn($"CCD restore failed ({DisplayConfigException.DescribeError(adjusted)}); falling back to restoring modes only.");
        }

        var modes = snapshot.Entries.ToDictionary(e => e.GdiDeviceName, e => e.Mode, StringComparer.OrdinalIgnoreCase);
        var primary = snapshot.Entries.FirstOrDefault(e => e.WasPrimary)?.GdiDeviceName;

        return RollBack(modes, primary)
            ? ApplyResult.Ok()
            : ApplyResult.Fail("The previous display configuration could not be fully restored. See the log for details.");
    }

    /// <summary>
    /// Puts a set of saved modes back. Every field is named explicitly here — unlike a forward
    /// change, a restore is meant to reinstate the state wholesale, orientation and position included.
    /// </summary>
    /// <returns>True when everything was restored.</returns>
    private bool RollBack(IReadOnlyDictionary<string, DEVMODE> modes, string? primaryDevice)
    {
        var restorable = new Dictionary<string, DEVMODE>(StringComparer.OrdinalIgnoreCase);

        foreach (var (device, saved) in modes)
        {
            var mode = saved;

            // Unlike a forward change, a restore reinstates state wholesale, so every field it
            // captured is named explicitly rather than left to be inherited.
            mode.dmFields = DeviceModeConstants.DM_BITSPERPEL
                | DeviceModeConstants.DM_PELSWIDTH
                | DeviceModeConstants.DM_PELSHEIGHT
                | DeviceModeConstants.DM_DISPLAYFREQUENCY
                | DeviceModeConstants.DM_POSITION
                | DeviceModeConstants.DM_DISPLAYORIENTATION;

            restorable[device] = mode;
        }

        var push = PushChanges(restorable.Keys, restorable, primaryDevice);

        if (!push.Ok)
        {
            _logger.Error($"Restore failed: {push.Error}.");
            return false;
        }

        // A revert that does not stick is barely a revert: without this the reverted-from
        // configuration would still be the one waiting after a reboot.
        var persisted = PersistCurrentConfiguration();
        if (!persisted.Succeeded)
        {
            _logger.Warn($"Restored, but the configuration could not be saved: {persisted.Message}");
        }

        return true;
    }

    private static string DescribeDevice(IEnumerable<MonitorInfo> monitors, string gdiDeviceName) =>
        monitors.FirstOrDefault(m => string.Equals(m.GdiDeviceName, gdiDeviceName, StringComparison.OrdinalIgnoreCase))
            ?.FriendlyName
        ?? gdiDeviceName;

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
            // targetAvailable, not DISPLAYCONFIG_TARGET_IS_CONNECTED. Windows clears the connected
            // bit when a monitor is switched off but leaves targetAvailable set, so filtering on
            // "connected" makes disabled monitors vanish from the list entirely — and a monitor you
            // cannot see is one you cannot switch back on. An empty connector has neither flag, and
            // that is what this is meant to exclude.
            if (path.targetInfo.targetAvailable == 0)
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

        var orientation = MapOrientation(path.targetInfo.rotation);
        var isRotated = orientation is DisplayOrientation.Portrait or DisplayOrientation.PortraitFlipped;

        DisplayResolution? resolution = null;
        DisplayResolution? panelResolution = null;
        DisplayResolution? signalResolution = null;
        DisplayPosition? position = null;
        var refreshRate = RefreshRate.Unknown;
        var interlaced = false;

        if (isActive)
        {
            if (TryResolveSourceMode(snapshot, path, out var sourceMode))
            {
                // The source mode is the surface before rotation, because rotation happens when the
                // GPU scans it out. Desktop space is what the rest of the app works in, so transpose
                // for quarter turns here. A 180° turn is not a transpose and must not swap.
                var panel = new DisplayResolution((int)sourceMode.width, (int)sourceMode.height);

                panelResolution = panel;
                resolution = isRotated ? new DisplayResolution(panel.Height, panel.Width) : panel;
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
            PanelResolution = panelResolution,
            SignalResolution = signalResolution,
            RefreshRate = refreshRate,
            Orientation = orientation,
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

    /// <summary>
    /// Converts to DEVMODE's orientation values, which are 0-based unlike CCD's 1-based rotations.
    /// </summary>
    private static uint ToDeviceOrientation(DisplayOrientation orientation) => orientation switch
    {
        DisplayOrientation.Portrait => DeviceModeConstants.DMDO_90,
        DisplayOrientation.LandscapeFlipped => DeviceModeConstants.DMDO_180,
        DisplayOrientation.PortraitFlipped => DeviceModeConstants.DMDO_270,
        _ => DeviceModeConstants.DMDO_DEFAULT,
    };

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
