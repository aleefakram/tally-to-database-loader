using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using TallyDbLoader.Core.Tally;
using Xunit;
using Dapper;

namespace TallyDbLoader.Tests
{
    public class CompanyInfoFetcherTests : IDisposable
    {
        private readonly SqliteConnection _conn;

        public CompanyInfoFetcherTests()
        {
            _conn = new SqliteConnection("Data Source=:memory:");
            _conn.Open();
            _conn.Execute("CREATE TABLE config (name VARCHAR(64) PRIMARY KEY, value VARCHAR(1024))");
        }

        public void Dispose()
        {
            _conn.Dispose();
        }

        private class FakeTallyClient : ITallyClient
        {
            public TallyCompanyInfo? StubbedInfo { get; set; }

            public Task<string> PostXMLAsync(string xmlRequest) => throw new NotImplementedException();
            public Task<string> FetchLedgersXmlAsync(string companyName) => throw new NotImplementedException();
            public Task<List<TallyCompanyInfo>> FetchActiveCompaniesDetailedAsync() => throw new NotImplementedException();
            public Task<List<string>> FetchActiveCompaniesAsync() => throw new NotImplementedException();

            public Task<TallyCompanyInfo?> FetchCompanyInfoAsync(string companyName)
            {
                return Task.FromResult(StubbedInfo);
            }
        }

        [Fact]
        public async Task FetchAndPersist_Success_WritesToConfigTable()
        {
            var fakeInfo = new TallyCompanyInfo
            {
                Name = "Acme Test Ltd",
                Guid = "guid-999",
                BooksFrom = new DateTime(2026, 4, 1),
                BooksTo = new DateTime(2026, 5, 25),
                AltMstId = 1500,
                AltVchId = 3200
            };

            var fakeClient = new FakeTallyClient { StubbedInfo = fakeInfo };
            var fetcher = new CompanyInfoFetcher(fakeClient);

            // Execute
            var info = await fetcher.FetchAndPersist("Acme Test Ltd", _conn);

            // Assert returned object
            Assert.NotNull(info);
            Assert.Equal("Acme Test Ltd", info.Name);

            // Assert written to DB
            var companyName = await _conn.QueryFirstOrDefaultAsync<string>("SELECT value FROM config WHERE name = 'Company Name'");
            var periodFrom = await _conn.QueryFirstOrDefaultAsync<string>("SELECT value FROM config WHERE name = 'Period From'");
            var periodTo = await _conn.QueryFirstOrDefaultAsync<string>("SELECT value FROM config WHERE name = 'Period To'");
            var altMaster = await _conn.QueryFirstOrDefaultAsync<string>("SELECT value FROM config WHERE name = 'Last AlterID Master'");
            var altVoucher = await _conn.QueryFirstOrDefaultAsync<string>("SELECT value FROM config WHERE name = 'Last AlterID Transaction'");

            Assert.Equal("Acme Test Ltd", companyName);
            Assert.Equal("2026-04-01", periodFrom);
            Assert.Equal("2026-05-25", periodTo);
            Assert.Equal("1500", altMaster);
            Assert.Equal("3200", altVoucher);
        }

        [Fact]
        public async Task FetchAndPersist_OfflineOrClosed_ThrowsException()
        {
            var fakeClient = new FakeTallyClient { StubbedInfo = null };
            var fetcher = new CompanyInfoFetcher(fakeClient);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fetcher.FetchAndPersist("Acme Test Ltd", _conn));
        }
    }
}
