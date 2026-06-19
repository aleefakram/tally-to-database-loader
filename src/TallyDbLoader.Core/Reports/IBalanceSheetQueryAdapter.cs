using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using TallyDbLoader.Core.Models;

namespace TallyDbLoader.Core.Reports
{
    public interface IBalanceSheetQueryAdapter
    {
        string BuildLedgerSql(BalanceSheetTableNames names, bool includeClosingStock);
        string BuildGroupSql(BalanceSheetTableNames names);
        Task<BalanceSheetRawData> QueryAsync(
            DbConnection connection,
            BalanceSheetTableNames names,
            BalanceSheetVerificationRequest request,
            CancellationToken cancellationToken);
    }
}
