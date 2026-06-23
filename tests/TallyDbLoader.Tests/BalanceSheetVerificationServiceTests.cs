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

        [Fact]
        public async Task GenerateAsync_InvalidSchemaOrTablePrefix_ReturnsFailedAndLogsHistory()
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
                    TablePrefix = "123-invalid-prefix",
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

                Assert.Equal("failed", report.Status);
                Assert.Contains("invalid", report.ErrorSummary);
                Assert.Single(repo.GetRecentBalanceSheetVerificationRuns(10));
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                if (File.Exists(configPath)) try { File.Delete(configPath); } catch { }
            }
        }

        [Fact]
        public void InitializeDatabase_UpgradesV5ToV6AndPreservesData()
        {
            string dbPath = Path.Combine(Path.GetTempPath(), $"bs_migration_{Guid.NewGuid()}.db");
            try
            {
                using (var conn = new SqliteConnection($"Data Source={dbPath}"))
                {
                    conn.Open();
                    conn.Execute("CREATE TABLE database_profiles (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT, technology TEXT);");
                    conn.Execute("CREATE TABLE company_profiles (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT, db_profile_id INTEGER, status TEXT);");
                    conn.Execute(@"
                        CREATE TABLE balance_sheet_runs (
                            id INTEGER PRIMARY KEY AUTOINCREMENT,
                            company_id INTEGER NOT NULL REFERENCES company_profiles(id) ON DELETE CASCADE,
                            target_identity TEXT NOT NULL,
                            financial_year_start TEXT NOT NULL,
                            as_at_date TEXT NOT NULL,
                            generated_at TEXT NOT NULL,
                            liability_total TEXT NOT NULL,
                            asset_total TEXT NOT NULL,
                            difference TEXT NOT NULL,
                            balance_tolerance TEXT NOT NULL,
                            status TEXT NOT NULL,
                            warning_summary TEXT,
                            error_summary TEXT
                        );");

                    conn.Execute("INSERT INTO company_profiles (id, name, db_profile_id, status) VALUES (1, 'Test Co', 1, 'idle');");
                    conn.Execute(@"
                        INSERT INTO balance_sheet_runs (
                            id, company_id, target_identity, financial_year_start, as_at_date, generated_at,
                            liability_total, asset_total, difference, balance_tolerance, status, warning_summary, error_summary
                        ) VALUES (
                            101, 1, 'sqlite:dummy:main:', '2025-04-01T00:00:00.0000000', '2025-06-05T00:00:00.0000000', '2025-06-19T00:00:00.0000000',
                            '1000.0000', '1000.0000', '0.0000', '0.0000', 'balanced', NULL, NULL
                        );");

                    conn.Execute("PRAGMA user_version = 5;");
                }

                DatabaseHelper.InitializeDatabase(dbPath);

                using (var conn = new SqliteConnection($"Data Source={dbPath}"))
                {
                    conn.Open();
                    var version = conn.ExecuteScalar<int>("PRAGMA user_version;");
                    Assert.Equal(6, version);

                    var count = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM balance_sheet_runs WHERE id = 101;");
                    Assert.Equal(1, count);

                    var companyId = conn.ExecuteScalar<int>("SELECT company_id FROM balance_sheet_runs WHERE id = 101;");
                    Assert.Equal(1, companyId);

                    conn.Execute(@"
                        INSERT INTO balance_sheet_runs (
                            company_id, target_identity, financial_year_start, as_at_date, generated_at,
                            liability_total, asset_total, difference, balance_tolerance, status, warning_summary, error_summary
                        ) VALUES (
                            NULL, 'unknown:unknown:unknown:unknown', '2025-04-01T00:00:00.0000000', '2025-06-05T00:00:00.0000000', '2025-06-19T00:00:00.0000000',
                            '0.0000', '0.0000', '0.0000', '0.0000', 'failed', NULL, 'Sync Job not found.'
                        );");

                    var nullCount = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM balance_sheet_runs WHERE company_id IS NULL;");
                    Assert.Equal(1, nullCount);
                }
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
            }
        }

        [Fact]
        public async Task GenerateAsync_Adapter_ParsesPositiveStockValueToSignedNegative()
        {
            string targetPath = Path.Combine(Path.GetTempPath(), $"bs_target_{Guid.NewGuid()}.db");
            try
            {
                using (var connection = new SqliteConnection($"Data Source={targetPath}"))
                {
                    connection.Open();
                    connection.Execute("CREATE TABLE IF NOT EXISTS mst_ledger (name TEXT PRIMARY KEY, parent TEXT, opening_balance DECIMAL(17,2));");
                    connection.Execute("CREATE TABLE IF NOT EXISTS mst_group (name TEXT PRIMARY KEY, parent TEXT, primary_group TEXT, is_revenue INTEGER);");
                    connection.Execute("CREATE TABLE IF NOT EXISTS trn_accounting (guid TEXT, ledger TEXT, amount DECIMAL(17,2));");
                    connection.Execute("CREATE TABLE IF NOT EXISTS trn_voucher (guid TEXT PRIMARY KEY, date TEXT, is_order_voucher INTEGER, is_inventory_voucher INTEGER);");
                    connection.Execute("CREATE TABLE IF NOT EXISTS trn_closingstock_ledger (ledger TEXT, stock_date TEXT, stock_value DECIMAL(17,2));");

                    connection.Execute("INSERT INTO mst_group (name, parent, primary_group, is_revenue) VALUES ('Stock', '', 'Stock-in-hand', 0);");
                    connection.Execute("INSERT INTO mst_ledger (name, parent, opening_balance) VALUES ('Inventory', 'Stock', 0.00);");

                    // Seed raw positive closing stock (which becomes opening stock for period starting 2025-05-01)
                    connection.Execute("INSERT INTO trn_closingstock_ledger (ledger, stock_date, stock_value) VALUES ('Inventory', '2025-04-30', 2000.00);");
                }

                var request = new BalanceSheetVerificationRequest
                {
                    CompanyProfileId = 1,
                    FinancialYearStart = new DateTime(2025, 5, 1),
                    AsAtDate = new DateTime(2025, 6, 1)
                };

                var adapter = new SqliteBalanceSheetQueryAdapter();
                using (var connection = new SqliteConnection($"Data Source={targetPath}"))
                {
                    connection.Open();
                    var raw = await adapter.QueryAsync(connection, BalanceSheetTableNames.Create("main", "", "sqlite"), request, CancellationToken.None);
                    var stockLedger = raw.Ledgers.Single(l => l.LedgerName == "Inventory");

                    // Assert positive database stock_value 2000.00 became signed negative -2000.00 in C# OpeningStockValue
                    Assert.Equal(-2000m, stockLedger.OpeningStockValue);
                }
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                if (File.Exists(targetPath)) try { File.Delete(targetPath); } catch { }
            }
        }

        [Fact]
        public async Task GenerateAsync_SeededDb_ComputesOpeningDifferenceAndStockClosing()
        {
            string configPath = Path.Combine(Path.GetTempPath(), $"bs_config_{Guid.NewGuid()}.db");
            string targetPath = Path.Combine(Path.GetTempPath(), $"bs_target_{Guid.NewGuid()}.db");
            try
            {
                DatabaseHelper.InitializeDatabase(configPath);

                using (var connection = new SqliteConnection($"Data Source={targetPath}"))
                {
                    connection.Open();
                    connection.Execute("CREATE TABLE IF NOT EXISTS mst_ledger (name TEXT PRIMARY KEY, parent TEXT, opening_balance DECIMAL(17,2));");
                    connection.Execute("CREATE TABLE IF NOT EXISTS mst_group (name TEXT PRIMARY KEY, parent TEXT, primary_group TEXT, is_revenue INTEGER);");
                    connection.Execute("CREATE TABLE IF NOT EXISTS trn_accounting (guid TEXT, ledger TEXT, amount DECIMAL(17,2));");
                    connection.Execute("CREATE TABLE IF NOT EXISTS trn_voucher (guid TEXT PRIMARY KEY, date TEXT, is_order_voucher INTEGER, is_inventory_voucher INTEGER);");
                    connection.Execute("CREATE TABLE IF NOT EXISTS trn_closingstock_ledger (ledger TEXT, stock_date TEXT, stock_value DECIMAL(17,2));");

                    connection.Execute("INSERT INTO mst_group (name, parent, primary_group, is_revenue) VALUES ('Capital', '', 'Capital Account', 0);");
                    connection.Execute("INSERT INTO mst_group (name, parent, primary_group, is_revenue) VALUES ('Stock', '', 'Stock-in-hand', 0);");
                    
                    connection.Execute("INSERT INTO mst_ledger (name, parent, opening_balance) VALUES ('Equity', 'Capital', 5000.00);"); // Credit
                    connection.Execute("INSERT INTO mst_ledger (name, parent, opening_balance) VALUES ('Inventory', 'Stock', 0.00);");

                    // Seed raw positive closing stock (which becomes opening stock for period starting 2025-05-01)
                    connection.Execute("INSERT INTO trn_closingstock_ledger (ledger, stock_date, stock_value) VALUES ('Inventory', '2025-04-30', 2000.00);");
                }

                var repo = new ConfigRepository(configPath);
                repo.SaveDatabaseProfile(new DatabaseProfile { Name = "SQLite Target", Technology = "sqlite" });
                var db = repo.GetDatabaseProfileByName("SQLite Target");
                Assert.NotNull(db);

                repo.SaveCompanyProfile(new CompanyProfile
                {
                    Name = "Test Company",
                    DbProfileId = db.Id,
                    TargetCatalog = targetPath,
                    Schema = "main",
                    TablePrefix = string.Empty,
                    BooksFrom = new DateTime(2025, 4, 1),
                    BooksTo = new DateTime(2025, 6, 5),
                    Status = "idle"
                });
                var company = repo.GetAllCompanyProfiles()[0];

                var request = new BalanceSheetVerificationRequest
                {
                    CompanyProfileId = company.Id,
                    FinancialYearStart = new DateTime(2025, 5, 1),
                    AsAtDate = new DateTime(2025, 6, 1)
                };

                var service = new BalanceSheetVerificationService(repo);
                var report = await service.GenerateAsync(request, CancellationToken.None);

                Assert.NotNull(report);
                Assert.True(report.Status == "balanced", report.ErrorSummary ?? "No error summary");

                // Opening Difference (including P&L opening 2000 credit): Credit 5000 + Credit 2000 (P&L) - Debit 2000 (stock_value) = 5000 Credit surplus. Should show as 5000 Debit Difference on Assets.
                var diffLine = report.AssetSide.Lines.FirstOrDefault(l => l.Name == "Difference in opening balances");
                Assert.NotNull(diffLine);
                Assert.Equal(5000m, diffLine.Amount);

                // Verify Stock-in-hand routes to Assets side
                using (var connection = new SqliteConnection($"Data Source={targetPath}"))
                {
                    connection.Open();
                    connection.Execute("INSERT INTO trn_closingstock_ledger (ledger, stock_date, stock_value) VALUES ('Inventory', '2025-06-01', 2500.00);");
                }
                
                report = await service.GenerateAsync(request, CancellationToken.None);
                var stockLine = report.AssetSide.Lines.FirstOrDefault(l => l.Name == "Stock-in-hand");
                Assert.NotNull(stockLine);
                Assert.Equal(2500m, stockLine.Amount);

                // Assert report remains balanced and difference line is correct after closing stock change
                Assert.True(report.Status == "balanced", report.ErrorSummary ?? "No error summary");
                var diffLineAfter = report.AssetSide.Lines.FirstOrDefault(l => l.Name == "Difference in opening balances");
                Assert.NotNull(diffLineAfter);
                Assert.Equal(5000m, diffLineAfter.Amount);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                if (File.Exists(configPath)) try { File.Delete(configPath); } catch { }
                if (File.Exists(targetPath)) try { File.Delete(targetPath); } catch { }
            }
        }

        [Fact]
        public async Task GenerateAsync_SeededDb_BalancedPrePeriodStock_NoOpeningDifference()
        {
            string configPath = Path.Combine(Path.GetTempPath(), $"bs_config_{Guid.NewGuid()}.db");
            string targetPath = Path.Combine(Path.GetTempPath(), $"bs_target_{Guid.NewGuid()}.db");
            try
            {
                DatabaseHelper.InitializeDatabase(configPath);

                using (var connection = new SqliteConnection($"Data Source={targetPath}"))
                {
                    connection.Open();
                    connection.Execute("CREATE TABLE IF NOT EXISTS mst_ledger (name TEXT PRIMARY KEY, parent TEXT, opening_balance DECIMAL(17,2));");
                    connection.Execute("CREATE TABLE IF NOT EXISTS mst_group (name TEXT PRIMARY KEY, parent TEXT, primary_group TEXT, is_revenue INTEGER);");
                    connection.Execute("CREATE TABLE IF NOT EXISTS trn_accounting (guid TEXT, ledger TEXT, amount DECIMAL(17,2));");
                    connection.Execute("CREATE TABLE IF NOT EXISTS trn_voucher (guid TEXT PRIMARY KEY, date TEXT, is_order_voucher INTEGER, is_inventory_voucher INTEGER);");
                    connection.Execute("CREATE TABLE IF NOT EXISTS trn_closingstock_ledger (ledger TEXT, stock_date TEXT, stock_value DECIMAL(17,2));");

                    connection.Execute("INSERT INTO mst_group (name, parent, primary_group, is_revenue) VALUES ('Capital', '', 'Capital Account', 0);");
                    connection.Execute("INSERT INTO mst_group (name, parent, primary_group, is_revenue) VALUES ('Stock', '', 'Stock-in-hand', 0);");
                    
                    // Seed balanced opening state: Capital 5000 (credit) + Stock 2000 (debit) + Bank 5000 (debit)
                    // P&L Opening (credit) derived from stock opening is 2000.
                    // Total Opening = 5000 (Capital) + 2000 (P&L) - 2000 (Stock) - 5000 (Bank) = 0.
                    connection.Execute("INSERT INTO mst_ledger (name, parent, opening_balance) VALUES ('Equity', 'Capital', 5000.00);");
                    connection.Execute("INSERT INTO mst_ledger (name, parent, opening_balance) VALUES ('Inventory', 'Stock', 0.00);");

                    // Seed raw positive closing stock (Debit 2000)
                    connection.Execute("INSERT INTO trn_closingstock_ledger (ledger, stock_date, stock_value) VALUES ('Inventory', '2025-04-30', 2000.00);");

                    // Seed balancing pre-period transaction (Debit 5000 to Assets to balance Capital + P&L)
                    connection.Execute("INSERT INTO mst_group (name, parent, primary_group, is_revenue) VALUES ('Assets', '', 'Current Assets', 0);");
                    connection.Execute("INSERT INTO mst_ledger (name, parent, opening_balance) VALUES ('Bank', 'Assets', -5000.00);");
                }

                var repo = new ConfigRepository(configPath);
                repo.SaveDatabaseProfile(new DatabaseProfile { Name = "SQLite Target", Technology = "sqlite" });
                var db = repo.GetDatabaseProfileByName("SQLite Target");
                Assert.NotNull(db);

                repo.SaveCompanyProfile(new CompanyProfile
                {
                    Name = "Test Company",
                    DbProfileId = db.Id,
                    TargetCatalog = targetPath,
                    Schema = "main",
                    TablePrefix = string.Empty,
                    BooksFrom = new DateTime(2025, 4, 1),
                    BooksTo = new DateTime(2025, 6, 5),
                    Status = "idle"
                });
                var company = repo.GetAllCompanyProfiles()[0];

                var request = new BalanceSheetVerificationRequest
                {
                    CompanyProfileId = company.Id,
                    FinancialYearStart = new DateTime(2025, 5, 1),
                    AsAtDate = new DateTime(2025, 6, 1)
                };

                var service = new BalanceSheetVerificationService(repo);
                var report = await service.GenerateAsync(request, CancellationToken.None);

                Assert.NotNull(report);
                Assert.True(report.Status == "balanced", report.ErrorSummary ?? "No error summary");

                // Total Opening = 0. No difference line should exist on either Assets or Liabilities side.
                var diffLineAsset = report.AssetSide.Lines.FirstOrDefault(l => l.Name == "Difference in opening balances");
                var diffLineLiab = report.LiabilitySide.Lines.FirstOrDefault(l => l.Name == "Difference in opening balances");
                Assert.Null(diffLineAsset);
                Assert.Null(diffLineLiab);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                if (File.Exists(configPath)) try { File.Delete(configPath); } catch { }
                if (File.Exists(targetPath)) try { File.Delete(targetPath); } catch { }
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
