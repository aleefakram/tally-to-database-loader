using System;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using TallyDbLoader.Core.Sync;
using Xunit;
using Dapper;

namespace TallyDbLoader.Tests
{
    public class WatermarkRepositoryTests : IDisposable
    {
        private readonly SqliteConnection _conn;

        public WatermarkRepositoryTests()
        {
            _conn = new SqliteConnection("Data Source=:memory:");
            _conn.Open();
            _conn.Execute("CREATE TABLE config (name VARCHAR(64) PRIMARY KEY, value VARCHAR(1024))");
        }

        public void Dispose()
        {
            _conn.Dispose();
        }

        [Fact]
        public async Task Read_NoValues_ReturnsZeros()
        {
            var repo = new WatermarkRepository(_conn);
            var watermarks = await repo.ReadAsync();

            Assert.Equal(0, watermarks.MasterAlterId);
            Assert.Equal(0, watermarks.TransactionAlterId);
        }

        [Fact]
        public async Task WriteAndRead_Success_SavesAndRetrievesValues()
        {
            var repo = new WatermarkRepository(_conn);

            // Write
            await repo.WriteAsync(450, 980);

            // Read
            var watermarks = await repo.ReadAsync();

            Assert.Equal(450, watermarks.MasterAlterId);
            Assert.Equal(980, watermarks.TransactionAlterId);

            // Overwrite
            await repo.WriteAsync(455, 990);
            watermarks = await repo.ReadAsync();

            Assert.Equal(455, watermarks.MasterAlterId);
            Assert.Equal(990, watermarks.TransactionAlterId);
        }
    }
}
