using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace EverLinkHost;

/// <summary>Converts a bool to Visibility.Visible/Collapsed. Available for any future
/// conditional-visibility need in the UI (e.g. an error indicator).</summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
