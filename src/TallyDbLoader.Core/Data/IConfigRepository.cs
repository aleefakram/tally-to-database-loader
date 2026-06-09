using System.Collections.Generic;
using TallyDbLoader.Core.Models;

namespace TallyDbLoader.Core.Data
{
    public interface IConfigRepository
    {
        void SaveDatabaseProfile(DatabaseProfile profile);
        DatabaseProfile? GetDatabaseProfileByName(string name);
        DatabaseProfile? GetDatabaseProfileById(int id);
        List<DatabaseProfile> GetAllDatabaseProfiles();
        void SaveCompanyProfile(CompanyProfile company);
        List<CompanyProfile> GetAllCompanyProfiles();
        void DeleteCompanyProfile(int id);
        TallySettings GetTallySettings();
        void SaveTallySettings(TallySettings settings);
        void DeleteDatabaseProfile(int id);
        long AddSyncRun(SyncRun run);
        List<SyncRun> GetRecentSyncRuns(int limit = 50);
        List<SyncRun> GetSyncRunsForCompany(int companyId, int limit = 50);

        bool TryStartCompanyProfile(int id);
        void MarkCompanyProfileUnknown(int id, string reason, System.DateTime now);
        void CompleteCompanyProfileRun(
            int id,
            string finalStatus,
            System.DateTime endedAt,
            int durationMs,
            long rowsWritten,
            bool incrementErrorCount);
        void UpdateSyncRun(SyncRun run);
        void ReconcileStaleRuns(System.DateTime now);
        long ResolveCompanyProfileSafetyState(
            int companyProfileId,
            string actor,
            string reason,
            System.DateTime resolvedAt);
    }
}
