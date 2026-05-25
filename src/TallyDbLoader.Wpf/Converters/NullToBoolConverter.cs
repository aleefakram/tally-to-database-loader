using System;
using System.Globalization;
using System.Windows.Data;

namespace TallyDbLoader.Wpf.Converters
{
    public class NullToBoolConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            bool invert = parameter?.ToString()?.ToLower() == "invert";
            return invert ? value == null : value != null;
        }
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }
}
