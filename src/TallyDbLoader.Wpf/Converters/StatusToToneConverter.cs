using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace TallyDbLoader.Wpf.Converters
{
    public class StatusToToneConverter : IValueConverter
    {
        private static readonly ConcurrentDictionary<string, SolidColorBrush> _brushCache = new ConcurrentDictionary<string, SolidColorBrush>();

        private static SolidColorBrush GetFrozenBrush(string hexColor)
        {
            return _brushCache.GetOrAdd(hexColor, hex =>
            {
                var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
                brush.Freeze();
                return brush;
            });
        }

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            string status = (value?.ToString() ?? "idle").ToLower();
            if (status == "ok" || status == "success" || status == "healthy" || status == "running")
                return GetFrozenBrush("#16a34a"); // status-ok/running: green
            if (status == "warn" || status == "warning" || status == "paused" || status == "stale")
                return GetFrozenBrush("#d97706"); // status-warn/paused: amber
            if (status == "err" || status == "error" || status == "failed")
                return GetFrozenBrush("#dc2626"); // status-err: red
            return GetFrozenBrush("#888888"); // status-idle: gray
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }
}
