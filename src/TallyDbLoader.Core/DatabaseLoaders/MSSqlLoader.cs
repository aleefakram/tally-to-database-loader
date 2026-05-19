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
                await bulkCopy.WriteToServerAsync(data);
            }
        }
    }
}
