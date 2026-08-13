using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using ScreenShift.Services;
using ScreenShift.ViewModels;
using ScreenShift.Views;

namespace ScreenShift;

public partial class App : Application
{
    /// <summary>Local\ scope: one instance per signed-in user, not per machine.</summary>
    private const string SingleInstanceMutexName = @"Local\ScreenShift.SingleInstance";

    /// <summary>Broadcast by a second launch so the running instance shows its window.</summary>
    public static readonly uint ShowExistingInstanceMessage =
        Native.ShellInterop.RegisterWindowMessage("ScreenShift.ShowExistingInstance");

    private IAppLogger _logger = NullLogger.Instance;
    private HotkeyService? _hotkeyService;
    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Two instances mean two tray icons, a second set of hotkey registrations that silently
        // fails, and two windows saving over the same profiles.json with independent in-memory
        // lists. Nothing good lives there, so a second launch just wakes the first instance up.
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isFirstInstance);

        if (!isFirstInstance)
        {
            Native.ShellInterop.PostMessage(
                Native.ShellInterop.HWND_BROADCAST, ShowExistingInstanceMessage, IntPtr.Zero, IntPtr.Zero);
            Shutdown();
            return;
        }

        _logger = new FileLogger();

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        _logger.Info($"--- ScreenShift {version} starting ({RuntimeInformation.OSDescription}, {RuntimeInformation.ProcessArchitecture}) ---");

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        try
        {
            // The DisplayService constructor verifies every P/Invoke struct size. If the
            // marshalled layout is wrong, failing here is far better than passing user32 a
            // misshapen buffer and reading back plausible-looking nonsense.
            var displayService = new DisplayService(_logger);
            var profileService = new DisplayProfileService(displayService, _logger);
            var settingsService = new SettingsService(_logger);
            _hotkeyService = new HotkeyService(_logger);

            // The dialogs want the main window as their owner, but the window needs the view
            // model, which needs the interaction service. A late-bound accessor unties that knot
            // without an ordering hack.
            MainWindow? window = null;
            var interaction = new DialogUserInteraction(() => window);

            var viewModel = new MainViewModel(
                displayService, profileService, _hotkeyService, settingsService, interaction, _logger);

            window = new MainWindow(viewModel);
            MainWindow = window;

            // --minimized (what the startup registration passes) begins life in the tray. The
            // window handle still has to exist — the tray icon and the hotkeys both hang off it —
            // so it is created without showing the window. If the tray is disabled the flag is
            // ignored, because an invisible app with no icon would be unreachable.
            var startMinimized = e.Args.Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase));

            if (startMinimized && settingsService.Current.ShowTrayIcon)
            {
                new WindowInteropHelper(window).EnsureHandle();
                _logger.Info("Started minimized to the tray.");
            }
            else
            {
                window.Show();
            }
        }
        catch (Exception ex)
        {
            _logger.Error("ScreenShift could not start.", ex);
            MessageBox.Show(
                $"ScreenShift could not start.\n\n{ex.Message}\n\nDetails were written to:\n{AppPaths.LogDirectory}",
                "ScreenShift",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hotkeyService?.Dispose();
        _logger.Info($"--- ScreenShift exiting (code {e.ApplicationExitCode}) ---");
        base.OnExit(e);
    }

    /// <summary>
    /// A failure while reading display state should not take the window down — the user still needs
    /// the Refresh button to try again.
    /// </summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger.Error("Unhandled exception on the UI thread.", e.Exception);

        MessageBox.Show(
            $"Something went wrong.\n\n{e.Exception.Message}\n\nDetails were written to:\n{AppPaths.LogDirectory}",
            "ScreenShift",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);

        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        _logger.Error("Unhandled exception outside the UI thread.", e.ExceptionObject as Exception);
    }
}
