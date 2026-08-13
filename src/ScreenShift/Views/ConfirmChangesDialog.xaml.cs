using System.Windows;
using System.Windows.Threading;
using ScreenShift.Services;

namespace ScreenShift.Views;

/// <summary>
/// The "keep or revert" prompt shown after a display change has been applied.
/// </summary>
/// <remarks>
/// Deliberately unable to be dismissed without answering: no title bar, no close button, and the
/// only way out is one of the two buttons or the deadline. It also positions itself on the primary
/// display rather than over the main window, since the main window may have just been moved onto a
/// monitor that is no longer showing anything.
/// </remarks>
public partial class ConfirmChangesDialog : Window
{
    private readonly DispatcherTimer _timer;
    private readonly TimeSpan _timeout;
    private DateTime _deadline;

    private ConfirmChangesDialog(string summary, TimeSpan timeout)
    {
        InitializeComponent();

        _timeout = timeout;
        SummaryText.Text = summary;

        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _timer.Tick += OnTick;

        Loaded += OnLoaded;
        Closed += (_, _) => _timer.Stop();
    }

    /// <summary>
    /// Applies the change confirmation flow. Returns true to keep, false to revert.
    /// </summary>
    public static bool Show(Window? owner, string summary, TimeSpan timeout)
    {
        var dialog = new ConfirmChangesDialog(summary, timeout);

        // Only take an owner that is actually on screen; an owner being shown modally over a dark
        // monitor would drag this dialog there too.
        if (owner is { IsVisible: true })
        {
            dialog.Owner = owner;
        }

        return dialog.ShowDialog() == true;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PositionOnPrimaryDisplay();

        _deadline = DateTime.UtcNow + _timeout;
        UpdateCountdown();
        _timer.Start();

        // Focus the safe option, so a stray Enter does not silently keep a broken configuration.
        RevertButton.Focus();
        Activate();
    }

    /// <summary>
    /// Windows anchors the primary display at (0,0) in WPF's coordinate space too, so the primary
    /// screen's bounds are all that is needed to centre on it.
    /// </summary>
    private void PositionOnPrimaryDisplay()
    {
        Left = Math.Max(0, (SystemParameters.PrimaryScreenWidth - ActualWidth) / 2d);
        Top = Math.Max(0, (SystemParameters.PrimaryScreenHeight - ActualHeight) / 3d);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (DateTime.UtcNow >= _deadline)
        {
            _timer.Stop();

            // Timing out means the user could not or did not confirm, which is the case the whole
            // mechanism exists for: revert.
            DialogResult = false;
            return;
        }

        UpdateCountdown();
    }

    private void UpdateCountdown()
    {
        var remaining = _deadline - DateTime.UtcNow;
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        var seconds = (int)Math.Ceiling(remaining.TotalSeconds);
        CountdownText.Text = seconds == 1
            ? "Reverting in 1 second…"
            : $"Reverting in {seconds} seconds…";

        var fraction = _timeout.TotalMilliseconds <= 0 ? 0d : remaining.TotalMilliseconds / _timeout.TotalMilliseconds;
        var track = ((FrameworkElement)ProgressFill.Parent).ActualWidth;
        ProgressFill.Width = Math.Max(0d, track * fraction);
    }

    private void OnKeep(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        DialogResult = true;
    }

    private void OnRevert(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        DialogResult = false;
    }
}

/// <summary>Wires the dialog up to the view models without letting them reference a window.</summary>
public sealed class DialogUserConfirmation : IUserConfirmation
{
    private readonly Func<Window?> _ownerAccessor;

    public DialogUserConfirmation(Func<Window?> ownerAccessor)
    {
        _ownerAccessor = ownerAccessor;
    }

    public bool ConfirmDisplayChange(string summary, TimeSpan timeout) =>
        ConfirmChangesDialog.Show(_ownerAccessor(), summary, timeout);

    public void ShowError(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
}
