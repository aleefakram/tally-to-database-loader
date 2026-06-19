using System;
using System.Globalization;
using System.Windows.Data;

namespace TallyDbLoader.Wpf.Converters
{
    public class IndianCurrencyConverter : IValueConverter
    {
        private static readonly CultureInfo IndianCulture = new CultureInfo("en-IN");

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is decimal amount)
            {
                return amount.ToString("N2", IndianCulture);
            }
            if (value != null && decimal.TryParse(value.ToString(), NumberStyles.Any, culture, out var parsedAmount))
            {
                return parsedAmount.ToString("N2", IndianCulture);
            }
            return value?.ToString() ?? "0.00";
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
