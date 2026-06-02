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
            var columnTypes = new System.Collections.Generic.Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);

            using (var conn = new NpgsqlConnection(_connectionString))
            {
                await conn.OpenAsync();

                try
                {
                    using (var schemaCmd = new NpgsqlCommand($"SELECT * FROM \"{tableName}\" WHERE 1=0", conn))
                    using (var reader = await schemaCmd.ExecuteReaderAsync())
                    {
                        var columnSchema = await reader.GetColumnSchemaAsync();
                        foreach (var col in columnSchema)
                        {
                            if (col.ColumnName != null && col.DataTypeName != null)
                            {
                                columnTypes[col.ColumnName] = col.DataTypeName;
                            }
                        }
                    }
                }
                catch
                {
                    // Fallback to empty schema dictionary if schema query fails
                }

                var cols = new System.Collections.Generic.List<string>();
                foreach (DataColumn col in data.Columns) cols.Add($"\"{col.ColumnName}\"");
                var colString = string.Join(",", cols);

                var currentColumn = "";
                var currentVal = (object?)null;
                var currentTargetType = "";
                var currentRowIndex = 0;

                try
                {
                    using (var writer = await conn.BeginBinaryImportAsync($"COPY \"{tableName}\" ({colString}) FROM STDIN (FORMAT BINARY)"))
                    {
                        foreach (DataRow row in data.Rows)
                        {
                            await writer.StartRowAsync();
                            foreach (DataColumn col in data.Columns)
                            {
                                currentColumn = col.ColumnName;
                                var val = row[col.ColumnName];
                                currentVal = val;
                                columnTypes.TryGetValue(col.ColumnName, out var dbTypeName);
                                currentTargetType = dbTypeName ?? "";

                                if (val == null || val == DBNull.Value)
                                {
                                    await writer.WriteNullAsync();
                                }
                                else
                                {
                                    if (!string.IsNullOrEmpty(dbTypeName))
                                    {
                                        var norm = dbTypeName.ToLowerInvariant();
                                        try
                                        {
                                            if (norm == "date" || norm == "pg_catalog.date")
                                            {
                                                if (val is DateTime dt)
                                                {
                                                    val = DateOnly.FromDateTime(dt);
                                                }
                                                else
                                                {
                                                    val = DateOnly.Parse(val.ToString()!);
                                                }
                                            }
                                            else if (norm == "smallint" || norm == "int2")
                                            {
                                                val = Convert.ToInt16(val);
                                            }
                                            else if (norm == "integer" || norm == "int4")
                                            {
                                                val = Convert.ToInt32(val);
                                            }
                                            else if (norm == "bigint" || norm == "int8")
                                            {
                                                val = Convert.ToInt64(val);
                                            }
                                            else if (norm == "numeric" || norm == "decimal")
                                            {
                                                val = Convert.ToDecimal(val);
                                            }
                                            else if (norm == "boolean" || norm == "bool")
                                            {
                                                val = Convert.ToBoolean(val);
                                            }
                                        }
                                        catch
                                        {
                                            // Fallback to original value if conversion fails
                                        }
                                    }
                                    currentVal = val; // update with casted value for logging
                                    await writer.WriteAsync(val);
                                }
                            }
                            currentRowIndex++;
                        }
                        await writer.CompleteAsync();
                    }
                }
                catch (System.Exception ex)
                {
                    var schemaInfo = new System.Text.StringBuilder();
                    foreach (DataColumn col in data.Columns)
                    {
                        columnTypes.TryGetValue(col.ColumnName, out var targetT);
                        schemaInfo.Append($"{col.ColumnName}: DT={col.DataType?.Name ?? "null"}, DB={targetT ?? "null"}; ");
                    }

                    throw new System.Exception(
                        $"PostgreSqlLoader error on table '{tableName}', column '{currentColumn}' (Row {currentRowIndex}). " +
                        $"Value: '{currentVal}' (Type: {currentVal?.GetType()?.Name ?? "null"}). " +
                        $"Target Database Type: '{currentTargetType}'. " +
                        $"Table Schema: {schemaInfo.ToString()}. " +
                        $"Error: {ex.Message}", ex);
                }
            }
        }

        public string TruncateSql(string tableName) => $"TRUNCATE TABLE \"{tableName}\"";

        public string CascadeUpdateSql(string primaryTable, string childTable, string field) =>
            $"UPDATE \"{childTable}\" AS t SET \"{field}\" = s.name FROM \"{primaryTable}\" AS s WHERE s.guid = t._{field} ;";

        public string VoucherNumberUpdateSql() =>
            "UPDATE trn_voucher AS t SET voucher_number = s.voucher_number FROM _vchnumber AS s WHERE s.guid = t.guid;";

        public string CountAutoNumberVoucherTypesSql() =>
            "SELECT COUNT(*) AS c FROM mst_vouchertype WHERE numbering_method LIKE '%Auto%' ;";
    }
}
