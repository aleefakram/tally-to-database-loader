using System;
using System.Data.Common;
using System.Threading.Tasks;
using Dapper;

namespace TallyDbLoader.Core.Sync
{
    public class StagingTableManager
    {
        private readonly DbConnection _conn;

        public StagingTableManager(DbConnection conn)
        {
            _conn = conn ?? throw new ArgumentNullException(nameof(conn));
        }

        public async Task EnsureStagingTablesAsync()
        {
            var connTypeName = _conn.GetType().Name;
            var isSqlServer = connTypeName.Contains("SqlConnection");

            if (isSqlServer)
            {
                await _conn.ExecuteAsync("IF OBJECT_ID('config', 'U') IS NULL CREATE TABLE config (name VARCHAR(64) PRIMARY KEY, value VARCHAR(1024))");
                await _conn.ExecuteAsync("IF OBJECT_ID('_diff', 'U') IS NULL CREATE TABLE _diff (guid VARCHAR(64) PRIMARY KEY, alterid BIGINT)");
                await _conn.ExecuteAsync("IF OBJECT_ID('_delete', 'U') IS NULL CREATE TABLE _delete (guid VARCHAR(64) PRIMARY KEY)");
                await _conn.ExecuteAsync("IF OBJECT_ID('_vchnumber', 'U') IS NULL CREATE TABLE _vchnumber (guid VARCHAR(64) PRIMARY KEY, voucher_number VARCHAR(64))");
            }
            else
            {
                await _conn.ExecuteAsync("CREATE TABLE IF NOT EXISTS config (name VARCHAR(64) PRIMARY KEY, value VARCHAR(1024))");
                await _conn.ExecuteAsync("CREATE TABLE IF NOT EXISTS _diff (guid VARCHAR(64) PRIMARY KEY, alterid BIGINT)");
                await _conn.ExecuteAsync("CREATE TABLE IF NOT EXISTS _delete (guid VARCHAR(64) PRIMARY KEY)");
                await _conn.ExecuteAsync("CREATE TABLE IF NOT EXISTS _vchnumber (guid VARCHAR(64) PRIMARY KEY, voucher_number VARCHAR(64))");
            }
        }

        public async Task TruncateStagingTablesAsync()
        {
            var connTypeName = _conn.GetType().Name;
            var isSqlite = connTypeName.Contains("SqliteConnection");

            if (isSqlite)
            {
                await _conn.ExecuteAsync("DELETE FROM _diff");
                await _conn.ExecuteAsync("DELETE FROM _delete");
                await _conn.ExecuteAsync("DELETE FROM _vchnumber");
            }
            else
            {
                await _conn.ExecuteAsync("TRUNCATE TABLE _diff");
                await _conn.ExecuteAsync("TRUNCATE TABLE _delete");
                await _conn.ExecuteAsync("TRUNCATE TABLE _vchnumber");
            }
        }
    }
}
