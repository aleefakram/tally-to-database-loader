using Xunit;
using TallyDbLoader.Core.Sync;
using TallyDbLoader.Core.Data;

namespace TallyDbLoader.Tests
{
    public class BackgroundSyncWorkerTests
    {
        [Fact]
        public void Test_BackgroundSyncWorker_StartStop()
        {
            string testDb = "test_worker.db";
            if (System.IO.File.Exists(testDb)) System.IO.File.Delete(testDb);

            DatabaseHelper.InitializeDatabase(testDb);
            var repo = new ConfigRepository(testDb);
            var worker = new BackgroundSyncWorker(repo, "localhost", 9000);

            Assert.False(worker.IsRunning);

            worker.Start();
            Assert.True(worker.IsRunning);

            worker.Stop();
            Assert.False(worker.IsRunning);

            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (System.IO.File.Exists(testDb)) System.IO.File.Delete(testDb);
        }
    }
}
