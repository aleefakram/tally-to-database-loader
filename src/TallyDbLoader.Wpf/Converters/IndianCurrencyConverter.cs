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
            if (value == null) return "0.00";
            if (decimal.TryParse(value.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var amount))
            {
                return amount.ToString("N2", IndianCulture);
            }
            return value.ToString() ?? "0.00";
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
