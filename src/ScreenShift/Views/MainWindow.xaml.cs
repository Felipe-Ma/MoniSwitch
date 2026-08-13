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

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;

        InitializeComponent();
        DataContext = viewModel;

        _displayChangeTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = DisplayChangeDebounce,
        };
        _displayChangeTimer.Tick += OnDisplayChangeSettled;

        Loaded += (_, _) => _viewModel.Refresh();
        Closed += (_, _) => _displayChangeTimer.Stop();
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
