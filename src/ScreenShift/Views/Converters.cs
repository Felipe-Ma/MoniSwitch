using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ScreenShift.Views;

/// <summary>
/// Bool to Visibility, with an <see cref="Invert"/> switch so a single class covers both
/// "show when true" and "show when false" without a second converter type.
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    /// <summary>Whether the hidden state collapses (default) or merely goes invisible.</summary>
    public bool UseHidden { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is bool b && b;

        if (Invert)
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : UseHidden ? Visibility.Hidden : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
