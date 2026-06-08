using System;
using System.Data;
using System.Data.Common;
using System.Threading.Tasks;
using TallyDbLoader.Core.Tally;

namespace TallyDbLoader.Core.Sync
{
    public class UnsupportedFullSyncTablePromoter : IFullSyncTablePromoter
    {
        public Task<StageResult> StageAsync(DataTable data, TableConfig table, DbConnection conn)
        {
            throw new NotSupportedException("Safe promotion is not supported for this database technology.");
        }

        public Task ValidateStagingAsync(TableConfig table, DbConnection conn)
        {
            throw new NotSupportedException("Safe promotion is not supported for this database technology.");
        }

        public Task PromoteStagedAsync(TableConfig table, System.Collections.Generic.List<string> columns, DbConnection conn, DbTransaction transaction)
        {
            throw new NotSupportedException("Safe promotion is not supported for this database technology.");
        }

        public Task CleanupStagingAsync(TableConfig table, DbConnection conn)
        {
            throw new NotSupportedException("Safe promotion is not supported for this database technology.");
        }
    }
}
