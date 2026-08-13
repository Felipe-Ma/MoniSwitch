using ScreenShift.Models;
using ScreenShift.Probe;
using ScreenShift.Services;

// A console harness over DisplayService. The point is to be able to check monitor detection
// against real hardware without a UI in the way — run it, unplug something, run it again.
//
//   ScreenShift.Probe                      dump the detected monitors
//   ScreenShift.Probe --modes              dump raw DEVMODE data and supported modes
//   ScreenShift.Probe --test-apply <kind>  apply a change to a non-primary display, then undo it
//   ScreenShift.Probe --profile-save <name>    save the current configuration as a profile
//   ScreenShift.Probe --profile-list           list saved profiles
//   ScreenShift.Probe --profile-apply <name>   apply a profile (add --dry to only print the plan)
//   ScreenShift.Probe --profile-delete <name>  delete a profile
//   ScreenShift.Probe --test-dialog [seconds]  show the keep/revert prompt; it must time out
//   ScreenShift.Probe --capture <file.png> render the main window to an image instead

Console.OutputEncoding = System.Text.Encoding.UTF8;

if (args.Any(a => string.Equals(a, "--modes", StringComparison.OrdinalIgnoreCase)))
{
    return ModeDump.Run();
}

if (args.Any(a => string.Equals(a, "--paths", StringComparison.OrdinalIgnoreCase)))
{
    return PathDump.Dump();
}

var topologyIndex = Array.FindIndex(args, a => string.Equals(a, "--topology", StringComparison.OrdinalIgnoreCase));
if (topologyIndex >= 0)
{
    if (topologyIndex + 1 >= args.Length)
    {
        Console.Error.WriteLine("--topology needs a name: extend, clone, internal or external.");
        return 2;
    }

    return PathDump.ApplyTopology(args[topologyIndex + 1]);
}

var forceIndex = Array.FindIndex(args, a => string.Equals(a, "--force-enable", StringComparison.OrdinalIgnoreCase));
if (forceIndex >= 0)
{
    if (forceIndex + 1 >= args.Length || !uint.TryParse(args[forceIndex + 1], out var forceTargetId))
    {
        Console.Error.WriteLine("--force-enable needs a target id, e.g. --force-enable 4357");
        return 2;
    }

    return PathDump.ForceEnable(forceTargetId);
}

if (args.Any(a => string.Equals(a, "--check-persist", StringComparison.OrdinalIgnoreCase)))
{
    return PersistenceCheck.Run();
}

if (args.Any(a => string.Equals(a, "--persist", StringComparison.OrdinalIgnoreCase)))
{
    var persistLogger = new ConsoleLogger();
    var persistResult = new DisplayService(persistLogger).PersistCurrentConfiguration();

    Console.WriteLine(persistResult.Succeeded
        ? "The current display configuration was saved; it should survive a reboot."
        : $"Could not persist: {persistResult.Message}");

    return persistResult.Succeeded ? 0 : 1;
}

var makeIconIndex = Array.FindIndex(args, a => string.Equals(a, "--make-icon", StringComparison.OrdinalIgnoreCase));
if (makeIconIndex >= 0)
{
    if (makeIconIndex + 1 >= args.Length)
    {
        Console.Error.WriteLine("--make-icon needs an output path, e.g. --make-icon app.ico");
        return 2;
    }

    return MakeIcon.Run(args[makeIconIndex + 1]);
}

if (args.Any(a => string.Equals(a, "--test-tray", StringComparison.OrdinalIgnoreCase)))
{
    return TrayTest.Run();
}

if (args.Any(a => string.Equals(a, "--test-hotkey", StringComparison.OrdinalIgnoreCase)))
{
    return HotkeyTest.Run();
}

var profileHotkeyIndex = Array.FindIndex(args, a => string.Equals(a, "--profile-hotkey", StringComparison.OrdinalIgnoreCase));
if (profileHotkeyIndex >= 0)
{
    if (profileHotkeyIndex + 2 >= args.Length)
    {
        Console.Error.WriteLine("--profile-hotkey needs a profile name and a gesture (or 'clear'), e.g. --profile-hotkey Baseline Ctrl+Alt+1");
        return 2;
    }

    return ProfileCommands.SetHotkey(args[profileHotkeyIndex + 1], args[profileHotkeyIndex + 2]);
}

var profileSaveIndex = Array.FindIndex(args, a => string.Equals(a, "--profile-save", StringComparison.OrdinalIgnoreCase));
if (profileSaveIndex >= 0)
{
    if (profileSaveIndex + 1 >= args.Length)
    {
        Console.Error.WriteLine("--profile-save needs a name, e.g. --profile-save Gaming");
        return 2;
    }

    return ProfileCommands.Save(args[profileSaveIndex + 1]);
}

if (args.Any(a => string.Equals(a, "--profile-list", StringComparison.OrdinalIgnoreCase)))
{
    return ProfileCommands.List();
}

var profileApplyIndex = Array.FindIndex(args, a => string.Equals(a, "--profile-apply", StringComparison.OrdinalIgnoreCase));
if (profileApplyIndex >= 0)
{
    if (profileApplyIndex + 1 >= args.Length)
    {
        Console.Error.WriteLine("--profile-apply needs a profile name.");
        return 2;
    }

    var dry = args.Any(a => string.Equals(a, "--dry", StringComparison.OrdinalIgnoreCase));
    return ProfileCommands.Apply(args[profileApplyIndex + 1], dry);
}

var profileDeleteIndex = Array.FindIndex(args, a => string.Equals(a, "--profile-delete", StringComparison.OrdinalIgnoreCase));
if (profileDeleteIndex >= 0)
{
    if (profileDeleteIndex + 1 >= args.Length)
    {
        Console.Error.WriteLine("--profile-delete needs a profile name.");
        return 2;
    }

    return ProfileCommands.Delete(args[profileDeleteIndex + 1]);
}

var dialogTestIndex = Array.FindIndex(args, a => string.Equals(a, "--test-dialog", StringComparison.OrdinalIgnoreCase));
if (dialogTestIndex >= 0)
{
    var seconds = dialogTestIndex + 1 < args.Length && double.TryParse(args[dialogTestIndex + 1], out var parsed)
        ? parsed
        : 4d;
    return DialogTest.Run(seconds);
}

var setIndex = Array.FindIndex(args, a => string.Equals(a, "--set", StringComparison.OrdinalIgnoreCase));
if (setIndex >= 0)
{
    if (setIndex + 1 >= args.Length)
    {
        Console.Error.WriteLine("--set needs a display number, e.g. --set 1 --refresh 180 --primary");
        return 2;
    }

    return SetCommand.Run(args, setIndex + 1);
}

var testIndex = Array.FindIndex(args, a => string.Equals(a, "--test-apply", StringComparison.OrdinalIgnoreCase));
if (testIndex >= 0)
{
    if (testIndex + 1 >= args.Length)
    {
        Console.Error.WriteLine("--test-apply needs a change type: refresh, resolution or primary.");
        return 2;
    }

    return ApplyTest.Run(args[testIndex + 1]);
}

var captureIndex = Array.FindIndex(args, a => string.Equals(a, "--capture", StringComparison.OrdinalIgnoreCase));
if (captureIndex >= 0)
{
    if (captureIndex + 1 >= args.Length)
    {
        Console.Error.WriteLine("--capture needs an output path, e.g. --capture window.png");
        return 2;
    }

    // Matches the app's default window size, so the capture proves the no-scroll layout.
    return WindowCapture.Run(args[captureIndex + 1], width: 1240, height: 900);
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
        Write("Resolution", monitor.Resolution?.ToString() + (monitor.IsRotated ? "  (desktop space)" : string.Empty));
        Write("Panel mode", monitor.PanelResolution?.ToString() ?? "—");
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
