using System;
using System.Collections.Generic;
using TallyDbLoader.Core.Data;
using TallyDbLoader.Core.Models;
using Xunit;

namespace TallyDbLoader.Tests
{
    public class ConfigImportServiceTests
    {
        private class FakeConfigRepository : IConfigRepository
        {
            public List<DatabaseProfile> DatabaseProfiles { get; set; } = new List<DatabaseProfile>();
            public List<CompanyProfile> CompanyProfiles { get; set; } = new List<CompanyProfile>();

            public List<ResolvedDatabaseProfileImport>? LastDatabaseImports { get; set; }
            public List<ResolvedCompanyProfileImport>? LastCompanyImports { get; set; }
            public string? LastActor { get; set; }
            public string? LastReason { get; set; }
            public string? LastBeforeJson { get; set; }
            public string? LastAfterJson { get; set; }

            public List<DatabaseProfile> GetAllDatabaseProfiles() => DatabaseProfiles;
            public List<CompanyProfile> GetAllCompanyProfiles() => CompanyProfiles;

            public void ImportSanitizedConfig(
                List<ResolvedDatabaseProfileImport> databaseProfiles,
                List<ResolvedCompanyProfileImport> companyProfiles,
                string actor,
                string reason,
                string beforeJson,
                string afterJson)
            {
                LastDatabaseImports = databaseProfiles;
                LastCompanyImports = companyProfiles;
                LastActor = actor;
                LastReason = reason;
                LastBeforeJson = beforeJson;
                LastAfterJson = afterJson;
            }

            public void SaveDatabaseProfile(DatabaseProfile profile) => throw new NotImplementedException();
            public DatabaseProfile? GetDatabaseProfileByName(string name) => throw new NotImplementedException();
            public DatabaseProfile? GetDatabaseProfileById(int id) => throw new NotImplementedException();
            public void SaveCompanyProfile(CompanyProfile company) => throw new NotImplementedException();
            public void DeleteCompanyProfile(int id) => throw new NotImplementedException();
            public TallySettings GetTallySettings() => throw new NotImplementedException();
            public void SaveTallySettings(TallySettings settings) => throw new NotImplementedException();
            public void DeleteDatabaseProfile(int id) => throw new NotImplementedException();
            public long AddSyncRun(SyncRun run) => throw new NotImplementedException();
            public List<SyncRun> GetRecentSyncRuns(int limit = 50) => throw new NotImplementedException();
            public List<SyncRun> GetSyncRunsForCompany(int companyId, int limit = 50) => throw new NotImplementedException();
            public bool TryStartCompanyProfile(int id) => throw new NotImplementedException();
            public void MarkCompanyProfileUnknown(int id, string reason, DateTime now) => throw new NotImplementedException();
            public void CompleteCompanyProfileRun(int id, string finalStatus, DateTime endedAt, int durationMs, long rowsWritten, bool incrementErrorCount) => throw new NotImplementedException();
            public void UpdateSyncRun(SyncRun run) => throw new NotImplementedException();
            public void ReconcileStaleRuns(DateTime now) => throw new NotImplementedException();
            public long ResolveCompanyProfileSafetyState(int companyProfileId, string actor, string reason, DateTime resolvedAt) => throw new NotImplementedException();
        }

        [Fact]
        public void Constructor_Throws_WhenRepositoryIsNull()
        {
            Assert.Throws<ArgumentNullException>(() => new ConfigImportService(null!));
        }

        [Fact]
        public void ImportJson_ThrowsNotImplementedException_Initially()
        {
            var fake = new FakeConfigRepository();
            var service = new ConfigImportService(fake);

            Assert.Throws<NotImplementedException>(() =>
                service.ImportJson("{}", new ImportDecision(), "actor", "reason"));
        }
    }
}
