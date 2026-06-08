using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Text;
using System.Threading.Tasks;
using TallyDbLoader.Core.Tally;

namespace TallyDbLoader.Core.Sync
{
    public class SqliteFullSyncTablePromoter : IFullSyncTablePromoter
    {
        public async Task<int> StageValidateAndPromoteAsync(DataTable data, TableConfig table, DbConnection targetConn)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (table == null) throw new ArgumentNullException(nameof(table));
            if (targetConn == null) throw new ArgumentNullException(nameof(targetConn));

            var tableName = table.Name;
            var stagingTableName = $"__tally_fullsync_staging_{tableName}";

            // 1. Create Staging Table by copying live table columns (without constraints)
            using (var cmd = targetConn.CreateCommand())
            {
                cmd.CommandText = $"DROP TABLE IF EXISTS {stagingTableName};";
                await cmd.ExecuteNonQueryAsync();
            }

            using (var cmd = targetConn.CreateCommand())
            {
                cmd.CommandText = $"CREATE TABLE {stagingTableName} AS SELECT * FROM {tableName} WHERE 1=0;";
                await cmd.ExecuteNonQueryAsync();
            }

            try
            {
                // 2. Load data into staging table directly using targetConn
                if (data.Rows.Count > 0)
                {
                    var sb = new StringBuilder();
                    sb.Append($"INSERT INTO {stagingTableName} (");
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

                    using (var insertCmd = targetConn.CreateCommand())
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
                                param.Value = row[i] ?? DBNull.Value;
                            }
                            await insertCmd.ExecuteNonQueryAsync();
                        }
                    }
                }

                // 3. Validation
                var hasGuid = false;
                foreach (DataColumn col in data.Columns)
                {
                    if (col.ColumnName.Equals("guid", StringComparison.OrdinalIgnoreCase))
                    {
                        hasGuid = true;
                        break;
                    }
                }

                var isPrimary = table.Nature?.Equals("Primary", StringComparison.OrdinalIgnoreCase) == true;

                if (isPrimary)
                {
                    if (!hasGuid)
                    {
                        throw new InvalidOperationException($"GUID column is missing from Table {tableName} config/data, but Nature is Primary.");
                    }

                    // Check for null or empty GUIDs
                    using (var cmd = targetConn.CreateCommand())
                    {
                        cmd.CommandText = $"SELECT COUNT(*) FROM {stagingTableName} WHERE guid IS NULL OR guid = '';";
                        var nullOrEmptyCount = Convert.ToInt64(await cmd.ExecuteScalarAsync());
                        if (nullOrEmptyCount > 0)
                        {
                            throw new InvalidOperationException($"Table {tableName} contains {nullOrEmptyCount} rows with null or empty GUID.");
                        }
                    }

                    // Check for duplicate GUIDs
                    using (var cmd = targetConn.CreateCommand())
                    {
                        cmd.CommandText = $"SELECT COUNT(*) FROM (SELECT guid FROM {stagingTableName} GROUP BY guid HAVING COUNT(*) > 1);";
                        var duplicateCount = Convert.ToInt64(await cmd.ExecuteScalarAsync());
                        if (duplicateCount > 0)
                        {
                            throw new InvalidOperationException($"Table {tableName} contains duplicate GUIDs.");
                        }
                    }
                }

                // 4. Promote within one transaction using explicit column lists
                using (var transaction = targetConn.BeginTransaction())
                {
                    try
                    {
                        // Delete live table
                        using (var deleteCmd = targetConn.CreateCommand())
                        {
                            deleteCmd.Transaction = transaction;
                            deleteCmd.CommandText = $"DELETE FROM {tableName};";
                            await deleteCmd.ExecuteNonQueryAsync();
                        }

                        // Copy staged rows using explicit column lists
                        if (data.Rows.Count > 0)
                        {
                            var cols = new List<string>();
                            for (int i = 0; i < data.Columns.Count; i++)
                            {
                                cols.Add(data.Columns[i].ColumnName);
                            }
                            var colsStr = string.Join(", ", cols);

                            using (var promoteCmd = targetConn.CreateCommand())
                            {
                                promoteCmd.Transaction = transaction;
                                promoteCmd.CommandText = $"INSERT INTO {tableName} ({colsStr}) SELECT {colsStr} FROM {stagingTableName};";
                                await promoteCmd.ExecuteNonQueryAsync();
                            }
                        }

                        transaction.Commit();
                    }
                    catch
                    {
                        transaction.Rollback();
                        throw;
                    }
                }

                return data.Rows.Count;
            }
            finally
            {
                // Clean up staging table
                using (var cleanCmd = targetConn.CreateCommand())
                {
                    cleanCmd.CommandText = $"DROP TABLE IF EXISTS {stagingTableName};";
                    await cleanCmd.ExecuteNonQueryAsync();
                }
            }
        }
    }
}
