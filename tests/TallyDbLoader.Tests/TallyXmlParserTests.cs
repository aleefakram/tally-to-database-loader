using Xunit;
using TallyDbLoader.Core.Tally;

namespace TallyDbLoader.Tests
{
    public class TallyXmlParserTests
    {
        [Fact]
        public void Test_ParseLedgers_ValidXml()
        {
            var xml = @"<ENVELOPE>
                <BODY>
                    <DATA>
                        <COLLECTION>
                            <LEDGER NAME=""Cash"">
                                <GUID>cash-guid-123</GUID>
                                <PARENT>Cash-in-hand</PARENT>
                                <OPENINGBALANCE>-500.50</OPENINGBALANCE>
                                <CLOSINGBALANCE>-1500.00</CLOSINGBALANCE>
                            </LEDGER>
                        </COLLECTION>
                    </DATA>
                </BODY>
            </ENVELOPE>";

            var ledgers = TallyXmlParser.ParseLedgers(xml);

            Assert.Single(ledgers);
            Assert.Equal("cash-guid-123", ledgers[0].Guid);
            Assert.Equal("Cash", ledgers[0].Name);
            Assert.Equal("Cash-in-hand", ledgers[0].Parent);
            Assert.Equal(-500.50m, ledgers[0].OpeningBalance);
            Assert.Equal(-1500.00m, ledgers[0].ClosingBalance);
        }
    }
}
