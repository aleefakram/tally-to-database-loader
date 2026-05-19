using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using TallyDbLoader.Core.DatabaseLoaders;

namespace TallyDbLoader.Core.Data
{
    public static class StagingLoaderHelper
    {
        public static async Task LoadGuidsToStagingAsync(IDatabaseLoader loader, string stagingTable, List<string> guids)
        {
            var dt = new DataTable(stagingTable);
            dt.Columns.Add("guid", typeof(string));
            foreach (var guid in guids)
            {
                dt.Rows.Add(guid);
            }
            await loader.LoadBulkDataAsync(dt, stagingTable);
        }

        public static async Task LoadVoucherNumbersToStagingAsync(IDatabaseLoader loader, string stagingTable, List<(string Guid, string VoucherNumber)> vouchers)
        {
            var dt = new DataTable(stagingTable);
            dt.Columns.Add("guid", typeof(string));
            dt.Columns.Add("voucher_number", typeof(string));
            foreach (var v in vouchers)
            {
                dt.Rows.Add(v.Guid, v.VoucherNumber);
            }
            await loader.LoadBulkDataAsync(dt, stagingTable);
        }
    }
}
