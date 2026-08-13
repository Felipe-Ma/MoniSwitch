using ScreenShift.Models;
using ScreenShift.Probe;
using ScreenShift.Services;

// A console harness over DisplayService. The point is to be able to check monitor detection
// against real hardware without a UI in the way — run it, unplug something, run it again.
//
//   ScreenShift.Probe                      dump the detected monitors
//   ScreenShift.Probe --capture <file.png> render the main window to an image instead

Console.OutputEncoding = System.Text.Encoding.UTF8;

var captureIndex = Array.FindIndex(args, a => string.Equals(a, "--capture", StringComparison.OrdinalIgnoreCase));
if (captureIndex >= 0)
{
    if (captureIndex + 1 >= args.Length)
    {
        Console.Error.WriteLine("--capture needs an output path, e.g. --capture window.png");
        return 2;
    }

    return WindowCapture.Run(args[captureIndex + 1], width: 1280, height: 820);
}

var logger = new ConsoleLogger();

try
{
    var service = new DisplayService(logger);
    var monitors = service.GetMonitors();

    Console.WriteLine();
    Console.WriteLine($"=== {monitors.Count} monitor(s) detected ===");
    Console.WriteLine();

    var index = 0;
    foreach (var monitor in monitors)
    {
        index++;
        Console.WriteLine($"[{index}] {monitor.FriendlyName}{(monitor.HasEdidName ? string.Empty : "  (name not from EDID)")}");
        Write("Windows name", monitor.GdiDeviceName ?? "—");
        Write("Display number", monitor.DisplayNumber?.ToString() ?? "—");
        Write("State", Describe(monitor));
        Write("Resolution", monitor.Resolution?.ToString() ?? "—");
        Write("Desktop area", monitor.DesktopSize?.ToString() + (monitor.IsRotated ? "  (rotated)" : string.Empty));
        Write("Aspect", monitor.Resolution?.AspectRatio ?? "—");
        Write("Signal size", monitor.SignalResolution?.ToString() ?? "—");
        Write("Refresh rate", monitor.RefreshRate.ToString() + (monitor.IsInterlaced ? " (interlaced)" : string.Empty));
        Write("Exact rate", monitor.RefreshRate.IsKnown ? $"{monitor.RefreshRate.Numerator}/{monitor.RefreshRate.Denominator}" : "—");
        Write("Orientation", monitor.Orientation.ToString());
        Write("Position", monitor.Position?.ToString() ?? "—");
        Write("Connection", monitor.Connection.ToString());
        Write("EDID", monitor.EdidVendorCode is null ? "—" : $"{monitor.EdidVendorCode} / 0x{monitor.EdidProductCode:X4}");
        Write("Adapter", monitor.AdapterId);
        Write("Adapter path", monitor.AdapterDevicePath ?? "—");
        Write("Target id", monitor.TargetId.ToString());
        Write("Source id", monitor.IsEnabled ? monitor.SourceId.ToString() : "—");
        Write("Device path", string.IsNullOrEmpty(monitor.DevicePath) ? "—" : monitor.DevicePath);
        Console.WriteLine();
    }

    var enabled = monitors.Where(m => m.IsEnabled).ToList();
    if (enabled.Count > 0)
    {
        var left = enabled.Min(m => m.Bounds!.Value.X);
        var top = enabled.Min(m => m.Bounds!.Value.Y);
        var right = enabled.Max(m => m.Bounds!.Value.X + m.Bounds!.Value.Width);
        var bottom = enabled.Max(m => m.Bounds!.Value.Y + m.Bounds!.Value.Height);
        Console.WriteLine($"Virtual desktop: ({left}, {top}) to ({right}, {bottom})  =  {right - left} × {bottom - top}");
    }

    var duplicateNames = monitors.GroupBy(m => m.FriendlyName).Where(g => g.Count() > 1).ToList();
    foreach (var group in duplicateNames)
    {
        var keys = string.Join(", ", group.Select(m => m.StableKey));
        Console.WriteLine($"Note: {group.Count()} monitors share the name \"{group.Key}\"; they are told apart by stable key: {keys}");
    }

    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine("Enumeration failed:");
    Console.Error.WriteLine(ex);
    return 1;
}

static void Write(string label, string value) => Console.WriteLine($"      {label,-14} {value}");

static string Describe(MonitorInfo monitor)
{
    var parts = new List<string> { monitor.IsEnabled ? "enabled" : "disabled" };
    if (monitor.IsPrimary)
    {
        parts.Add("primary");
    }

    if (monitor.IsCloned)
    {
        parts.Add("cloned");
    }

    return string.Join(", ", parts);
}

internal sealed class ConsoleLogger : IAppLogger
{
    public void Log(LogLevel level, string message, Exception? exception = null)
    {
        if (level < LogLevel.Info)
        {
            return;
        }

        var previous = Console.ForegroundColor;
        Console.ForegroundColor = level switch
        {
            LogLevel.Warn => ConsoleColor.Yellow,
            LogLevel.Error => ConsoleColor.Red,
            _ => ConsoleColor.DarkGray,
        };

        Console.WriteLine($"{level,-5} {message}");
        if (exception is not null)
        {
            Console.WriteLine(exception);
        }

        Console.ForegroundColor = previous;
    }
}
