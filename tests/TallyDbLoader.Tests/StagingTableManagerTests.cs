using System;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using TallyDbLoader.Core.Sync;
using Xunit;
using Dapper;

namespace TallyDbLoader.Tests
{
    public class StagingTableManagerTests : IDisposable
    {
        private readonly SqliteConnection _conn;

        public StagingTableManagerTests()
        {
            _conn = new SqliteConnection("Data Source=:memory:");
            _conn.Open();
        }

        public void Dispose()
        {
            _conn.Dispose();
        }

        [Fact]
        public async Task EnsureStagingTables_CreatesTables()
        {
            var manager = new StagingTableManager(_conn);

            // Verify tables don't exist yet (by trying to query them and expecting error)
            await Assert.ThrowsAsync<SqliteException>(() => _conn.ExecuteAsync("SELECT * FROM _diff"));

            // Create tables
            await manager.EnsureStagingTablesAsync();

            // Verify they now exist
            var diffCount = await _conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM _diff");
            var deleteCount = await _conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM _delete");
            var vchCount = await _conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM _vchnumber");
            var configCount = await _conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM config");

            Assert.Equal(0, diffCount);
            Assert.Equal(0, deleteCount);
            Assert.Equal(0, vchCount);
            Assert.Equal(0, configCount);
        }

        [Fact]
        public async Task TruncateStagingTables_ClearsTables()
        {
            var manager = new StagingTableManager(_conn);
            await manager.EnsureStagingTablesAsync();

            // Insert dummy data
            await _conn.ExecuteAsync("INSERT INTO _diff (guid, alterid) VALUES ('g1', 10)");
            await _conn.ExecuteAsync("INSERT INTO _delete (guid) VALUES ('g2')");
            await _conn.ExecuteAsync("INSERT INTO _vchnumber (guid, voucher_number) VALUES ('g3', 'V1')");

            Assert.Equal(1, await _conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM _diff"));
            Assert.Equal(1, await _conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM _delete"));
            Assert.Equal(1, await _conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM _vchnumber"));

            // Truncate
            await manager.TruncateStagingTablesAsync();

            Assert.Equal(0, await _conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM _diff"));
            Assert.Equal(0, await _conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM _delete"));
            Assert.Equal(0, await _conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM _vchnumber"));
        }
    }
}
