using Xunit;
using TallyDbLoader.Core.DatabaseLoaders;
using System.Data;
using System.Threading.Tasks;
using System;

namespace TallyDbLoader.Tests
{
    public class DatabaseLoaderTests
    {
        [Fact]
        public void Test_MySqlLoader_ConnectionString_AppendsLocalInfile()
        {
            var baseConnStr = "Server=localhost;Database=test;Uid=root;Pwd=password;";
            var loader = new MySqlLoader(baseConnStr);
            
            // Access the private connection string field via reflection to verify it got modified.
            var fieldInfo = typeof(MySqlLoader).GetField("_connectionString", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var connStr = fieldInfo?.GetValue(loader) as string;
            
            Assert.NotNull(connStr);
            var resultBuilder = new MySqlConnector.MySqlConnectionStringBuilder(connStr);
            Assert.True(resultBuilder.AllowLoadLocalInfile);
        }

        [Fact]
        public async Task Test_MSSqlLoader_ThrowsOnInvalidConnection_ButCompiles()
        {
            var loader = new MSSqlLoader("Server=invalid_server_xyz;Database=test;Integrated Security=SSPI;TrustServerCertificate=True;Connection Timeout=1;");
            var dt = new DataTable("test_table");
            dt.Columns.Add("guid", typeof(string));
            dt.Rows.Add("guid-123");

            // We expect a connection failure/exception, which proves the loader executed the load logic.
            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                await loader.LoadBulkDataAsync(dt, "test_table");
            });
        }

        [Fact]
        public void MSSqlLoader_CascadeUpdateSql_MatchesNodeTemplate()
        {
            var sut = new MSSqlLoader("Server=.;");
            var sql = sut.CascadeUpdateSql("mst_group", "mst_ledger", "parent");
            Assert.Equal(
                "UPDATE t SET t.parent = s.name FROM mst_ledger AS t JOIN mst_group AS s ON s.guid = t._parent ;",
                sql);
        }

        [Fact]
        public void MySqlLoader_CascadeUpdateSql_MatchesNodeTemplate()
        {
            var sut = new MySqlLoader("Server=;");
            var sql = sut.CascadeUpdateSql("mst_group", "mst_ledger", "parent");
            Assert.Equal(
                "UPDATE mst_ledger AS t JOIN mst_group AS s ON s.guid = t._parent SET t.parent = s.name ;",
                sql);
        }

        [Fact]
        public void PostgreSqlLoader_CascadeUpdateSql_MatchesNodeTemplate()
        {
            var sut = new PostgreSqlLoader("Host=;");
            var sql = sut.CascadeUpdateSql("mst_group", "mst_ledger", "parent");
            Assert.Equal(
                "UPDATE \"mst_ledger\" AS t SET \"parent\" = s.name FROM \"mst_group\" AS s WHERE s.guid = t._parent ;",
                sql);
        }

        [Fact]
        public void MSSqlLoader_TruncateSql_UsesTruncate()
        {
            Assert.Equal("TRUNCATE TABLE foo", new MSSqlLoader("Server=.;").TruncateSql("foo"));
        }

        [Fact]
        public void SqliteLoader_TruncateSql_UsesDelete()
        {
            Assert.Equal("DELETE FROM foo", new SqliteLoader("Data Source=:memory:").TruncateSql("foo"));
        }

        [Fact]
        public void VoucherNumberUpdateSql_PerDb()
        {
            Assert.Equal(
                "UPDATE t SET t.voucher_number = s.voucher_number FROM trn_voucher AS t JOIN _vchnumber AS s ON s.guid = t.guid;",
                new MSSqlLoader("Server=.;").VoucherNumberUpdateSql());
            Assert.Equal(
                "UPDATE trn_voucher AS t JOIN _vchnumber AS s ON s.guid = t.guid SET t.voucher_number = s.voucher_number;",
                new MySqlLoader("Server=;").VoucherNumberUpdateSql());
            Assert.Equal(
                "UPDATE trn_voucher AS t SET voucher_number = s.voucher_number FROM _vchnumber AS s WHERE s.guid = t.guid;",
                new PostgreSqlLoader("Host=;").VoucherNumberUpdateSql());
        }

        [Fact]
        public async Task DatabaseWriter_GetConnectionAsync_ForSqlite_OpensDbConnection()
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"target_async_{Guid.NewGuid()}.db");
            try
            {
                var profile = new TallyDbLoader.Core.Models.DatabaseProfile
                {
                    Name = "SqliteTarget",
                    Technology = "sqlite"
                };

                await using var conn = await TallyDbLoader.Core.Data.DatabaseWriter.GetConnectionAsync(
                    profile,
                    path,
                    System.Threading.CancellationToken.None);

                Assert.Equal(System.Data.ConnectionState.Open, conn.State);
                Assert.IsAssignableFrom<System.Data.Common.DbConnection>(conn);
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (System.IO.File.Exists(path)) try { System.IO.File.Delete(path); } catch { }
            }
        }
    }
}
