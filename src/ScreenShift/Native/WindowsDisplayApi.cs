using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ScreenShift.Services;

namespace ScreenShift.Native;

/// <summary>Raised when a display configuration API fails in a way the caller cannot paper over.</summary>
public sealed class DisplayConfigException : Exception
{
    public DisplayConfigException(string operation, int errorCode)
        : base($"{operation} failed with Win32 error {errorCode} ({DescribeError(errorCode)}).")
    {
        Operation = operation;
        ErrorCode = errorCode;
    }

    public string Operation { get; }

    public int ErrorCode { get; }

    internal static string DescribeError(int code) => code switch
    {
        NativeConstants.ERROR_ACCESS_DENIED => "ERROR_ACCESS_DENIED",
        NativeConstants.ERROR_GEN_FAILURE => "ERROR_GEN_FAILURE",
        NativeConstants.ERROR_NOT_SUPPORTED => "ERROR_NOT_SUPPORTED",
        NativeConstants.ERROR_INVALID_PARAMETER => "ERROR_INVALID_PARAMETER",
        NativeConstants.ERROR_INSUFFICIENT_BUFFER => "ERROR_INSUFFICIENT_BUFFER",
        _ => new Win32Exception(code).Message,
    };
}

/// <summary>A consistent pair of path and mode arrays, as returned by one QueryDisplayConfig call.</summary>
internal sealed class DisplayConfigSnapshot
{
    public DisplayConfigSnapshot(DISPLAYCONFIG_PATH_INFO[] paths, DISPLAYCONFIG_MODE_INFO[] modes)
    {
        Paths = paths;
        Modes = modes;
    }

    public DISPLAYCONFIG_PATH_INFO[] Paths { get; }

    public DISPLAYCONFIG_MODE_INFO[] Modes { get; }

    public static DisplayConfigSnapshot Empty { get; } = new([], []);
}

/// <summary>
/// The only place in the app that talks to user32's display APIs. Everything it returns is still
/// shaped like the native data — turning that into friendly models is <see cref="Services.DisplayService"/>'s job.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsDisplayApi
{
    /// <summary>
    /// The topology can change between sizing the buffers and filling them (hot-plug, GPU reset,
    /// a laptop lid closing). Windows reports that as ERROR_INSUFFICIENT_BUFFER and expects a retry.
    /// </summary>
    private const int MaxQueryAttempts = 5;

    private readonly IAppLogger _logger;

    public WindowsDisplayApi(IAppLogger logger)
    {
        _logger = logger;
        NativeStructLayout.Verify();
    }

    /// <summary>
    /// Reads the current display configuration.
    /// </summary>
    /// <param name="flags">
    /// QDC_ONLY_ACTIVE_PATHS for just what is lit up, or QDC_ALL_PATHS to also see connected
    /// monitors that are currently switched off.
    /// </param>
    public DisplayConfigSnapshot Query(uint flags)
    {
        for (var attempt = 1; attempt <= MaxQueryAttempts; attempt++)
        {
            var rc = NativeMethods.GetDisplayConfigBufferSizes(flags, out var pathCount, out var modeCount);
            if (rc != NativeConstants.ERROR_SUCCESS)
            {
                throw new DisplayConfigException("GetDisplayConfigBufferSizes", rc);
            }

            if (pathCount == 0)
            {
                _logger.Warn($"GetDisplayConfigBufferSizes reported 0 paths for flags 0x{flags:X}.");
                return DisplayConfigSnapshot.Empty;
            }

            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[Math.Max(modeCount, 1)];

            rc = NativeMethods.QueryDisplayConfig(flags, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);

            if (rc == NativeConstants.ERROR_SUCCESS)
            {
                // The counts come back reduced to what was actually written; anything past them is stale.
                Array.Resize(ref paths, (int)pathCount);
                Array.Resize(ref modes, (int)modeCount);

                _logger.Debug($"QueryDisplayConfig(0x{flags:X}) returned {pathCount} path(s), {modeCount} mode(s) on attempt {attempt}.");
                return new DisplayConfigSnapshot(paths, modes);
            }

            if (rc != NativeConstants.ERROR_INSUFFICIENT_BUFFER)
            {
                throw new DisplayConfigException("QueryDisplayConfig", rc);
            }

            _logger.Warn($"QueryDisplayConfig(0x{flags:X}) hit ERROR_INSUFFICIENT_BUFFER on attempt {attempt}; the topology changed mid-query. Retrying.");
        }

        throw new DisplayConfigException("QueryDisplayConfig", NativeConstants.ERROR_INSUFFICIENT_BUFFER);
    }

    /// <summary>
    /// Reads the configuration Windows has stored for the currently connected set of monitors —
    /// that is, what the desktop will look like after the next reboot.
    /// </summary>
    /// <remarks>
    /// QDC_DATABASE_CURRENT is the one query flag that insists on a real pointer for the topology
    /// id; passing null fails with ERROR_INVALID_PARAMETER.
    /// </remarks>
    public DisplayConfigSnapshot QueryDatabase(out uint topologyId)
    {
        topologyId = 0;

        for (var attempt = 1; attempt <= MaxQueryAttempts; attempt++)
        {
            // GetDisplayConfigBufferSizes rejects QDC_DATABASE_CURRENT outright, so the buffers are
            // sized against QDC_ALL_PATHS instead. That is an over-estimate, which is fine: the
            // query lowers the counts to what it actually wrote.
            var rc = NativeMethods.GetDisplayConfigBufferSizes(
                NativeConstants.QDC_ALL_PATHS, out var pathCount, out var modeCount);

            if (rc != NativeConstants.ERROR_SUCCESS)
            {
                throw new DisplayConfigException("GetDisplayConfigBufferSizes(for QDC_DATABASE_CURRENT)", rc);
            }

            if (pathCount == 0)
            {
                return DisplayConfigSnapshot.Empty;
            }

            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[Math.Max(modeCount, 1)];

            // QDC_DATABASE_CURRENT is a modifier, not a mode of its own: it has to be combined with
            // QDC_ONLY_ACTIVE_PATHS, and it is the one query flag that requires a real topology-id
            // pointer rather than null.
            rc = NativeMethods.QueryDisplayConfig(
                NativeConstants.QDC_DATABASE_CURRENT | NativeConstants.QDC_ONLY_ACTIVE_PATHS,
                ref pathCount, paths, ref modeCount, modes, out topologyId);

            if (rc == NativeConstants.ERROR_SUCCESS)
            {
                Array.Resize(ref paths, (int)pathCount);
                Array.Resize(ref modes, (int)modeCount);
                return new DisplayConfigSnapshot(paths, modes);
            }

            if (rc != NativeConstants.ERROR_INSUFFICIENT_BUFFER)
            {
                throw new DisplayConfigException("QueryDisplayConfig(QDC_DATABASE_CURRENT)", rc);
            }
        }

        throw new DisplayConfigException("QueryDisplayConfig(QDC_DATABASE_CURRENT)", NativeConstants.ERROR_INSUFFICIENT_BUFFER);
    }

    /// <summary>
    /// Applies a configuration through the CCD API, optionally writing it to the persistence
    /// database so it survives a reboot.
    /// </summary>
    /// <returns>ERROR_SUCCESS, or a Win32 error code.</returns>
    public int SetConfiguration(DisplayConfigSnapshot configuration, uint flags)
    {
        var result = NativeMethods.SetDisplayConfig(
            (uint)configuration.Paths.Length,
            configuration.Paths,
            (uint)configuration.Modes.Length,
            configuration.Modes,
            flags);

        _logger.Debug($"SetDisplayConfig({configuration.Paths.Length} path(s), flags=0x{flags:X}) -> {result} ({DisplayConfigException.DescribeError(result)}).");
        return result;
    }

    /// <summary>
    /// Looks up the monitor's EDID name and stable device path.
    /// Returns false for targets that have no name to give (some virtual/indirect displays), which
    /// is a normal condition rather than a failure.
    /// </summary>
    public bool TryGetTargetDeviceName(LUID adapterId, uint targetId, out DISPLAYCONFIG_TARGET_DEVICE_NAME result)
    {
        result = new DISPLAYCONFIG_TARGET_DEVICE_NAME
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DISPLAYCONFIG_DEVICE_INFO_TYPE.GetTargetName,
                size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                adapterId = adapterId,
                id = targetId,
            },
        };

        var rc = NativeMethods.DisplayConfigGetDeviceInfo(ref result);
        if (rc == NativeConstants.ERROR_SUCCESS)
        {
            return true;
        }

        _logger.Warn($"DisplayConfigGetDeviceInfo(GET_TARGET_NAME) failed for adapter {adapterId} target {targetId}: {rc} ({DisplayConfigException.DescribeError(rc)}).");
        return false;
    }

    /// <summary>Looks up the GDI device name (<c>\\.\DISPLAYn</c>) behind a source id.</summary>
    /// <remarks>
    /// Inactive sources legitimately have no GDI name, so a failure here is logged at debug level
    /// and the caller falls back to showing nothing.
    /// </remarks>
    public bool TryGetSourceDeviceName(LUID adapterId, uint sourceId, out string gdiDeviceName)
    {
        var request = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DISPLAYCONFIG_DEVICE_INFO_TYPE.GetSourceName,
                size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                adapterId = adapterId,
                id = sourceId,
            },
            viewGdiDeviceName = string.Empty,
        };

        var rc = NativeMethods.DisplayConfigGetDeviceInfo(ref request);
        if (rc == NativeConstants.ERROR_SUCCESS)
        {
            gdiDeviceName = request.viewGdiDeviceName ?? string.Empty;
            return gdiDeviceName.Length > 0;
        }

        _logger.Debug($"DisplayConfigGetDeviceInfo(GET_SOURCE_NAME) failed for adapter {adapterId} source {sourceId}: {rc} ({DisplayConfigException.DescribeError(rc)}).");
        gdiDeviceName = string.Empty;
        return false;
    }

    /// <summary>
    /// Looks up the adapter's device path. The adapter LUID is only unique within a boot session,
    /// so this path is what lets a saved profile still recognise the GPU after a reboot or driver update.
    /// </summary>
    public bool TryGetAdapterDevicePath(LUID adapterId, out string adapterDevicePath)
    {
        var request = new DISPLAYCONFIG_ADAPTER_NAME
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DISPLAYCONFIG_DEVICE_INFO_TYPE.GetAdapterName,
                size = (uint)Marshal.SizeOf<DISPLAYCONFIG_ADAPTER_NAME>(),
                adapterId = adapterId,
                id = 0,
            },
            adapterDevicePath = string.Empty,
        };

        var rc = NativeMethods.DisplayConfigGetDeviceInfo(ref request);
        if (rc == NativeConstants.ERROR_SUCCESS)
        {
            adapterDevicePath = request.adapterDevicePath ?? string.Empty;
            return adapterDevicePath.Length > 0;
        }

        _logger.Debug($"DisplayConfigGetDeviceInfo(GET_ADAPTER_NAME) failed for adapter {adapterId}: {rc} ({DisplayConfigException.DescribeError(rc)}).");
        adapterDevicePath = string.Empty;
        return false;
    }

    // ------------------------------------------------------------------
    // GDI side: enumerating and applying display modes.
    // ------------------------------------------------------------------

    /// <summary>Reads the mode a display is running right now.</summary>
    public bool TryGetCurrentMode(string gdiDeviceName, out DEVMODE mode)
    {
        mode = DEVMODE.Create();

        if (NativeMethods.EnumDisplaySettingsEx(gdiDeviceName, DeviceModeConstants.ENUM_CURRENT_SETTINGS, ref mode, 0))
        {
            return true;
        }

        _logger.Warn($"EnumDisplaySettingsEx(ENUM_CURRENT_SETTINGS) failed for {gdiDeviceName}: Win32 error {Marshal.GetLastWin32Error()}.");
        return false;
    }

    /// <summary>
    /// Walks the driver's whole mode list. Called without EDS_ROTATEDMODE, so the modes come back
    /// in the panel's native orientation regardless of how the display is currently turned.
    /// </summary>
    public List<DEVMODE> EnumerateModes(string gdiDeviceName)
    {
        var modes = new List<DEVMODE>();

        for (var index = 0; ; index++)
        {
            var mode = DEVMODE.Create();

            if (!NativeMethods.EnumDisplaySettingsEx(gdiDeviceName, index, ref mode, 0))
            {
                // The list is terminated by a plain false, not by an error code.
                break;
            }

            modes.Add(mode);

            if (index > 8192)
            {
                _logger.Warn($"EnumDisplaySettingsEx for {gdiDeviceName} passed 8192 modes; stopping to avoid spinning on a broken driver.");
                break;
            }
        }

        _logger.Debug($"EnumDisplaySettingsEx returned {modes.Count} raw mode(s) for {gdiDeviceName}.");
        return modes;
    }

    /// <summary>
    /// Asks the driver whether a mode would be accepted, without applying it. Cheap insurance
    /// before a change that could otherwise leave a monitor dark.
    /// </summary>
    public int TestMode(string gdiDeviceName, ref DEVMODE mode) =>
        NativeMethods.ChangeDisplaySettingsEx(
            gdiDeviceName, ref mode, IntPtr.Zero, DeviceModeConstants.CDS_TEST, IntPtr.Zero);

    /// <summary>
    /// Stages a mode for one display without applying it. Pair with <see cref="CommitStagedChanges"/>.
    /// </summary>
    public int StageMode(string gdiDeviceName, ref DEVMODE mode, uint extraFlags = 0)
    {
        var flags = DeviceModeConstants.CDS_UPDATEREGISTRY | DeviceModeConstants.CDS_NORESET | extraFlags;
        var result = NativeMethods.ChangeDisplaySettingsEx(gdiDeviceName, ref mode, IntPtr.Zero, flags, IntPtr.Zero);

        _logger.Debug($"ChangeDisplaySettingsEx({gdiDeviceName}, flags=0x{flags:X}) -> {DeviceModeConstants.DescribeChangeResult(result)}.");
        return result;
    }

    /// <summary>
    /// Applies a mode to one display straight away, bypassing the staging mechanism.
    /// </summary>
    /// <remarks>
    /// The fallback for when a staged batch is refused. Slower and visibly less tidy — the desktop
    /// reshuffles once per display — but it commits each display on its own terms rather than
    /// asking the driver to validate a whole pending configuration at once.
    /// </remarks>
    /// <param name="persist">
    /// False applies the mode dynamically without writing it to the registry. Worth falling back to:
    /// the registry write is a separate way for the call to fail, and a change that survives only
    /// until reboot beats a change that does not happen at all.
    /// </param>
    public int ApplyModeImmediately(string gdiDeviceName, ref DEVMODE mode, uint extraFlags = 0, bool persist = true)
    {
        var flags = (persist ? DeviceModeConstants.CDS_UPDATEREGISTRY : 0u) | extraFlags;
        var result = NativeMethods.ChangeDisplaySettingsEx(gdiDeviceName, ref mode, IntPtr.Zero, flags, IntPtr.Zero);

        _logger.Debug($"ChangeDisplaySettingsEx({gdiDeviceName}, {(persist ? "immediate" : "dynamic")}, flags=0x{flags:X}) -> {DeviceModeConstants.DescribeChangeResult(result)}.");
        return result;
    }

    /// <summary>Applies everything staged so far, in one switch.</summary>
    public int CommitStagedChanges()
    {
        var result = NativeMethods.ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);

        _logger.Debug($"ChangeDisplaySettingsEx(commit) -> {DeviceModeConstants.DescribeChangeResult(result)}.");
        return result;
    }
}

/// <summary>
/// Checks the marshalled size of every native struct against the size the Windows headers define.
/// A mismatch means the runtime would hand user32 a differently shaped buffer than it expects, and
/// the symptom of that is plausible-looking garbage rather than an error code — so it is worth
/// failing loudly at startup instead.
/// </summary>
internal static class NativeStructLayout
{
    private static bool _verified;

    public static void Verify()
    {
        if (_verified)
        {
            return;
        }

        Expect<LUID>(8);
        Expect<POINTL>(8);
        Expect<RECTL>(16);
        Expect<DISPLAYCONFIG_RATIONAL>(8);
        Expect<DISPLAYCONFIG_2DREGION>(8);
        Expect<DISPLAYCONFIG_PATH_SOURCE_INFO>(20);
        Expect<DISPLAYCONFIG_PATH_TARGET_INFO>(48);
        Expect<DISPLAYCONFIG_PATH_INFO>(72);
        Expect<DISPLAYCONFIG_VIDEO_SIGNAL_INFO>(48);
        Expect<DISPLAYCONFIG_TARGET_MODE>(48);
        Expect<DISPLAYCONFIG_SOURCE_MODE>(20);
        Expect<DISPLAYCONFIG_DESKTOP_IMAGE_INFO>(40);
        Expect<DISPLAYCONFIG_MODE_INFO>(64);
        Expect<DISPLAYCONFIG_DEVICE_INFO_HEADER>(20);
        Expect<DISPLAYCONFIG_TARGET_DEVICE_NAME>(420);
        Expect<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(84);
        Expect<DISPLAYCONFIG_ADAPTER_NAME>(276);
        Expect<DEVMODE>(220);

        _verified = true;
    }

    private static void Expect<T>(int expectedSize) where T : struct
    {
        var actual = Marshal.SizeOf<T>();
        if (actual != expectedSize)
        {
            throw new InvalidOperationException(
                $"P/Invoke layout error: {typeof(T).Name} marshals to {actual} bytes but Windows expects {expectedSize}.");
        }
    }
}
