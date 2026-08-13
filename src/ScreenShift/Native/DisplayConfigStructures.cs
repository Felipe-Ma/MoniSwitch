using System.Runtime.InteropServices;

namespace ScreenShift.Native;

// ---------------------------------------------------------------------------
// Win32 "CCD" (Connecting and Configuring Displays) types.
//
// Layout notes, because getting these wrong produces garbage rather than errors:
//
//  * Every struct here is LayoutKind.Sequential with the DEFAULT pack. Do not add
//    Pack = 1. The C headers use natural alignment, and DISPLAYCONFIG_VIDEO_SIGNAL_INFO
//    leads with a UINT64, which forces 8-byte alignment on everything containing it.
//  * Expected sizes (asserted at runtime by NativeStructLayout.Verify):
//        LUID                            8
//        DISPLAYCONFIG_PATH_SOURCE_INFO  20
//        DISPLAYCONFIG_PATH_TARGET_INFO  48
//        DISPLAYCONFIG_PATH_INFO         72
//        DISPLAYCONFIG_MODE_INFO         64
//        DISPLAYCONFIG_TARGET_DEVICE_NAME  420
//        DISPLAYCONFIG_SOURCE_DEVICE_NAME   84
//        DISPLAYCONFIG_ADAPTER_NAME        276
//  * All strings are UTF-16 fixed-size inline arrays (ByValTStr with CharSet.Unicode).
// ---------------------------------------------------------------------------

/// <summary>Win32 LUID. Locally unique, but only for the current boot session.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct LUID : IEquatable<LUID>
{
    public uint LowPart;
    public int HighPart;

    public readonly bool Equals(LUID other) => LowPart == other.LowPart && HighPart == other.HighPart;
    public readonly override bool Equals(object? obj) => obj is LUID other && Equals(other);
    public readonly override int GetHashCode() => HashCode.Combine(LowPart, HighPart);
    public readonly override string ToString() => $"{HighPart:X8}:{LowPart:X8}";
}

[StructLayout(LayoutKind.Sequential)]
internal struct POINTL
{
    public int x;
    public int y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RECTL
{
    public int left;
    public int top;
    public int right;
    public int bottom;
}

/// <summary>A fraction. Windows reports refresh rates this way so 59.94 Hz stays exact (60000/1001).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_RATIONAL
{
    public uint Numerator;
    public uint Denominator;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_2DREGION
{
    public uint cx;
    public uint cy;
}

internal enum DISPLAYCONFIG_MODE_INFO_TYPE : uint
{
    Zero = 0,
    Source = 1,
    Target = 2,
    DesktopImage = 3,
}

internal enum DISPLAYCONFIG_ROTATION : uint
{
    Identity = 1,
    Rotate90 = 2,
    Rotate180 = 3,
    Rotate270 = 4,
}

internal enum DISPLAYCONFIG_SCALING : uint
{
    Identity = 1,
    Centered = 2,
    Stretched = 3,
    AspectRatioCenteredMax = 4,
    Custom = 5,
    Preferred = 128,
}

internal enum DISPLAYCONFIG_SCANLINE_ORDERING : uint
{
    Unspecified = 0,
    Progressive = 1,
    Interlaced = 2, // == InterlacedUpperFieldFirst
    InterlacedLowerFieldFirst = 3,
}

internal enum DISPLAYCONFIG_PIXELFORMAT : uint
{
    Pixel8Bpp = 1,
    Pixel16Bpp = 2,
    Pixel24Bpp = 3,
    Pixel32Bpp = 4,
    PixelNonGdi = 5,
}

/// <summary>How the target is physically wired. Backed by uint because Internal is 0x80000000.</summary>
internal enum DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY : uint
{
    Other = 0xFFFFFFFF,
    Hd15 = 0,
    SVideo = 1,
    CompositeVideo = 2,
    ComponentVideo = 3,
    Dvi = 4,
    Hdmi = 5,
    Lvds = 6,
    DJpn = 8,
    Sdi = 9,
    DisplayPortExternal = 10,
    DisplayPortEmbedded = 11,
    UdiExternal = 12,
    UdiEmbedded = 13,
    SdtvDongle = 14,
    Miracast = 15,
    IndirectWired = 16,
    IndirectVirtual = 17,
    DisplayPortUsbTunnel = 18,
    Internal = 0x80000000,
}

internal enum DISPLAYCONFIG_DEVICE_INFO_TYPE
{
    GetSourceName = 1,
    GetTargetName = 2,
    GetTargetPreferredMode = 3,
    GetAdapterName = 4,
    SetTargetPersistence = 5,
    GetTargetBaseType = 6,
    GetSupportVirtualResolution = 7,
    SetSupportVirtualResolution = 8,
    GetAdvancedColorInfo = 9,
    SetAdvancedColorState = 10,
    GetSdrWhiteLevel = 11,
}

/// <summary>
/// Source half of a path: the desktop surface the GPU scans out. Maps 1:1 to a
/// <c>\\.\DISPLAYn</c> GDI device.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_PATH_SOURCE_INFO
{
    public LUID adapterId;
    public uint id;

    /// <summary>
    /// Union. Without QDC_VIRTUAL_MODE_AWARE this is a plain index into the mode array.
    /// With it, the low 16 bits are the source-mode index and the high 16 the clone group.
    /// We query without that flag, so read it as a plain index (see SourceModeInfoIdx).
    /// </summary>
    public uint modeInfoIdx;

    public uint statusFlags;

    /// <summary>
    /// The source is driving at least one active path. Note this is not "is primary" —
    /// primary is defined by the source mode sitting at desktop origin (0,0).
    /// </summary>
    public readonly bool IsInUse =>
        (statusFlags & NativeConstants.DISPLAYCONFIG_SOURCE_IN_USE) != 0;
}

/// <summary>Target half of a path: the physical connector/monitor being driven.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_PATH_TARGET_INFO
{
    public LUID adapterId;
    public uint id;

    /// <summary>Union; see the note on the source-side modeInfoIdx.</summary>
    public uint modeInfoIdx;

    public DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY outputTechnology;
    public DISPLAYCONFIG_ROTATION rotation;
    public DISPLAYCONFIG_SCALING scaling;
    public DISPLAYCONFIG_RATIONAL refreshRate;
    public DISPLAYCONFIG_SCANLINE_ORDERING scanLineOrdering;

    /// <summary>Win32 BOOL (4 bytes), not a 1-byte bool. Marshal as int and compare.</summary>
    public int targetAvailable;

    public uint statusFlags;

    public readonly bool IsConnected =>
        (statusFlags & NativeConstants.DISPLAYCONFIG_TARGET_IS_CONNECTED) != 0;

    public readonly bool IsForcible =>
        (statusFlags & NativeConstants.DISPLAYCONFIG_TARGET_FORCIBLE) != 0;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_PATH_INFO
{
    public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
    public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
    public uint flags;

    public readonly bool IsActive =>
        (flags & NativeConstants.DISPLAYCONFIG_PATH_ACTIVE) != 0;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_VIDEO_SIGNAL_INFO
{
    public ulong pixelRate;
    public DISPLAYCONFIG_RATIONAL hSyncFreq;
    public DISPLAYCONFIG_RATIONAL vSyncFreq;
    public DISPLAYCONFIG_2DREGION activeSize;
    public DISPLAYCONFIG_2DREGION totalSize;

    /// <summary>
    /// Union of a bare UINT32 videoStandard and a bitfield
    /// { videoStandard:16, vSyncFreqDivider:6, reserved:10 }. Kept as one uint;
    /// the pieces are exposed as properties below.
    /// </summary>
    public uint videoStandardAndFlags;

    public DISPLAYCONFIG_SCANLINE_ORDERING scanLineOrdering;

    public readonly ushort VideoStandard => (ushort)(videoStandardAndFlags & 0xFFFF);
    public readonly byte VSyncFreqDivider => (byte)((videoStandardAndFlags >> 16) & 0x3F);
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_TARGET_MODE
{
    public DISPLAYCONFIG_VIDEO_SIGNAL_INFO targetVideoSignalInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_SOURCE_MODE
{
    public uint width;
    public uint height;
    public DISPLAYCONFIG_PIXELFORMAT pixelFormat;
    public POINTL position;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_DESKTOP_IMAGE_INFO
{
    public POINTL PathSourceSize;
    public RECTL DesktopImageRegion;
    public RECTL DesktopImageClip;
}

/// <summary>
/// The anonymous union inside DISPLAYCONFIG_MODE_INFO. Explicit layout is safe here:
/// all three members are blittable value types with no references to overlap.
/// Largest member is DISPLAYCONFIG_TARGET_MODE at 48 bytes.
/// </summary>
[StructLayout(LayoutKind.Explicit)]
internal struct DISPLAYCONFIG_MODE_INFO_UNION
{
    [FieldOffset(0)] public DISPLAYCONFIG_TARGET_MODE targetMode;
    [FieldOffset(0)] public DISPLAYCONFIG_SOURCE_MODE sourceMode;
    [FieldOffset(0)] public DISPLAYCONFIG_DESKTOP_IMAGE_INFO desktopImageInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_MODE_INFO
{
    public DISPLAYCONFIG_MODE_INFO_TYPE infoType;

    /// <summary>Source id when infoType is Source, target id when it is Target.</summary>
    public uint id;

    public LUID adapterId;
    public DISPLAYCONFIG_MODE_INFO_UNION mode;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_DEVICE_INFO_HEADER
{
    public DISPLAYCONFIG_DEVICE_INFO_TYPE type;

    /// <summary>Must be sizeof(the whole containing struct), not of the header.</summary>
    public uint size;

    public LUID adapterId;
    public uint id;
}

/// <summary>Yields the monitor's friendly (EDID) name and its stable device path.</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct DISPLAYCONFIG_TARGET_DEVICE_NAME
{
    public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
    public uint flags;
    public DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY outputTechnology;
    public ushort edidManufactureId;
    public ushort edidProductCodeId;
    public uint connectorInstance;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    public string monitorFriendlyDeviceName;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string monitorDevicePath;

    /// <summary>Bit 0 of flags: the friendly name came from the EDID rather than being synthesised.</summary>
    public readonly bool FriendlyNameFromEdid => (flags & 0x1) != 0;

    /// <summary>Bit 1 of flags: the device has no EDID-provided friendly name at all.</summary>
    public readonly bool FriendlyNameForced => (flags & 0x2) != 0;

    /// <summary>Bit 2 of flags: edidManufactureId/edidProductCodeId are valid.</summary>
    public readonly bool EdidIdsValid => (flags & 0x4) != 0;
}

/// <summary>Yields the GDI device name for a source, e.g. <c>\\.\DISPLAY1</c>.</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
{
    public DISPLAYCONFIG_DEVICE_INFO_HEADER header;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string viewGdiDeviceName;
}

/// <summary>
/// Yields the adapter's device path. Unlike the adapter LUID, this survives a reboot,
/// which is what makes it usable for matching saved profiles to hardware later on.
/// </summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct DISPLAYCONFIG_ADAPTER_NAME
{
    public DISPLAYCONFIG_DEVICE_INFO_HEADER header;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string adapterDevicePath;
}
