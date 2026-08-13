using System.Runtime.InteropServices;

namespace ScreenShift.Native;

// ---------------------------------------------------------------------------
// The GDI half of the display APIs: DEVMODE, EnumDisplaySettingsEx and
// ChangeDisplaySettingsEx.
//
// Why both API families are here. The CCD API (QueryDisplayConfig and friends,
// in DisplayConfigStructures.cs) is the modern one and owns topology — which
// monitors exist and which are lit. But it cannot enumerate the modes a monitor
// supports, and building a valid DISPLAYCONFIG_TARGET_MODE by hand means
// inventing pixel clocks and sync timings. GDI enumerates real driver-blessed
// modes and applies them by value, which is exactly what Phase 2 needs.
//
// So: CCD reads topology and identity, GDI enumerates and sets modes.
// ---------------------------------------------------------------------------

/// <summary>
/// DEVMODE, display variant.
/// </summary>
/// <remarks>
/// The real struct carries a union at offset 76 that is either printer fields (eight shorts) or
/// display fields (POINTL + two DWORDs). Both are exactly 16 bytes, so declaring only the display
/// variant inline keeps the layout correct without an explicit union. Marshals to 220 bytes, which
/// <see cref="NativeStructLayout"/> asserts.
/// </remarks>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct DEVMODE
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string dmDeviceName;

    public ushort dmSpecVersion;
    public ushort dmDriverVersion;

    /// <summary>Must be set to sizeof(DEVMODE) before any call, or the API rejects the buffer.</summary>
    public ushort dmSize;

    public ushort dmDriverExtra;

    /// <summary>Which of the fields below are meaningful. Anything not flagged here is ignored.</summary>
    public uint dmFields;

    // --- union: display variant ---
    public POINTL dmPosition;
    public uint dmDisplayOrientation;
    public uint dmDisplayFixedOutput;
    // --- end union ---

    public short dmColor;
    public short dmDuplex;
    public short dmYResolution;
    public short dmTTOption;
    public short dmCollate;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string dmFormName;

    public ushort dmLogPixels;
    public uint dmBitsPerPel;
    public uint dmPelsWidth;
    public uint dmPelsHeight;
    public uint dmDisplayFlags;

    /// <summary>
    /// Whole hertz only — 60 for both 59.94 Hz and 60 Hz. The exact rate lives on the CCD side.
    /// </summary>
    public uint dmDisplayFrequency;

    public uint dmICMMethod;
    public uint dmICMIntent;
    public uint dmMediaType;
    public uint dmDitherType;
    public uint dmReserved1;
    public uint dmReserved2;
    public uint dmPanningWidth;
    public uint dmPanningHeight;

    /// <summary>A zeroed DEVMODE with dmSize filled in, ready to pass to the API.</summary>
    public static DEVMODE Create() => new()
    {
        dmDeviceName = string.Empty,
        dmFormName = string.Empty,
        dmSize = (ushort)Marshal.SizeOf<DEVMODE>(),
    };
}

internal static class DeviceModeConstants
{
    // --- dmFields bits ------------------------------------------------------
    public const uint DM_POSITION = 0x00000020;
    public const uint DM_DISPLAYORIENTATION = 0x00000080;
    public const uint DM_DISPLAYFIXEDOUTPUT = 0x20000000;
    public const uint DM_BITSPERPEL = 0x00040000;
    public const uint DM_PELSWIDTH = 0x00080000;
    public const uint DM_PELSHEIGHT = 0x00100000;
    public const uint DM_DISPLAYFLAGS = 0x00200000;
    public const uint DM_DISPLAYFREQUENCY = 0x00400000;

    // --- ChangeDisplaySettingsEx flags --------------------------------------
    /// <summary>Persist the mode so it survives a reboot.</summary>
    public const uint CDS_UPDATEREGISTRY = 0x00000001;

    /// <summary>Validate without applying. Used to reject a mode before it can black anything out.</summary>
    public const uint CDS_TEST = 0x00000002;

    public const uint CDS_FULLSCREEN = 0x00000004;
    public const uint CDS_GLOBAL = 0x00000008;

    /// <summary>Make this display the primary. Requires its position to be (0,0) in the same call.</summary>
    public const uint CDS_SET_PRIMARY = 0x00000010;

    /// <summary>
    /// Stage the change without applying it. Every display in a batch is staged with this, then one
    /// final call with a null device commits them together — which is as close to atomic as the GDI
    /// path gets, and avoids the screen reshuffling once per monitor.
    /// </summary>
    public const uint CDS_NORESET = 0x10000000;

    public const uint CDS_RESET = 0x40000000;

    // --- ChangeDisplaySettingsEx return codes -------------------------------
    public const int DISP_CHANGE_SUCCESSFUL = 0;
    public const int DISP_CHANGE_RESTART = 1;
    public const int DISP_CHANGE_FAILED = -1;
    public const int DISP_CHANGE_BADMODE = -2;
    public const int DISP_CHANGE_NOTUPDATED = -3;
    public const int DISP_CHANGE_BADFLAGS = -4;
    public const int DISP_CHANGE_BADPARAM = -5;
    public const int DISP_CHANGE_BADDUALVIEW = -6;

    // --- EnumDisplaySettingsEx ----------------------------------------------
    public const int ENUM_CURRENT_SETTINGS = -1;
    public const int ENUM_REGISTRY_SETTINGS = -2;
    public const uint EDS_RAWMODE = 0x00000002;

    /// <summary>
    /// Report modes in the display's current rotation rather than its native one. Left off
    /// deliberately: ScreenShift models resolution unrotated, matching Windows Settings.
    /// </summary>
    public const uint EDS_ROTATEDMODE = 0x00000004;

    // --- dmDisplayOrientation ------------------------------------------------
    // Note these are 0-based, unlike DISPLAYCONFIG_ROTATION which is 1-based.
    public const uint DMDO_DEFAULT = 0;
    public const uint DMDO_90 = 1;
    public const uint DMDO_180 = 2;
    public const uint DMDO_270 = 3;

    public static string DescribeChangeResult(int code) => code switch
    {
        DISP_CHANGE_SUCCESSFUL => "DISP_CHANGE_SUCCESSFUL",
        DISP_CHANGE_RESTART => "DISP_CHANGE_RESTART (the change needs a reboot)",
        DISP_CHANGE_FAILED => "DISP_CHANGE_FAILED (the driver rejected the mode)",
        DISP_CHANGE_BADMODE => "DISP_CHANGE_BADMODE (the mode is not supported)",
        DISP_CHANGE_NOTUPDATED => "DISP_CHANGE_NOTUPDATED (could not write the registry)",
        DISP_CHANGE_BADFLAGS => "DISP_CHANGE_BADFLAGS",
        DISP_CHANGE_BADPARAM => "DISP_CHANGE_BADPARAM",
        DISP_CHANGE_BADDUALVIEW => "DISP_CHANGE_BADDUALVIEW",
        _ => $"unknown code {code}",
    };
}
