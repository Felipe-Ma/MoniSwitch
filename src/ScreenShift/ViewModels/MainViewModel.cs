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
    private readonly DisplayProfileService _profileService;
    private readonly HotkeyService _hotkeyService;
    private readonly SettingsService _settingsService;
    private readonly IUserInteraction _interaction;
    private readonly IAppLogger _logger;

    private bool _isApplying;
    private bool _startWithWindows;
    private MonitorViewModel? _selectedMonitor;
    private string _summaryText = string.Empty;
    private string _statusText = "Ready.";
    private string? _errorMessage;
    private bool _isRefreshing;
    private double _viewportWidth;
    private double _viewportHeight;
    private string _virtualDesktopText = "—";

    public MainViewModel(
        IDisplayService displayService,
        DisplayProfileService profileService,
        HotkeyService hotkeyService,
        SettingsService settingsService,
        IUserInteraction interaction,
        IAppLogger logger)
    {
        _displayService = displayService;
        _profileService = profileService;
        _hotkeyService = hotkeyService;
        _settingsService = settingsService;
        _interaction = interaction;
        _logger = logger;

        RefreshCommand = new RelayCommand(Refresh, () => !_isRefreshing && !_isApplying);
        ApplyCommand = new RelayCommand(ApplyChanges, () => !_isApplying && SelectedMonitor?.HasPendingChanges == true);
        ResetCommand = new RelayCommand(ResetChanges, () => !_isApplying && SelectedMonitor?.HasPendingChanges == true);
        SaveProfileCommand = new RelayCommand(SaveCurrentAsProfile, () => !_isApplying);

        _hotkeyService.ProfileHotkeyPressed += ApplyProfileById;

        try
        {
            _startWithWindows = StartupRegistration.IsEnabled();
        }
        catch (Exception ex)
        {
            _logger.Warn($"Could not read the startup registration: {ex.Message}");
        }

        LoadProfiles();
    }

    /// <summary>Every connected monitor, enabled first.</summary>
    public ObservableCollection<MonitorViewModel> Monitors { get; } = [];

    /// <summary>The subset drawn on the layout canvas. Disabled monitors have no rectangle to draw.</summary>
    public ObservableCollection<MonitorViewModel> EnabledMonitors { get; } = [];

    /// <summary>Saved display layouts, in profiles.json order.</summary>
    public ObservableCollection<ProfileViewModel> Profiles { get; } = [];

    public bool HasProfiles => Profiles.Count > 0;

    // --- Settings, written through to settings.json on change --------------

    public bool ShowTrayIcon
    {
        get => _settingsService.Current.ShowTrayIcon;
        set
        {
            if (_settingsService.Current.ShowTrayIcon == value)
            {
                return;
            }

            _settingsService.Current.ShowTrayIcon = value;
            _settingsService.Save();
            OnPropertyChanged();
        }
    }

    public bool CloseToTray
    {
        get => _settingsService.Current.CloseToTray;
        set
        {
            if (_settingsService.Current.CloseToTray == value)
            {
                return;
            }

            _settingsService.Current.CloseToTray = value;
            _settingsService.Save();
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Launch at sign-in, minimized to the tray. Backed by the registry Run key rather than
    /// settings.json — the registry is what Windows actually reads, and Task Manager's Startup
    /// page edits the same entry, so there is no second copy to drift out of step.
    /// </summary>
    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (_startWithWindows == value)
            {
                return;
            }

            try
            {
                if (value)
                {
                    StartupRegistration.Enable();
                }
                else
                {
                    StartupRegistration.Disable();
                }

                _startWithWindows = value;
                _logger.Info(value
                    ? "Registered to start with Windows (minimized to the tray)."
                    : "Removed the start-with-Windows registration.");
            }
            catch (Exception ex)
            {
                _logger.Error("Changing the startup registration failed.", ex);
                _interaction.ShowError("Could not change startup registration", ex.Message);
            }

            // Raised even on failure, so a checkbox that could not take effect snaps back.
            OnPropertyChanged();
        }
    }

    public RelayCommand RefreshCommand { get; }

    public RelayCommand SaveProfileCommand { get; }

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

    /// <summary>Applies the selected monitor's pending changes through the confirmation flow.</summary>
    private void ApplyChanges()
    {
        if (SelectedMonitor?.BuildRequest() is { } request)
        {
            _ = RunApplyAsync([request], request.ToString(), $"Applied at {DateTime.Now:HH:mm:ss}");
        }
    }

    /// <summary>
    /// The pipeline every kind of change goes through: snapshot, apply, let the user confirm before
    /// the deadline, restore if they decline or cannot answer.
    /// </summary>
    private async Task RunApplyAsync(
        IReadOnlyList<MonitorChangeRequest> requests,
        string confirmationSummary,
        string appliedStatus)
    {
        if (_isApplying || requests.Count == 0)
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

            var result = await Task.Run(() => _displayService.Apply(requests));

            if (!result.Succeeded)
            {
                var detail = result.Message ?? "The change was rejected.";
                if (result.RolledBack)
                {
                    detail += "\n\nThe previous display settings were restored.";
                }

                _logger.Warn($"Apply failed: {detail}");
                _interaction.ShowError("Could not apply the change", detail);
                StatusText = "The change was rejected.";
                return;
            }

            await Task.Delay(SettleDelay);

            var keep = _interaction.ConfirmDisplayChange(confirmationSummary, RollbackTimeout);

            if (keep)
            {
                _logger.Info("User kept the new display settings.");
                StatusText = appliedStatus;
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
                _interaction.ShowError(
                    "Revert failed",
                    restored.Message ?? "The previous display configuration could not be fully restored.");
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Applying display changes threw.", ex);
            _interaction.ShowError("Could not apply the change", ex.Message);
            StatusText = "The change failed.";
        }
        finally
        {
            // Clear the flag before refreshing: Refresh deliberately no-ops while applying.
            IsApplying = false;
            Refresh();
        }
    }

    // ------------------------------------------------------------------
    // Profiles
    // ------------------------------------------------------------------

    /// <summary>The window calls this once its handle exists; hotkeys cannot register earlier.</summary>
    public void InitializeHotkeys(IntPtr windowHandle)
    {
        _hotkeyService.Initialize(windowHandle);
        SyncHotkeys();
    }

    /// <summary>Forwarded from the window procedure. True when the message was a hotkey of ours.</summary>
    public bool HandleWindowMessage(int msg, IntPtr wParam) => _hotkeyService.HandleMessage(msg, wParam);

    private void ApplyProfileById(Guid profileId)
    {
        if (Profiles.FirstOrDefault(p => p.Model.Id == profileId) is { } profile)
        {
            ApplyProfile(profile);
        }
    }

    private void SyncHotkeys()
    {
        if (!_hotkeyService.IsInitialized)
        {
            return;
        }

        var failures = _hotkeyService.Sync(_profileService.Profiles.Select(p => (p.Id, p.Name, p.Hotkey)));

        if (failures.Count > 0)
        {
            StatusText = failures[0];
        }
    }

    private void SetHotkeyForProfile(ProfileViewModel profile)
    {
        var result = _interaction.PromptForHotkey(profile.Name, profile.Model.Hotkey);
        if (result.Cancelled)
        {
            return;
        }

        try
        {
            var takenFrom = _profileService.SetHotkey(profile.Model.Id, result.Hotkey);
            LoadProfiles();

            StatusText = result.Hotkey is null
                ? $"Hotkey removed from \"{profile.Name}\"."
                : takenFrom is null
                    ? $"{result.Hotkey} now applies \"{profile.Name}\"."
                    : $"{result.Hotkey} now applies \"{profile.Name}\" (taken from \"{takenFrom}\").";
        }
        catch (Exception ex)
        {
            _logger.Error("Setting a hotkey failed.", ex);
            _interaction.ShowError("Could not set the hotkey", ex.Message);
        }
    }

    private void LoadProfiles()
    {
        Profiles.Clear();

        foreach (var profile in _profileService.Profiles)
        {
            Profiles.Add(new ProfileViewModel(
                profile,
                ApplyProfile,
                RenameProfile,
                UpdateProfile,
                DuplicateProfile,
                DeleteProfile,
                SetHotkeyForProfile,
                () => !_isApplying));
        }

        OnPropertyChanged(nameof(HasProfiles));

        // Wholesale re-sync keeps registrations exactly in step with the list, whatever changed.
        SyncHotkeys();
    }

    private void SaveCurrentAsProfile()
    {
        var name = _interaction.PromptForText("Save profile", "Name for the current display configuration:", "New profile");
        if (name is null)
        {
            return;
        }

        try
        {
            var profile = _profileService.SaveCurrentAs(name);
            LoadProfiles();
            StatusText = $"Saved profile \"{profile.Name}\".";
        }
        catch (Exception ex)
        {
            _logger.Error("Saving a profile failed.", ex);
            _interaction.ShowError("Could not save the profile", ex.Message);
        }
    }

    private void ApplyProfile(ProfileViewModel profile)
    {
        if (_isApplying)
        {
            return;
        }

        ProfilePlan plan;
        try
        {
            plan = _profileService.BuildPlan(profile.Model);
        }
        catch (Exception ex)
        {
            _logger.Error($"Planning profile \"{profile.Name}\" failed.", ex);
            _interaction.ShowError("Could not apply the profile", ex.Message);
            return;
        }

        foreach (var warning in plan.Warnings)
        {
            _logger.Warn($"Profile \"{profile.Name}\": {warning}");
        }

        if (!plan.HasWork)
        {
            // No requests plus warnings means nothing usable matched — very different news from
            // "everything already matches", so the two get different treatment.
            if (plan.Warnings.Count > 0)
            {
                _interaction.ShowError(
                    $"Profile \"{profile.Name}\"",
                    string.Join("\n", plan.Warnings) + "\n\nNothing was changed.");
            }
            else
            {
                StatusText = $"Profile \"{profile.Name}\" is already active.";
            }

            return;
        }

        _ = RunApplyAsync(plan.Requests, plan.Summary(), $"Applied profile \"{profile.Name}\".");
    }

    private void RenameProfile(ProfileViewModel profile)
    {
        var name = _interaction.PromptForText("Rename profile", $"New name for \"{profile.Name}\":", profile.Name);
        if (name is null)
        {
            return;
        }

        try
        {
            _profileService.Rename(profile.Model.Id, name);
            LoadProfiles();
        }
        catch (Exception ex)
        {
            _logger.Error("Renaming a profile failed.", ex);
            _interaction.ShowError("Could not rename the profile", ex.Message);
        }
    }

    private void UpdateProfile(ProfileViewModel profile)
    {
        if (!_interaction.ConfirmAction(
                "Update profile",
                $"Overwrite \"{profile.Name}\" with the current display configuration?"))
        {
            return;
        }

        try
        {
            _profileService.UpdateFromCurrent(profile.Model.Id);
            LoadProfiles();
            StatusText = $"Updated \"{profile.Name}\" from the current configuration.";
        }
        catch (Exception ex)
        {
            _logger.Error("Updating a profile failed.", ex);
            _interaction.ShowError("Could not update the profile", ex.Message);
        }
    }

    private void DuplicateProfile(ProfileViewModel profile)
    {
        try
        {
            var copy = _profileService.Duplicate(profile.Model.Id);
            LoadProfiles();
            StatusText = $"Duplicated to \"{copy.Name}\".";
        }
        catch (Exception ex)
        {
            _logger.Error("Duplicating a profile failed.", ex);
            _interaction.ShowError("Could not duplicate the profile", ex.Message);
        }
    }

    private void DeleteProfile(ProfileViewModel profile)
    {
        if (!_interaction.ConfirmAction("Delete profile", $"Delete \"{profile.Name}\"? This cannot be undone."))
        {
            return;
        }

        try
        {
            _profileService.Delete(profile.Model.Id);
            LoadProfiles();
            StatusText = $"Deleted \"{profile.Name}\".";
        }
        catch (Exception ex)
        {
            _logger.Error("Deleting a profile failed.", ex);
            _interaction.ShowError("Could not delete the profile", ex.Message);
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
        SaveProfileCommand.RaiseCanExecuteChanged();

        foreach (var profile in Profiles)
        {
            profile.RaiseCanExecuteChanged();
        }
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
