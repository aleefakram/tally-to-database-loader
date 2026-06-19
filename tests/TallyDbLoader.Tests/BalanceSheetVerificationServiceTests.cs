using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using TallyDbLoader.Core.Data;
using TallyDbLoader.Core.Models;
using TallyDbLoader.Core.Reports;
using Xunit;

namespace TallyDbLoader.Tests
{
    public class BalanceSheetVerificationServiceTests
    {
        [Fact]
        public async Task GenerateAsync_WithSqliteTarget_ReturnsBalancedReportAndRecordsHistory()
        {
            string configPath = Path.Combine(Path.GetTempPath(), $"bs_config_{Guid.NewGuid()}.db");
            string targetPath = Path.Combine(Path.GetTempPath(), $"bs_target_{Guid.NewGuid()}.db");
            try
            {
                DatabaseHelper.InitializeDatabase(configPath);
                SeedTarget(targetPath);

                var repo = new ConfigRepository(configPath);
                repo.SaveDatabaseProfile(new DatabaseProfile { Name = "SQLite Target", Technology = "sqlite" });
                var db = repo.GetDatabaseProfileByName("SQLite Target");
                Assert.NotNull(db);

                repo.SaveCompanyProfile(new CompanyProfile
                {
                    Name = "Demo Co",
                    DbProfileId = db.Id,
                    TargetCatalog = targetPath,
                    Schema = "main",
                    TablePrefix = string.Empty,
                    BooksFrom = new DateTime(2025, 4, 1),
                    BooksTo = new DateTime(2025, 6, 5),
                    Status = "idle"
                });
                var company = repo.GetAllCompanyProfiles()[0];

                var service = new BalanceSheetVerificationService(repo);
                var report = await service.GenerateAsync(new BalanceSheetVerificationRequest
                {
                    CompanyProfileId = company.Id,
                    FinancialYearStart = new DateTime(2025, 4, 1),
                    AsAtDate = new DateTime(2025, 6, 5)
                }, CancellationToken.None);

                Assert.Equal("balanced", report.Status);
                Assert.Equal(1000m, report.LiabilityTotal);
                Assert.Equal(1000m, report.AssetTotal);
                Assert.Single(repo.GetRecentBalanceSheetVerificationRuns(10));
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                if (File.Exists(configPath)) try { File.Delete(configPath); } catch { }
                if (File.Exists(targetPath)) try { File.Delete(targetPath); } catch { }
            }
        }

        [Fact]
        public async Task GenerateAsync_DebitOnlyTransfer_CalculatesLessTransferredFromRealDb()
        {
            string configPath = Path.Combine(Path.GetTempPath(), $"bs_config_{Guid.NewGuid()}.db");
            string targetPath = Path.Combine(Path.GetTempPath(), $"bs_target_{Guid.NewGuid()}.db");
            try
            {
                DatabaseHelper.InitializeDatabase(configPath);

                using (var conn = new SqliteConnection($"Data Source={targetPath}"))
                {
                    conn.Open();
                    conn.Execute("CREATE TABLE mst_group (name TEXT, parent TEXT, primary_group TEXT, is_revenue INTEGER);");
                    conn.Execute("CREATE TABLE mst_ledger (name TEXT, parent TEXT, opening_balance DECIMAL(17,2));");
                    conn.Execute("CREATE TABLE trn_voucher (guid TEXT, date DATE, is_order_voucher INTEGER, is_inventory_voucher INTEGER);");
                    conn.Execute("CREATE TABLE trn_accounting (guid TEXT, ledger TEXT, amount DECIMAL(17,2));");
                    conn.Execute("CREATE TABLE trn_closingstock_ledger (ledger TEXT, stock_date DATE, stock_value DECIMAL(17,2));");

                    conn.Execute("INSERT INTO mst_group (name, parent, primary_group, is_revenue) VALUES ('Capital Account', '', 'Capital Account', 0), ('Current Assets', '', 'Current Assets', 0);");
                    conn.Execute("INSERT INTO mst_ledger (name, parent, opening_balance) VALUES ('Capital', 'Capital Account', 1000), ('Cash', 'Current Assets', -1000), ('Profit & Loss A/c', 'Capital Account', 0);");

                    conn.Execute("INSERT INTO trn_voucher (guid, date, is_order_voucher, is_inventory_voucher) VALUES ('v1', '2025-05-01', 0, 0);");
                    conn.Execute("INSERT INTO trn_accounting (guid, ledger, amount) VALUES ('v1', 'Profit & Loss A/c', -150);");

                    conn.Execute("INSERT INTO trn_voucher (guid, date, is_order_voucher, is_inventory_voucher) VALUES ('v2', '2025-05-15', 0, 0);");
                    conn.Execute("INSERT INTO trn_accounting (guid, ledger, amount) VALUES ('v2', 'Profit & Loss A/c', 50);");
                }

                var repo = new ConfigRepository(configPath);
                repo.SaveDatabaseProfile(new DatabaseProfile { Name = "SQLite Target", Technology = "sqlite" });
                var db = repo.GetDatabaseProfileByName("SQLite Target");
                Assert.NotNull(db);

                repo.SaveCompanyProfile(new CompanyProfile
                {
                    Name = "Demo Co",
                    DbProfileId = db.Id,
                    TargetCatalog = targetPath,
                    Schema = "main",
                    TablePrefix = string.Empty,
                    BooksFrom = new DateTime(2025, 4, 1),
                    BooksTo = new DateTime(2025, 6, 5),
                    Status = "idle"
                });
                var company = repo.GetAllCompanyProfiles()[0];

                var service = new BalanceSheetVerificationService(repo);
                var report = await service.GenerateAsync(new BalanceSheetVerificationRequest
                {
                    CompanyProfileId = company.Id,
                    FinancialYearStart = new DateTime(2025, 4, 1),
                    AsAtDate = new DateTime(2025, 6, 5)
                }, CancellationToken.None);

                Assert.Equal(150m, report.ProfitAndLoss.LessTransferred);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                if (File.Exists(configPath)) try { File.Delete(configPath); } catch { }
                if (File.Exists(targetPath)) try { File.Delete(targetPath); } catch { }
            }
        }

        [Fact]
        public async Task GenerateAsync_InvalidDateRange_ReturnsFailedAndLogsHistory()
        {
            string configPath = Path.Combine(Path.GetTempPath(), $"bs_config_{Guid.NewGuid()}.db");
            try
            {
                DatabaseHelper.InitializeDatabase(configPath);
                var repo = new ConfigRepository(configPath);
                repo.SaveDatabaseProfile(new DatabaseProfile { Name = "SQLite Target", Technology = "sqlite" });
                var db = repo.GetDatabaseProfileByName("SQLite Target");
                repo.SaveCompanyProfile(new CompanyProfile
                {
                    Name = "Demo Co",
                    DbProfileId = db!.Id,
                    TargetCatalog = "dummy",
                    Schema = "main",
                    TablePrefix = string.Empty,
                    BooksFrom = new DateTime(2025, 4, 1),
                    BooksTo = new DateTime(2025, 6, 5),
                    Status = "idle"
                });
                var company = repo.GetAllCompanyProfiles()[0];

                var service = new BalanceSheetVerificationService(repo);
                var report = await service.GenerateAsync(new BalanceSheetVerificationRequest
                {
                    CompanyProfileId = company.Id,
                    FinancialYearStart = new DateTime(2025, 4, 1),
                    AsAtDate = new DateTime(2025, 3, 31)
                }, CancellationToken.None);

                Assert.Equal("failed", report.Status);
                Assert.Contains("cannot be before Financial Year Start", report.ErrorSummary);
                Assert.Single(repo.GetRecentBalanceSheetVerificationRuns(10));
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                if (File.Exists(configPath)) try { File.Delete(configPath); } catch { }
            }
        }

        [Fact]
        public async Task GenerateAsync_MissingCompany_ReturnsFailedAndLogsNullCompanyId()
        {
            string configPath = Path.Combine(Path.GetTempPath(), $"bs_config_{Guid.NewGuid()}.db");
            try
            {
                DatabaseHelper.InitializeDatabase(configPath);
                var repo = new ConfigRepository(configPath);
                var service = new BalanceSheetVerificationService(repo);

                var report = await service.GenerateAsync(new BalanceSheetVerificationRequest
                {
                    CompanyProfileId = 9999,
                    FinancialYearStart = new DateTime(2025, 4, 1),
                    AsAtDate = new DateTime(2025, 6, 5)
                }, CancellationToken.None);

                Assert.Equal("failed", report.Status);
                Assert.Contains("Sync Job with ID 9999 was not found", report.ErrorSummary);

                var runs = repo.GetRecentBalanceSheetVerificationRuns(10);
                Assert.Single(runs);
                Assert.Null(runs[0].CompanyProfileId);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                if (File.Exists(configPath)) try { File.Delete(configPath); } catch { }
            }
        }

        private static void SeedTarget(string path)
        {
            using var conn = new SqliteConnection($"Data Source={path}");
            conn.Open();
            conn.Execute("CREATE TABLE mst_group (name TEXT, parent TEXT, primary_group TEXT, is_revenue INTEGER);");
            conn.Execute("CREATE TABLE mst_ledger (name TEXT, parent TEXT, opening_balance DECIMAL(17,2));");
            conn.Execute("CREATE TABLE trn_voucher (guid TEXT, date DATE, is_order_voucher INTEGER, is_inventory_voucher INTEGER);");
            conn.Execute("CREATE TABLE trn_accounting (guid TEXT, ledger TEXT, amount DECIMAL(17,2));");
            conn.Execute("CREATE TABLE trn_closingstock_ledger (ledger TEXT, stock_date DATE, stock_value DECIMAL(17,2));");

            conn.Execute("INSERT INTO mst_group (name, parent, primary_group, is_revenue) VALUES ('Capital Account', '', 'Capital Account', 0), ('Current Assets', '', 'Current Assets', 0);");
            conn.Execute("INSERT INTO mst_ledger (name, parent, opening_balance) VALUES ('Capital', 'Capital Account', 1000), ('Cash', 'Current Assets', -1000);");
        }
    }
}
