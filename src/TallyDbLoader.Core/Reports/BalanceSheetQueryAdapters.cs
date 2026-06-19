using System;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using TallyDbLoader.Core.Models;

namespace TallyDbLoader.Core.Reports
{
    public abstract class BalanceSheetQueryAdapterBase : IBalanceSheetQueryAdapter
    {
        protected abstract string Quote(string identifier);

        protected virtual string Qualify(BalanceSheetTableNames names, string table)
        {
            return $"{Quote(names.Schema)}.{Quote(table)}";
        }

        protected abstract string BuildTableExistsSql();

        protected virtual object BuildTableExistsParameters(BalanceSheetTableNames names, string tableName)
        {
            return new { Schema = names.Schema, TableName = tableName };
        }

        protected virtual async Task<bool> ClosingStockTableExistsAsync(
            DbConnection connection,
            BalanceSheetTableNames names,
            CancellationToken cancellationToken)
        {
            var command = new CommandDefinition(
                BuildTableExistsSql(),
                BuildTableExistsParameters(names, names.TrnClosingStockLedger),
                cancellationToken: cancellationToken);
            var count = await connection.ExecuteScalarAsync<long>(command);
            return count > 0;
        }

        public string BuildGroupSql(BalanceSheetTableNames names)
        {
            string group = Qualify(names, names.MstGroup);
            return $@"
SELECT
    name AS Name,
    COALESCE(parent, '') AS ParentName,
    COALESCE(primary_group, '') AS PrimaryGroup,
    CASE WHEN COALESCE(is_revenue, 0) = 1 THEN 1 ELSE 0 END AS IsRevenue
FROM {group};";
        }

        public string BuildLedgerSql(BalanceSheetTableNames names, bool includeClosingStock)
        {
            string ledger = Qualify(names, names.MstLedger);
            string group = Qualify(names, names.MstGroup);
            string accounting = Qualify(names, names.TrnAccounting);
            string voucher = Qualify(names, names.TrnVoucher);
            string closingStockCte = string.Empty;
            string closingStockSelect = "0 AS ClosingStockValue,\n    0 AS HasClosingStockValue,\n    0 AS OpeningStockValue,\n    0 AS HasOpeningStockValue";
            string closingStockJoin = string.Empty;

            if (includeClosingStock)
            {
                string closingStock = Qualify(names, names.TrnClosingStockLedger);
                closingStockCte = $@",
closing_stock_ranked AS (
    SELECT ledger, stock_value, ROW_NUMBER() OVER (PARTITION BY ledger ORDER BY stock_date DESC) AS rn
    FROM {closingStock}
    WHERE stock_date <= @AsAtDate
),
opening_stock_ranked AS (
    SELECT ledger, stock_value, ROW_NUMBER() OVER (PARTITION BY ledger ORDER BY stock_date DESC) AS rn
    FROM {closingStock}
    WHERE stock_date < @FinancialYearStart
)";
                closingStockSelect = @"-COALESCE(closing_stock_ranked.stock_value, 0) AS ClosingStockValue,
    CASE WHEN closing_stock_ranked.ledger IS NULL THEN 0 ELSE 1 END AS HasClosingStockValue,
    -COALESCE(opening_stock_ranked.stock_value, 0) AS OpeningStockValue,
    CASE WHEN opening_stock_ranked.ledger IS NULL THEN 0 ELSE 1 END AS HasOpeningStockValue";
                closingStockJoin = @"LEFT JOIN closing_stock_ranked ON closing_stock_ranked.ledger = l.name AND closing_stock_ranked.rn = 1
LEFT JOIN opening_stock_ranked ON opening_stock_ranked.ledger = l.name AND opening_stock_ranked.rn = 1";
            }

            return $@"
WITH pre_period AS (
    SELECT a.ledger AS ledger, SUM(a.amount) AS amount
    FROM {accounting} a
    JOIN {voucher} v ON v.guid = a.guid
    WHERE v.is_order_voucher = 0
      AND v.is_inventory_voucher = 0
      AND v.date < @FinancialYearStart
    GROUP BY a.ledger
),
current_period AS (
    SELECT a.ledger AS ledger,
           SUM(a.amount) AS amount,
           SUM(CASE WHEN a.amount < 0 THEN ABS(a.amount) ELSE 0 END) AS debit
    FROM {accounting} a
    JOIN {voucher} v ON v.guid = a.guid
    WHERE v.is_order_voucher = 0
      AND v.is_inventory_voucher = 0
      AND v.date >= @FinancialYearStart
      AND v.date <= @AsAtDate
    GROUP BY a.ledger
){closingStockCte}
SELECT
    l.name AS LedgerName,
    l.parent AS ParentGroupName,
    COALESCE(g.primary_group, '') AS PrimaryGroup,
    CASE WHEN COALESCE(g.is_revenue, 0) = 1 THEN 1 ELSE 0 END AS IsRevenue,
    COALESCE(l.opening_balance, 0) AS OpeningBalance,
    COALESCE(pre_period.amount, 0) AS PrePeriodMovement,
    COALESCE(current_period.amount, 0) AS CurrentPeriodMovement,
    COALESCE(current_period.debit, 0) AS CurrentPeriodDebit,
    {closingStockSelect}
FROM {ledger} l
LEFT JOIN {group} g ON g.name = l.parent
LEFT JOIN pre_period ON pre_period.ledger = l.name
LEFT JOIN current_period ON current_period.ledger = l.name
{closingStockJoin};";
        }

        public virtual async Task<BalanceSheetRawData> QueryAsync(
            DbConnection connection,
            BalanceSheetTableNames names,
            BalanceSheetVerificationRequest request,
            CancellationToken cancellationToken)
        {
            bool hasClosingStockTable = await ClosingStockTableExistsAsync(connection, names, cancellationToken);

            var command = new CommandDefinition(
                BuildLedgerSql(names, hasClosingStockTable),
                new
                {
                    FinancialYearStart = request.FinancialYearStart.Date,
                    AsAtDate = request.AsAtDate.Date
                },
                cancellationToken: cancellationToken);

            var rows = await connection.QueryAsync<BalanceSheetLedgerRow>(command);
            var groups = await connection.QueryAsync<BalanceSheetGroupRow>(
                new CommandDefinition(
                    BuildGroupSql(names),
                    cancellationToken: cancellationToken));
            var rawData = new BalanceSheetRawData
            {
                Ledgers = rows.ToList(),
                Groups = groups.ToList(),
                HasClosingStockTable = hasClosingStockTable
            };
            if (!hasClosingStockTable)
            {
                rawData.Warnings.Add($"Optional table '{names.TrnClosingStockLedger}' was not found; Stock-in-Hand uses ledger balances.");
            }
            return rawData;
        }
    }

    public sealed class SqliteBalanceSheetQueryAdapter : BalanceSheetQueryAdapterBase
    {
        protected override string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";
        protected override string BuildTableExistsSql()
        {
            return "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @TableName;";
        }

        protected override string Qualify(BalanceSheetTableNames names, string table)
        {
            return Quote(table);
        }
    }

    public sealed class MssqlBalanceSheetQueryAdapter : BalanceSheetQueryAdapterBase
    {
        protected override string Quote(string identifier) => $"[{identifier.Replace("]", "]]")}]";
        protected override string BuildTableExistsSql()
        {
            return "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = @Schema AND TABLE_NAME = @TableName;";
        }
    }

    public sealed class PostgresBalanceSheetQueryAdapter : BalanceSheetQueryAdapterBase
    {
        protected override string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";
        protected override string BuildTableExistsSql()
        {
            return "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = @Schema AND table_name = @TableName;";
        }
    }

    public sealed class MySqlBalanceSheetQueryAdapter : BalanceSheetQueryAdapterBase
    {
        protected override string Quote(string identifier) => $"`{identifier.Replace("`", "``")}`";
        protected override string BuildTableExistsSql()
        {
            return "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = @Schema AND table_name = @TableName;";
        }
    }
}
