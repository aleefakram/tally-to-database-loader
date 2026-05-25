using System;
using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TallyDbLoader.Wpf.Converters
{
    public class CountToVisibilityConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            int count = 0;
            if (value is bool b) count = b ? 1 : 0;
            else if (value is int c) count = c;
            else if (value is ICollection coll) count = coll.Count;
            else if (value != null && int.TryParse(value.ToString(), out int parsed)) count = parsed;

            bool invert = parameter?.ToString() == "invert";
            if (invert)
                return count > 0 ? Visibility.Collapsed : Visibility.Visible;
            return count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }
}
