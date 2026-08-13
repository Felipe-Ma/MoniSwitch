namespace ScreenShift.Models;

/// <summary>
/// One mode a display will accept, as reported by the driver.
/// </summary>
/// <remarks>
/// Sizes are in desktop space, matching <see cref="MonitorInfo.Resolution"/> — the GDI mode list
/// for a portrait display already comes back transposed, so no conversion happens on the way in.
/// <para>
/// <see cref="RefreshHz"/> is whole hertz because DEVMODE has nowhere to put a fraction: both
/// 59.94 Hz and 60 Hz are distinct modes that report as 59 and 60. The exact rate for whatever is
/// currently applied comes from the CCD side instead, via <see cref="MonitorInfo.RefreshRate"/>.
/// </para>
/// </remarks>
public sealed record DisplayMode(int Width, int Height, uint RefreshHz)
{
    public DisplayResolution Resolution => new(Width, Height);

    public long PixelCount => (long)Width * Height;

    public override string ToString() => $"{Width} × {Height} @ {RefreshHz} Hz";
}

/// <summary>
/// A requested change to one monitor. Every setting is nullable and null means "leave alone", so a
/// caller can change only the refresh rate without having to restate everything else.
/// </summary>
public sealed class MonitorChangeRequest
{
    /// <summary>Which monitor. Matched on <see cref="MonitorInfo.StableKey"/>.</summary>
    public required MonitorInfo Monitor { get; init; }

    /// <summary>
    /// Switch the monitor on or off. Null leaves it as it is.
    /// </summary>
    /// <remarks>
    /// This is a topology change, which is a different kind of operation from the rest: it goes
    /// through the CCD API rather than GDI, and it invalidates the GDI device names of the other
    /// displays, so it is always carried out first and on its own.
    /// </remarks>
    public bool? Enabled { get; init; }

    public DisplayResolution? Resolution { get; init; }

    public uint? RefreshHz { get; init; }

    /// <summary>
    /// Rotate the display. Changing between landscape and portrait transposes the desktop
    /// resolution as well; that happens automatically unless <see cref="Resolution"/> also says
    /// otherwise, in which case the explicit resolution wins and is taken to be in the new orientation.
    /// </summary>
    public DisplayOrientation? Orientation { get; init; }

    /// <summary>
    /// Move the display's top-left corner in virtual desktop coordinates. Applied before any
    /// primary-display translation, so the two compose rather than fight.
    /// </summary>
    public DisplayPosition? Position { get; init; }

    /// <summary>
    /// When true, this monitor becomes the primary. Only one request in a batch may set it.
    /// Setting it moves every display, since the primary defines the desktop origin.
    /// </summary>
    public bool MakePrimary { get; init; }

    public bool ChangesAnything =>
        Enabled is not null
        || Resolution is not null
        || RefreshHz is not null
        || Orientation is not null
        || Position is not null
        || MakePrimary;

    /// <summary>True when anything here needs the GDI mode stage rather than the topology stage.</summary>
    public bool ChangesMode =>
        Resolution is not null || RefreshHz is not null || Orientation is not null || Position is not null;

    public override string ToString()
    {
        var parts = new List<string>();

        if (Enabled is { } enabled)
        {
            parts.Add(enabled ? "enable" : "disable");
        }

        if (Resolution is { } resolution)
        {
            parts.Add(resolution.ToString());
        }

        if (RefreshHz is { } hz)
        {
            parts.Add($"{hz} Hz");
        }

        if (Orientation is { } orientation)
        {
            parts.Add(orientation.ToString());
        }

        if (Position is { } position)
        {
            parts.Add($"at {position}");
        }

        if (MakePrimary)
        {
            parts.Add("primary");
        }

        return $"{Monitor.FriendlyName}: {(parts.Count == 0 ? "no change" : string.Join(", ", parts))}";
    }
}

/// <summary>Outcome of an apply or restore.</summary>
public sealed class ApplyResult
{
    public required bool Succeeded { get; init; }

    /// <summary>Human-readable reason, set when <see cref="Succeeded"/> is false.</summary>
    public string? Message { get; init; }

    /// <summary>
    /// True when the attempt failed and the previous configuration was put back. False on a clean
    /// failure that never touched anything, and on success.
    /// </summary>
    public bool RolledBack { get; init; }

    public static ApplyResult Ok() => new() { Succeeded = true };

    public static ApplyResult Fail(string message, bool rolledBack = false) =>
        new() { Succeeded = false, Message = message, RolledBack = rolledBack };
}
