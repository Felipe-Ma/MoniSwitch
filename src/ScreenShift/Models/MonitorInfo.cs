namespace ScreenShift.Models;

/// <summary>
/// One physical monitor as ScreenShift sees it: a snapshot, immutable, produced by
/// <see cref="Services.DisplayService"/> from a QueryDisplayConfig pass.
/// </summary>
/// <remarks>
/// Identity is the interesting part. Three ids are carried, in decreasing order of trust:
/// <list type="bullet">
///   <item><see cref="DevicePath"/> — the monitor plus the port it is plugged into. Survives
///   reboots and driver updates, and stays distinct between two identical monitors. This is the
///   key profiles will be written against.</item>
///   <item><see cref="AdapterDevicePath"/> plus <see cref="TargetId"/> — same idea, split into
///   GPU and connector. Useful as a fallback when a device path changes shape.</item>
///   <item><see cref="GdiDeviceName"/> and <see cref="DisplayNumber"/> — what the user sees in
///   Windows Settings, but Windows reassigns these freely. Display only, never for matching.</item>
/// </list>
/// </remarks>
public sealed class MonitorInfo
{
    /// <summary>
    /// Stable per-monitor-per-port identifier, e.g.
    /// <c>\\?\DISPLAY#BNQ7FEE#5&amp;3a637623&amp;0&amp;UID4353#{e6f0...}</c>.
    /// Empty only for exotic targets that refuse to report a name.
    /// </summary>
    public required string DevicePath { get; init; }

    /// <summary>EDID name such as "BenQ EX271Q", or a synthesised fallback like "Display 2".</summary>
    public required string FriendlyName { get; init; }

    /// <summary>True when <see cref="FriendlyName"/> came from the monitor's EDID rather than being invented.</summary>
    public bool HasEdidName { get; init; }

    /// <summary>GDI name, e.g. <c>\\.\DISPLAY1</c>. Null while the monitor is disabled.</summary>
    public string? GdiDeviceName { get; init; }

    /// <summary>The trailing number of <see cref="GdiDeviceName"/> — what Windows labels the display.</summary>
    public int? DisplayNumber { get; init; }

    /// <summary>Adapter LUID as a string. Unique only within the current boot session.</summary>
    public required string AdapterId { get; init; }

    /// <summary>Adapter device path. Unlike the LUID this persists across reboots.</summary>
    public string? AdapterDevicePath { get; init; }

    /// <summary>Connector id on that adapter.</summary>
    public required uint TargetId { get; init; }

    /// <summary>Desktop surface id feeding the connector. Meaningless while disabled.</summary>
    public uint SourceId { get; init; }

    /// <summary>Three-letter PNP vendor code decoded from the EDID, e.g. "BNQ". Null when unavailable.</summary>
    public string? EdidVendorCode { get; init; }

    /// <summary>EDID product code. Pairs with <see cref="EdidVendorCode"/> to identify the model.</summary>
    public ushort? EdidProductCode { get; init; }

    /// <summary>Physically plugged in and reported by the driver.</summary>
    public bool IsConnected { get; init; }

    /// <summary>Currently part of the desktop, i.e. lit up.</summary>
    public bool IsEnabled { get; init; }

    /// <summary>Sits at desktop origin (0,0), which is Windows' definition of the primary display.</summary>
    public bool IsPrimary { get; init; }

    /// <summary>
    /// The display mode's resolution, as Windows Settings presents it — always in the panel's
    /// native orientation, so a rotated 1440p monitor still reads 2560 × 1440. This is the value a
    /// resolution picker operates on. For the rectangle it occupies on the desktop, use
    /// <see cref="DesktopSize"/> or <see cref="Bounds"/>. Null while disabled.
    /// </summary>
    public DisplayResolution? Resolution { get; init; }

    /// <summary>
    /// Resolution of the signal actually going down the cable. Differs from <see cref="Resolution"/>
    /// when the GPU is scaling — e.g. a 1080p desktop stretched onto a 4K panel.
    /// </summary>
    public DisplayResolution? SignalResolution { get; init; }

    public RefreshRate RefreshRate { get; init; } = RefreshRate.Unknown;

    public DisplayOrientation Orientation { get; init; } = DisplayOrientation.Landscape;

    /// <summary>Top-left corner in virtual desktop coordinates. Null while disabled.</summary>
    public DisplayPosition? Position { get; init; }

    public MonitorConnection Connection { get; init; } = MonitorConnection.Unknown;

    /// <summary>True when the signal is interlaced, which makes the refresh rate a field rate.</summary>
    public bool IsInterlaced { get; init; }

    /// <summary>True when this target is part of a clone group (more than one target on one source).</summary>
    public bool IsCloned { get; init; }

    /// <summary>Quarter-turned, so the desktop rectangle is the mode resolution transposed.</summary>
    public bool IsRotated =>
        Orientation is DisplayOrientation.Portrait or DisplayOrientation.PortraitFlipped;

    /// <summary>
    /// Size of the rectangle this monitor occupies on the desktop.
    /// </summary>
    /// <remarks>
    /// The CCD source mode reports the surface <em>before</em> rotation — a portrait 1440p monitor
    /// still comes back as 2560 × 1440 — because rotation is applied when the GPU scans the surface
    /// out to the connector. Windows' own desktop rectangle for that monitor is 1440 × 2560, so the
    /// transpose has to happen here or every layout calculation lands in the wrong place.
    /// A 180° rotation is not a transpose and deliberately does not swap.
    /// </remarks>
    public DisplayResolution? DesktopSize =>
        Resolution is { IsEmpty: false } r
            ? IsRotated ? new DisplayResolution(r.Height, r.Width) : r
            : null;

    /// <summary>Rectangle this monitor occupies on the virtual desktop, or null while disabled.</summary>
    public (int X, int Y, int Width, int Height)? Bounds =>
        Position is { } p && DesktopSize is { } size
            ? (p.X, p.Y, size.Width, size.Height)
            : null;

    /// <summary>
    /// Key used to line a saved profile up with real hardware. Prefers the device path and only
    /// falls back to adapter+target when a monitor will not report one.
    /// </summary>
    public string StableKey =>
        !string.IsNullOrEmpty(DevicePath)
            ? DevicePath
            : $"{AdapterDevicePath ?? AdapterId}#{TargetId}";

    public override string ToString() =>
        $"{FriendlyName} [{(IsEnabled ? "enabled" : "disabled")}] {Resolution} @ {RefreshRate}";
}
