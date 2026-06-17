using Xunit;
using TallyDbLoader.Core.Tally;
using System.Collections.Generic;
using System;
using System.Data;

namespace TallyDbLoader.Tests
{
    public class DynamicXmlParserTests
    {
        [Fact]
        public void Test_ParseXml_ReturnsValidDataTable()
        {
            var tableConfig = new TableConfig
            {
                Name = "mst_ledger",
                Collection = "Ledger",
                Fields = new List<FieldConfig>
                {
                    new FieldConfig { Name = "guid", Field = "Guid", Type = "text" },
                    new FieldConfig { Name = "name", Field = "Name", Type = "text" },
                    new FieldConfig { Name = "alterid", Field = "AlterId", Type = "number" },
                    new FieldConfig { Name = "opening_balance", Field = "OpeningBalance", Type = "amount" },
                    new FieldConfig { Name = "is_revenue", Field = "IsRevenue", Type = "logical" },
                    new FieldConfig { Name = "created_date", Field = "CreatedDate", Type = "date" }
                }
            };

            var xml = @"<ENVELOPE>
  <BODY>
    <DATA>
      <ROW>
        <F01>guid-12345</F01>
        <F02>Capital Account</F02>
        <F03>1001</F03>
        <F04>-150000.50</F04>
        <F05>1</F05>
        <F06>2026-04-01</F06>
      </ROW>
      <ROW>
        <F01>guid-67890</F01>
        <F02>Sales Account</F02>
        <F03>1002</F03>
        <F04>250000.75</F04>
        <F05>0</F05>
        <F06>ñ</F06>
      </ROW>
    </DATA>
  </BODY>
</ENVELOPE>";

            var dataTable = DynamicXmlParser.ParseXml(xml, tableConfig);
            Assert.NotNull(dataTable);
            Assert.Equal(2, dataTable.Rows.Count);
            
            // Check Row 1
            var row1 = dataTable.Rows[0];
            Assert.Equal("guid-12345", row1["guid"]);
            Assert.Equal("Capital Account", row1["name"]);
            Assert.Equal(1001m, row1["alterid"]);
            Assert.Equal(-150000.50m, row1["opening_balance"]);
            Assert.Equal(true, row1["is_revenue"]);
            Assert.Equal(new DateTime(2026, 4, 1), row1["created_date"]);

            // Check Row 2
            var row2 = dataTable.Rows[1];
            Assert.Equal("guid-67890", row2["guid"]);
            Assert.Equal("Sales Account", row2["name"]);
            Assert.Equal(1002m, row2["alterid"]);
            Assert.Equal(250000.75m, row2["opening_balance"]);
            Assert.Equal(false, row2["is_revenue"]);
            Assert.Equal(DBNull.Value, row2["created_date"]);
        }

        [Fact]
        public void ParseXml_RemovesInvalidXmlControlCharacters()
        {
            var tableConfig = new TableConfig
            {
                Name = "mst_ledger",
                Collection = "Ledger",
                Fields = new List<FieldConfig>
                {
                    new FieldConfig { Name = "guid", Field = "Guid", Type = "text" },
                    new FieldConfig { Name = "name", Field = "Name", Type = "text" }
                }
            };

            var xml = "<ENVELOPE><BODY><DATA><ROW><F01>guid-123</F01><F02>Cash\u0004Account</F02></ROW></DATA></BODY></ENVELOPE>";

            var dataTable = DynamicXmlParser.ParseXml(xml, tableConfig);

            Assert.Single(dataTable.Rows);
            Assert.Equal("CashAccount", dataTable.Rows[0]["name"]);
        }

        [Theory]
        [InlineData("Cash&#x04;Account", "CashAccount")]
        [InlineData("Cash&#x4;Account", "CashAccount")]
        [InlineData("Cash&#4;Account", "CashAccount")]
        [InlineData("Cash&#04;Account", "CashAccount")]
        [InlineData("Cash&#x1F;Account", "CashAccount")]
        [InlineData("Cash&#31;Account", "CashAccount")]
        [InlineData("Cash&#x20;Account", "Cash&#x20;Account")] // Valid character reference, keeps intact
        [InlineData("Cash&#32;Account", "Cash&#32;Account")] // Valid character reference, keeps intact
        public void XmlSanitizer_RemovesInvalidXmlCharacterEntities(string input, string expected)
        {
            var result = XmlSanitizer.Sanitize(input);
            Assert.Equal(expected, result);
        }

        [Fact]
        public void ParseXml_HandlesInvalidXmlEntitiesWithoutThrowing()
        {
            var tableConfig = new TableConfig
            {
                Name = "mst_ledger",
                Collection = "Ledger",
                Fields = new List<FieldConfig>
                {
                    new FieldConfig { Name = "guid", Field = "Guid", Type = "text" },
                    new FieldConfig { Name = "name", Field = "Name", Type = "text" }
                }
            };

            var xml = "<ENVELOPE><BODY><DATA><ROW><F01>guid-123</F01><F02>Cash&#x04;Account</F02></ROW></DATA></BODY></ENVELOPE>";

            var dataTable = DynamicXmlParser.ParseXml(xml, tableConfig);

            Assert.Single(dataTable.Rows);
            Assert.Equal("CashAccount", dataTable.Rows[0]["name"]);
        }
    }
}
