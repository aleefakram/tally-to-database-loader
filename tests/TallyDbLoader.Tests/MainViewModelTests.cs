using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using TallyDbLoader.Core.Data;
using TallyDbLoader.Core.Tally;
using TallyDbLoader.Wpf;
using Xunit;

namespace TallyDbLoader.Tests
{
    public class MainViewModelTests
    {
        private class MockHttpMessageHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _sender;

            public MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> sender)
            {
                _sender = sender;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
            {
                return Task.FromResult(_sender(request));
            }
        }

        [Fact]
        public async Task Test_DetectActiveCompanies_SingleCompany_SelectsAutomatically()
        {
            string dbPath = "vm_test_single.db";
            if (File.Exists(dbPath)) File.Delete(dbPath);
            DatabaseHelper.InitializeDatabase(dbPath);

            var vm = new MainViewModel(dbPath);
            vm.TallyServer = "localhost";
            vm.TallyPort = 9000;

            var xmlResponse = @"<ENVELOPE>
  <BODY>
    <DATA>
      <ROW>
        <NAME>Single Company Ltd</NAME>
        <ISGROUP>No</ISGROUP>
      </ROW>
    </DATA>
  </BODY>
</ENVELOPE>";

            var mockHandler = new MockHttpMessageHandler((req) => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(xmlResponse, System.Text.Encoding.UTF8, "text/xml")
            });
            var httpClient = new HttpClient(mockHandler);

            vm.TallyClientFactory = (server, port) => new TallyClient(httpClient, server, port);

            string? messageShown = null;
            vm.MessageBoxShowHandler = (msg, caption, btn, icon) =>
            {
                messageShown = msg;
            };

            await vm.DetectActiveCompaniesAsync();

            Assert.Equal("Single Company Ltd", vm.JobCompany);
            Assert.Contains("Selected Company: Single Company Ltd", messageShown);

            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }

        [Fact]
        public async Task Test_DetectActiveCompanies_MultipleCompanies_UserSelectsOne()
        {
            string dbPath = "vm_test_multi.db";
            if (File.Exists(dbPath)) File.Delete(dbPath);
            DatabaseHelper.InitializeDatabase(dbPath);

            var vm = new MainViewModel(dbPath);
            vm.TallyServer = "localhost";
            vm.TallyPort = 9000;

            var xmlResponse = @"<ENVELOPE>
  <BODY>
    <DATA>
      <ROW>
        <NAME>Company A</NAME>
        <ISGROUP>No</ISGROUP>
      </ROW>
      <ROW>
        <NAME>Group Company B</NAME>
        <ISGROUP>Yes</ISGROUP>
      </ROW>
    </DATA>
  </BODY>
</ENVELOPE>";

            var mockHandler = new MockHttpMessageHandler((req) => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(xmlResponse, System.Text.Encoding.UTF8, "text/xml")
            });
            var httpClient = new HttpClient(mockHandler);

            vm.TallyClientFactory = (server, port) => new TallyClient(httpClient, server, port);

            vm.CompanySelector = (companies) =>
            {
                Assert.Equal(2, companies.Count);
                return companies[1]; // Select Group Company B
            };

            string? messageShown = null;
            vm.MessageBoxShowHandler = (msg, caption, btn, icon) =>
            {
                messageShown = msg;
            };

            await vm.DetectActiveCompaniesAsync();

            Assert.Equal("Group Company B", vm.JobCompany);
            Assert.Contains("Selected Company: Group Company B", messageShown);

            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }

        [Fact]
        public async Task Test_DetectActiveCompanies_MultipleCompanies_UserCancels()
        {
            string dbPath = "vm_test_cancel.db";
            if (File.Exists(dbPath)) File.Delete(dbPath);
            DatabaseHelper.InitializeDatabase(dbPath);

            var vm = new MainViewModel(dbPath);
            vm.TallyServer = "localhost";
            vm.TallyPort = 9000;
            vm.JobCompany = "Initial Company";

            var xmlResponse = @"<ENVELOPE>
  <BODY>
    <DATA>
      <ROW>
        <NAME>Company A</NAME>
        <ISGROUP>No</ISGROUP>
      </ROW>
      <ROW>
        <NAME>Group Company B</NAME>
        <ISGROUP>Yes</ISGROUP>
      </ROW>
    </DATA>
  </BODY>
</ENVELOPE>";

            var mockHandler = new MockHttpMessageHandler((req) => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(xmlResponse, System.Text.Encoding.UTF8, "text/xml")
            });
            var httpClient = new HttpClient(mockHandler);

            vm.TallyClientFactory = (server, port) => new TallyClient(httpClient, server, port);

            vm.CompanySelector = (companies) => null; // Cancel selection

            bool messageBoxCalled = false;
            vm.MessageBoxShowHandler = (msg, caption, btn, icon) =>
            {
                messageBoxCalled = true;
            };

            await vm.DetectActiveCompaniesAsync();

            Assert.Equal("Initial Company", vm.JobCompany);
            Assert.False(messageBoxCalled);

            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }
}
