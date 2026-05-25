using Xunit;
using TallyDbLoader.Core.Tally;

namespace TallyDbLoader.Tests
{
    public class WatermarkXmlBuilderTests
    {
        [Fact]
        public void Build_NoCompanyName_KeepsSvCurrentCompany()
        {
            var xml = WatermarkXmlBuilder.Build(null);
            Assert.Contains("##SVCurrentCompany", xml);
            Assert.Contains("$AltMstId", xml);
            Assert.Contains("$AltVchId", xml);
            Assert.Contains("ASCII (Comma Delimited)", xml);
        }

        [Fact]
        public void Build_WithCompanyName_SubstitutesAndEscapes()
        {
            var xml = WatermarkXmlBuilder.Build("Acme & Co");
            Assert.DoesNotContain("##SVCurrentCompany", xml);
            Assert.Contains("\"Acme &amp; Co\"", xml);
        }
    }
}
