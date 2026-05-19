using Xunit;
using TallyDbLoader.Core.Tally;
using System.Collections.Generic;

namespace TallyDbLoader.Tests
{
    public class DynamicTdlXmlGeneratorTests
    {
        [Fact]
        public void Test_GenerateXML_ProducesValidTDLEnvelope()
        {
            var tableConfig = new TableConfig
            {
                Name = "mst_ledger",
                Collection = "Ledger.AllBillAllocations",
                Fields = new List<FieldConfig>
                {
                    new FieldConfig { Name = "name", Field = "Name", Type = "text" },
                    new FieldConfig { Name = "opening_balance", Field = "OpeningBalance", Type = "amount" },
                    new FieldConfig { Name = "is_revenue", Field = "IsRevenue", Type = "logical" }
                },
                Filters = new List<string> { "FilterName1" },
                Fetch = new List<string> { "FetchProperty1" }
            };

            var xml = DynamicTdlXmlGenerator.GenerateXml(tableConfig, "MyCompany", "20260401", "20260519");
            
            Assert.Contains("<SVCURRENTCOMPANY>MyCompany</SVCURRENTCOMPANY>", xml);
            Assert.Contains("<SVFROMDATE>20260401</SVFROMDATE>", xml);
            Assert.Contains("<SVTODATE>20260519</SVTODATE>", xml);
            
            // Explode hierarchy checks
            Assert.Contains("<PART NAME=\"MyPart01\"><LINES>MyLine01</LINES><REPEAT>MyLine01 : MyCollection</REPEAT>", xml);
            Assert.Contains("<PART NAME=\"MyPart02\"><LINES>MyLine02</LINES><REPEAT>MyLine02 : AllBillAllocations</REPEAT>", xml);
            
            // Formula check
            Assert.Contains("<FIELD NAME=\"Fld01\"><SET>$Name</SET>", xml);
            Assert.Contains("<FIELD NAME=\"Fld02\"><SET>$$StringFindAndReplace:(if $$IsDebit:$OpeningBalance then -$$NumValue:$OpeningBalance else $$NumValue:$OpeningBalance):\"(-)\":\"-\"</SET>", xml);
            Assert.Contains("<FIELD NAME=\"Fld03\"><SET>if $IsRevenue then 1 else 0</SET>", xml);
        }
    }
}
