using System.Data;
using System.Data.Common;
using System.Threading.Tasks;
using TallyDbLoader.Core.Tally;

namespace TallyDbLoader.Core.Sync
{
    public class StageResult
    {
        public int RowCount { get; set; }
        public System.Collections.Generic.List<string> Columns { get; set; } = new System.Collections.Generic.List<string>();
    }

    public interface IFullSyncTablePromoter
    {
        /// <summary>
        /// Creates the staging table and loads data into it.
        /// </summary>
        Task<StageResult> StageAsync(DataTable data, TableConfig table, DbConnection conn);

        /// <summary>
        /// Validates the staged data before promotion (e.g. primary key uniqueness, null checks).
        /// </summary>
        Task ValidateStagingAsync(TableConfig table, DbConnection conn);

        /// <summary>
        /// Promotes the staged data to the live table inside an active database transaction.
        /// </summary>
        Task PromoteStagedAsync(TableConfig table, System.Collections.Generic.List<string> columns, DbConnection conn, DbTransaction transaction);

        /// <summary>
        /// Cleans up any staging table. Should be executed as a best-effort operation in a finally block.
        /// </summary>
        Task CleanupStagingAsync(TableConfig table, DbConnection conn);
    }
}
