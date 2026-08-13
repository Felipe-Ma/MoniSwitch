using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ScreenShift.Native;

/// <summary>Flags, status bits and Win32 error codes used by the display configuration APIs.</summary>
internal static class NativeConstants
{
    // --- QueryDisplayConfig / GetDisplayConfigBufferSizes flags -------------
    public const uint QDC_ALL_PATHS = 0x00000001;
    public const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
    public const uint QDC_DATABASE_CURRENT = 0x00000004;
    public const uint QDC_VIRTUAL_MODE_AWARE = 0x00000010;
    public const uint QDC_INCLUDE_HMD = 0x00000020;

    // --- DISPLAYCONFIG_PATH_INFO.flags -------------------------------------
    public const uint DISPLAYCONFIG_PATH_ACTIVE = 0x00000001;
    public const uint DISPLAYCONFIG_PATH_PREFERRED_UNSCALED = 0x00000004;
    public const uint DISPLAYCONFIG_PATH_SUPPORT_VIRTUAL_MODE = 0x00000008;

    // --- DISPLAYCONFIG_PATH_SOURCE_INFO.statusFlags ------------------------
    public const uint DISPLAYCONFIG_SOURCE_IN_USE = 0x00000001;

    // --- DISPLAYCONFIG_PATH_TARGET_INFO.statusFlags ------------------------
    public const uint DISPLAYCONFIG_TARGET_IS_CONNECTED = 0x00000001;
    public const uint DISPLAYCONFIG_TARGET_IN_USE = 0x00000002;
    public const uint DISPLAYCONFIG_TARGET_FORCIBLE = 0x00000004;
    public const uint DISPLAYCONFIG_TARGET_FORCED_AVAILABILITY_BOOT = 0x00000008;
    public const uint DISPLAYCONFIG_TARGET_FORCED_AVAILABILITY_PATH = 0x00000010;
    public const uint DISPLAYCONFIG_TARGET_FORCED_AVAILABILITY_SYSTEM = 0x00000020;
    public const uint DISPLAYCONFIG_TARGET_IS_HMD = 0x00000040;

    // --- Win32 error codes we actually branch on ---------------------------
    public const int ERROR_SUCCESS = 0;
    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_NOT_SUPPORTED = 50;
    public const int ERROR_INVALID_PARAMETER = 87;
    public const int ERROR_INSUFFICIENT_BUFFER = 122;
    public const int ERROR_GEN_FAILURE = 31;

    // --- SetDisplayConfig flags --------------------------------------------
    // Windows 11 keeps display configuration in the CCD database, not the registry keys the GDI
    // path writes to, so this is the only way to make a change survive a reboot.
    public const uint SDC_TOPOLOGY_INTERNAL = 0x00000001;
    public const uint SDC_TOPOLOGY_CLONE = 0x00000002;
    public const uint SDC_TOPOLOGY_EXTEND = 0x00000004;
    public const uint SDC_TOPOLOGY_EXTERNAL = 0x00000008;
    public const uint SDC_TOPOLOGY_SUPPLIED = 0x00000010;

    /// <summary>Use the path and mode arrays the caller supplies rather than a stored topology.</summary>
    public const uint SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x00000020;

    /// <summary>Check the configuration without applying it. The CCD counterpart of CDS_TEST.</summary>
    public const uint SDC_VALIDATE = 0x00000040;

    public const uint SDC_APPLY = 0x00000080;
    public const uint SDC_NO_OPTIMIZATION = 0x00000100;

    /// <summary>Persist the configuration. Must be combined with SDC_APPLY.</summary>
    public const uint SDC_SAVE_TO_DATABASE = 0x00000200;

    /// <summary>Let Windows adjust the supplied configuration if it needs to in order to apply it.</summary>
    public const uint SDC_ALLOW_CHANGES = 0x00000400;

    public const uint SDC_PATH_PERSIST_IF_REQUIRED = 0x00000800;
    public const uint SDC_FORCE_MODE_ENUMERATION = 0x00001000;
    public const uint SDC_ALLOW_PATH_ORDER_CHANGES = 0x00002000;
    public const uint SDC_VIRTUAL_MODE_AWARE = 0x00008000;

    // --- Window messages ---------------------------------------------------
    public const int WM_DISPLAYCHANGE = 0x007E;
    public const int WM_DEVICECHANGE = 0x0219;

    // --- DwmSetWindowAttribute ---------------------------------------------
    /// <summary>Build 19041 and later. Builds 18985-19040 used 19 for the same thing.</summary>
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE_LEGACY = 19;
}

/// <summary>
/// Raw P/Invoke declarations. Nothing above <see cref="WindowsDisplayApi"/> should call these
/// directly — the wrapper is what turns return codes into exceptions and native buffers into
/// managed arrays.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class NativeMethods
{
    private const string User32 = "user32.dll";
    private const string Dwmapi = "dwmapi.dll";

    /// <summary>
    /// Asks how many path and mode records the current configuration needs. The answer can go
    /// stale immediately (a monitor may be hot-plugged a microsecond later), which is why
    /// QueryDisplayConfig is allowed to come back with ERROR_INSUFFICIENT_BUFFER.
    /// </summary>
    [DllImport(User32, ExactSpelling = true)]
    internal static extern int GetDisplayConfigBufferSizes(
        uint flags,
        out uint numPathArrayElements,
        out uint numModeInfoArrayElements);

    /// <summary>
    /// Fills the caller's arrays with the current display topology. The counts are in/out:
    /// on success they are lowered to the number of records actually written.
    /// </summary>
    /// <param name="currentTopologyId">
    /// Must be IntPtr.Zero unless QDC_DATABASE_CURRENT is set, in which case it must point at a
    /// DISPLAYCONFIG_TOPOLOGY_ID. Passing a non-null pointer with other flags fails with
    /// ERROR_INVALID_PARAMETER.
    /// </param>
    [DllImport(User32, ExactSpelling = true)]
    internal static extern int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements,
        [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
        IntPtr currentTopologyId);

    /// <summary>
    /// The QDC_DATABASE_CURRENT form, which needs a real pointer for the topology id rather than
    /// null. Reading the stored configuration is how persistence can be checked without rebooting.
    /// </summary>
    [DllImport(User32, ExactSpelling = true)]
    internal static extern int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements,
        [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
        out uint currentTopologyId);

    /// <summary>
    /// Applies and optionally saves a display configuration. Returns a Win32 error code.
    /// </summary>
    /// <remarks>
    /// Passing zero counts with null arrays is legal when the flags name a stored topology instead
    /// of a supplied configuration.
    /// </remarks>
    [DllImport(User32, ExactSpelling = true)]
    internal static extern int SetDisplayConfig(
        uint numPathArrayElements,
        [In] DISPLAYCONFIG_PATH_INFO[]? pathArray,
        uint numModeInfoArrayElements,
        [In] DISPLAYCONFIG_MODE_INFO[]? modeInfoArray,
        uint flags);

    // DisplayConfigGetDeviceInfo takes a DISPLAYCONFIG_DEVICE_INFO_HEADER* whose real type is
    // decided by header.type. Overloading per struct is the safe way to express that: each
    // overload marshals a correctly sized buffer, so the callee cannot write past the end.

    [DllImport(User32, ExactSpelling = true)]
    internal static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);

    [DllImport(User32, ExactSpelling = true)]
    internal static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

    [DllImport(User32, ExactSpelling = true)]
    internal static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_ADAPTER_NAME requestPacket);

    /// <summary>
    /// Reads one display mode. <paramref name="iModeNum"/> is either an index into the driver's
    /// mode list (call with 0, 1, 2 ... until it returns false) or ENUM_CURRENT_SETTINGS /
    /// ENUM_REGISTRY_SETTINGS.
    /// </summary>
    [DllImport(User32, CharSet = CharSet.Unicode, EntryPoint = "EnumDisplaySettingsExW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumDisplaySettingsEx(
        string? lpszDeviceName,
        int iModeNum,
        ref DEVMODE lpDevMode,
        uint dwFlags);

    /// <summary>
    /// Applies (or with CDS_NORESET, stages) a mode for one display. Returns a DISP_CHANGE_* code,
    /// not a Win32 error, and hwnd must always be null.
    /// </summary>
    [DllImport(User32, CharSet = CharSet.Unicode, EntryPoint = "ChangeDisplaySettingsExW")]
    internal static extern int ChangeDisplaySettingsEx(
        string? lpszDeviceName,
        ref DEVMODE lpDevMode,
        IntPtr hwnd,
        uint dwflags,
        IntPtr lParam);

    /// <summary>
    /// The null-DEVMODE form. Called with a null device name and no flags, it commits everything
    /// previously staged with CDS_NORESET.
    /// </summary>
    [DllImport(User32, CharSet = CharSet.Unicode, EntryPoint = "ChangeDisplaySettingsExW")]
    internal static extern int ChangeDisplaySettingsEx(
        string? lpszDeviceName,
        IntPtr lpDevMode,
        IntPtr hwnd,
        uint dwflags,
        IntPtr lParam);

    [DllImport(Dwmapi, ExactSpelling = true)]
    internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
