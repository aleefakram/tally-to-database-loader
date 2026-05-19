using Xunit;
using TallyDbLoader.Core.Tally;
using System.Text;

namespace TallyDbLoader.Tests
{
    public class TallyClientTests
    {
        [Fact]
        public void Test_Unicode_Content_Encoding()
        {
            var xml = "<ENVELOPE><HEADER><VERSION>1</VERSION></HEADER></ENVELOPE>";
            var content = TallyClient.CreateTallyContent(xml);
            
            Assert.Equal("text/xml", content.Headers.ContentType.MediaType);
            Assert.Equal("utf-16", content.Headers.ContentType.CharSet);
        }
    }
}
