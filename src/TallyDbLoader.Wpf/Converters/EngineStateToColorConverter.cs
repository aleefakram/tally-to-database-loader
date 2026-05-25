using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace TallyDbLoader.Wpf.Converters
{
    public class EngineStateToColorConverter : IValueConverter
    {
        private static readonly SolidColorBrush _runningBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(22, 163, 74));
        private static readonly SolidColorBrush _pausedBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(217, 119, 6));
        private static readonly SolidColorBrush _idleBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(150, 150, 150));

        static EngineStateToColorConverter()
        {
            _runningBrush.Freeze();
            _pausedBrush.Freeze();
            _idleBrush.Freeze();
        }

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is TallyDbLoader.Wpf.EngineState state)
            {
                return state switch
                {
                    TallyDbLoader.Wpf.EngineState.Running => _runningBrush,
                    TallyDbLoader.Wpf.EngineState.Paused => _pausedBrush,
                    _ => _idleBrush
                };
            }
            return _idleBrush;
        }
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }
}
