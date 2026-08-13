using System.Collections.ObjectModel;
using System.ComponentModel;
using ScreenShift.Models;
using ScreenShift.Services;

namespace ScreenShift.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    /// <summary>Fraction of the layout canvas left as breathing room around the monitor rectangles.</summary>
    private const double LayoutPadding = 0.94;

    /// <summary>Gap drawn between adjacent monitors so their borders do not merge into one block.</summary>
    private const double TileInset = 2d;

    /// <summary>How long the user has to confirm a change before it is undone. Per the spec.</summary>
    private static readonly TimeSpan RollbackTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Breathing room between the mode switch and the confirmation prompt. Monitors take a moment
    /// to resync after a mode change, and a prompt drawn into that gap can be missed entirely.
    /// </summary>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(700);

    private readonly IDisplayService _displayService;
    private readonly IUserConfirmation _confirmation;
    private readonly IAppLogger _logger;

    private bool _isApplying;
    private MonitorViewModel? _selectedMonitor;
    private string _summaryText = string.Empty;
    private string _statusText = "Ready.";
    private string? _errorMessage;
    private bool _isRefreshing;
    private double _viewportWidth;
    private double _viewportHeight;
    private string _virtualDesktopText = "—";

    public MainViewModel(IDisplayService displayService, IUserConfirmation confirmation, IAppLogger logger)
    {
        _displayService = displayService;
        _confirmation = confirmation;
        _logger = logger;

        RefreshCommand = new RelayCommand(Refresh, () => !_isRefreshing && !_isApplying);
        ApplyCommand = new RelayCommand(ApplyChanges, () => !_isApplying && SelectedMonitor?.HasPendingChanges == true);
        ResetCommand = new RelayCommand(ResetChanges, () => !_isApplying && SelectedMonitor?.HasPendingChanges == true);
    }

    /// <summary>Every connected monitor, enabled first.</summary>
    public ObservableCollection<MonitorViewModel> Monitors { get; } = [];

    /// <summary>The subset drawn on the layout canvas. Disabled monitors have no rectangle to draw.</summary>
    public ObservableCollection<MonitorViewModel> EnabledMonitors { get; } = [];

    public RelayCommand RefreshCommand { get; }

    public RelayCommand ApplyCommand { get; }

    public RelayCommand ResetCommand { get; }

    public MonitorViewModel? SelectedMonitor
    {
        get => _selectedMonitor;
        set
        {
            if (ReferenceEquals(_selectedMonitor, value))
            {
                return;
            }

            if (_selectedMonitor is not null)
            {
                _selectedMonitor.IsSelected = false;
            }

            _selectedMonitor = value;

            if (_selectedMonitor is not null)
            {
                _selectedMonitor.IsSelected = true;
                EnsureModesLoaded(_selectedMonitor);
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelection));
            RaiseChangeCommandsCanExecute();
        }
    }

    public bool IsApplying
    {
        get => _isApplying;
        private set
        {
            if (SetProperty(ref _isApplying, value))
            {
                RefreshCommand.RaiseCanExecuteChanged();
                RaiseChangeCommandsCanExecute();
            }
        }
    }

    public bool HasSelection => _selectedMonitor is not null;

    /// <summary>False when every connected monitor is switched off, so the canvas can say so.</summary>
    public bool HasEnabledMonitors => EnabledMonitors.Count > 0;

    public string SummaryText
    {
        get => _summaryText;
        private set => SetProperty(ref _summaryText, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string VirtualDesktopText
    {
        get => _virtualDesktopText;
        private set => SetProperty(ref _virtualDesktopText, value);
    }

    /// <summary>Set when enumeration failed outright; the view shows this instead of the monitor list.</summary>
    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_errorMessage);

    public string LogFilePath => (_logger as FileLogger)?.FilePath ?? AppPaths.LogDirectory;

    /// <summary>Re-reads the display configuration and rebuilds the monitor list.</summary>
    public void Refresh()
    {
        // Applying a change fires WM_DISPLAYCHANGE, which would otherwise rebuild the list (and so
        // discard the selection and pending edits) while the confirmation prompt is still up.
        if (_isRefreshing || _isApplying)
        {
            return;
        }

        _isRefreshing = true;
        RefreshCommand.RaiseCanExecuteChanged();

        // Selection is restored by stable id rather than by index, so it survives Windows
        // renumbering the displays between one refresh and the next.
        var previousKey = SelectedMonitor?.StableKey;

        try
        {
            var monitors = _displayService.GetMonitors();

            foreach (var existing in Monitors)
            {
                existing.PropertyChanged -= OnMonitorPropertyChanged;
            }

            Monitors.Clear();
            EnabledMonitors.Clear();
            _selectedMonitor = null;

            foreach (var monitor in monitors)
            {
                var vm = new MonitorViewModel(monitor);
                vm.PropertyChanged += OnMonitorPropertyChanged;
                Monitors.Add(vm);

                if (monitor.IsEnabled && monitor.Bounds is not null)
                {
                    EnabledMonitors.Add(vm);
                }
            }

            ErrorMessage = null;
            OnPropertyChanged(nameof(HasEnabledMonitors));
            UpdateSummary();
            RecalculateLayout();

            SelectedMonitor = Monitors.FirstOrDefault(m => m.StableKey == previousKey)
                ?? Monitors.FirstOrDefault(m => m.IsPrimary)
                ?? Monitors.FirstOrDefault();

            StatusText = $"Last refreshed {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to enumerate monitors.", ex);

            Monitors.Clear();
            EnabledMonitors.Clear();
            _selectedMonitor = null;
            OnPropertyChanged(nameof(SelectedMonitor));
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(HasEnabledMonitors));

            SummaryText = "No displays available";
            VirtualDesktopText = "—";
            ErrorMessage = ex.Message;
            StatusText = $"Refresh failed at {DateTime.Now:HH:mm:ss}";
        }
        finally
        {
            _isRefreshing = false;
            RefreshCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// Applies the selected monitor's pending changes, then asks the user to confirm them before
    /// the deadline runs out.
    /// </summary>
    private async void ApplyChanges()
    {
        if (_isApplying || SelectedMonitor is not { } monitor || monitor.BuildRequest() is not { } request)
        {
            return;
        }

        IsApplying = true;
        StatusText = "Applying display changes…";

        try
        {
            // Taken before anything is touched, so there is always a known-good state to go back to
            // even if the apply succeeds and the result turns out to be unusable.
            var snapshot = _displayService.CaptureSnapshot();

            var result = await Task.Run(() => _displayService.Apply([request]));

            if (!result.Succeeded)
            {
                var detail = result.Message ?? "The change was rejected.";
                if (result.RolledBack)
                {
                    detail += "\n\nThe previous display settings were restored.";
                }

                _logger.Warn($"Apply failed: {detail}");
                _confirmation.ShowError("Could not apply the change", detail);
                StatusText = "The change was rejected.";
                return;
            }

            await Task.Delay(SettleDelay);

            var keep = _confirmation.ConfirmDisplayChange(request.ToString(), RollbackTimeout);

            if (keep)
            {
                _logger.Info("User kept the new display settings.");
                StatusText = $"Applied at {DateTime.Now:HH:mm:ss}";
                return;
            }

            _logger.Info("User declined the new display settings (or the prompt timed out); reverting.");
            var restored = await Task.Run(() => _displayService.Restore(snapshot));

            if (restored.Succeeded)
            {
                StatusText = "Reverted to the previous settings.";
            }
            else
            {
                StatusText = "Revert failed — see the log.";
                _confirmation.ShowError(
                    "Revert failed",
                    restored.Message ?? "The previous display configuration could not be fully restored.");
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Applying display changes threw.", ex);
            _confirmation.ShowError("Could not apply the change", ex.Message);
            StatusText = "The change failed.";
        }
        finally
        {
            // Clear the flag before refreshing: Refresh deliberately no-ops while applying.
            IsApplying = false;
            Refresh();
        }
    }

    private void ResetChanges()
    {
        SelectedMonitor?.ResetPendingChanges();
        RaiseChangeCommandsCanExecute();
    }

    /// <summary>
    /// Reads the monitor's mode list the first time it is selected. Enumerating a few hundred modes
    /// is quick, but there is no reason to do it for monitors the user never opens.
    /// </summary>
    private void EnsureModesLoaded(MonitorViewModel monitor)
    {
        if (monitor.ModesLoaded || !monitor.IsEnabled)
        {
            return;
        }

        try
        {
            var modes = _displayService.GetSupportedModes(monitor.Model);
            var current = _displayService.GetCurrentMode(monitor.Model);
            monitor.LoadModes(modes, current);
        }
        catch (Exception ex)
        {
            _logger.Error($"Could not read the mode list for {monitor.Title}.", ex);
        }
    }

    private void RaiseChangeCommandsCanExecute()
    {
        ApplyCommand.RaiseCanExecuteChanged();
        ResetCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Called by the view whenever the layout canvas is resized.</summary>
    public void SetLayoutViewport(double width, double height)
    {
        if (Math.Abs(_viewportWidth - width) < 0.5 && Math.Abs(_viewportHeight - height) < 0.5)
        {
            return;
        }

        _viewportWidth = width;
        _viewportHeight = height;
        RecalculateLayout();
    }

    /// <summary>
    /// Projects virtual desktop coordinates onto the canvas: one uniform scale for both axes so
    /// the model stays proportional, centred in whatever space the view gives us.
    /// </summary>
    private void RecalculateLayout()
    {
        if (EnabledMonitors.Count == 0 || _viewportWidth <= 0 || _viewportHeight <= 0)
        {
            return;
        }

        // Coordinates are signed — monitors placed left of or above the primary have negative
        // origins — so the bounding box has to be computed rather than assumed to start at zero.
        var left = EnabledMonitors.Min(m => m.Model.Bounds!.Value.X);
        var top = EnabledMonitors.Min(m => m.Model.Bounds!.Value.Y);
        var right = EnabledMonitors.Max(m => m.Model.Bounds!.Value.X + m.Model.Bounds!.Value.Width);
        var bottom = EnabledMonitors.Max(m => m.Model.Bounds!.Value.Y + m.Model.Bounds!.Value.Height);

        double desktopWidth = right - left;
        double desktopHeight = bottom - top;

        if (desktopWidth <= 0 || desktopHeight <= 0)
        {
            return;
        }

        var scale = Math.Min(_viewportWidth / desktopWidth, _viewportHeight / desktopHeight) * LayoutPadding;
        var offsetX = (_viewportWidth - (desktopWidth * scale)) / 2d;
        var offsetY = (_viewportHeight - (desktopHeight * scale)) / 2d;

        foreach (var monitor in EnabledMonitors)
        {
            var bounds = monitor.Model.Bounds!.Value;

            monitor.PreviewX = ((bounds.X - left) * scale) + offsetX + TileInset;
            monitor.PreviewY = ((bounds.Y - top) * scale) + offsetY + TileInset;
            monitor.PreviewWidth = Math.Max(1d, (bounds.Width * scale) - (TileInset * 2d));
            monitor.PreviewHeight = Math.Max(1d, (bounds.Height * scale) - (TileInset * 2d));
        }

        VirtualDesktopText = $"{desktopWidth:0} × {desktopHeight:0} at ({left}, {top})";
    }

    private void UpdateSummary()
    {
        var total = Monitors.Count;
        var enabled = Monitors.Count(m => m.IsEnabled);

        SummaryText = total switch
        {
            0 => "No displays detected",
            1 => "1 display connected",
            _ => $"{total} displays connected · {enabled} enabled",
        };
    }

    /// <summary>
    /// The two lists (layout canvas and monitor cards) select independently, so selection is
    /// funnelled through here. Only the transition to selected is acted on — the deselect that
    /// WPF raises first would otherwise clear the selection that is about to be made.
    /// </summary>
    private void OnMonitorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not MonitorViewModel monitor)
        {
            return;
        }

        if (e.PropertyName == nameof(MonitorViewModel.IsSelected) && monitor.IsSelected)
        {
            SelectedMonitor = monitor;
        }
        else if (e.PropertyName == nameof(MonitorViewModel.HasPendingChanges)
                 && ReferenceEquals(monitor, _selectedMonitor))
        {
            RaiseChangeCommandsCanExecute();
        }
    }
}
