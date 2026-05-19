using Xunit;
using TallyDbLoader.Core.Tally;
using System.Collections.Generic;

namespace TallyDbLoader.Tests
{
    public class YamlConfigParserTests
    {
        [Fact]
        public void Test_ParseYaml_ReturnsValidObjectStructure()
        {
            var yaml = @"
master:
  - name: mst_group
    collection: Group
    nature: Primary
    fields:
      - name: guid
        field: Guid
        type: text
      - name: is_revenue
        field: IsRevenue
        type: logical
    filters:
      - filter1
    fetch:
      - fetch1
    cascade_delete:
      - table: trn_accounting
        field: _ledger
";
            var config = YamlConfigParser.Parse(yaml);
            Assert.NotNull(config);
            Assert.Single(config.Master);
            var group = config.Master[0];
            Assert.Equal("mst_group", group.Name);
            Assert.Equal("Group", group.Collection);
            Assert.Equal("Primary", group.Nature);
            Assert.Equal(2, group.Fields.Count);
            Assert.Equal("guid", group.Fields[0].Name);
            Assert.Equal("Guid", group.Fields[0].Field);
            Assert.Equal("text", group.Fields[0].Type);
            Assert.Equal("is_revenue", group.Fields[1].Name);
            Assert.Equal("logical", group.Fields[1].Type);
            Assert.Single(group.Filters!);
            Assert.Single(group.Fetch!);
            Assert.Single(group.CascadeDelete!);
            Assert.Equal("trn_accounting", group.CascadeDelete[0].Table);
            Assert.Equal("_ledger", group.CascadeDelete[0].Field);
        }
    }
}
