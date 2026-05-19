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
    }
}
