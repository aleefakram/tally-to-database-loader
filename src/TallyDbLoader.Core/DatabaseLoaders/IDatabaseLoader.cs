using System.Data;
using System.Threading.Tasks;

namespace TallyDbLoader.Core.DatabaseLoaders
{
    public interface IDatabaseLoader
    {
        Task LoadBulkDataAsync(DataTable data, string tableName);
        string TruncateSql(string tableName);
        string CascadeUpdateSql(string primaryTable, string childTable, string field);
        string VoucherNumberUpdateSql();
        string CountAutoNumberVoucherTypesSql();
    }
}
