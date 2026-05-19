using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using TallyDbLoader.Core.Data;
using TallyDbLoader.Core.DatabaseLoaders;
using Xunit;

namespace TallyDbLoader.Tests
{
    public class StagingLoaderHelperTests
    {
        private class MockDatabaseLoader : IDatabaseLoader
        {
            public DataTable? LastDataLoaded { get; private set; }
            public string? LastTableLoaded { get; private set; }

            public Task LoadBulkDataAsync(DataTable data, string tableName)
            {
                LastDataLoaded = data;
                LastTableLoaded = tableName;
                return Task.CompletedTask;
            }
        }

        [Fact]
        public async Task Test_LoadGuidsToStagingAsync_BuildsCorrectDataTable()
        {
            var mockLoader = new MockDatabaseLoader();
            var guids = new List<string> { "guid-1", "guid-2", "guid-3" };

            await StagingLoaderHelper.LoadGuidsToStagingAsync(mockLoader, "_diff", guids);

            Assert.Equal("_diff", mockLoader.LastTableLoaded);
            Assert.NotNull(mockLoader.LastDataLoaded);
            Assert.Single(mockLoader.LastDataLoaded.Columns);
            Assert.Equal("guid", mockLoader.LastDataLoaded.Columns[0].ColumnName);
            Assert.Equal(3, mockLoader.LastDataLoaded.Rows.Count);
            Assert.Equal("guid-1", mockLoader.LastDataLoaded.Rows[0]["guid"]);
            Assert.Equal("guid-2", mockLoader.LastDataLoaded.Rows[1]["guid"]);
            Assert.Equal("guid-3", mockLoader.LastDataLoaded.Rows[2]["guid"]);
        }

        [Fact]
        public async Task Test_LoadVoucherNumbersToStagingAsync_BuildsCorrectDataTable()
        {
            var mockLoader = new MockDatabaseLoader();
            var vouchers = new List<(string Guid, string VoucherNumber)>
            {
                ("g1", "v1"),
                ("g2", "v2")
            };

            await StagingLoaderHelper.LoadVoucherNumbersToStagingAsync(mockLoader, "_vchnumber", vouchers);

            Assert.Equal("_vchnumber", mockLoader.LastTableLoaded);
            Assert.NotNull(mockLoader.LastDataLoaded);
            Assert.Equal(2, mockLoader.LastDataLoaded.Columns.Count);
            Assert.Equal("guid", mockLoader.LastDataLoaded.Columns[0].ColumnName);
            Assert.Equal("voucher_number", mockLoader.LastDataLoaded.Columns[1].ColumnName);
            Assert.Equal(2, mockLoader.LastDataLoaded.Rows.Count);
            Assert.Equal("g1", mockLoader.LastDataLoaded.Rows[0]["guid"]);
            Assert.Equal("v1", mockLoader.LastDataLoaded.Rows[0]["voucher_number"]);
            Assert.Equal("g2", mockLoader.LastDataLoaded.Rows[1]["guid"]);
            Assert.Equal("v2", mockLoader.LastDataLoaded.Rows[1]["voucher_number"]);
        }
    }
}
