using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using ScreenShift.Native;
using ScreenShift.ViewModels;

namespace ScreenShift.Views;

public partial class MainWindow : Window
{
    /// <summary>
    /// Windows fires WM_DISPLAYCHANGE several times for a single user action (and a driver can add
    /// more), so refreshes are coalesced instead of running once per message.
    /// </summary>
    private static readonly TimeSpan DisplayChangeDebounce = TimeSpan.FromMilliseconds(400);

    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _displayChangeTimer;

    private TrayIcon? _tray;
    private bool _exitRequested;

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;

        InitializeComponent();
        DataContext = viewModel;

        // Set explicitly rather than relying on the embedded exe icon, so the title bar and
        // taskbar look right even when the window is hosted by another process (the probe).
        Icon = IconArt.Render(32);

        _displayChangeTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = DisplayChangeDebounce,
        };
        _displayChangeTimer.Tick += OnDisplayChangeSettled;

        Loaded += (_, _) => _viewModel.Refresh();
        Closed += (_, _) =>
        {
            _displayChangeTimer.Stop();
            _tray?.Dispose();
            _tray = null;
        };

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var handle = new WindowInteropHelper(this).Handle;
        ApplyDarkTitleBar(handle);

        if (HwndSource.FromHwnd(handle) is { } source)
        {
            source.AddHook(WndProc);
        }

        // Both need a real HWND, which is why this happens here and not in the constructor.
        _viewModel.InitializeHotkeys(handle);
        UpdateTrayIcon();
    }

    /// <summary>
    /// Closing while the tray icon is on means "get out of my way", not "stop" — and the process
    /// staying alive is what keeps the global hotkeys working.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exitRequested && _tray is not null && _viewModel.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    /// <summary>Really exits, bypassing close-to-tray. The tray menu's Exit lands here.</summary>
    public void ForceExit()
    {
        _exitRequested = true;
        Close();
    }

    public void RestoreFromTray()
    {
        Show();

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ShowTrayIcon))
        {
            UpdateTrayIcon();
        }
    }

    private void UpdateTrayIcon()
    {
        if (_viewModel.ShowTrayIcon && _tray is null && new WindowInteropHelper(this).Handle != IntPtr.Zero)
        {
            _tray = new TrayIcon(this, _viewModel);
        }
        else if (!_viewModel.ShowTrayIcon && _tray is not null)
        {
            _tray.Dispose();
            _tray = null;
        }
    }

    /// <summary>
    /// WPF windows get the light title bar unless the app opts in, which looks wrong sitting on
    /// top of a dark window. The attribute id changed during Windows 10 20H1, so the older one is
    /// tried as a fallback; on builds that support neither, both calls fail harmlessly.
    /// </summary>
    private static void ApplyDarkTitleBar(IntPtr handle)
    {
        var enabled = 1;

        var result = NativeMethods.DwmSetWindowAttribute(
            handle, NativeConstants.DWMWA_USE_IMMERSIVE_DARK_MODE, ref enabled, sizeof(int));

        if (result != 0)
        {
            NativeMethods.DwmSetWindowAttribute(
                handle, NativeConstants.DWMWA_USE_IMMERSIVE_DARK_MODE_LEGACY, ref enabled, sizeof(int));
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeConstants.WM_DISPLAYCHANGE)
        {
            _displayChangeTimer.Stop();
            _displayChangeTimer.Start();
        }

        // Tray callbacks and WM_HOTKEY arrive here because this window's handle registered both.
        _tray?.HandleMessage(msg, wParam, lParam);
        _viewModel.HandleWindowMessage(msg, wParam);

        // A second launch broadcast this instead of starting: bring this instance forward.
        if (App.ShowExistingInstanceMessage != 0 && msg == (int)App.ShowExistingInstanceMessage)
        {
            RestoreFromTray();
        }

        return IntPtr.Zero;
    }

    private void OnDisplayChangeSettled(object? sender, EventArgs e)
    {
        _displayChangeTimer.Stop();
        _viewModel.Refresh();
    }

    private void OnLayoutHostSizeChanged(object sender, SizeChangedEventArgs e) =>
        _viewModel.SetLayoutViewport(e.NewSize.Width, e.NewSize.Height);
}
