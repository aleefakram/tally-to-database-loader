using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using TallyDbLoader.Core.Tally;

namespace TallyDbLoader.Core.Sync
{
    public class PostgresFullSyncTablePromoter : IFullSyncTablePromoter
    {
        private string Quote(string identifier)
        {
            return $"\"{identifier.Replace("\"", "\"\"")}\"";
        }

        public async Task<StageResult> StageAsync(DataTable data, TableConfig table, DbConnection conn)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (table == null) throw new ArgumentNullException(nameof(table));
            if (conn == null) throw new ArgumentNullException(nameof(conn));

            var tableName = table.Name;
            var stagingTableName = $"__tally_fullsync_staging_{tableName}";

            try
            {
                // 1. Create Staging Table by copying live table columns (without constraints)
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = $"DROP TABLE IF EXISTS {Quote(stagingTableName)};";
                    await cmd.ExecuteNonQueryAsync();
                }

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = $"CREATE TABLE {Quote(stagingTableName)} AS SELECT * FROM {Quote(tableName)} WHERE 1=0;";
                    await cmd.ExecuteNonQueryAsync();
                }

                var columnTypes = await GetColumnTypesAsync(conn, stagingTableName);

                var columns = new List<string>();
                for (int i = 0; i < data.Columns.Count; i++)
                {
                    columns.Add(data.Columns[i].ColumnName);
                }

                // 2. Load data into staging table
                if (data.Rows.Count > 0)
                {
                    var sb = new StringBuilder();
                    sb.Append($"INSERT INTO {Quote(stagingTableName)} (");
                    for (int i = 0; i < data.Columns.Count; i++)
                    {
                        sb.Append(Quote(data.Columns[i].ColumnName));
                        if (i < data.Columns.Count - 1) sb.Append(", ");
                    }
                    sb.Append(") VALUES (");
                    for (int i = 0; i < data.Columns.Count; i++)
                    {
                        sb.Append($"@p{i}");
                        if (i < data.Columns.Count - 1) sb.Append(", ");
                    }
                    sb.Append(")");

                    using (var insertCmd = conn.CreateCommand())
                    {
                        insertCmd.CommandText = sb.ToString();
                        for (int i = 0; i < data.Columns.Count; i++)
                        {
                            var param = insertCmd.CreateParameter();
                            param.ParameterName = $"@p{i}";
                            insertCmd.Parameters.Add(param);
                        }

                        foreach (DataRow row in data.Rows)
                        {
                            for (int i = 0; i < data.Columns.Count; i++)
                            {
                                var param = insertCmd.Parameters[i];
                                var columnName = data.Columns[i].ColumnName;
                                columnTypes.TryGetValue(columnName, out var targetType);
                                param.Value = ConvertValueForPostgresParameter(row[i], targetType);
                            }
                            await insertCmd.ExecuteNonQueryAsync();
                        }
                    }
                }

                return new StageResult { RowCount = data.Rows.Count, Columns = columns };
            }
            catch
            {
                try { await CleanupStagingAsync(table, conn); } catch { }
                throw;
            }
        }

        private async Task<Dictionary<string, string>> GetColumnTypesAsync(DbConnection conn, string tableName)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT column_name, data_type
                    FROM information_schema.columns
                    WHERE table_schema = current_schema()
                      AND table_name = @tableName;";

                var param = cmd.CreateParameter();
                param.ParameterName = "@tableName";
                param.Value = tableName;
                cmd.Parameters.Add(param);

                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        var columnName = reader.GetString(0);
                        var dataType = reader.GetString(1);
                        result[columnName] = dataType;
                    }
                }
            }

            return result;
        }

        internal static object ConvertValueForPostgresParameter(object? value, string? targetType)
        {
            if (value == null || value == DBNull.Value)
                return DBNull.Value;

            if (string.IsNullOrWhiteSpace(targetType))
                return value;

            var normalizedType = targetType.Trim().ToLowerInvariant();

            if ((normalizedType == "smallint" || normalizedType == "int2") && value is bool boolValue)
                return boolValue ? (short)1 : (short)0;

            if ((normalizedType == "integer" || normalizedType == "int4") && value is bool intBoolValue)
                return intBoolValue ? 1 : 0;

            if ((normalizedType == "bigint" || normalizedType == "int8") && value is bool longBoolValue)
                return longBoolValue ? 1L : 0L;

            if ((normalizedType == "smallint" || normalizedType == "int2") && value is string smallIntString)
                return short.Parse(smallIntString, CultureInfo.InvariantCulture);

            return value;
        }

        public async Task ValidateStagingAsync(TableConfig table, DbConnection conn)
        {
            if (table == null) throw new ArgumentNullException(nameof(table));
            if (conn == null) throw new ArgumentNullException(nameof(conn));

            var tableName = table.Name;
            var stagingTableName = $"__tally_fullsync_staging_{tableName}";

            var isPrimary = table.Nature?.Equals("Primary", StringComparison.OrdinalIgnoreCase) == true;

            if (isPrimary)
            {
                var hasGuidConfig = false;
                if (table.Fields != null)
                {
                    foreach (var field in table.Fields)
                    {
                        if (field.Name.Equals("guid", StringComparison.OrdinalIgnoreCase))
                        {
                            hasGuidConfig = true;
                            break;
                        }
                    }
                }
                if (!hasGuidConfig)
                {
                    throw new InvalidOperationException($"GUID column is missing from Table {tableName} config, but Nature is Primary.");
                }

                // Check for null or empty GUIDs
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = $"SELECT COUNT(*) FROM {Quote(stagingTableName)} WHERE {Quote("guid")} IS NULL OR {Quote("guid")} = '';";
                    var nullOrEmptyCount = Convert.ToInt64(await cmd.ExecuteScalarAsync());
                    if (nullOrEmptyCount > 0)
                    {
                        throw new InvalidOperationException($"Table {tableName} contains {nullOrEmptyCount} rows with null or empty GUID.");
                    }
                }

                // Check for duplicate GUIDs
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = $"SELECT COUNT(*) FROM (SELECT {Quote("guid")} FROM {Quote(stagingTableName)} GROUP BY {Quote("guid")} HAVING COUNT(*) > 1) AS tmp;";
                    var duplicateCount = Convert.ToInt64(await cmd.ExecuteScalarAsync());
                    if (duplicateCount > 0)
                    {
                        throw new InvalidOperationException($"Table {tableName} contains duplicate GUIDs.");
                    }
                }
            }
        }

        public async Task PromoteStagedAsync(TableConfig table, List<string> columns, DbConnection conn, DbTransaction transaction)
        {
            if (table == null) throw new ArgumentNullException(nameof(table));
            if (conn == null) throw new ArgumentNullException(nameof(conn));
            if (transaction == null) throw new ArgumentNullException(nameof(transaction));

            var tableName = table.Name;
            var stagingTableName = $"__tally_fullsync_staging_{tableName}";

            // Delete live table
            using (var deleteCmd = conn.CreateCommand())
            {
                deleteCmd.Transaction = transaction;
                deleteCmd.CommandText = $"DELETE FROM {Quote(tableName)};";
                await deleteCmd.ExecuteNonQueryAsync();
            }

            // Copy staged rows using explicit column lists
            if (columns != null && columns.Count > 0)
            {
                var quotedCols = new List<string>();
                foreach (var col in columns)
                {
                    quotedCols.Add(Quote(col));
                }
                var colsStr = string.Join(", ", quotedCols);

                using (var promoteCmd = conn.CreateCommand())
                {
                    promoteCmd.Transaction = transaction;
                    promoteCmd.CommandText = $"INSERT INTO {Quote(tableName)} ({colsStr}) SELECT {colsStr} FROM {Quote(stagingTableName)};";
                    await promoteCmd.ExecuteNonQueryAsync();
                }
            }
        }

        public async Task CleanupStagingAsync(TableConfig table, DbConnection conn)
        {
            if (table == null) throw new ArgumentNullException(nameof(table));
            if (conn == null) throw new ArgumentNullException(nameof(conn));

            var tableName = table.Name;
            var stagingTableName = $"__tally_fullsync_staging_{tableName}";

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"DROP TABLE IF EXISTS {Quote(stagingTableName)};";
                await cmd.ExecuteNonQueryAsync();
            }
        }
    }
}
