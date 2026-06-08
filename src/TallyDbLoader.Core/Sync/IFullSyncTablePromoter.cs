using System.Data;
using System.Data.Common;
using System.Threading.Tasks;
using TallyDbLoader.Core.Tally;

namespace TallyDbLoader.Core.Sync
{
    public interface IFullSyncTablePromoter
    {
        /// <summary>
        /// Stages the data, runs validation, and promotes it to the live table inside a transaction.
        /// Returns the number of promoted rows.
        /// </summary>
        Task<int> StageValidateAndPromoteAsync(DataTable data, TableConfig table, DbConnection targetConn);
    }
}
