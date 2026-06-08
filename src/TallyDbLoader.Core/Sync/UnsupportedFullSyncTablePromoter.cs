using System;
using System.Data;
using System.Data.Common;
using System.Threading.Tasks;
using TallyDbLoader.Core.Tally;

namespace TallyDbLoader.Core.Sync
{
    public class UnsupportedFullSyncTablePromoter : IFullSyncTablePromoter
    {
        public Task<int> StageValidateAndPromoteAsync(DataTable data, TableConfig table, DbConnection targetConn)
        {
            throw new NotSupportedException("Safe promotion is not supported for this database technology.");
        }
    }
}
