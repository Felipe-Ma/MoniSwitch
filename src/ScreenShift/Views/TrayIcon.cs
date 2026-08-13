using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using ScreenShift.Native;
using ScreenShift.ViewModels;

namespace ScreenShift.Views;

/// <summary>
/// The system tray icon: left-click restores the window, right-click opens a menu of profiles.
/// </summary>
/// <remarks>
/// Built directly on Shell_NotifyIcon rather than a package or WinForms interop, consistent with
/// the rest of the native layer. It lives in Views because the menu is UI: the items bind straight
/// to the profile view models' commands, so enable/disable state during an apply comes for free.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class TrayIcon : IDisposable
{
    private const uint IconId = 1;

    /// <summary>Callback message for tray mouse events; WM_APP offset avoids every shell range.</summary>
    public const int CallbackMessage = ShellInterop.WM_APP + 0x51;

    private readonly MainWindow _window;
    private readonly MainViewModel _viewModel;
    private readonly IntPtr _hwnd;
    private readonly uint _taskbarCreatedMessage;
    private readonly IntPtr _hIcon;

    private bool _added;
    private bool _disposed;

    public TrayIcon(MainWindow window, MainViewModel viewModel)
    {
        _window = window;
        _viewModel = viewModel;
        _hwnd = new WindowInteropHelper(window).Handle;

        // Explorer broadcasts this when it restarts; without re-adding on receipt, the icon
        // vanishes for good after an Explorer crash.
        _taskbarCreatedMessage = ShellInterop.RegisterWindowMessage("TaskbarCreated");

        _hIcon = IconArt.CreateHIcon(Math.Max(16, ShellInterop.GetSystemMetrics(ShellInterop.SM_CXSMICON)));
        Add();
    }

    /// <summary>Handles the window's messages that belong to the tray. True when consumed.</summary>
    public bool HandleMessage(int msg, IntPtr wParam, IntPtr lParam)
    {
        if (_disposed)
        {
            return false;
        }

        if (_taskbarCreatedMessage != 0 && msg == (int)_taskbarCreatedMessage)
        {
            _added = false;
            Add();
            return true;
        }

        if (msg != CallbackMessage)
        {
            return false;
        }

        // Legacy callback behaviour (no NIM_SETVERSION): lParam is the mouse message.
        switch ((int)lParam & 0xFFFF)
        {
            case ShellInterop.WM_LBUTTONUP:
            case ShellInterop.WM_LBUTTONDBLCLK:
                _window.RestoreFromTray();
                return true;

            case ShellInterop.WM_RBUTTONUP:
            case ShellInterop.WM_CONTEXTMENU:
                ShowMenu();
                return true;
        }

        return false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_added)
        {
            var data = NOTIFYICONDATA.Create(_hwnd, IconId);
            ShellInterop.Shell_NotifyIcon(ShellInterop.NIM_DELETE, ref data);
            _added = false;
        }

        if (_hIcon != IntPtr.Zero)
        {
            ShellInterop.DestroyIcon(_hIcon);
        }
    }

    private void Add()
    {
        var data = NOTIFYICONDATA.Create(_hwnd, IconId);
        data.uFlags = ShellInterop.NIF_MESSAGE | ShellInterop.NIF_ICON | ShellInterop.NIF_TIP;
        data.uCallbackMessage = CallbackMessage;
        data.hIcon = _hIcon;
        data.szTip = "ScreenShift";

        _added = ShellInterop.Shell_NotifyIcon(ShellInterop.NIM_ADD, ref data);
    }

    /// <summary>
    /// Menu layout, per the spec: header, profiles, then Open and Exit. Rebuilt on every open so
    /// it always reflects the current profile list without any change tracking.
    /// </summary>
    private void ShowMenu()
    {
        // Without this, the menu will not close when the user clicks elsewhere — the classic
        // tray-menu bug. The window may be hidden; its handle still serves.
        ShellInterop.SetForegroundWindow(_hwnd);

        var menu = new ContextMenu
        {
            Placement = PlacementMode.MousePoint,
            StaysOpen = false,
        };

        menu.Items.Add(new MenuItem
        {
            Header = "ScreenShift",
            IsEnabled = false,
            FontWeight = FontWeights.SemiBold,
        });
        menu.Items.Add(new Separator());

        if (_viewModel.Profiles.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "No profiles saved", IsEnabled = false });
        }
        else
        {
            foreach (var profile in _viewModel.Profiles)
            {
                menu.Items.Add(new MenuItem
                {
                    Header = profile.Name,
                    InputGestureText = profile.Model.Hotkey ?? string.Empty,
                    Command = profile.ApplyCommand,
                });
            }
        }

        menu.Items.Add(new Separator());

        var open = new MenuItem { Header = "Open ScreenShift" };
        open.Click += (_, _) => _window.RestoreFromTray();
        menu.Items.Add(open);

        var exit = new MenuItem { Header = "Exit" };
        exit.Click += (_, _) => _window.ForceExit();
        menu.Items.Add(exit);

        menu.IsOpen = true;
    }
}
