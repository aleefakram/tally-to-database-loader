using System;
using System.Data;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace TallyDbLoader.Core.DatabaseLoaders
{
    public class SqliteLoader : IDatabaseLoader
    {
        private readonly string _connectionString;

        public SqliteLoader(string connectionString)
        {
            _connectionString = connectionString;
        }

        public async Task LoadBulkDataAsync(DataTable data, string tableName)
        {
            if (data.Rows.Count == 0) return;

            using (var conn = new SqliteConnection(_connectionString))
            {
                await conn.OpenAsync();
                using (var transaction = conn.BeginTransaction())
                {
                    var sb = new StringBuilder();
                    sb.Append($"INSERT INTO {tableName} (");
                    for (int i = 0; i < data.Columns.Count; i++)
                    {
                        sb.Append(data.Columns[i].ColumnName);
                        if (i < data.Columns.Count - 1) sb.Append(", ");
                    }
                    sb.Append(") VALUES (");
                    for (int i = 0; i < data.Columns.Count; i++)
                    {
                        sb.Append($"@p{i}");
                        if (i < data.Columns.Count - 1) sb.Append(", ");
                    }
                    sb.Append(")");

                    var cmdText = sb.ToString();

                    using (var cmd = new SqliteCommand(cmdText, conn, transaction))
                    {
                        // Pre-create parameters
                        for (int i = 0; i < data.Columns.Count; i++)
                        {
                            cmd.Parameters.Add(new SqliteParameter($"@p{i}", DBNull.Value));
                        }

                        foreach (DataRow row in data.Rows)
                        {
                            for (int i = 0; i < data.Columns.Count; i++)
                            {
                                cmd.Parameters[i].Value = row[i] ?? DBNull.Value;
                            }
                            await cmd.ExecuteNonQueryAsync();
                        }
                    }
                    await transaction.CommitAsync();
                }
            }
        }
    }
}
