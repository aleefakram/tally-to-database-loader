using System;
using System.Globalization;
using System.Windows.Data;

namespace TallyDbLoader.Wpf.Converters
{
    public class RelativeTimeConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is DateTime dt)
            {
                var span = DateTime.Now - dt;
                if (span.TotalSeconds < 60) return "just now";
                if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
                if (span.TotalHours < 24) return $"{(int)span.TotalHours}h ago";
                if (span.TotalDays < 7) return $"{(int)span.TotalDays}d ago";
                return dt.ToString("yyyy-MM-dd HH:mm");
            }
            return "never";
        }
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }
}
