using System;
using System.Globalization;
using System.Windows.Data;

namespace TallyDbLoader.Wpf.Converters
{
    public class NumberConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value == null) return "0";
            if (long.TryParse(value.ToString(), out long val))
            {
                return val.ToString("N0", culture);
            }
            return value.ToString() ?? "0";
        }
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }
}
