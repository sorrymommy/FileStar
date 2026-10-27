using System.Globalization;
using System.Windows.Data;

namespace FileStar.Views.Converters;

/// <summary>Turns ItemsControl.AlternationIndex into a 1-based row number.</summary>
public sealed class RowNumberConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int index ? (index + 1).ToString() : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
