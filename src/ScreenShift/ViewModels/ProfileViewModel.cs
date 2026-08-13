using ScreenShift.Models;

namespace ScreenShift.ViewModels;

/// <summary>
/// Presentation wrapper around one saved profile. The commands are supplied by
/// <see cref="MainViewModel"/> as delegates, so this class never has to know about the services —
/// it is a card in a list, nothing more.
/// </summary>
public sealed class ProfileViewModel : ObservableObject
{
    public ProfileViewModel(
        DisplayProfile model,
        Action<ProfileViewModel> apply,
        Action<ProfileViewModel> rename,
        Action<ProfileViewModel> update,
        Action<ProfileViewModel> duplicate,
        Action<ProfileViewModel> delete,
        Action<ProfileViewModel> setHotkey,
        Func<bool> canAct)
    {
        Model = model;

        ApplyCommand = new RelayCommand(() => apply(this), canAct);
        RenameCommand = new RelayCommand(() => rename(this), canAct);
        UpdateCommand = new RelayCommand(() => update(this), canAct);
        DuplicateCommand = new RelayCommand(() => duplicate(this), canAct);
        DeleteCommand = new RelayCommand(() => delete(this), canAct);
        SetHotkeyCommand = new RelayCommand(() => setHotkey(this), canAct);
    }

    public DisplayProfile Model { get; }

    public RelayCommand ApplyCommand { get; }

    public RelayCommand RenameCommand { get; }

    public RelayCommand UpdateCommand { get; }

    public RelayCommand DuplicateCommand { get; }

    public RelayCommand DeleteCommand { get; }

    public RelayCommand SetHotkeyCommand { get; }

    public string Name => Model.Name;

    /// <summary>The gesture when one is set, otherwise an invitation — doubles as the button label.</summary>
    public string HotkeyButtonText => string.IsNullOrEmpty(Model.Hotkey) ? "Hotkey…" : Model.Hotkey!;

    public string SummaryText
    {
        get
        {
            var total = Model.Monitors.Count;
            var on = Model.Monitors.Count(m => m.Enabled);
            var primary = Model.Monitors.FirstOrDefault(m => m is { Enabled: true, Primary: true })?.FriendlyName;

            var counts = $"{on}/{total} displays on";
            var summary = primary is null ? counts : $"{counts} · primary {primary}";
            return $"{summary} · updated {Model.UpdatedAt:d MMM HH:mm}";
        }
    }

    /// <summary>Per-monitor breakdown, shown as the card's tooltip.</summary>
    public string DetailToolTip => string.Join("\n", Model.Monitors);

    public void RaiseCanExecuteChanged()
    {
        ApplyCommand.RaiseCanExecuteChanged();
        RenameCommand.RaiseCanExecuteChanged();
        UpdateCommand.RaiseCanExecuteChanged();
        DuplicateCommand.RaiseCanExecuteChanged();
        DeleteCommand.RaiseCanExecuteChanged();
        SetHotkeyCommand.RaiseCanExecuteChanged();
    }
}
