using System;
using System.Globalization;
using System.Windows.Data;

namespace TallyDbLoader.Wpf.Converters
{
    public class UppercaseConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is string s) return s.ToUpper();
            return value?.ToString()?.ToUpper() ?? string.Empty;
        }
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }
}
