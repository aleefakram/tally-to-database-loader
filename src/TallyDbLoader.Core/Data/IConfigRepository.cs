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
        void AddSyncRun(SyncRun run);
        List<SyncRun> GetRecentSyncRuns(int limit = 50);
        List<SyncRun> GetSyncRunsForCompany(int companyId, int limit = 50);
    }
}
