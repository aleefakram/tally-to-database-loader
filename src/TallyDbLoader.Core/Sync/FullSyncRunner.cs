using System;
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
            long total = 0;
            var all = new System.Collections.Generic.List<TableConfig>();
            all.AddRange(config.Master);
            all.AddRange(config.Transaction);

            foreach (var table in all)
            {
                var xml = DynamicTdlXmlGenerator.GenerateXml(table, companyName,
                    fromDate.ToString("yyyyMMdd"), toDate.ToString("yyyyMMdd"));
                var response = await _tally.PostXMLAsync(xml);
                var dt = DynamicXmlParser.ParseXml(response, table);

                var promotedCount = await _promoter.StageValidateAndPromoteAsync(dt, table, targetConn);
                total += promotedCount;
            }
            return total;
        }
    }
}
