using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ZeppieSays.UI;

/// <summary>
/// Visible when the bound text is empty: for "nothing here yet" hints. With ConverterParameter="Not" it's the other
/// way round, for a box that should only show once there is text in it.
/// </summary>
public sealed class EmptyToVisibleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrEmpty(value as string) != (parameter as string == "Not") ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
