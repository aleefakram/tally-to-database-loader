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
    }
}
