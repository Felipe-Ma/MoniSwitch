using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ScreenShift.ViewModels;

/// <summary>
/// Minimal INotifyPropertyChanged base. Hand-rolled on purpose: the whole app needs about
/// forty lines of MVVM support, which is not worth a package dependency.
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>Assigns and raises the change notification only when the value actually differs.</summary>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
