using System.Diagnostics;
using System.Windows;
using ScreenShift.Views;

namespace ScreenShift.Probe;

/// <summary>
/// Shows the real keep/revert dialog and verifies its deadline behaviour: left alone, it must
/// close by itself and answer "revert".
/// </summary>
/// <remarks>
/// This is the one part of the safety net that cannot be exercised by the apply tests, because
/// they bypass the prompt — and it is also the part that matters most, since it is what rescues a
/// user staring at a black screen who cannot click anything.
/// </remarks>
internal static class DialogTest
{
    public static int Run(double timeoutSeconds)
    {
        var exitCode = 1;

        var thread = new Thread(() =>
        {
            try
            {
                // The dialog's XAML leans on the theme's StaticResources, so a bare Application
                // with the dictionary merged in has to exist before the window can load.
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/ScreenShift;component/Themes/Dark.xaml"),
                });

                Console.WriteLine($"Showing the keep/revert dialog with a {timeoutSeconds:0.#}s deadline. Do not click it.");

                var stopwatch = Stopwatch.StartNew();
                var keep = ConfirmChangesDialog.Show(
                    owner: null,
                    "Dialog self-test — do not click anything. This prompt should count down and close on its own.",
                    TimeSpan.FromSeconds(timeoutSeconds));
                stopwatch.Stop();

                var elapsed = stopwatch.Elapsed.TotalSeconds;
                Console.WriteLine($"Dialog returned keep={keep} after {elapsed:0.0}s.");

                if (!keep && elapsed >= timeoutSeconds - 0.5)
                {
                    Console.WriteLine("PASS — an unanswered prompt reverts.");
                    exitCode = 0;
                }
                else
                {
                    Console.Error.WriteLine("FAIL — expected keep=false at the deadline.");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Dialog test failed: {ex}");
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        return exitCode;
    }
}
