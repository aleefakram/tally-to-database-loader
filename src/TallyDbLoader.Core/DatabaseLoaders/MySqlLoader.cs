using System.Data;
using System.Threading.Tasks;
using MySqlConnector;

namespace TallyDbLoader.Core.DatabaseLoaders
{
    public class MySqlLoader : IDatabaseLoader
    {
        private readonly string _connectionString;

        public MySqlLoader(string connectionString)
        {
            var builder = new MySqlConnectionStringBuilder(connectionString);
            builder.AllowLoadLocalInfile = true;
            _connectionString = builder.ConnectionString;
        }

        public async Task LoadBulkDataAsync(DataTable data, string tableName)
        {
            using (var conn = new MySqlConnection(_connectionString))
            {
                await conn.OpenAsync();
                var bulkCopy = new MySqlBulkCopy(conn)
                {
                    DestinationTableName = tableName
                };
                await bulkCopy.WriteToServerAsync(data);
            }
        }

        public string TruncateSql(string tableName) => $"TRUNCATE TABLE {tableName}";

        public string CascadeUpdateSql(string primaryTable, string childTable, string field) =>
            $"UPDATE {childTable} AS t JOIN {primaryTable} AS s ON s.guid = t._{field} SET t.{field} = s.name ;";

        public string VoucherNumberUpdateSql() =>
            "UPDATE trn_voucher AS t JOIN _vchnumber AS s ON s.guid = t.guid SET t.voucher_number = s.voucher_number;";

        public string CountAutoNumberVoucherTypesSql() =>
            "SELECT COUNT(*) AS c FROM mst_vouchertype WHERE numbering_method LIKE '%Auto%' ;";
    }
}
