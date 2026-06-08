using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading.Tasks;
using TallyDbLoader.Core.Tally;

namespace TallyDbLoader.Core.Sync
{
    public class FullSyncRunner
    {
        private readonly ITallyClient _tally;
        private readonly IFullSyncTablePromoter _promoter;

        public FullSyncRunner(ITallyClient tally, IFullSyncTablePromoter promoter)
        {
            _tally = tally ?? throw new ArgumentNullException(nameof(tally));
            _promoter = promoter ?? throw new ArgumentNullException(nameof(promoter));
        }

        public async Task<long> Run(TallyExportConfig config, string companyName,
            DateTime fromDate, DateTime toDate, DbConnection targetConn)
        {
            var all = new List<TableConfig>();
            all.AddRange(config.Master);
            all.AddRange(config.Transaction);

            var stagedTables = new List<TableConfig>();
            var stageResults = new Dictionary<TableConfig, StageResult>();
            // 0. Centralized SQL identifier validation
            var provider = targetConn.GetType().Name;
            foreach (var table in all)
            {
                DbIdentifierPolicy.ValidateTableConfig(table, provider);
            }

            long totalRows = 0;
            try
            {
                // 1. Fetch, Parse, and Stage one-by-one (outside transaction) to optimize memory
                foreach (var table in all)
                {
                    var xml = DynamicTdlXmlGenerator.GenerateXml(table, companyName,
                        fromDate.ToString("yyyyMMdd"), toDate.ToString("yyyyMMdd"));
                    var response = await _tally.PostXMLAsync(xml);
                    var dt = DynamicXmlParser.ParseXml(response, table);

                    var stageResult = await _promoter.StageAsync(dt, table, targetConn);
                    stagedTables.Add(table);
                    stageResults[table] = stageResult;
                    totalRows += stageResult.RowCount;
                }

                // 2. Validate all staging tables (outside transaction)
                foreach (var table in stagedTables)
                {
                    await _promoter.ValidateStagingAsync(table, targetConn);
                }

                // 3. Promote all staged tables (inside short transaction)
                using (var transaction = targetConn.BeginTransaction())
                {
                    try
                      {
                        foreach (var table in stagedTables)
                        {
                            var result = stageResults[table];
                            await _promoter.PromoteStagedAsync(table, result.Columns, targetConn, transaction);
                        }
                        transaction.Commit();
                    }
                    catch
                    {
                        transaction.Rollback();
                        throw;
                    }
                }
            }
            finally
            {
                // 4. Cleanup staging tables (best-effort, outside transaction)
                foreach (var table in stagedTables)
                {
                    try
                    {
                        await _promoter.CleanupStagingAsync(table, targetConn);
                    }
                    catch (Exception ex)
                    {
                        TallyDbLoader.Core.Logging.FileLogger.LogMessage($"[Staging Cleanup Warning] Failed to drop staging table for '{table.Name}': {ex.Message}");
                    }
                }
            }

            return totalRows;
        }
    }
}
