using System.Diagnostics;
using System.IO;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ScreenShift.Models;
using ScreenShift.Native;
using ScreenShift.Services;
using ScreenShift.Views;

namespace ScreenShift.Probe;

/// <summary>
/// Writes the application .ico from <see cref="IconArt"/> — the same drawing the tray uses at
/// runtime, so the two can never disagree. PNG-compressed entries, which every supported Windows
/// version reads.
/// </summary>
internal static class MakeIcon
{
    private static readonly int[] Sizes = [16, 24, 32, 48, 64, 128, 256];

    public static int Run(string outputPath)
    {
        var images = new List<byte[]>();

        foreach (var size in Sizes)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(IconArt.Render(size)));

            using var stream = new MemoryStream();
            encoder.Save(stream);
            images.Add(stream.ToArray());
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var file = File.Create(outputPath);
        using var writer = new BinaryWriter(file);

        // ICONDIR: reserved, type 1 (icon), count.
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)Sizes.Length);

        // Directory entries, then the image data they point at.
        var offset = 6 + (16 * Sizes.Length);

        for (var i = 0; i < Sizes.Length; i++)
        {
            var size = Sizes[i];

            writer.Write((byte)(size == 256 ? 0 : size)); // 0 encodes 256
            writer.Write((byte)(size == 256 ? 0 : size));
            writer.Write((byte)0);  // colour count: not palettised
            writer.Write((byte)0);  // reserved
            writer.Write((ushort)1);  // planes
            writer.Write((ushort)32); // bpp
            writer.Write((uint)images[i].Length);
            writer.Write((uint)offset);

            offset += images[i].Length;
        }

        foreach (var image in images)
        {
            writer.Write(image);
        }

        Console.WriteLine($"Wrote {outputPath} ({Sizes.Length} sizes, {file.Length} bytes).");
        return 0;
    }
}

/// <summary>Exercises the Run-key registration: on, off, status. The 'on' path registers the
/// probe's own path, so tests should always finish with 'off'.</summary>
internal static class StartupCommand
{
    public static int Run(string action)
    {
        switch (action.ToLowerInvariant())
        {
            case "status":
                Console.WriteLine(StartupRegistration.IsEnabled()
                    ? $"Registered: {StartupRegistration.RegisteredCommand()}"
                    : "Not registered to start with Windows.");
                return 0;

            case "on":
                StartupRegistration.Enable();
                Console.WriteLine($"Registered: {StartupRegistration.RegisteredCommand()}");
                return 0;

            case "off":
                StartupRegistration.Disable();
                Console.WriteLine($"Removed. IsEnabled now: {StartupRegistration.IsEnabled()}");
                return 0;

            default:
                Console.Error.WriteLine("Use --startup status, on or off.");
                return 2;
        }
    }
}

/// <summary>
/// Adds a real tray icon for a moment, then removes it. Verifies the Shell_NotifyIcon calls and
/// the runtime HICON creation — the parts of the tray that can fail structurally.
/// </summary>
internal static class TrayTest
{
    public static int Run()
    {
        var exitCode = 1;

        var thread = new Thread(() =>
        {
            var source = new HwndSource(new HwndSourceParameters("ScreenShiftTrayTest")
            {
                WindowStyle = 0,
                Width = 0,
                Height = 0,
                ParentWindow = new IntPtr(-3), // HWND_MESSAGE
            });

            var hIcon = IconArt.CreateHIcon(16);

            try
            {
                if (hIcon == IntPtr.Zero)
                {
                    Console.Error.WriteLine("FAIL — CreateHIcon returned null.");
                    return;
                }

                var data = NOTIFYICONDATA.Create(source.Handle, 99);
                data.uFlags = ShellInterop.NIF_MESSAGE | ShellInterop.NIF_ICON | ShellInterop.NIF_TIP;
                data.uCallbackMessage = ShellInterop.WM_APP + 0x52;
                data.hIcon = hIcon;
                data.szTip = "ScreenShift self-test";

                var added = ShellInterop.Shell_NotifyIcon(ShellInterop.NIM_ADD, ref data);
                Console.WriteLine($"NIM_ADD -> {added}");

                Thread.Sleep(1200);

                var deleted = ShellInterop.Shell_NotifyIcon(ShellInterop.NIM_DELETE, ref data);
                Console.WriteLine($"NIM_DELETE -> {deleted}");

                if (added && deleted)
                {
                    Console.WriteLine("PASS — tray icon added and removed.");
                    exitCode = 0;
                }
                else
                {
                    Console.Error.WriteLine("FAIL — Shell_NotifyIcon refused.");
                }
            }
            finally
            {
                ShellInterop.DestroyIcon(hIcon);
                source.Dispose();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        return exitCode;
    }
}

/// <summary>
/// End-to-end hotkey check: registers Ctrl+Alt+F9 through the real <see cref="HotkeyService"/>,
/// injects that combination with keybd_event, and requires the WM_HOTKEY round trip to fire the
/// service's event.
/// </summary>
internal static class HotkeyTest
{
    private const string TestGesture = "Ctrl+Alt+F9";

    private const byte VK_CONTROL = 0x11;
    private const byte VK_MENU = 0x12;
    private const byte VK_F9 = 0x78;

    public static int Run()
    {
        var exitCode = 1;

        var thread = new Thread(() =>
        {
            var logger = new ConsoleLogger();
            var source = new HwndSource(new HwndSourceParameters("ScreenShiftHotkeyTest")
            {
                WindowStyle = 0,
                Width = 0,
                Height = 0,
                ParentWindow = new IntPtr(-3),
            });

            var service = new HotkeyService(logger);

            try
            {
                if (!HotkeyGesture.TryParse(TestGesture, out var gesture))
                {
                    Console.Error.WriteLine("FAIL — the test gesture did not parse.");
                    return;
                }

                service.Initialize(source.Handle);

                var failures = service.Sync([(Guid.NewGuid(), "Self-test", TestGesture)]);
                if (failures.Count > 0)
                {
                    Console.Error.WriteLine($"FAIL — could not register {gesture}: {failures[0]}");
                    return;
                }

                var frame = new DispatcherFrame();
                var fired = false;

                service.ProfileHotkeyPressed += _ =>
                {
                    fired = true;
                    frame.Continue = false;
                };

                source.AddHook((IntPtr _, int msg, IntPtr wParam, IntPtr _, ref bool _) =>
                {
                    service.HandleMessage(msg, wParam);
                    return IntPtr.Zero;
                });

                Console.WriteLine($"Registered {gesture}; injecting the key combination…");

                ShellInterop.keybd_event(VK_CONTROL, 0, 0, IntPtr.Zero);
                ShellInterop.keybd_event(VK_MENU, 0, 0, IntPtr.Zero);
                ShellInterop.keybd_event(VK_F9, 0, 0, IntPtr.Zero);
                ShellInterop.keybd_event(VK_F9, 0, ShellInterop.KEYEVENTF_KEYUP, IntPtr.Zero);
                ShellInterop.keybd_event(VK_MENU, 0, ShellInterop.KEYEVENTF_KEYUP, IntPtr.Zero);
                ShellInterop.keybd_event(VK_CONTROL, 0, ShellInterop.KEYEVENTF_KEYUP, IntPtr.Zero);

                var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                timeout.Tick += (_, _) =>
                {
                    timeout.Stop();
                    frame.Continue = false;
                };
                timeout.Start();

                Dispatcher.PushFrame(frame);
                timeout.Stop();

                if (fired)
                {
                    Console.WriteLine("PASS — WM_HOTKEY arrived and the profile event fired.");
                    exitCode = 0;
                }
                else
                {
                    Console.Error.WriteLine("FAIL — the hotkey never arrived.");
                }
            }
            finally
            {
                service.Dispose();
                source.Dispose();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        return exitCode;
    }
}
