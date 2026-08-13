namespace ScreenShift.Models;

/// <summary>
/// One monitor's saved state inside a profile. This is a flat storage DTO, deliberately separate
/// from <see cref="MonitorInfo"/>: the JSON schema should not pick up computed properties, and it
/// has to stay stable even if the runtime models change shape.
/// </summary>
/// <remarks>
/// The identity fields mirror the matching strategy, in decreasing order of trust: the device path
/// pins the monitor to a port and survives reboots; adapter path + target id survive a device path
/// changing shape; the EDID ids survive the monitor moving to another port, but cannot tell two
/// identical monitors apart. <see cref="FriendlyName"/> is never used for matching — it is there so
/// warnings about a missing monitor can name it.
/// <para>
/// Width/Height are desktop space (a portrait 1440p monitor stores 1440 × 2560), matching what the
/// GDI mode list offers and what gets written back. The mode fields are nullable because a monitor
/// saved while switched off has no mode to record.
/// </para>
/// </remarks>
public sealed class ProfileMonitorConfig
{
    public string DevicePath { get; set; } = string.Empty;

    public string? AdapterPath { get; set; }

    public uint TargetId { get; set; }

    public string FriendlyName { get; set; } = string.Empty;

    public string? EdidVendor { get; set; }

    public ushort? EdidProduct { get; set; }

    public bool Enabled { get; set; }

    public bool Primary { get; set; }

    public int? Width { get; set; }

    public int? Height { get; set; }

    /// <summary>Whole hertz, as GDI stores it — the value that gets written back on apply.</summary>
    public uint? RefreshHz { get; set; }

    public DisplayOrientation Orientation { get; set; } = DisplayOrientation.Landscape;

    public int? PosX { get; set; }

    public int? PosY { get; set; }

    public ProfileMonitorConfig Clone() => (ProfileMonitorConfig)MemberwiseClone();

    public override string ToString() => Enabled
        ? $"{FriendlyName}: {Width} × {Height} @ {RefreshHz} Hz at ({PosX}, {PosY}), {Orientation}{(Primary ? ", primary" : string.Empty)}"
        : $"{FriendlyName}: off";
}

/// <summary>
/// A named display layout. Persisted as human-readable JSON in %APPDATA%\ScreenShift\profiles.json.
/// </summary>
public sealed class DisplayProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public List<ProfileMonitorConfig> Monitors { get; set; } = [];
}
