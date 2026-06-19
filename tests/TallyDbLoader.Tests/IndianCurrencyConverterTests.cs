using System.Globalization;
using TallyDbLoader.Wpf.Converters;
using Xunit;

namespace TallyDbLoader.Tests
{
    public class IndianCurrencyConverterTests
    {
        [Fact]
        public void Convert_Decimal_UsesIndianGroupingWithTwoDecimals()
        {
            var converter = new IndianCurrencyConverter();

            var text = converter.Convert(6504742.51m, typeof(string), null, CultureInfo.InvariantCulture);

            Assert.Equal("65,04,742.51", text);
        }
    }
}
