using System.Data;
using System.Threading.Tasks;
using Npgsql;

namespace TallyDbLoader.Core.DatabaseLoaders
{
    public class PostgreSqlLoader : IDatabaseLoader
    {
        private readonly string _connectionString;

        public PostgreSqlLoader(string connectionString)
        {
            _connectionString = connectionString;
        }

        public async Task LoadBulkDataAsync(DataTable data, string tableName)
        {
            using (var conn = new NpgsqlConnection(_connectionString))
            {
                await conn.OpenAsync();
                
                var cols = new System.Collections.Generic.List<string>();
                foreach (DataColumn col in data.Columns) cols.Add($"\"{col.ColumnName}\"");
                var colString = string.Join(",", cols);
                
                using (var writer = await conn.BeginBinaryImportAsync($"COPY \"{tableName}\" ({colString}) FROM STDIN (FORMAT BINARY)"))
                {
                    foreach (DataRow row in data.Rows)
                    {
                        await writer.StartRowAsync();
                        foreach (DataColumn col in data.Columns)
                        {
                            await writer.WriteAsync(row[col.ColumnName]);
                        }
                    }
                    await writer.CompleteAsync();
                }
            }
        }
    }
}
