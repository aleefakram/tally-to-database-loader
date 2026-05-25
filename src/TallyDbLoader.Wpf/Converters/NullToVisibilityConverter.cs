using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TallyDbLoader.Wpf.Converters
{
    public class NullToVisibilityConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            bool invert = parameter?.ToString() == "invert";
            bool isNull = value == null;
            if (invert)
                return isNull ? Visibility.Visible : Visibility.Collapsed;
            return isNull ? Visibility.Collapsed : Visibility.Visible;
        }
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }
}
