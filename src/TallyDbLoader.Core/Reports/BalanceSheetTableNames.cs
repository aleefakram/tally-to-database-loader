using System;
using System.Text.RegularExpressions;
using TallyDbLoader.Core.Sync;

namespace TallyDbLoader.Core.Reports
{
    public class BalanceSheetTableNames
    {
        private static readonly Regex PrefixRegex = new Regex(@"^[a-zA-Z_][a-zA-Z0-9_]*$", RegexOptions.Compiled);

        public string Schema { get; private set; } = string.Empty;
        public string Prefix { get; private set; } = string.Empty;
        public string Provider { get; private set; } = string.Empty;
        public string MstGroup { get; private set; } = string.Empty;
        public string MstLedger { get; private set; } = string.Empty;
        public string TrnVoucher { get; private set; } = string.Empty;
        public string TrnAccounting { get; private set; } = string.Empty;
        public string TrnClosingStockLedger { get; private set; } = string.Empty;

        public static BalanceSheetTableNames Create(string? schema, string? prefix, string provider)
        {
            string normalizedSchema = string.IsNullOrWhiteSpace(schema) ? "public" : schema.Trim();
            string normalizedPrefix = prefix?.Trim() ?? string.Empty;

            DbIdentifierPolicy.Validate(normalizedSchema, provider);
            if (normalizedPrefix.Length > 0 && !PrefixRegex.IsMatch(normalizedPrefix))
            {
                throw new InvalidOperationException($"Table prefix '{normalizedPrefix}' is invalid.");
            }

            var result = new BalanceSheetTableNames
            {
                Schema = normalizedSchema,
                Prefix = normalizedPrefix,
                Provider = provider,
                MstGroup = Build(normalizedPrefix, "mst_group", provider),
                MstLedger = Build(normalizedPrefix, "mst_ledger", provider),
                TrnVoucher = Build(normalizedPrefix, "trn_voucher", provider),
                TrnAccounting = Build(normalizedPrefix, "trn_accounting", provider),
                TrnClosingStockLedger = Build(normalizedPrefix, "trn_closingstock_ledger", provider)
            };

            return result;
        }

        private static string Build(string prefix, string logicalName, string provider)
        {
            var physical = prefix + logicalName;
            DbIdentifierPolicy.Validate(physical, provider);
            return physical;
        }
    }
}
