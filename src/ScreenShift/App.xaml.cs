using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using ScreenShift.Services;
using ScreenShift.ViewModels;
using ScreenShift.Views;

namespace ScreenShift;

public partial class App : Application
{
    private IAppLogger _logger = NullLogger.Instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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

            // The confirmation dialog wants the main window as its owner, but the window needs the
            // view model, which needs the confirmation service. A late-bound accessor unties that
            // knot without an ordering hack.
            MainWindow? window = null;
            var confirmation = new DialogUserConfirmation(() => window);

            var viewModel = new MainViewModel(displayService, confirmation, _logger);

            window = new MainWindow(viewModel);
            MainWindow = window;
            window.Show();
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
