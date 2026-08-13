using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ScreenShift.Services;
using ScreenShift.ViewModels;
using ScreenShift.Views;

namespace ScreenShift.Probe;

/// <summary>
/// Renders the real MainWindow to a PNG.
/// </summary>
/// <remarks>
/// This exists so the UI can be checked without a person having to look at it — XAML problems
/// (a binding that silently resolves to nothing, a panel arranged off its parent, text the same
/// colour as its background) compile fine and log nothing. It draws the window's own visual tree
/// via RenderTargetBitmap, so nothing outside this application is captured.
/// </remarks>
internal static class WindowCapture
{
    /// <summary>Long enough for Loaded, the enumeration, and the layout pass that follows it.</summary>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(900);

    private const double RenderScale = 1.5;

    public static int Run(string outputPath, double width, double height)
    {
        var exitCode = 0;

        var thread = new Thread(() =>
        {
            try
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

                // A bare Application has none of App.xaml's resources, so the theme has to be
                // merged in by hand or every StaticResource lookup in MainWindow.xaml throws.
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/ScreenShift;component/Themes/Dark.xaml"),
                });

                var logger = new FileLogger();
                var displayService = new DisplayService(logger);
                var viewModel = new MainViewModel(
                    displayService,
                    new DisplayProfileService(displayService, logger),
                    new DialogUserInteraction(() => null),
                    logger);

                var window = new MainWindow(viewModel)
                {
                    Width = width,
                    Height = height,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                };

                var timer = new DispatcherTimer { Interval = SettleDelay };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();

                    try
                    {
                        Capture(window, outputPath);
                        Console.WriteLine($"Wrote {outputPath}");
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"Capture failed: {ex}");
                        exitCode = 1;
                    }
                    finally
                    {
                        window.Close();
                        app.Shutdown();
                    }
                };

                window.Show();
                timer.Start();
                app.Run();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Capture host failed: {ex}");
                exitCode = 1;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        return exitCode;
    }

    private static void Capture(Window window, string outputPath)
    {
        window.UpdateLayout();

        var pixelWidth = (int)Math.Ceiling(window.ActualWidth * RenderScale);
        var pixelHeight = (int)Math.Ceiling(window.ActualHeight * RenderScale);

        var bitmap = new RenderTargetBitmap(
            pixelWidth,
            pixelHeight,
            96d * RenderScale,
            96d * RenderScale,
            PixelFormats.Pbgra32);

        bitmap.Render(window);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var stream = File.Create(outputPath);
        encoder.Save(stream);
    }
}
