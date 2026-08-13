using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ScreenShift.Native;

// Shell and input interop: the tray icon, global hotkeys, and runtime icon creation.
// Kept separate from NativeMethods.cs deliberately — that file is the display-configuration
// surface, and mixing subsystems would blur what each layer is allowed to touch.

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct NOTIFYICONDATA
{
    public uint cbSize;
    public IntPtr hWnd;
    public uint uID;
    public uint uFlags;
    public uint uCallbackMessage;
    public IntPtr hIcon;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string szTip;

    public uint dwState;
    public uint dwStateMask;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string szInfo;

    /// <summary>Union of uTimeout and uVersion. Left at 0: legacy callback behaviour, where
    /// lParam of the callback message carries the plain mouse message.</summary>
    public uint uTimeoutOrVersion;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    public string szInfoTitle;

    public uint dwInfoFlags;
    public Guid guidItem;
    public IntPtr hBalloonIcon;

    public static NOTIFYICONDATA Create(IntPtr hwnd, uint id) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = hwnd,
        uID = id,
        szTip = string.Empty,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };
}

[StructLayout(LayoutKind.Sequential)]
internal struct ICONINFO
{
    /// <summary>Win32 BOOL: nonzero for an icon, zero for a cursor.</summary>
    public int fIcon;

    public uint xHotspot;
    public uint yHotspot;
    public IntPtr hbmMask;
    public IntPtr hbmColor;
}

[SupportedOSPlatform("windows")]
internal static class ShellInterop
{
    private const string User32 = "user32.dll";
    private const string Shell32 = "shell32.dll";
    private const string Gdi32 = "gdi32.dll";

    // --- Shell_NotifyIcon ---------------------------------------------------
    public const uint NIM_ADD = 0x0;
    public const uint NIM_MODIFY = 0x1;
    public const uint NIM_DELETE = 0x2;

    public const uint NIF_MESSAGE = 0x1;
    public const uint NIF_ICON = 0x2;
    public const uint NIF_TIP = 0x4;

    // --- Window messages ----------------------------------------------------
    public const int WM_APP = 0x8000;
    public const int WM_HOTKEY = 0x0312;
    public const int WM_LBUTTONUP = 0x0202;
    public const int WM_LBUTTONDBLCLK = 0x0203;
    public const int WM_RBUTTONUP = 0x0205;
    public const int WM_CONTEXTMENU = 0x007B;

    // --- RegisterHotKey modifiers. Values happen to match System.Windows.Input.ModifierKeys. ---
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;

    /// <summary>Suppresses auto-repeat while the combination is held. Without it, holding the
    /// hotkey would fire apply-after-apply into the confirmation flow.</summary>
    public const uint MOD_NOREPEAT = 0x4000;

    public const int SM_CXSMICON = 49;

    [DllImport(Shell32, CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);

    /// <summary>Explorer broadcasts this named message when it (re)starts; every tray icon has to
    /// re-add itself on receipt or it silently disappears after an Explorer crash.</summary>
    [DllImport(User32, CharSet = CharSet.Unicode, EntryPoint = "RegisterWindowMessageW")]
    internal static extern uint RegisterWindowMessage(string message);

    /// <summary>Required before opening the tray menu, or it will not dismiss on an outside click.</summary>
    [DllImport(User32, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>Every top-level window, hidden ones included — how a second instance pokes the first.</summary>
    internal static readonly IntPtr HWND_BROADCAST = new(0xFFFF);

    [DllImport(User32, CharSet = CharSet.Unicode, EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport(User32, ExactSpelling = true)]
    internal static extern int GetSystemMetrics(int index);

    // --- Global hotkeys -----------------------------------------------------

    [DllImport(User32, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport(User32, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    /// <summary>Used only by the probe's hotkey self-test to inject the registered combination.</summary>
    [DllImport(User32, ExactSpelling = true)]
    internal static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, IntPtr dwExtraInfo);

    public const uint KEYEVENTF_KEYUP = 0x2;

    // --- Icon creation ------------------------------------------------------

    [DllImport(Gdi32, ExactSpelling = true)]
    internal static extern IntPtr CreateBitmap(int nWidth, int nHeight, uint nPlanes, uint nBitCount, byte[]? lpBits);

    [DllImport(User32, ExactSpelling = true)]
    internal static extern IntPtr CreateIconIndirect(ref ICONINFO iconInfo);

    [DllImport(User32, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport(Gdi32, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteObject(IntPtr hObject);
}
