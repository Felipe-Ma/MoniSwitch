using System.Collections.ObjectModel;
using ScreenShift.Models;

namespace ScreenShift.ViewModels;

/// <summary>A label/value pair shown in the details panel.</summary>
public sealed record DetailRow(string Label, string Value);

/// <summary>
/// Presentation wrapper around one <see cref="MonitorInfo"/>.
/// </summary>
/// <remarks>
/// The preview rectangle lives here rather than in the view because a later phase wants
/// drag-and-drop repositioning: at that point the drag handler writes back into
/// <see cref="PreviewX"/>/<see cref="PreviewY"/>, and the view model converts to virtual desktop
/// coordinates. Keeping the mapping on this side means the view never has to know about it.
/// </remarks>
public sealed class MonitorViewModel : ObservableObject
{
    private bool _isSelected;
    private double _previewX;
    private double _previewY;
    private double _previewWidth;
    private double _previewHeight;

    private IReadOnlyList<DisplayMode> _modes = [];
    private DisplayResolution? _appliedResolution;
    private uint? _appliedRefreshHz;
    private DisplayResolution? _selectedResolution;
    private uint? _selectedRefreshHz;
    private bool _makePrimary;
    private bool _wantEnabled;
    private bool _suppressRefreshRateRebuild;

    public MonitorViewModel(MonitorInfo model)
    {
        Model = model;
        _wantEnabled = model.IsEnabled;
    }

    public MonitorInfo Model { get; }

    public string StableKey => Model.StableKey;

    public string Title => Model.FriendlyName;

    /// <summary>What the tile shows in its corner — Windows' own display number where we have one.</summary>
    public string NumberBadge => Model.DisplayNumber?.ToString() ?? "•";

    public bool IsEnabled => Model.IsEnabled;

    public bool IsPrimary => Model.IsPrimary;

    public bool IsCloned => Model.IsCloned;

    public string ResolutionText => Model.Resolution?.ToString() ?? "Off";

    public string RefreshRateText => Model.IsEnabled ? Model.RefreshRate.ToString() : "—";

    public string ConnectionText => Model.Connection switch
    {
        MonitorConnection.DisplayPort => "DisplayPort",
        MonitorConnection.Hdmi => "HDMI",
        MonitorConnection.Dvi => "DVI",
        MonitorConnection.Vga => "VGA",
        MonitorConnection.UsbC => "USB-C",
        MonitorConnection.Internal => "Internal",
        MonitorConnection.Wireless => "Wireless",
        MonitorConnection.Virtual => "Virtual",
        MonitorConnection.Composite => "Analog",
        MonitorConnection.Other => "Other",
        _ => "Unknown",
    };

    /// <summary>Short status word shown on the tile: Primary / Enabled / Disabled.</summary>
    public string StateText =>
        !Model.IsEnabled ? "Disabled"
        : Model.IsPrimary ? "Primary"
        : "Enabled";

    public string OrientationText => Model.Orientation switch
    {
        DisplayOrientation.Portrait => "Portrait",
        DisplayOrientation.LandscapeFlipped => "Landscape (flipped)",
        DisplayOrientation.PortraitFlipped => "Portrait (flipped)",
        _ => "Landscape",
    };

    /// <summary>
    /// Only worth showing when the display is turned. Resolution is reported in the panel's native
    /// orientation (matching Windows Settings), so without this a portrait monitor reads as
    /// "2560 × 1440" next to a tall tile, which looks like a bug.
    /// </summary>
    public bool ShowOrientationBadge =>
        Model.IsEnabled && Model.Orientation != DisplayOrientation.Landscape;

    public string OrientationBadgeText => Model.Orientation switch
    {
        DisplayOrientation.Portrait => "Portrait",
        DisplayOrientation.LandscapeFlipped => "Flipped 180°",
        DisplayOrientation.PortraitFlipped => "Portrait 270°",
        _ => string.Empty,
    };

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    // --- Scale-model rectangle, in device-independent pixels within the layout canvas ---------

    public double PreviewX
    {
        get => _previewX;
        set => SetProperty(ref _previewX, value);
    }

    public double PreviewY
    {
        get => _previewY;
        set => SetProperty(ref _previewY, value);
    }

    public double PreviewWidth
    {
        get => _previewWidth;
        set
        {
            if (SetProperty(ref _previewWidth, value))
            {
                OnPropertyChanged(nameof(ShowTileDetails));
            }
        }
    }

    public double PreviewHeight
    {
        get => _previewHeight;
        set
        {
            if (SetProperty(ref _previewHeight, value))
            {
                OnPropertyChanged(nameof(ShowTileDetails));
            }
        }
    }

    /// <summary>
    /// Whether the layout tile is big enough to carry more than its number. A tall stack of
    /// monitors squeezes every tile, and text that overflows a tile gets clipped mid-glyph, which
    /// reads as a rendering bug — so below this size the tile shows the number alone.
    /// </summary>
    public bool ShowTileDetails => _previewHeight >= 74d && _previewWidth >= 86d;

    // --- Configuration (Phase 2) ---------------------------------------------

    /// <summary>Distinct resolutions this monitor supports, largest first. Desktop space.</summary>
    public ObservableCollection<DisplayResolution> AvailableResolutions { get; } = [];

    /// <summary>Refresh rates available at <see cref="SelectedResolution"/>, highest first.</summary>
    public ObservableCollection<uint> AvailableRefreshRates { get; } = [];

    public bool ModesLoaded { get; private set; }

    /// <summary>False for a disabled monitor, which has no GDI device to enumerate modes from.</summary>
    public bool CanConfigure => Model.IsEnabled && ModesLoaded && AvailableResolutions.Count > 0;

    public DisplayResolution? SelectedResolution
    {
        get => _selectedResolution;
        set
        {
            if (SetProperty(ref _selectedResolution, value) && !_suppressRefreshRateRebuild)
            {
                RebuildRefreshRates();
                OnPropertyChanged(nameof(HasPendingChanges));
            }
        }
    }

    public uint? SelectedRefreshHz
    {
        get => _selectedRefreshHz;
        set
        {
            if (SetProperty(ref _selectedRefreshHz, value))
            {
                OnPropertyChanged(nameof(HasPendingChanges));
            }
        }
    }

    /// <summary>Request that this monitor become the primary. Meaningless if it already is.</summary>
    public bool MakePrimary
    {
        get => _makePrimary;
        set
        {
            if (SetProperty(ref _makePrimary, value))
            {
                OnPropertyChanged(nameof(HasPendingChanges));
            }
        }
    }

    /// <summary>
    /// Whether the monitor should be switched on. Unlike the other settings this is meaningful for
    /// a monitor that is currently off — it is the only way to bring one back.
    /// </summary>
    public bool WantEnabled
    {
        get => _wantEnabled;
        set
        {
            if (SetProperty(ref _wantEnabled, value))
            {
                OnPropertyChanged(nameof(HasPendingChanges));
            }
        }
    }

    public bool CanBecomePrimary => Model.IsEnabled && !Model.IsPrimary;

    public bool HasPendingChanges => BuildRequest() is not null;

    /// <summary>
    /// Supplies the mode list and the mode currently applied. Both come from GDI, so they are
    /// directly comparable — deriving the current rate from the CCD value instead would mismatch
    /// on fractional rates (59.94 Hz reads as 59 in one and rounds to 60 in the other).
    /// </summary>
    public void LoadModes(IReadOnlyList<DisplayMode> modes, DisplayMode? current)
    {
        _modes = modes;

        AvailableResolutions.Clear();

        foreach (var resolution in modes
                     .Select(m => m.Resolution)
                     .Distinct()
                     .OrderByDescending(r => r.PixelCount))
        {
            AvailableResolutions.Add(resolution);
        }

        var currentResolution = current?.Resolution ?? Model.Resolution;

        // A mode the driver is running but does not advertise (a custom or overclocked mode) would
        // otherwise leave the picker blank and make "no change" look like a change.
        if (currentResolution is { IsEmpty: false } applied && !AvailableResolutions.Contains(applied))
        {
            AvailableResolutions.Insert(0, applied);
        }

        _appliedResolution = currentResolution;
        _appliedRefreshHz = current?.RefreshHz;

        _suppressRefreshRateRebuild = true;
        SelectedResolution = currentResolution;
        _suppressRefreshRateRebuild = false;

        RebuildRefreshRates();

        ModesLoaded = true;

        OnPropertyChanged(nameof(CanConfigure));
        OnPropertyChanged(nameof(HasPendingChanges));
    }

    /// <summary>Discards any unapplied selections and goes back to what is running now.</summary>
    public void ResetPendingChanges()
    {
        MakePrimary = false;
        WantEnabled = Model.IsEnabled;

        _suppressRefreshRateRebuild = true;
        SelectedResolution = _appliedResolution;
        _suppressRefreshRateRebuild = false;

        RebuildRefreshRates();
        OnPropertyChanged(nameof(HasPendingChanges));
    }

    /// <summary>The change to send to the service, or null when nothing would actually change.</summary>
    public MonitorChangeRequest? BuildRequest()
    {
        // Mode settings only apply to a monitor that is already on. A monitor being switched on in
        // this same request has no mode list yet, so there is nothing meaningful to ask for.
        var configurable = Model.IsEnabled;

        var request = new MonitorChangeRequest
        {
            Monitor = Model,
            Enabled = _wantEnabled != Model.IsEnabled ? _wantEnabled : null,
            Resolution = configurable && _selectedResolution is { } resolution && resolution != _appliedResolution
                ? resolution
                : null,
            RefreshHz = configurable && _selectedRefreshHz is { } hz && hz != _appliedRefreshHz
                ? hz
                : null,
            MakePrimary = configurable && _makePrimary && !Model.IsPrimary,
        };

        return request.ChangesAnything ? request : null;
    }

    /// <summary>
    /// Narrows the rate list to what the chosen resolution actually supports, keeping the current
    /// selection where possible and otherwise falling back to the fastest on offer.
    /// </summary>
    private void RebuildRefreshRates()
    {
        var previous = _selectedRefreshHz;

        AvailableRefreshRates.Clear();

        if (_selectedResolution is { } resolution)
        {
            foreach (var hz in _modes
                         .Where(m => m.Resolution == resolution)
                         .Select(m => m.RefreshHz)
                         .Distinct()
                         .OrderByDescending(hz => hz))
            {
                AvailableRefreshRates.Add(hz);
            }
        }

        if (AvailableRefreshRates.Count == 0)
        {
            if (_appliedRefreshHz is { } applied)
            {
                AvailableRefreshRates.Add(applied);
            }
            else
            {
                SelectedRefreshHz = null;
                return;
            }
        }

        SelectedRefreshHz =
            previous is { } wanted && AvailableRefreshRates.Contains(wanted) ? wanted
            : _appliedRefreshHz is { } current && AvailableRefreshRates.Contains(current) ? current
            : AvailableRefreshRates[0];
    }

    /// <summary>Everything the details panel shows, built once so the view stays a dumb list.</summary>
    public IReadOnlyList<DetailRow> Details => BuildDetails();

    private List<DetailRow> BuildDetails()
    {
        var rows = new List<DetailRow>
        {
            new("Status", DescribeStatus()),
            new("Windows name", Model.GdiDeviceName ?? "— (assigned when enabled)"),
            new("Display number", Model.DisplayNumber?.ToString() ?? "—"),
            new("Connection", ConnectionText),
            new("Resolution", Model.Resolution?.ToString() ?? "—"),
        };

        // Resolution above is desktop space. On a turned display the panel's own mode differs, and
        // that is the number the signal timings are expressed in, so it is worth naming.
        if (Model.IsRotated && Model.PanelResolution is { } panel)
        {
            rows.Add(new DetailRow("Panel mode", $"{panel} (unrotated)"));
        }

        if (Model.Resolution is { IsEmpty: false } resolution)
        {
            rows.Add(new DetailRow("Aspect ratio", resolution.AspectRatio));
        }

        // Only when the GPU is genuinely scaling — compared in panel space, so a rotated display
        // does not trip this.
        if (Model.IsGpuScaled && Model.SignalResolution is { } signal)
        {
            rows.Add(new DetailRow("Signal size", $"{signal} (GPU scaling)"));
        }

        rows.Add(new DetailRow("Refresh rate", DescribeRefreshRate()));
        rows.Add(new DetailRow("Orientation", OrientationText));
        rows.Add(new DetailRow("Position", Model.Position?.ToString() ?? "—"));
        rows.Add(new DetailRow("Monitor name", Model.HasEdidName ? Model.FriendlyName : $"{Model.FriendlyName} (no EDID name)"));

        if (Model.EdidVendorCode is { } vendor)
        {
            rows.Add(new DetailRow("EDID id", $"{vendor} · 0x{Model.EdidProductCode:X4}"));
        }

        rows.Add(new DetailRow("Adapter", Model.AdapterDevicePath ?? Model.AdapterId));
        rows.Add(new DetailRow("Target id", Model.TargetId.ToString()));
        rows.Add(new DetailRow("Stable id", Model.StableKey));

        return rows;
    }

    private string DescribeStatus()
    {
        var parts = new List<string> { Model.IsEnabled ? "Enabled" : "Disabled" };

        if (Model.IsPrimary)
        {
            parts.Add("primary display");
        }

        if (Model.IsCloned)
        {
            parts.Add("duplicated with another display");
        }

        return string.Join(", ", parts);
    }

    private string DescribeRefreshRate()
    {
        if (!Model.IsEnabled || !Model.RefreshRate.IsKnown)
        {
            return "—";
        }

        var text = Model.RefreshRate.ToString();
        var exact = $"{Model.RefreshRate.Numerator}/{Model.RefreshRate.Denominator}";

        // Showing the raw fraction matters for rates like 59.94 Hz, where the rounded number
        // hides the fact that it is 60000/1001 rather than a true 60 Hz.
        text += $"  ({exact})";

        if (Model.IsInterlaced)
        {
            text += "  interlaced";
        }

        return text;
    }
}
