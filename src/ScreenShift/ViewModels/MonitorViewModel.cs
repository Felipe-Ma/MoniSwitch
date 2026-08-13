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

    public MonitorViewModel(MonitorInfo model)
    {
        Model = model;
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

        // The desktop rectangle only differs from the mode resolution when the display is turned a
        // quarter turn, and that difference is exactly what trips up layout maths — so name it.
        if (Model.IsRotated && Model.DesktopSize is { } desktop)
        {
            rows.Add(new DetailRow("Desktop area", $"{desktop} (rotated)"));
        }

        if (Model.Resolution is { IsEmpty: false } resolution)
        {
            rows.Add(new DetailRow("Aspect ratio", resolution.AspectRatio));
        }

        // Only worth surfacing when the GPU is scaling: desktop size and signal size disagree.
        if (Model.SignalResolution is { IsEmpty: false } signal && signal != Model.Resolution)
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
