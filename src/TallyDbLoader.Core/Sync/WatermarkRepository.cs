using System;
using System.Data.Common;
using System.Threading.Tasks;
using Dapper;

namespace TallyDbLoader.Core.Sync
{
    public class WatermarkRepository
    {
        private readonly DbConnection _conn;

        public WatermarkRepository(DbConnection conn)
        {
            _conn = conn ?? throw new ArgumentNullException(nameof(conn));
        }

        public async Task<(long MasterAlterId, long TransactionAlterId)> ReadAsync()
        {
            var masterStr = await _conn.QueryFirstOrDefaultAsync<string>(
                "SELECT value FROM config WHERE name = 'Last AlterID Master'");
            var txnStr = await _conn.QueryFirstOrDefaultAsync<string>(
                "SELECT value FROM config WHERE name = 'Last AlterID Transaction'");

            long masterAlterId = 0;
            long txnAlterId = 0;

            if (!string.IsNullOrEmpty(masterStr))
            {
                long.TryParse(masterStr, out masterAlterId);
            }

            if (!string.IsNullOrEmpty(txnStr))
            {
                long.TryParse(txnStr, out txnAlterId);
            }

            return (masterAlterId, txnAlterId);
        }

        public async Task WriteAsync(long masterAlterId, long transactionAlterId)
        {
            await UpsertAsync("Last AlterID Master", masterAlterId.ToString());
            await UpsertAsync("Last AlterID Transaction", transactionAlterId.ToString());
        }

        private async Task UpsertAsync(string name, string value)
        {
            await _conn.ExecuteAsync("DELETE FROM config WHERE name = @name", new { name });
            await _conn.ExecuteAsync("INSERT INTO config (name, value) VALUES (@name, @value)", new { name, value });
        }
    }
}
