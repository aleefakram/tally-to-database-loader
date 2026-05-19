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

        [Fact]
        public void Test_GenerateXml_WithAlterIdFilter_AppendsFormula()
        {
            var tableConfig = new TableConfig
            {
                Name = "mst_ledger",
                Collection = "Ledger",
                Fields = new List<FieldConfig>
                {
                    new FieldConfig { Name = "guid", Field = "Guid", Type = "text" }
                },
                Filters = new List<string> { "$AlterID > 500" }
            };

            var xml = DynamicTdlXmlGenerator.GenerateXml(tableConfig, "TestCompany", "20260401", "20260519");

            Assert.Contains("<FILTER>Fltr01</FILTER>", xml);
            Assert.Contains("<SYSTEM TYPE=\"Formulae\" NAME=\"Fltr01\">$AlterID > 500</SYSTEM>", xml);
        }

        [Fact]
        public void Test_GenerateXml_ForDiffStaging_HasGuidAndAlterIdFields()
        {
            var tableConfig = new TableConfig
            {
                Name = "_diff",
                Collection = "Ledger",
                Fields = new List<FieldConfig>
                {
                    new FieldConfig { Name = "guid", Field = "Guid", Type = "text" },
                    new FieldConfig { Name = "alterid", Field = "AlterId", Type = "text" }
                }
            };

            var xml = DynamicTdlXmlGenerator.GenerateXml(tableConfig, "TestCompany", "20260401", "20260519");

            Assert.Contains("<FIELD NAME=\"Fld01\"><SET>$Guid</SET>", xml);
            Assert.Contains("<FIELD NAME=\"Fld02\"><SET>$AlterId</SET>", xml);
        }
    }
}
