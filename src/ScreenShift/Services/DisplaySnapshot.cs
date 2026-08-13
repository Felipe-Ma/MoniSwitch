using ScreenShift.Native;

namespace ScreenShift.Services;

/// <summary>One display's complete state at the moment a snapshot was taken.</summary>
internal sealed record SavedDisplayMode(string GdiDeviceName, DEVMODE Mode, bool WasPrimary);

/// <summary>
/// The display configuration as it was before a change, kept so it can be put back.
/// </summary>
/// <remarks>
/// Opaque by design: it holds raw DEVMODE structs, and handing those out would let callers
/// construct one by hand and apply it. The only thing you can do with a snapshot is restore it.
/// </remarks>
public sealed class DisplaySnapshot
{
    internal DisplaySnapshot(IReadOnlyList<SavedDisplayMode> entries, DisplayConfigSnapshot? configuration)
    {
        Entries = entries;
        Configuration = configuration;
        CapturedAt = DateTime.Now;
    }

    internal IReadOnlyList<SavedDisplayMode> Entries { get; }

    /// <summary>
    /// The full CCD path and mode arrays. This is what makes a snapshot able to undo a topology
    /// change: the per-display modes below describe how each monitor was configured, but only this
    /// records which monitors were switched on in the first place.
    /// </summary>
    internal DisplayConfigSnapshot? Configuration { get; }

    public DateTime CapturedAt { get; }

    public int DisplayCount => Entries.Count;

    public bool IsEmpty => Entries.Count == 0 && Configuration is null;

    public override string ToString() =>
        $"{Entries.Count} display(s) captured at {CapturedAt:HH:mm:ss.fff}";
}
