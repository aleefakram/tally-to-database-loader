using System;
using System.Data.Common;
using System.Threading.Tasks;
using Dapper;

namespace TallyDbLoader.Core.Tally
{
    public class CompanyInfoFetcher
    {
        private readonly ITallyClient _client;

        public CompanyInfoFetcher(ITallyClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
        }

        public async Task<TallyCompanyInfo> FetchAndPersist(string companyName, DbConnection targetConn)
        {
            var info = await _client.FetchCompanyInfoAsync(companyName);
            if (info == null)
            {
                throw new InvalidOperationException("Target company is closed or offline in Tally");
            }

            // Clear the config table
            await targetConn.ExecuteAsync("DELETE FROM config");

            // Build metadata list to insert
            var fromStr = info.BooksFrom?.ToString("yyyy-MM-dd") ?? "2000-01-01";
            var toStr = info.BooksTo?.ToString("yyyy-MM-dd") ?? DateTime.Today.ToString("yyyy-MM-dd");

            var metadata = new[]
            {
                new { Name = "Update Timestamp", Value = DateTime.Now.ToString("g") },
                new { Name = "Company Name", Value = info.Name },
                new { Name = "Period From", Value = fromStr },
                new { Name = "Period To", Value = toStr },
                new { Name = "Last AlterID Master", Value = info.AltMstId.ToString() },
                new { Name = "Last AlterID Transaction", Value = info.AltVchId.ToString() }
            };

            foreach (var item in metadata)
            {
                await targetConn.ExecuteAsync(
                    "INSERT INTO config (name, value) VALUES (@Name, @Value)",
                    new { item.Name, item.Value });
            }

            return info;
        }
    }
}
