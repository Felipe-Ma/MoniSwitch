using System.Globalization;

namespace ScreenShift.Models;

/// <summary>Rotation of the desktop image on a display. Values are the rotation in degrees.</summary>
public enum DisplayOrientation
{
    Landscape = 0,
    Portrait = 90,
    LandscapeFlipped = 180,
    PortraitFlipped = 270,
}

/// <summary>How a monitor is physically attached.</summary>
public enum MonitorConnection
{
    Unknown,
    Internal,
    Vga,
    Dvi,
    Hdmi,
    DisplayPort,
    UsbC,
    Wireless,
    Virtual,
    Composite,
    Other,
}

/// <summary>
/// A refresh rate as Windows actually stores it. Keeping the fraction rather than a double is what
/// lets 59.94 Hz (60000/1001) round-trip without drifting into "60" and back out as something else.
/// </summary>
public readonly record struct RefreshRate(uint Numerator, uint Denominator)
{
    public static RefreshRate Unknown => new(0, 0);

    public bool IsKnown => Denominator != 0 && Numerator != 0;

    public double Hz => Denominator == 0 ? 0d : (double)Numerator / Denominator;

    /// <summary>
    /// Rounds to a whole number only when the true value is genuinely that close, so 143.998 Hz
    /// shows as "144 Hz" while 59.94 Hz keeps its decimals instead of being flattened to "60 Hz".
    /// </summary>
    public override string ToString()
    {
        if (!IsKnown)
        {
            return "—";
        }

        var hz = Hz;
        var whole = Math.Round(hz);

        return Math.Abs(hz - whole) < 0.01d
            ? whole.ToString("0", CultureInfo.CurrentCulture) + " Hz"
            : hz.ToString("0.###", CultureInfo.CurrentCulture) + " Hz";
    }
}

/// <summary>A pixel size. Width and height are the desktop surface, already accounting for rotation.</summary>
public readonly record struct DisplayResolution(int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public long PixelCount => (long)Width * Height;

    /// <summary>"16:9", "21:9", ... reduced by GCD. Useful for spotting an ultrawide at a glance.</summary>
    public string AspectRatio
    {
        get
        {
            if (IsEmpty)
            {
                return "—";
            }

            var divisor = Gcd(Width, Height);
            return $"{Width / divisor}:{Height / divisor}";
        }
    }

    public override string ToString() => IsEmpty ? "—" : $"{Width} × {Height}";

    private static int Gcd(int a, int b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return a;
    }
}

/// <summary>
/// Top-left corner of a display in virtual desktop coordinates. The primary display sits at (0,0),
/// so monitors placed to its left or above it have negative coordinates — which the layout maths
/// has to tolerate rather than clamp.
/// </summary>
public readonly record struct DisplayPosition(int X, int Y)
{
    public override string ToString() => $"{X}, {Y}";
}
