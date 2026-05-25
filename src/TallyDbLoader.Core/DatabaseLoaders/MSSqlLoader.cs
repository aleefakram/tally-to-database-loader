using System.Data;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace TallyDbLoader.Core.DatabaseLoaders
{
    public class MSSqlLoader : IDatabaseLoader
    {
        private readonly string _connectionString;

        public MSSqlLoader(string connectionString)
        {
            _connectionString = connectionString;
        }

        public async Task LoadBulkDataAsync(DataTable data, string tableName)
        {
            using (var bulkCopy = new SqlBulkCopy(_connectionString))
            {
                bulkCopy.DestinationTableName = tableName;
                foreach (DataColumn col in data.Columns)
                {
                    bulkCopy.ColumnMappings.Add(col.ColumnName, col.ColumnName);
                }
                await bulkCopy.WriteToServerAsync(data);
            }
        }

        public string TruncateSql(string tableName) => $"TRUNCATE TABLE {tableName}";

        public string CascadeUpdateSql(string primaryTable, string childTable, string field) =>
            $"UPDATE t SET t.{field} = s.name FROM {childTable} AS t JOIN {primaryTable} AS s ON s.guid = t._{field} ;";

        public string VoucherNumberUpdateSql() =>
            "UPDATE t SET t.voucher_number = s.voucher_number FROM trn_voucher AS t JOIN _vchnumber AS s ON s.guid = t.guid;";

        public string CountAutoNumberVoucherTypesSql() =>
            "SELECT COUNT(*) AS c FROM mst_vouchertype WHERE numbering_method LIKE '%Auto%' ;";
    }
}
