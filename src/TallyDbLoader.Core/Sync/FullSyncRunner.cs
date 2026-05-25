using System;
using System.Data.Common;
using System.Threading.Tasks;
using TallyDbLoader.Core.DatabaseLoaders;
using TallyDbLoader.Core.Tally;

namespace TallyDbLoader.Core.Sync
{
    public class FullSyncRunner
    {
        private readonly ITallyClient _tally;
        private readonly IDatabaseLoader _loader;

        public FullSyncRunner(ITallyClient tally, IDatabaseLoader loader)
        {
            _tally = tally ?? throw new ArgumentNullException(nameof(tally));
            _loader = loader ?? throw new ArgumentNullException(nameof(loader));
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

                using (var trunc = targetConn.CreateCommand())
                {
                    trunc.CommandText = _loader.TruncateSql(table.Name);
                    trunc.ExecuteNonQuery();
                }

                if (dt.Rows.Count > 0)
                {
                    await _loader.LoadBulkDataAsync(dt, table.Name);
                    total += dt.Rows.Count;
                }
            }
            return total;
        }
    }
}
