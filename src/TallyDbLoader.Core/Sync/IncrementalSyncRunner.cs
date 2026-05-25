using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading.Tasks;
using TallyDbLoader.Core.DatabaseLoaders;
using TallyDbLoader.Core.Tally;
using Dapper;

namespace TallyDbLoader.Core.Sync
{
    public class IncrementalSyncRunner
    {
        private readonly ITallyClient _tally;
        private readonly IDatabaseLoader _loader;

        public IncrementalSyncRunner(ITallyClient tally, IDatabaseLoader loader)
        {
            _tally = tally ?? throw new ArgumentNullException(nameof(tally));
            _loader = loader ?? throw new ArgumentNullException(nameof(loader));
        }

        public async Task RunAsync(TallyExportConfig config, string companyName,
            DateTime fromDate, DateTime toDate, DbConnection conn)
        {
            var staging = new StagingTableManager(conn);
            await staging.EnsureStagingTablesAsync();

            var repo = new WatermarkRepository(conn);
            var (lastMasterDb, lastTxnDb) = await repo.ReadAsync();

            var companyInfo = await _tally.FetchCompanyInfoAsync(companyName);
            if (companyInfo == null)
            {
                throw new InvalidOperationException($"Could not fetch company info for '{companyName}'");
            }

            var masterChanged = companyInfo.AltMstId != lastMasterDb;
            var txnChanged = companyInfo.AltVchId != lastTxnDb;

            if (!masterChanged && !txnChanged) return;

            var primary = new List<TableConfig>();
            if (masterChanged) primary.AddRange(config.Master.Where(t => t.Nature == "Primary"));
            if (txnChanged) primary.AddRange(config.Transaction.Where(t => t.Nature == "Primary"));

            // Phase 1: Diff & Delete
            await RunPhase1DiffAsync(primary, companyName, fromDate, toDate, conn);

            // Phase 2: Refetch
            await RunPhase2RefetchAsync(
                masterChanged ? config.Master : new List<TableConfig>(),
                txnChanged ? config.Transaction : new List<TableConfig>(),
                lastMasterDb, lastTxnDb, companyName, fromDate, toDate, conn);

            // Phase 3: Cascade Updates
            if (masterChanged)
            {
                await RunPhase3CascadeUpdateAsync(primary, conn);
            }

            // Phase 3: Voucher Refresh
            if (txnChanged)
            {
                await RunPhase3VoucherRefreshAsync(config.Transaction, companyName, fromDate, toDate, conn);
            }

            // Phase 4: Atomic Commit Watermark
            await staging.TruncateStagingTablesAsync();
            await repo.WriteAsync(companyInfo.AltMstId, companyInfo.AltVchId);
        }

        public async Task RunPhase1DiffAsync(IEnumerable<TableConfig> primaryTables, string companyName,
            DateTime fromDate, DateTime toDate, DbConnection conn)
        {
            var staging = new StagingTableManager(conn);
            await staging.EnsureStagingTablesAsync();

            foreach (var active in primaryTables)
            {
                await conn.ExecuteAsync(_loader.TruncateSql("_diff"));
                await conn.ExecuteAsync(_loader.TruncateSql("_delete"));

                var diffTable = new TableConfig
                {
                    Name = "_diff",
                    Collection = active.Collection,
                    Nature = "",
                    Fields = new List<FieldConfig>
                    {
                        new() { Name = "guid", Field = "Guid", Type = "text" },
                        new() { Name = "alterid", Field = "AlterId", Type = "number" }
                    },
                    Fetch = new List<string> { "AlterId" },
                    Filters = active.Filters ?? new List<string>()
                };

                var xml = DynamicTdlXmlGenerator.GenerateXml(diffTable, companyName,
                    fromDate.ToString("yyyyMMdd"), toDate.ToString("yyyyMMdd"));
                var resp = await _tally.PostXMLAsync(xml);
                var diffData = DynamicXmlParser.ParseXml(resp, diffTable);
                if (diffData.Rows.Count > 0)
                {
                    await _loader.LoadBulkDataAsync(diffData, "_diff");
                }

                await conn.ExecuteAsync($"INSERT INTO _delete (guid) SELECT guid FROM {active.Name} WHERE guid NOT IN (SELECT guid FROM _diff)");
                await conn.ExecuteAsync($"INSERT INTO _delete (guid) SELECT t.guid FROM {active.Name} AS t JOIN _diff AS s ON s.guid = t.guid WHERE s.alterid <> t.alterid");
                await conn.ExecuteAsync($"DELETE FROM {active.Name} WHERE guid IN (SELECT guid FROM _delete)");

                if (active.CascadeDelete != null)
                {
                    foreach (var cd in active.CascadeDelete)
                    {
                        await conn.ExecuteAsync($"DELETE FROM {cd.Table} WHERE {cd.Field} IN (SELECT guid FROM _delete)");
                    }
                }
            }
        }

        public async Task RunPhase2RefetchAsync(
            IEnumerable<TableConfig> masterTables,
            IEnumerable<TableConfig> transactionTables,
            long lastMasterId, long lastTransactionId,
            string companyName, DateTime fromDate, DateTime toDate, DbConnection conn)
        {
            await RefetchTablesAsync(masterTables, lastMasterId, companyName, fromDate, toDate);
            await RefetchTablesAsync(transactionTables, lastTransactionId, companyName, fromDate, toDate);
        }

        private async Task RefetchTablesAsync(IEnumerable<TableConfig> tables, long watermark,
            string companyName, DateTime fromDate, DateTime toDate)
        {
            foreach (var t in tables)
            {
                var filters = new List<string>(t.Filters ?? new List<string>())
                {
                    $"$AlterID > {watermark}"
                };
                var clone = new TableConfig
                {
                    Name = t.Name,
                    Collection = t.Collection,
                    Nature = t.Nature,
                    Fields = t.Fields,
                    Fetch = t.Fetch,
                    Filters = filters,
                    CascadeUpdate = t.CascadeUpdate,
                    CascadeDelete = t.CascadeDelete
                };
                var xml = DynamicTdlXmlGenerator.GenerateXml(clone, companyName,
                    fromDate.ToString("yyyyMMdd"), toDate.ToString("yyyyMMdd"));
                var resp = await _tally.PostXMLAsync(xml);
                var dt = DynamicXmlParser.ParseXml(resp, clone);
                if (dt.Rows.Count > 0)
                {
                    await _loader.LoadBulkDataAsync(dt, t.Name);
                }
            }
        }

        public async Task RunPhase3CascadeUpdateAsync(IEnumerable<TableConfig> primaryTables, DbConnection conn)
        {
            foreach (var active in primaryTables)
            {
                if (active.CascadeUpdate == null) continue;
                foreach (var cu in active.CascadeUpdate)
                {
                    var sql = _loader.CascadeUpdateSql(active.Name, cu.Table, cu.Field);
                    await conn.ExecuteAsync(sql);
                }
            }
        }

        public async Task RunPhase3VoucherRefreshAsync(IEnumerable<TableConfig> transactionTables,
            string companyName, DateTime fromDate, DateTime toDate, DbConnection conn)
        {
            var voucher = transactionTables.FirstOrDefault(t => t.Name == "trn_voucher");
            if (voucher == null) return;

            long count = await conn.ExecuteScalarAsync<long>(_loader.CountAutoNumberVoucherTypesSql());
            if (count == 0) return;

            await conn.ExecuteAsync(_loader.TruncateSql("_vchnumber"));

            var filters = new List<string>(voucher.Filters ?? new List<string>())
            {
                "$$IsEqual:($NumberingMethod:VoucherType:$VoucherTypeName):\"Automatic\""
            };
            var temp = new TableConfig
            {
                Name = "_vchnumber",
                Collection = voucher.Collection,
                Nature = "",
                Fields = new List<FieldConfig>
                {
                    new() { Name = "guid", Field = "Guid", Type = "text" },
                    new() { Name = "voucher_number", Field = "VoucherNumber", Type = "text" }
                },
                Filters = filters
            };
            var xml = DynamicTdlXmlGenerator.GenerateXml(temp, companyName,
                fromDate.ToString("yyyyMMdd"), toDate.ToString("yyyyMMdd"));
            var resp = await _tally.PostXMLAsync(xml);
            var dt = DynamicXmlParser.ParseXml(resp, temp);
            if (dt.Rows.Count > 0)
            {
                await _loader.LoadBulkDataAsync(dt, "_vchnumber");
            }

            await conn.ExecuteAsync(_loader.VoucherNumberUpdateSql());
        }
    }
}
