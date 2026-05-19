using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TallyDbLoader.Core.Tally;
using Xunit;

namespace TallyDbLoader.Tests
{
    public class TallyClientTests
    {
        private class MockHttpMessageHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

            public MockHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
            {
                _handler = handler;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return _handler(request);
            }
        }

        [Fact]
        public void Test_Unicode_Content_Encoding()
        {
            var xml = "<ENVELOPE><HEADER><VERSION>1</VERSION></HEADER></ENVELOPE>";
            var content = TallyClient.CreateTallyContent(xml);
            
            Assert.Equal("text/xml", content.Headers.ContentType.MediaType);
            Assert.Equal("utf-16", content.Headers.ContentType.CharSet);
        }

        [Fact]
        public async Task Test_FetchActiveCompaniesDetailedAsync_Success()
        {
            var mockXmlResponse = @"<ENVELOPE>
  <BODY>
    <DATA>
      <ROW>
        <NAME>Yaghma Kabab Kaloor 2024-25</NAME>
        <ISGROUP>No</ISGROUP>
      </ROW>
      <ROW>
        <NAME>Consolidated Group</NAME>
        <ISGROUP>Yes</ISGROUP>
      </ROW>
    </DATA>
  </BODY>
</ENVELOPE>";

            var mockHandler = new MockHttpMessageHandler(async (req) =>
            {
                var content = await req.Content.ReadAsStringAsync();
                Assert.Contains("<TYPE>Company</TYPE>", content);
                Assert.Contains("$IsGroupCompany", content);

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(mockXmlResponse, Encoding.UTF8, "text/xml")
                };
            });

            var httpClient = new HttpClient(mockHandler);
            var client = new TallyClient(httpClient, "localhost", 9000);

            var companies = await client.FetchActiveCompaniesDetailedAsync();

            Assert.NotNull(companies);
            Assert.Equal(2, companies.Count);
            
            Assert.Equal("Yaghma Kabab Kaloor 2024-25", companies[0].Name);
            Assert.False(companies[0].IsGroup);

            Assert.Equal("Consolidated Group", companies[1].Name);
            Assert.True(companies[1].IsGroup);
        }

        [Fact]
        public async Task Test_FetchActiveCompaniesDetailedAsync_FallbackParsing()
        {
            // Test flat format fallback
            var mockXmlResponse = @"<ENVELOPE>
  <BODY>
    <COMPANYNAME>Flat Company 1</COMPANYNAME>
    <COMPANY NAME=""Flat Company 2""></COMPANY>
  </BODY>
</ENVELOPE>";

            var mockHandler = new MockHttpMessageHandler((req) =>
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(mockXmlResponse, Encoding.UTF8, "text/xml")
                });
            });

            var httpClient = new HttpClient(mockHandler);
            var client = new TallyClient(httpClient, "localhost", 9000);

            var companies = await client.FetchActiveCompaniesDetailedAsync();

            Assert.NotNull(companies);
            Assert.Equal(2, companies.Count);
            
            Assert.Equal("Flat Company 1", companies[0].Name);
            Assert.False(companies[0].IsGroup);

            Assert.Equal("Flat Company 2", companies[1].Name);
            Assert.False(companies[1].IsGroup);
        }

        [Fact]
        public async Task Test_FetchLedgersXmlAsync_SendsCorrectRequest()
        {
            var targetCompanyName = "Yaghma Kabab Kaloor 2024-25";
            var mockXmlResponse = "<ENVELOPE><BODY>Success</BODY></ENVELOPE>";

            var mockHandler = new MockHttpMessageHandler(async (req) =>
            {
                var content = await req.Content.ReadAsStringAsync();
                Assert.Contains($"<SVCURRENTCOMPANY>{targetCompanyName}</SVCURRENTCOMPANY>", content);
                Assert.Contains("<ID>Ledger</ID>", content);

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(mockXmlResponse, Encoding.UTF8, "text/xml")
                };
            });

            var httpClient = new HttpClient(mockHandler);
            var client = new TallyClient(httpClient, "localhost", 9000);

            var response = await client.FetchLedgersXmlAsync(targetCompanyName);

            Assert.Equal(mockXmlResponse, response);
        }
    }
}
