using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ZeppieSays.UI;

/// <summary>Visible when the bound text is empty: for "nothing here yet" hints.</summary>
public sealed class EmptyToVisibleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrEmpty(value as string) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
