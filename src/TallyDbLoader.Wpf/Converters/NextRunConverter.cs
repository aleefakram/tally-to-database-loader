using System;
using System.Globalization;
using System.Windows.Data;

namespace TallyDbLoader.Wpf.Converters
{
    public class NextRunConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length >= 3 && values[0] is DateTime lastRun && values[1] is int interval && values[2] is int enabled)
            {
                if (enabled == 0) return "Disabled";
                var next = lastRun.AddMinutes(interval);
                if (next < DateTime.Now) return "Pending";
                return next.ToString("HH:mm:ss");
            }
            return "Pending";
        }
        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotImplementedException();
    }
}
