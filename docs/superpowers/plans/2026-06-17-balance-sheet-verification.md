# Balance Sheet Verification Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a manual WPF Balance Sheet Verification view that computes a Tally-style top-level Balance Sheet from synced target database data.

**Architecture:** Put all accounting and database logic in `TallyDbLoader.Core`. Use focused Core report models, provider query adapters, and a `BalanceSheetVerificationService`; WPF only binds inputs, invokes the async service, and formats the report. Store lightweight verification history in the local SQLite config database through `IConfigRepository`.

**Tech Stack:** .NET 8, C#, WPF, SQLite config DB, Dapper, Microsoft.Data.Sqlite, Microsoft.Data.SqlClient, Npgsql, MySqlConnector, xUnit.

---

## Reference

Approved design spec: `docs/superpowers/specs/2026-06-17-balance-sheet-verification-design.md`

Default verification command:

```powershell
dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore
```

## File Structure

Create:

- `src/TallyDbLoader.Core/Models/BalanceSheetModels.cs`: immutable-ish DTOs for report inputs, output, options, raw ledger rows, and history rows.
- `src/TallyDbLoader.Core/Reports/BalanceSheetTableNames.cs`: validates schema/table prefix and exposes physical table names for provider adapters.
- `src/TallyDbLoader.Core/Reports/IBalanceSheetQueryAdapter.cs`: async adapter contract and SQL generation contract.
- `src/TallyDbLoader.Core/Reports/BalanceSheetQueryAdapters.cs`: SQLite, MSSQL, PostgreSQL, and MySQL adapter implementations.
- `src/TallyDbLoader.Core/Reports/BalanceSheetCalculator.cs`: pure accounting transformation from raw ledger rows into the final two-sided report.
- `src/TallyDbLoader.Core/Reports/BalanceSheetVerificationService.cs`: orchestration service that loads profiles, opens target DB, invokes adapter/calculator, and records local history.
- `src/TallyDbLoader.Wpf/Converters/IndianCurrencyConverter.cs`: Indian digit grouping for report amounts.
- `src/TallyDbLoader.Wpf/Views/BalanceSheetVerificationPage.xaml`: report UI.
- `src/TallyDbLoader.Wpf/Views/BalanceSheetVerificationPage.xaml.cs`: page constructor.
- `tests/TallyDbLoader.Tests/BalanceSheetCalculatorTests.cs`: pure accounting tests.
- `tests/TallyDbLoader.Tests/BalanceSheetQueryAdapterTests.cs`: SQL/identifier/provider adapter tests.
- `tests/TallyDbLoader.Tests/BalanceSheetVerificationServiceTests.cs`: SQLite integration-style service tests.
- `tests/TallyDbLoader.Tests/IndianCurrencyConverterTests.cs`: WPF converter tests.

Modify:

- `src/TallyDbLoader.Core/Data/DatabaseHelper.cs`: add local SQLite migration `user_version = 5` and `balance_sheet_verification_runs` table.
- `src/TallyDbLoader.Core/Data/IConfigRepository.cs`: add company profile lookup and Balance Sheet history methods.
- `src/TallyDbLoader.Core/Data/ConfigRepository.cs`: implement new repository methods.
- `src/TallyDbLoader.Core/Data/DatabaseWriter.cs`: add async target `DbConnection` factory used by the report service.
- `src/TallyDbLoader.Wpf/App.xaml`: register `IndianCurrencyConverter`.
- `src/TallyDbLoader.Wpf/MainViewModel.cs`: add route, state, command, defaults, and async run method.
- `src/TallyDbLoader.Wpf/MainWindow.xaml`: add navigation button.
- `src/TallyDbLoader.Wpf/MainWindow.xaml.cs`: route to `BalanceSheetVerificationPage`.
- `src/TallyDbLoader.Wpf/Converters/StatusToToneConverter.cs`: recognize `balanced` and `out_of_balance`.
- `tests/TallyDbLoader.Tests/ConfigRepositoryTests.cs`: cover schema migration/history persistence.
- `tests/TallyDbLoader.Tests/MainViewModelTests.cs`: cover command wiring and defaults.
- `tests/TallyDbLoader.Tests/DatabaseLoaderTests.cs`: cover SQLite async connection factory.

---

### Task 1: Core Models and Options

**Files:**
- Create: `src/TallyDbLoader.Core/Models/BalanceSheetModels.cs`
- Create: `tests/TallyDbLoader.Tests/BalanceSheetModelsTests.cs`

- [ ] **Step 1: Write the failing options/model test**

Create `tests/TallyDbLoader.Tests/BalanceSheetModelsTests.cs`:

```csharp
using System;
using System.Linq;
using TallyDbLoader.Core.Models;
using Xunit;

namespace TallyDbLoader.Tests
{
    public class BalanceSheetModelsTests
    {
        [Fact]
        public void BalanceSheetVerificationOptions_Defaults_MatchSpec()
        {
            var options = new BalanceSheetVerificationOptions();

            Assert.Equal(0.05m, options.BalanceTolerance);
            Assert.Equal("Profit & Loss A/c", options.ProfitAndLossLedgerName);
        }

        [Fact]
        public void BalanceSheetReport_Difference_UsesAbsoluteSideDifference()
        {
            var report = new BalanceSheetReport
            {
                LiabilitySide = new BalanceSheetSide
                {
                    Title = "Liabilities",
                    Lines =
                    {
                        new BalanceSheetLine { Name = "Capital Account", Amount = 100m }
                    }
                },
                AssetSide = new BalanceSheetSide
                {
                    Title = "Assets",
                    Lines =
                    {
                        new BalanceSheetLine { Name = "Fixed Assets", Amount = 98m }
                    }
                }
            };

            Assert.Equal(100m, report.LiabilityTotal);
            Assert.Equal(98m, report.AssetTotal);
            Assert.Equal(2m, report.Difference);
        }
    }
}
```

- [ ] **Step 2: Run model tests to verify they fail**

Run:

```powershell
dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore --filter BalanceSheetModelsTests
```

Expected: fail because `BalanceSheetVerificationOptions`, `BalanceSheetReport`, `BalanceSheetSide`, and `BalanceSheetLine` do not exist.

- [ ] **Step 3: Add the Core model file**

Create `src/TallyDbLoader.Core/Models/BalanceSheetModels.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace TallyDbLoader.Core.Models
{
    public class BalanceSheetVerificationOptions
    {
        public decimal BalanceTolerance { get; set; } = 0.05m;
        public string ProfitAndLossLedgerName { get; set; } = "Profit & Loss A/c";
    }

    public class BalanceSheetVerificationRequest
    {
        public int CompanyProfileId { get; set; }
        public DateTime FinancialYearStart { get; set; }
        public DateTime AsAtDate { get; set; }
        public BalanceSheetVerificationOptions Options { get; set; } = new BalanceSheetVerificationOptions();
    }

    public class BalanceSheetReport
    {
        public int CompanyProfileId { get; set; }
        public string CompanyName { get; set; } = string.Empty;
        public DateTime FinancialYearStart { get; set; }
        public DateTime AsAtDate { get; set; }
        public BalanceSheetSide LiabilitySide { get; set; } = new BalanceSheetSide { Title = "Liabilities" };
        public BalanceSheetSide AssetSide { get; set; } = new BalanceSheetSide { Title = "Assets" };
        public ProfitAndLossBreakdown ProfitAndLoss { get; set; } = new ProfitAndLossBreakdown();
        public string Status { get; set; } = "failed";
        public decimal BalanceTolerance { get; set; } = 0.05m;
        public List<string> Warnings { get; set; } = new List<string>();
        public string? ErrorSummary { get; set; }
        public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

        public decimal LiabilityTotal => LiabilitySide.Total;
        public decimal AssetTotal => AssetSide.Total;
        public decimal Difference => Math.Abs(LiabilityTotal - AssetTotal);
    }

    public class BalanceSheetSide
    {
        public string Title { get; set; } = string.Empty;
        public List<BalanceSheetLine> Lines { get; set; } = new List<BalanceSheetLine>();
        public decimal Total
        {
            get
            {
                decimal total = 0m;
                foreach (var line in Lines)
                {
                    total += line.Amount;
                }
                return total;
            }
        }
    }

    public class BalanceSheetLine
    {
        public string Name { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public bool IsEmphasis { get; set; }
    }

    public class ProfitAndLossBreakdown
    {
        public decimal OpeningBalance { get; set; }
        public decimal CurrentPeriod { get; set; }
        public decimal LessTransferred { get; set; }
        public decimal NetAmount => OpeningBalance + CurrentPeriod - LessTransferred;
    }

    public class BalanceSheetLedgerRow
    {
        public string LedgerName { get; set; } = string.Empty;
        public string ParentGroupName { get; set; } = string.Empty;
        public string PrimaryGroup { get; set; } = string.Empty;
        public bool IsRevenue { get; set; }
        public decimal OpeningBalance { get; set; }
        public decimal PrePeriodMovement { get; set; }
        public decimal CurrentPeriodMovement { get; set; }
        public decimal ClosingStockValue { get; set; }
        public bool HasClosingStockValue { get; set; }
    }

    public class BalanceSheetRawData
    {
        public List<BalanceSheetLedgerRow> Ledgers { get; set; } = new List<BalanceSheetLedgerRow>();
        public bool HasClosingStockTable { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public class BalanceSheetVerificationRun
    {
        public long Id { get; set; }
        public int CompanyProfileId { get; set; }
        public string TargetIdentity { get; set; } = string.Empty;
        public DateTime FinancialYearStart { get; set; }
        public DateTime AsAtDate { get; set; }
        public DateTime GeneratedAt { get; set; }
        public decimal LiabilityTotal { get; set; }
        public decimal AssetTotal { get; set; }
        public decimal Difference { get; set; }
        public decimal BalanceTolerance { get; set; }
        public string Status { get; set; } = string.Empty;
        public string? WarningSummary { get; set; }
        public string? ErrorSummary { get; set; }
    }
}
```

- [ ] **Step 4: Run model tests**

Run:

```powershell
dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore --filter BalanceSheetModelsTests
```

Expected: pass.

- [ ] **Step 5: Commit**

```powershell
git add src/TallyDbLoader.Core/Models/BalanceSheetModels.cs tests/TallyDbLoader.Tests/BalanceSheetModelsTests.cs
git commit -m "feat: add balance sheet report models"
```

---

### Task 2: Local History Schema and Repository

**Files:**
- Modify: `src/TallyDbLoader.Core/Data/DatabaseHelper.cs`
- Modify: `src/TallyDbLoader.Core/Data/IConfigRepository.cs`
- Modify: `src/TallyDbLoader.Core/Data/ConfigRepository.cs`
- Modify: `tests/TallyDbLoader.Tests/ConfigRepositoryTests.cs`

- [ ] **Step 1: Write failing repository tests**

Append these tests to `tests/TallyDbLoader.Tests/ConfigRepositoryTests.cs`:

```csharp
[Fact]
public void InitializeDatabase_CreatesBalanceSheetVerificationRunsTable()
{
    string path = Path.Combine(Path.GetTempPath(), $"bs_schema_{Guid.NewGuid()}.db");
    try
    {
        DatabaseHelper.InitializeDatabase(path);
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
        conn.Open();

        int count = conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'balance_sheet_verification_runs';");
        int version = conn.ExecuteScalar<int>("PRAGMA user_version;");

        Assert.Equal(1, count);
        Assert.True(version >= 5);
    }
    finally
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(path)) try { File.Delete(path); } catch { }
    }
}

[Fact]
public void AddBalanceSheetVerificationRun_PersistsTotalsAndStatus()
{
    string path = Path.Combine(Path.GetTempPath(), $"bs_history_{Guid.NewGuid()}.db");
    try
    {
        DatabaseHelper.InitializeDatabase(path);
        var repo = new ConfigRepository(path);

        long id = repo.AddBalanceSheetVerificationRun(new BalanceSheetVerificationRun
        {
            CompanyProfileId = 12,
            TargetIdentity = "sqlite:sample.db:main:tally_",
            FinancialYearStart = new DateTime(2025, 4, 1),
            AsAtDate = new DateTime(2025, 6, 5),
            GeneratedAt = new DateTime(2026, 6, 17, 12, 0, 0, DateTimeKind.Utc),
            LiabilityTotal = 6504742.51m,
            AssetTotal = 6504742.50m,
            Difference = 0.01m,
            BalanceTolerance = 0.05m,
            Status = "balanced",
            WarningSummary = "stock fallback",
            ErrorSummary = null
        });

        Assert.True(id > 0);
        var rows = repo.GetRecentBalanceSheetVerificationRuns(10);
        Assert.Single(rows);
        Assert.Equal("balanced", rows[0].Status);
        Assert.Equal(6504742.51m, rows[0].LiabilityTotal);
        Assert.Equal("stock fallback", rows[0].WarningSummary);
    }
    finally
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(path)) try { File.Delete(path); } catch { }
    }
}
```

- [ ] **Step 2: Run repository tests to verify they fail**

Run:

```powershell
dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore --filter "InitializeDatabase_CreatesBalanceSheetVerificationRunsTable|AddBalanceSheetVerificationRun_PersistsTotalsAndStatus"
```

Expected: fail because the table and repository methods do not exist.

- [ ] **Step 3: Add repository interface methods**

Modify `src/TallyDbLoader.Core/Data/IConfigRepository.cs` and add:

```csharp
CompanyProfile? GetCompanyProfileById(int id);
long AddBalanceSheetVerificationRun(BalanceSheetVerificationRun run);
List<BalanceSheetVerificationRun> GetRecentBalanceSheetVerificationRuns(int limit = 50);
```

- [ ] **Step 4: Add database migration v5**

In `src/TallyDbLoader.Core/Data/DatabaseHelper.cs`, after the `if (version < 4)` block and before the final legacy status normalization, add:

```csharp
if (version < 5)
{
    conn.Execute(@"
        CREATE TABLE IF NOT EXISTS balance_sheet_verification_runs (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            company_profile_id INTEGER NOT NULL REFERENCES company_profiles(id) ON DELETE CASCADE,
            target_identity TEXT NOT NULL,
            financial_year_start TEXT NOT NULL,
            as_at_date TEXT NOT NULL,
            generated_at TEXT NOT NULL,
            liability_total TEXT NOT NULL,
            asset_total TEXT NOT NULL,
            difference TEXT NOT NULL,
            balance_tolerance TEXT NOT NULL,
            status TEXT NOT NULL,
            warning_summary TEXT NULL,
            error_summary TEXT NULL
        );", null, transaction);

    conn.Execute("CREATE INDEX IF NOT EXISTS ix_balance_sheet_verification_runs_company_generated ON balance_sheet_verification_runs(company_profile_id, generated_at DESC);", null, transaction);
    conn.Execute("PRAGMA user_version = 5;", null, transaction);
}
```

- [ ] **Step 5: Implement repository methods**

In `src/TallyDbLoader.Core/Data/ConfigRepository.cs`, add:

```csharp
public CompanyProfile? GetCompanyProfileById(int id)
{
    using (var conn = new SqliteConnection(_connectionString))
    {
        conn.Open();
        conn.Execute("PRAGMA foreign_keys = ON;");
        var profile = conn.QueryFirstOrDefault<CompanyProfile>(@"
            SELECT
                id AS Id, name AS Name, tally_guid AS TallyGuid,
                consolidated AS Consolidated, books_from AS BooksFrom,
                books_to AS BooksTo, db_profile_id AS DbProfileId,
                target_catalog AS TargetCatalog, schema AS Schema,
                table_prefix AS TablePrefix, mode AS Mode,
                interval_minutes AS IntervalMinutes, enabled AS Enabled,
                notify_on_error AS NotifyOnError, pause_on_tally_close AS PauseOnTallyClose,
                entity_flags AS EntityFlags, status AS Status,
                last_run_at AS LastRunAt, last_duration_ms AS LastDurationMs,
                last_rows_written AS LastRowsWritten, error_count_24h AS ErrorCount24h
            FROM company_profiles
            WHERE id = @Id;", new { Id = id });

        if (profile != null)
        {
            profile.Status = NormalizeCompanyProfileStatus(profile.Status);
            profile.Db = GetDatabaseProfileById(profile.DbProfileId);
        }

        return profile;
    }
}

public long AddBalanceSheetVerificationRun(BalanceSheetVerificationRun run)
{
    if (run == null) throw new ArgumentNullException(nameof(run));
    if (string.IsNullOrWhiteSpace(run.TargetIdentity)) throw new ArgumentException("TargetIdentity cannot be empty.", nameof(run));
    if (string.IsNullOrWhiteSpace(run.Status)) throw new ArgumentException("Status cannot be empty.", nameof(run));

    using (var conn = new SqliteConnection(_connectionString))
    {
        conn.Open();
        conn.Execute("PRAGMA foreign_keys = ON;");
        conn.Execute(@"
            INSERT INTO balance_sheet_verification_runs (
                company_profile_id, target_identity, financial_year_start, as_at_date,
                generated_at, liability_total, asset_total, difference, balance_tolerance,
                status, warning_summary, error_summary)
            VALUES (
                @CompanyProfileId, @TargetIdentity, @FinancialYearStart, @AsAtDate,
                @GeneratedAt, @LiabilityTotal, @AssetTotal, @Difference, @BalanceTolerance,
                @Status, @WarningSummary, @ErrorSummary);",
            new
            {
                run.CompanyProfileId,
                run.TargetIdentity,
                FinancialYearStart = run.FinancialYearStart.ToString("o"),
                AsAtDate = run.AsAtDate.ToString("o"),
                GeneratedAt = run.GeneratedAt.ToString("o"),
                LiabilityTotal = run.LiabilityTotal.ToString(System.Globalization.CultureInfo.InvariantCulture),
                AssetTotal = run.AssetTotal.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Difference = run.Difference.ToString(System.Globalization.CultureInfo.InvariantCulture),
                BalanceTolerance = run.BalanceTolerance.ToString(System.Globalization.CultureInfo.InvariantCulture),
                run.Status,
                run.WarningSummary,
                run.ErrorSummary
            });

        return conn.QuerySingle<long>("SELECT last_insert_rowid();");
    }
}

public List<BalanceSheetVerificationRun> GetRecentBalanceSheetVerificationRuns(int limit = 50)
{
    using (var conn = new SqliteConnection(_connectionString))
    {
        conn.Open();
        conn.Execute("PRAGMA foreign_keys = ON;");
        return conn.Query<BalanceSheetVerificationRun>(@"
            SELECT
                id AS Id,
                company_profile_id AS CompanyProfileId,
                target_identity AS TargetIdentity,
                financial_year_start AS FinancialYearStart,
                as_at_date AS AsAtDate,
                generated_at AS GeneratedAt,
                CAST(liability_total AS TEXT) AS LiabilityTotal,
                CAST(asset_total AS TEXT) AS AssetTotal,
                CAST(difference AS TEXT) AS Difference,
                CAST(balance_tolerance AS TEXT) AS BalanceTolerance,
                status AS Status,
                warning_summary AS WarningSummary,
                error_summary AS ErrorSummary
            FROM balance_sheet_verification_runs
            ORDER BY generated_at DESC
            LIMIT @Limit;", new { Limit = limit }).AsList();
    }
}
```

- [ ] **Step 6: Run repository tests**

Run:

```powershell
dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore --filter "InitializeDatabase_CreatesBalanceSheetVerificationRunsTable|AddBalanceSheetVerificationRun_PersistsTotalsAndStatus"
```

Expected: pass.

- [ ] **Step 7: Commit**

```powershell
git add src/TallyDbLoader.Core/Data/DatabaseHelper.cs src/TallyDbLoader.Core/Data/IConfigRepository.cs src/TallyDbLoader.Core/Data/ConfigRepository.cs tests/TallyDbLoader.Tests/ConfigRepositoryTests.cs
git commit -m "feat: record balance sheet verification history"
```

---

### Task 3: Async Target Connection Factory

**Files:**
- Modify: `src/TallyDbLoader.Core/Data/DatabaseWriter.cs`
- Modify: `tests/TallyDbLoader.Tests/DatabaseLoaderTests.cs`

- [ ] **Step 1: Write failing SQLite async connection test**

Append to `tests/TallyDbLoader.Tests/DatabaseLoaderTests.cs`:

```csharp
[Fact]
public async Task DatabaseWriter_GetConnectionAsync_ForSqlite_OpensDbConnection()
{
    string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"target_async_{Guid.NewGuid()}.db");
    try
    {
        var profile = new TallyDbLoader.Core.Models.DatabaseProfile
        {
            Name = "SqliteTarget",
            Technology = "sqlite"
        };

        await using var conn = await TallyDbLoader.Core.Data.DatabaseWriter.GetConnectionAsync(
            profile,
            path,
            System.Threading.CancellationToken.None);

        Assert.Equal(System.Data.ConnectionState.Open, conn.State);
        Assert.IsAssignableFrom<System.Data.Common.DbConnection>(conn);
    }
    finally
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (System.IO.File.Exists(path)) try { System.IO.File.Delete(path); } catch { }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run:

```powershell
dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore --filter DatabaseWriter_GetConnectionAsync_ForSqlite_OpensDbConnection
```

Expected: fail because `DatabaseWriter.GetConnectionAsync` does not exist.

- [ ] **Step 3: Add async connection factory**

In `src/TallyDbLoader.Core/Data/DatabaseWriter.cs`, add this method:

```csharp
public static async System.Threading.Tasks.Task<System.Data.Common.DbConnection> GetConnectionAsync(
    DatabaseProfile profile,
    string catalog,
    System.Threading.CancellationToken cancellationToken)
{
    if (profile == null) throw new ArgumentNullException(nameof(profile));
    System.Data.Common.DbConnection conn;

    if (profile.Technology.Equals("postgres", StringComparison.OrdinalIgnoreCase))
    {
        string sslParam = "";
        if (!profile.Server.Equals("localhost", StringComparison.OrdinalIgnoreCase) &&
            !profile.Server.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase))
        {
            sslParam = "SslMode=Require;TrustServerCertificate=True;";
        }
        string connStr = $"Host={profile.Server};Port={profile.Port};Username={profile.Username};Password={profile.Password};Database={catalog};{sslParam}";
        conn = new Npgsql.NpgsqlConnection(connStr);
    }
    else if (profile.Technology.Equals("mssql", StringComparison.OrdinalIgnoreCase))
    {
        string connStr = $"Server={profile.Server},{profile.Port};User Id={profile.Username};Password={profile.Password};Database={catalog};TrustServerCertificate=True;";
        conn = new Microsoft.Data.SqlClient.SqlConnection(connStr);
    }
    else if (profile.Technology.Equals("mysql", StringComparison.OrdinalIgnoreCase))
    {
        string connStr = $"Server={profile.Server};Port={profile.Port};User Id={profile.Username};Password={profile.Password};Database={catalog};AllowLoadLocalInfile=True;";
        conn = new MySqlConnector.MySqlConnection(connStr);
    }
    else if (profile.Technology.Equals("sqlite", StringComparison.OrdinalIgnoreCase))
    {
        string dbFile = catalog.EndsWith(".db", StringComparison.OrdinalIgnoreCase) ? catalog : $"{catalog}.db";
        conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbFile}");
    }
    else
    {
        throw new NotSupportedException($"Database technology '{profile.Technology}' is not supported.");
    }

    await conn.OpenAsync(cancellationToken);
    return conn;
}
```

- [ ] **Step 4: Run async connection test**

Run:

```powershell
dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore --filter DatabaseWriter_GetConnectionAsync_ForSqlite_OpensDbConnection
```

Expected: pass.

- [ ] **Step 5: Commit**

```powershell
git add src/TallyDbLoader.Core/Data/DatabaseWriter.cs tests/TallyDbLoader.Tests/DatabaseLoaderTests.cs
git commit -m "feat: add async target database connection factory"
```

---

### Task 4: Identifier-Safe Table Name Builder

**Files:**
- Create: `src/TallyDbLoader.Core/Reports/BalanceSheetTableNames.cs`
- Create: `tests/TallyDbLoader.Tests/BalanceSheetQueryAdapterTests.cs`

- [ ] **Step 1: Write failing table-name validation tests**

Create `tests/TallyDbLoader.Tests/BalanceSheetQueryAdapterTests.cs`:

```csharp
using System;
using TallyDbLoader.Core.Reports;
using Xunit;

namespace TallyDbLoader.Tests
{
    public class BalanceSheetQueryAdapterTests
    {
        [Fact]
        public void BalanceSheetTableNames_WithPrefix_QualifiesRequiredTables()
        {
            var names = BalanceSheetTableNames.Create("public", "tally_", "NpgsqlConnection");

            Assert.Equal("public", names.Schema);
            Assert.Equal("tally_mst_group", names.MstGroup);
            Assert.Equal("tally_mst_ledger", names.MstLedger);
            Assert.Equal("tally_trn_voucher", names.TrnVoucher);
            Assert.Equal("tally_trn_accounting", names.TrnAccounting);
            Assert.Equal("tally_trn_closingstock_ledger", names.TrnClosingStockLedger);
        }

        [Theory]
        [InlineData("public;drop table x", "tally_")]
        [InlineData("public", "tally;drop_")]
        [InlineData("public", "123bad")]
        public void BalanceSheetTableNames_WithUnsafeIdentifiers_Throws(string schema, string prefix)
        {
            Assert.Throws<InvalidOperationException>(() =>
                BalanceSheetTableNames.Create(schema, prefix, "SqliteConnection"));
        }
    }
}
```

- [ ] **Step 2: Run tests to verify failure**

Run:

```powershell
dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore --filter BalanceSheetQueryAdapterTests
```

Expected: fail because `BalanceSheetTableNames` does not exist.

- [ ] **Step 3: Add table-name builder**

Create `src/TallyDbLoader.Core/Reports/BalanceSheetTableNames.cs`:

```csharp
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
```

- [ ] **Step 4: Run table-name tests**

Run:

```powershell
dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore --filter BalanceSheetQueryAdapterTests
```

Expected: pass.

- [ ] **Step 5: Commit**

```powershell
git add src/TallyDbLoader.Core/Reports/BalanceSheetTableNames.cs tests/TallyDbLoader.Tests/BalanceSheetQueryAdapterTests.cs
git commit -m "feat: validate balance sheet report table names"
```

---

### Task 5: Query Adapter Contract and Provider SQL

**Files:**
- Create: `src/TallyDbLoader.Core/Reports/IBalanceSheetQueryAdapter.cs`
- Create: `src/TallyDbLoader.Core/Reports/BalanceSheetQueryAdapters.cs`
- Modify: `tests/TallyDbLoader.Tests/BalanceSheetQueryAdapterTests.cs`

- [ ] **Step 1: Add failing SQL generation tests**

Append to `tests/TallyDbLoader.Tests/BalanceSheetQueryAdapterTests.cs`:

```csharp
[Fact]
public void SqliteAdapter_BuildLedgerSql_UsesPrefixedTablesAndDateParameters()
{
    var adapter = new SqliteBalanceSheetQueryAdapter();
    var names = BalanceSheetTableNames.Create("main", "tally_", "SqliteConnection");

    string sql = adapter.BuildLedgerSql(names, includeClosingStock: true);

    Assert.Contains("\"tally_mst_ledger\"", sql);
    Assert.Contains("\"tally_trn_accounting\"", sql);
    Assert.Contains("@FinancialYearStart", sql);
    Assert.Contains("@AsAtDate", sql);
    Assert.DoesNotContain("2025-04-01", sql);
}

[Theory]
[InlineData("mssql", "[dbo].[tally_mst_ledger]")]
[InlineData("postgres", "\"public\".\"tally_mst_ledger\"")]
[InlineData("mysql", "`public`.`tally_mst_ledger`")]
public void ProviderAdapters_BuildLedgerSql_QualifySchemaAndTablePrefix(string providerName, string expectedLedgerTable)
{
    IBalanceSheetQueryAdapter adapter = providerName switch
    {
        "mssql" => new MssqlBalanceSheetQueryAdapter(),
        "postgres" => new PostgresBalanceSheetQueryAdapter(),
        "mysql" => new MySqlBalanceSheetQueryAdapter(),
        _ => throw new InvalidOperationException()
    };
    var provider = providerName == "mssql" ? "SqlConnection" : providerName == "postgres" ? "NpgsqlConnection" : "MySqlConnection";
    var schema = providerName == "mssql" ? "dbo" : "public";
    var names = BalanceSheetTableNames.Create(schema, "tally_", provider);

    string sql = adapter.BuildLedgerSql(names, includeClosingStock: true);

    Assert.Contains(expectedLedgerTable, sql);
    Assert.Contains("@FinancialYearStart", sql);
    Assert.Contains("@AsAtDate", sql);
}

[Fact]
public void SqliteAdapter_BuildLedgerSql_WithoutClosingStock_DoesNotReferenceClosingStockTable()
{
    var adapter = new SqliteBalanceSheetQueryAdapter();
    var names = BalanceSheetTableNames.Create("main", "tally_", "SqliteConnection");

    string sql = adapter.BuildLedgerSql(names, includeClosingStock: false);

    Assert.DoesNotContain("trn_closingstock_ledger", sql);
    Assert.Contains("0 AS ClosingStockValue", sql);
    Assert.Contains("0 AS HasClosingStockValue", sql);
}
```

- [ ] **Step 2: Run SQL generation tests to verify failure**

Run:

```powershell
dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore --filter "SqliteAdapter_BuildLedgerSql|ProviderAdapters_BuildLedgerSql"
```

Expected: fail because adapters do not exist.

- [ ] **Step 3: Add adapter contract**

Create `src/TallyDbLoader.Core/Reports/IBalanceSheetQueryAdapter.cs`:

```csharp
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using TallyDbLoader.Core.Models;

namespace TallyDbLoader.Core.Reports
{
    public interface IBalanceSheetQueryAdapter
    {
        string BuildLedgerSql(BalanceSheetTableNames names, bool includeClosingStock);
        Task<BalanceSheetRawData> QueryAsync(
            DbConnection connection,
            BalanceSheetTableNames names,
            BalanceSheetVerificationRequest request,
            CancellationToken cancellationToken);
    }
}
```

- [ ] **Step 4: Add provider adapters with executable SQLite path**

Create `src/TallyDbLoader.Core/Reports/BalanceSheetQueryAdapters.cs`:

```csharp
using System;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using TallyDbLoader.Core.Models;

namespace TallyDbLoader.Core.Reports
{
    public abstract class BalanceSheetQueryAdapterBase : IBalanceSheetQueryAdapter
    {
        protected abstract string Quote(string identifier);

        protected virtual string Qualify(BalanceSheetTableNames names, string table)
        {
            return $"{Quote(names.Schema)}.{Quote(table)}";
        }

        protected abstract string BuildTableExistsSql();

        protected virtual object BuildTableExistsParameters(BalanceSheetTableNames names, string tableName)
        {
            return new { Schema = names.Schema, TableName = tableName };
        }

        protected virtual async Task<bool> ClosingStockTableExistsAsync(
            DbConnection connection,
            BalanceSheetTableNames names,
            CancellationToken cancellationToken)
        {
            var command = new CommandDefinition(
                BuildTableExistsSql(),
                BuildTableExistsParameters(names, names.TrnClosingStockLedger),
                cancellationToken: cancellationToken);
            var count = await connection.ExecuteScalarAsync<long>(command);
            return count > 0;
        }

        public string BuildLedgerSql(BalanceSheetTableNames names, bool includeClosingStock)
        {
            string ledger = Qualify(names, names.MstLedger);
            string group = Qualify(names, names.MstGroup);
            string accounting = Qualify(names, names.TrnAccounting);
            string voucher = Qualify(names, names.TrnVoucher);
            string closingStockCte = string.Empty;
            string closingStockSelect = "0 AS ClosingStockValue,\n    0 AS HasClosingStockValue";
            string closingStockJoin = string.Empty;

            if (includeClosingStock)
            {
                string closingStock = Qualify(names, names.TrnClosingStockLedger);
                closingStockCte = $@",
closing_stock_ranked AS (
    SELECT ledger, stock_value, ROW_NUMBER() OVER (PARTITION BY ledger ORDER BY stock_date DESC) AS rn
    FROM {closingStock}
    WHERE stock_date <= @AsAtDate
)";
                closingStockSelect = @"COALESCE(closing_stock_ranked.stock_value, 0) AS ClosingStockValue,
    CASE WHEN closing_stock_ranked.ledger IS NULL THEN 0 ELSE 1 END AS HasClosingStockValue";
                closingStockJoin = "LEFT JOIN closing_stock_ranked ON closing_stock_ranked.ledger = l.name AND closing_stock_ranked.rn = 1";
            }

            return $@"
WITH pre_period AS (
    SELECT a.ledger AS ledger, SUM(a.amount) AS amount
    FROM {accounting} a
    JOIN {voucher} v ON v.guid = a.guid
    WHERE v.is_order_voucher = 0
      AND v.is_inventory_voucher = 0
      AND v.date < @FinancialYearStart
    GROUP BY a.ledger
),
current_period AS (
    SELECT a.ledger AS ledger, SUM(a.amount) AS amount
    FROM {accounting} a
    JOIN {voucher} v ON v.guid = a.guid
    WHERE v.is_order_voucher = 0
      AND v.is_inventory_voucher = 0
      AND v.date >= @FinancialYearStart
      AND v.date <= @AsAtDate
    GROUP BY a.ledger
){closingStockCte}
SELECT
    l.name AS LedgerName,
    l.parent AS ParentGroupName,
    COALESCE(g.primary_group, '') AS PrimaryGroup,
    CASE WHEN COALESCE(g.is_revenue, 0) = 1 THEN 1 ELSE 0 END AS IsRevenue,
    COALESCE(l.opening_balance, 0) AS OpeningBalance,
    COALESCE(pre_period.amount, 0) AS PrePeriodMovement,
    COALESCE(current_period.amount, 0) AS CurrentPeriodMovement,
    {closingStockSelect}
FROM {ledger} l
LEFT JOIN {group} g ON g.name = l.parent
LEFT JOIN pre_period ON pre_period.ledger = l.name
LEFT JOIN current_period ON current_period.ledger = l.name
{closingStockJoin};";
        }

        public virtual async Task<BalanceSheetRawData> QueryAsync(
            DbConnection connection,
            BalanceSheetTableNames names,
            BalanceSheetVerificationRequest request,
            CancellationToken cancellationToken)
        {
            bool hasClosingStockTable = await ClosingStockTableExistsAsync(connection, names, cancellationToken);

            var command = new CommandDefinition(
                BuildLedgerSql(names, hasClosingStockTable),
                new
                {
                    FinancialYearStart = request.FinancialYearStart.Date,
                    AsAtDate = request.AsAtDate.Date
                },
                cancellationToken: cancellationToken);

            var rows = await connection.QueryAsync<BalanceSheetLedgerRow>(command);
            var rawData = new BalanceSheetRawData
            {
                Ledgers = rows.ToList(),
                HasClosingStockTable = hasClosingStockTable
            };
            if (!hasClosingStockTable)
            {
                rawData.Warnings.Add($"Optional table '{names.TrnClosingStockLedger}' was not found; Stock-in-Hand uses ledger balances.");
            }
            return rawData;
        }
    }

    public sealed class SqliteBalanceSheetQueryAdapter : BalanceSheetQueryAdapterBase
    {
        protected override string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";
        protected override string BuildTableExistsSql()
        {
            return "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @TableName;";
        }

        protected override string Qualify(BalanceSheetTableNames names, string table)
        {
            return Quote(table);
        }
    }

    public sealed class MssqlBalanceSheetQueryAdapter : BalanceSheetQueryAdapterBase
    {
        protected override string Quote(string identifier) => $"[{identifier.Replace("]", "]]")}]";
        protected override string BuildTableExistsSql()
        {
            return "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = @Schema AND TABLE_NAME = @TableName;";
        }
    }

    public sealed class PostgresBalanceSheetQueryAdapter : BalanceSheetQueryAdapterBase
    {
        protected override string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";
        protected override string BuildTableExistsSql()
        {
            return "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = @Schema AND table_name = @TableName;";
        }
    }

    public sealed class MySqlBalanceSheetQueryAdapter : BalanceSheetQueryAdapterBase
    {
        protected override string Quote(string identifier) => $"`{identifier.Replace("`", "``")}`";
        protected override string BuildTableExistsSql()
        {
            return "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = @Schema AND table_name = @TableName;";
        }
    }
}
```

- [ ] **Step 5: Run SQL generation tests**

Run:

```powershell
dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore --filter "SqliteAdapter_BuildLedgerSql|ProviderAdapters_BuildLedgerSql"
```

Expected: pass.

- [ ] **Step 6: Commit**

```powershell
git add src/TallyDbLoader.Core/Reports/IBalanceSheetQueryAdapter.cs src/TallyDbLoader.Core/Reports/BalanceSheetQueryAdapters.cs tests/TallyDbLoader.Tests/BalanceSheetQueryAdapterTests.cs
git commit -m "feat: add balance sheet query adapters"
```

---

### Task 6: Pure Balance Sheet Calculator

**Files:**
- Create: `src/TallyDbLoader.Core/Reports/BalanceSheetCalculator.cs`
- Create: `tests/TallyDbLoader.Tests/BalanceSheetCalculatorTests.cs`

- [ ] **Step 1: Write failing calculator tests**

Create `tests/TallyDbLoader.Tests/BalanceSheetCalculatorTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using TallyDbLoader.Core.Models;
using TallyDbLoader.Core.Reports;
using Xunit;

namespace TallyDbLoader.Tests
{
    public class BalanceSheetCalculatorTests
    {
        private static BalanceSheetVerificationRequest Request() => new BalanceSheetVerificationRequest
        {
            CompanyProfileId = 1,
            FinancialYearStart = new DateTime(2025, 4, 1),
            AsAtDate = new DateTime(2025, 6, 5)
        };

        [Fact]
        public void Calculate_IncludesInvestmentsOnAssetSide()
        {
            var raw = new BalanceSheetRawData
            {
                Ledgers = new List<BalanceSheetLedgerRow>
                {
                    new() { LedgerName = "Owner Capital", PrimaryGroup = "Capital Account", OpeningBalance = 1000m },
                    new() { LedgerName = "Mutual Fund", PrimaryGroup = "Investments", OpeningBalance = -1000m }
                }
            };

            var report = BalanceSheetCalculator.Calculate("Demo Co", raw, Request());

            Assert.Contains(report.AssetSide.Lines, l => l.Name == "Investments" && l.Amount == 1000m);
            Assert.Equal("balanced", report.Status);
        }

        [Fact]
        public void Calculate_UsesCreditPositiveDebitNegativeConvention()
        {
            var raw = new BalanceSheetRawData
            {
                Ledgers = new List<BalanceSheetLedgerRow>
                {
                    new() { LedgerName = "Capital", PrimaryGroup = "Capital Account", OpeningBalance = 500m },
                    new() { LedgerName = "Cash", PrimaryGroup = "Current Assets", OpeningBalance = -500m }
                }
            };

            var report = BalanceSheetCalculator.Calculate("Demo Co", raw, Request());

            Assert.Equal(500m, report.LiabilityTotal);
            Assert.Equal(500m, report.AssetTotal);
        }

        [Fact]
        public void Calculate_ProfitAndLoss_CurrentPeriod_IncludesStockDelta()
        {
            var raw = new BalanceSheetRawData
            {
                HasClosingStockTable = true,
                Ledgers = new List<BalanceSheetLedgerRow>
                {
                    new() { LedgerName = "Capital", PrimaryGroup = "Capital Account", OpeningBalance = 1200m },
                    new() { LedgerName = "Cash", PrimaryGroup = "Current Assets", OpeningBalance = -1000m },
                    new() { LedgerName = "Stock", PrimaryGroup = "Stock-in-hand", OpeningBalance = -200m, ClosingStockValue = -300m, HasClosingStockValue = true },
                    new() { LedgerName = "Sales", PrimaryGroup = "Sales Accounts", IsRevenue = true, CurrentPeriodMovement = 400m },
                    new() { LedgerName = "Purchase", PrimaryGroup = "Purchase Accounts", IsRevenue = true, CurrentPeriodMovement = -200m }
                }
            };

            var report = BalanceSheetCalculator.Calculate("Demo Co", raw, Request());

            Assert.Equal(100m, report.ProfitAndLoss.CurrentPeriod);
            Assert.Contains(report.LiabilitySide.Lines, l => l.Name == "Profit & Loss A/c" && l.Amount == 100m);
        }

        [Fact]
        public void Calculate_ProfitAndLoss_OpeningBalance_UsesReservedLedgerOpening()
        {
            var raw = new BalanceSheetRawData
            {
                Ledgers = new List<BalanceSheetLedgerRow>
                {
                    new() { LedgerName = "Profit & Loss A/c", PrimaryGroup = "Capital Account", OpeningBalance = 250m },
                    new() { LedgerName = "Cash", PrimaryGroup = "Current Assets", OpeningBalance = -250m }
                }
            };

            var report = BalanceSheetCalculator.Calculate("Demo Co", raw, Request());

            Assert.Equal(250m, report.ProfitAndLoss.OpeningBalance);
        }

        [Fact]
        public void Calculate_LessTransferred_UsesDirectDebitPostingToProfitAndLossLedger()
        {
            var raw = new BalanceSheetRawData
            {
                Ledgers = new List<BalanceSheetLedgerRow>
                {
                    new() { LedgerName = "Capital", PrimaryGroup = "Capital Account", OpeningBalance = 1000m },
                    new() { LedgerName = "Profit & Loss A/c", PrimaryGroup = "Capital Account", CurrentPeriodMovement = -150m },
                    new() { LedgerName = "Cash", PrimaryGroup = "Current Assets", OpeningBalance = -850m }
                }
            };

            var report = BalanceSheetCalculator.Calculate("Demo Co", raw, Request());

            Assert.Equal(150m, report.ProfitAndLoss.LessTransferred);
        }

        [Fact]
        public void Calculate_SmallDifferenceWithinTolerance_IsBalanced()
        {
            var request = Request();
            request.Options.BalanceTolerance = 0.05m;
            var raw = new BalanceSheetRawData
            {
                Ledgers = new List<BalanceSheetLedgerRow>
                {
                    new() { LedgerName = "Capital", PrimaryGroup = "Capital Account", OpeningBalance = 100m },
                    new() { LedgerName = "Cash", PrimaryGroup = "Current Assets", OpeningBalance = -99.98m }
                }
            };

            var report = BalanceSheetCalculator.Calculate("Demo Co", raw, request);

            Assert.Equal("balanced", report.Status);
            Assert.Equal(0.02m, report.Difference);
        }
    }
}
```

- [ ] **Step 2: Run calculator tests to verify failure**

Run:

```powershell
dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore --filter BalanceSheetCalculatorTests
```

Expected: fail because `BalanceSheetCalculator` does not exist.

- [ ] **Step 3: Implement calculator**

Create `src/TallyDbLoader.Core/Reports/BalanceSheetCalculator.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using TallyDbLoader.Core.Models;

namespace TallyDbLoader.Core.Reports
{
    public static class BalanceSheetCalculator
    {
        private static readonly HashSet<string> LiabilityGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Capital Account",
            "Loans (Liability)",
            "Current Liabilities"
        };

        private static readonly HashSet<string> AssetGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Fixed Assets",
            "Investments",
            "Current Assets",
            "Branch / Divisions",
            "Misc. Expenses (ASSET)",
            "Suspense A/c",
            "Stock-in-hand"
        };

        public static BalanceSheetReport Calculate(
            string companyName,
            BalanceSheetRawData raw,
            BalanceSheetVerificationRequest request)
        {
            if (raw == null) throw new ArgumentNullException(nameof(raw));
            if (request == null) throw new ArgumentNullException(nameof(request));

            var report = new BalanceSheetReport
            {
                CompanyProfileId = request.CompanyProfileId,
                CompanyName = companyName,
                FinancialYearStart = request.FinancialYearStart,
                AsAtDate = request.AsAtDate,
                BalanceTolerance = request.Options.BalanceTolerance,
                Warnings = new List<string>(raw.Warnings)
            };

            var reservedPnl = raw.Ledgers.FirstOrDefault(l =>
                l.LedgerName.Equals(request.Options.ProfitAndLossLedgerName, StringComparison.OrdinalIgnoreCase));

            if (reservedPnl == null)
            {
                report.Warnings.Add($"Reserved Profit & Loss ledger '{request.Options.ProfitAndLossLedgerName}' was not found.");
            }

            decimal pnlOpening = reservedPnl?.OpeningBalance ?? 0m;
            decimal pnlLessTransferred = reservedPnl?.CurrentPeriodMovement < 0m
                ? Math.Abs(reservedPnl.CurrentPeriodMovement)
                : 0m;

            decimal revenueCurrent = raw.Ledgers
                .Where(l => l.IsRevenue && !l.LedgerName.Equals(request.Options.ProfitAndLossLedgerName, StringComparison.OrdinalIgnoreCase))
                .Sum(l => l.CurrentPeriodMovement);

            decimal revenuePrePeriod = raw.Ledgers
                .Where(l => l.IsRevenue && !l.LedgerName.Equals(request.Options.ProfitAndLossLedgerName, StringComparison.OrdinalIgnoreCase))
                .Sum(l => l.PrePeriodMovement);

            decimal stockOpening = raw.Ledgers
                .Where(l => l.PrimaryGroup.Equals("Stock-in-hand", StringComparison.OrdinalIgnoreCase))
                .Sum(l => Math.Abs(l.OpeningBalance + l.PrePeriodMovement));

            var stockLedgers = raw.Ledgers
                .Where(l => l.PrimaryGroup.Equals("Stock-in-hand", StringComparison.OrdinalIgnoreCase))
                .ToList();

            decimal stockClosing = stockLedgers.Any(l => l.HasClosingStockValue)
                ? stockLedgers.Sum(l => Math.Abs(l.ClosingStockValue))
                : stockLedgers.Sum(l => Math.Abs(l.OpeningBalance + l.PrePeriodMovement + l.CurrentPeriodMovement));

            if (stockLedgers.Any() && !stockLedgers.Any(l => l.HasClosingStockValue))
            {
                report.Warnings.Add("Stock-in-Hand closing values were not found; ledger balances were used.");
            }

            report.ProfitAndLoss.OpeningBalance = pnlOpening + revenuePrePeriod;
            report.ProfitAndLoss.CurrentPeriod = revenueCurrent + (stockClosing - stockOpening);
            report.ProfitAndLoss.LessTransferred = pnlLessTransferred;

            var grouped = raw.Ledgers
                .Where(l => !l.IsRevenue)
                .Where(l => !l.LedgerName.Equals(request.Options.ProfitAndLossLedgerName, StringComparison.OrdinalIgnoreCase))
                .GroupBy(l => NormalizeGroup(l.PrimaryGroup));

            foreach (var group in grouped)
            {
                decimal signedBalance = group.Sum(l =>
                {
                    if (group.Key.Equals("Stock-in-hand", StringComparison.OrdinalIgnoreCase) && l.HasClosingStockValue)
                    {
                        return l.ClosingStockValue;
                    }
                    return l.OpeningBalance + l.PrePeriodMovement + l.CurrentPeriodMovement;
                });

                if (signedBalance == 0m) continue;

                if (LiabilityGroups.Contains(group.Key))
                {
                    report.LiabilitySide.Lines.Add(new BalanceSheetLine
                    {
                        Name = group.Key,
                        Amount = Math.Abs(signedBalance),
                        IsEmphasis = true
                    });
                }
                else if (AssetGroups.Contains(group.Key))
                {
                    report.AssetSide.Lines.Add(new BalanceSheetLine
                    {
                        Name = group.Key,
                        Amount = Math.Abs(signedBalance),
                        IsEmphasis = true
                    });
                }
                else
                {
                    report.Warnings.Add($"Unrecognized primary group '{group.Key}' was excluded.");
                }
            }

            if (report.ProfitAndLoss.NetAmount > 0m)
            {
                report.LiabilitySide.Lines.Add(new BalanceSheetLine
                {
                    Name = request.Options.ProfitAndLossLedgerName,
                    Amount = report.ProfitAndLoss.NetAmount,
                    IsEmphasis = true
                });
            }
            else if (report.ProfitAndLoss.NetAmount < 0m)
            {
                report.AssetSide.Lines.Add(new BalanceSheetLine
                {
                    Name = request.Options.ProfitAndLossLedgerName,
                    Amount = Math.Abs(report.ProfitAndLoss.NetAmount),
                    IsEmphasis = true
                });
            }

            report.Status = report.Difference <= request.Options.BalanceTolerance
                ? "balanced"
                : "out_of_balance";

            return report;
        }

        private static string NormalizeGroup(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "(unresolved)" : value.Trim();
        }
    }
}
```

- [ ] **Step 4: Run calculator tests**

Run:

```powershell
dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore --filter BalanceSheetCalculatorTests
```

Expected: pass.

- [ ] **Step 5: Commit**

```powershell
git add src/TallyDbLoader.Core/Reports/BalanceSheetCalculator.cs tests/TallyDbLoader.Tests/BalanceSheetCalculatorTests.cs
git commit -m "feat: calculate balance sheet totals"
```

---

### Task 7: Verification Service Orchestration

**Files:**
- Create: `src/TallyDbLoader.Core/Reports/BalanceSheetVerificationService.cs`
- Create: `tests/TallyDbLoader.Tests/BalanceSheetVerificationServiceTests.cs`

- [ ] **Step 1: Write failing service test**

Create `tests/TallyDbLoader.Tests/BalanceSheetVerificationServiceTests.cs`:

```csharp
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

        private static void SeedTarget(string path)
        {
            using var conn = new SqliteConnection($"Data Source={path}");
            conn.Open();
            conn.Execute("CREATE TABLE mst_group (name TEXT, primary_group TEXT, is_revenue INTEGER);");
            conn.Execute("CREATE TABLE mst_ledger (name TEXT, parent TEXT, opening_balance DECIMAL(17,2));");
            conn.Execute("CREATE TABLE trn_voucher (guid TEXT, date DATE, is_order_voucher INTEGER, is_inventory_voucher INTEGER);");
            conn.Execute("CREATE TABLE trn_accounting (guid TEXT, ledger TEXT, amount DECIMAL(17,2));");
            conn.Execute("CREATE TABLE trn_closingstock_ledger (ledger TEXT, stock_date DATE, stock_value DECIMAL(17,2));");

            conn.Execute("INSERT INTO mst_group (name, primary_group, is_revenue) VALUES ('Capital Account', 'Capital Account', 0), ('Current Assets', 'Current Assets', 0);");
            conn.Execute("INSERT INTO mst_ledger (name, parent, opening_balance) VALUES ('Capital', 'Capital Account', 1000), ('Cash', 'Current Assets', -1000);");
        }
    }
}
```

- [ ] **Step 2: Run service test to verify failure**

Run:

```powershell
dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore --filter BalanceSheetVerificationServiceTests
```

Expected: fail because `BalanceSheetVerificationService` does not exist.

- [ ] **Step 3: Implement service**

Create `src/TallyDbLoader.Core/Reports/BalanceSheetVerificationService.cs`:

```csharp
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TallyDbLoader.Core.Data;
using TallyDbLoader.Core.Models;

namespace TallyDbLoader.Core.Reports
{
    public class BalanceSheetVerificationService
    {
        private readonly IConfigRepository _repo;

        public BalanceSheetVerificationService(IConfigRepository repo)
        {
            _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        }

        public async Task<BalanceSheetReport> GenerateAsync(
            BalanceSheetVerificationRequest request,
            CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            var company = _repo.GetCompanyProfileById(request.CompanyProfileId);
            if (company == null)
            {
                return Failed(request, string.Empty, $"Sync Job with ID {request.CompanyProfileId} was not found.");
            }

            var db = company.Db ?? _repo.GetDatabaseProfileById(company.DbProfileId);
            if (db == null)
            {
                return Failed(request, company.Name, $"Database Profile with ID {company.DbProfileId} was not found.");
            }

            string provider = GetProviderName(db.Technology);
            var names = BalanceSheetTableNames.Create(company.Schema, company.TablePrefix, provider);
            var adapter = CreateAdapter(db.Technology);
            var targetIdentity = $"{db.Technology}:{company.TargetCatalog}:{company.Schema}:{company.TablePrefix}";

            try
            {
                await using var conn = await DatabaseWriter.GetConnectionAsync(db, company.TargetCatalog, cancellationToken);
                var raw = await adapter.QueryAsync(conn, names, request, cancellationToken);
                var report = BalanceSheetCalculator.Calculate(company.Name, raw, request);
                report.GeneratedAt = DateTime.UtcNow;

                _repo.AddBalanceSheetVerificationRun(ToHistory(report, targetIdentity));
                return report;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var report = Failed(request, company.Name, ex.Message);
                _repo.AddBalanceSheetVerificationRun(ToHistory(report, targetIdentity));
                return report;
            }
        }

        private static BalanceSheetReport Failed(BalanceSheetVerificationRequest request, string companyName, string error)
        {
            return new BalanceSheetReport
            {
                CompanyProfileId = request.CompanyProfileId,
                CompanyName = companyName,
                FinancialYearStart = request.FinancialYearStart,
                AsAtDate = request.AsAtDate,
                BalanceTolerance = request.Options.BalanceTolerance,
                Status = "failed",
                ErrorSummary = error,
                GeneratedAt = DateTime.UtcNow
            };
        }

        private static BalanceSheetVerificationRun ToHistory(BalanceSheetReport report, string targetIdentity)
        {
            return new BalanceSheetVerificationRun
            {
                CompanyProfileId = report.CompanyProfileId,
                TargetIdentity = targetIdentity,
                FinancialYearStart = report.FinancialYearStart,
                AsAtDate = report.AsAtDate,
                GeneratedAt = report.GeneratedAt,
                LiabilityTotal = report.LiabilityTotal,
                AssetTotal = report.AssetTotal,
                Difference = report.Difference,
                BalanceTolerance = report.BalanceTolerance,
                Status = report.Status,
                WarningSummary = report.Warnings.Count == 0 ? null : string.Join("; ", report.Warnings),
                ErrorSummary = report.ErrorSummary
            };
        }

        private static string GetProviderName(string technology)
        {
            if (technology.Equals("sqlite", StringComparison.OrdinalIgnoreCase)) return "SqliteConnection";
            if (technology.Equals("mssql", StringComparison.OrdinalIgnoreCase)) return "SqlConnection";
            if (technology.Equals("postgres", StringComparison.OrdinalIgnoreCase)) return "NpgsqlConnection";
            if (technology.Equals("mysql", StringComparison.OrdinalIgnoreCase)) return "MySqlConnection";
            return technology;
        }

        private static IBalanceSheetQueryAdapter CreateAdapter(string technology)
        {
            if (technology.Equals("sqlite", StringComparison.OrdinalIgnoreCase)) return new SqliteBalanceSheetQueryAdapter();
            if (technology.Equals("mssql", StringComparison.OrdinalIgnoreCase)) return new MssqlBalanceSheetQueryAdapter();
            if (technology.Equals("postgres", StringComparison.OrdinalIgnoreCase)) return new PostgresBalanceSheetQueryAdapter();
            if (technology.Equals("mysql", StringComparison.OrdinalIgnoreCase)) return new MySqlBalanceSheetQueryAdapter();
            throw new NotSupportedException($"Database technology '{technology}' is not supported.");
        }
    }
}
```

- [ ] **Step 4: Run service test**

Run:

```powershell
dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore --filter BalanceSheetVerificationServiceTests
```

Expected: pass. The Task 5 adapter already probes for the optional `trn_closingstock_ledger` table and returns a warning when the table is absent.

- [ ] **Step 5: Commit**

```powershell
git add src/TallyDbLoader.Core/Reports/BalanceSheetVerificationService.cs tests/TallyDbLoader.Tests/BalanceSheetVerificationServiceTests.cs
git commit -m "feat: orchestrate balance sheet verification"
```

---

### Task 8: Indian Currency Converter

**Files:**
- Create: `src/TallyDbLoader.Wpf/Converters/IndianCurrencyConverter.cs`
- Modify: `src/TallyDbLoader.Wpf/App.xaml`
- Create: `tests/TallyDbLoader.Tests/IndianCurrencyConverterTests.cs`

- [ ] **Step 1: Write failing converter tests**

Create `tests/TallyDbLoader.Tests/IndianCurrencyConverterTests.cs`:

```csharp
using System.Globalization;
using TallyDbLoader.Wpf.Converters;
using Xunit;

namespace TallyDbLoader.Tests
{
    public class IndianCurrencyConverterTests
    {
        [Fact]
        public void Convert_Decimal_UsesIndianGroupingWithTwoDecimals()
        {
            var converter = new IndianCurrencyConverter();

            var text = converter.Convert(6504742.51m, typeof(string), null, CultureInfo.InvariantCulture);

            Assert.Equal("65,04,742.51", text);
        }
    }
}
```

- [ ] **Step 2: Run converter test to verify failure**

Run:

```powershell
dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore --filter IndianCurrencyConverterTests
```

Expected: fail because converter does not exist.

- [ ] **Step 3: Add converter**

Create `src/TallyDbLoader.Wpf/Converters/IndianCurrencyConverter.cs`:

```csharp
using System;
using System.Globalization;
using System.Windows.Data;

namespace TallyDbLoader.Wpf.Converters
{
    public class IndianCurrencyConverter : IValueConverter
    {
        private static readonly CultureInfo IndianCulture = new CultureInfo("en-IN");

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value == null) return "0.00";
            if (decimal.TryParse(value.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var amount))
            {
                return amount.ToString("N2", IndianCulture);
            }
            return value.ToString() ?? "0.00";
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
```

- [ ] **Step 4: Register converter in App.xaml**

In `src/TallyDbLoader.Wpf/App.xaml`, add:

```xml
<converters:IndianCurrencyConverter x:Key="IndianCurrencyConverter"/>
```

Place it alongside the existing converter resources.

- [ ] **Step 5: Run converter test**

Run:

```powershell
dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore --filter IndianCurrencyConverterTests
```

Expected: pass.

- [ ] **Step 6: Commit**

```powershell
git add src/TallyDbLoader.Wpf/Converters/IndianCurrencyConverter.cs src/TallyDbLoader.Wpf/App.xaml tests/TallyDbLoader.Tests/IndianCurrencyConverterTests.cs
git commit -m "feat: format balance sheet amounts for India"
```

---

### Task 9: WPF ViewModel Wiring

**Files:**
- Modify: `src/TallyDbLoader.Wpf/MainViewModel.cs`
- Modify: `tests/TallyDbLoader.Tests/MainViewModelTests.cs`

- [ ] **Step 1: Write failing ViewModel tests**

Append to `tests/TallyDbLoader.Tests/MainViewModelTests.cs`:

```csharp
[Fact]
public async Task RunBalanceSheetVerificationAsync_UsesSelectedCompanyAndStoresReport()
{
    string dbPath = $"vm_bs_{Guid.NewGuid():N}.db";
    try
    {
        DatabaseHelper.InitializeDatabase(dbPath);
        var repo = new ConfigRepository(dbPath);
        repo.SaveDatabaseProfile(new DatabaseProfile { Name = "Target", Technology = "sqlite" });
        var db = repo.GetDatabaseProfileByName("Target");
        Assert.NotNull(db);
        repo.SaveCompanyProfile(new CompanyProfile
        {
            Name = "Demo Co",
            DbProfileId = db.Id,
            TargetCatalog = "target.db",
            BooksFrom = new DateTime(2025, 4, 1),
            BooksTo = new DateTime(2025, 6, 5)
        });

        var vm = new MainViewModel(dbPath);
        vm.DisableDispatcher = true;
        vm.BalanceSheetVerificationRunner = (request, token) => Task.FromResult(new BalanceSheetReport
        {
            CompanyProfileId = request.CompanyProfileId,
            CompanyName = "Demo Co",
            FinancialYearStart = request.FinancialYearStart,
            AsAtDate = request.AsAtDate,
            Status = "balanced",
            LiabilitySide = new BalanceSheetSide
            {
                Title = "Liabilities",
                Lines = { new BalanceSheetLine { Name = "Capital Account", Amount = 100m } }
            },
            AssetSide = new BalanceSheetSide
            {
                Title = "Assets",
                Lines = { new BalanceSheetLine { Name = "Current Assets", Amount = 100m } }
            }
        });

        vm.BalanceSheetSelectedCompany = vm.Companies.Single();
        await vm.RunBalanceSheetVerificationAsync();

        Assert.NotNull(vm.BalanceSheetReport);
        Assert.Equal("balanced", vm.BalanceSheetReport.Status);
        Assert.False(vm.IsBalanceSheetVerificationRunning);
    }
    finally
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
    }
}
```

- [ ] **Step 2: Run ViewModel test to verify failure**

Run:

```powershell
dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore --filter RunBalanceSheetVerificationAsync_UsesSelectedCompanyAndStoresReport
```

Expected: fail because the properties and method do not exist.

- [ ] **Step 3: Add route, properties, command, and async method**

In `src/TallyDbLoader.Wpf/MainViewModel.cs`:

Add `BalanceSheetVerification` to `RouteScreen`.

Add properties:

```csharp
public ObservableCollection<BalanceSheetVerificationRun> BalanceSheetVerificationHistory { get; } = new ObservableCollection<BalanceSheetVerificationRun>();

private CompanyProfile? _balanceSheetSelectedCompany;
public CompanyProfile? BalanceSheetSelectedCompany
{
    get => _balanceSheetSelectedCompany;
    set
    {
        _balanceSheetSelectedCompany = value;
        OnPropertyChanged();
        if (value?.BooksFrom != null) BalanceSheetFinancialYearStart = value.BooksFrom.Value;
        if (value?.BooksTo != null) BalanceSheetAsAtDate = value.BooksTo.Value;
    }
}

private DateTime _balanceSheetFinancialYearStart = new DateTime(DateTime.Today.Month >= 4 ? DateTime.Today.Year : DateTime.Today.Year - 1, 4, 1);
public DateTime BalanceSheetFinancialYearStart
{
    get => _balanceSheetFinancialYearStart;
    set { _balanceSheetFinancialYearStart = value; OnPropertyChanged(); }
}

private DateTime _balanceSheetAsAtDate = DateTime.Today;
public DateTime BalanceSheetAsAtDate
{
    get => _balanceSheetAsAtDate;
    set { _balanceSheetAsAtDate = value; OnPropertyChanged(); }
}

private BalanceSheetReport? _balanceSheetReport;
public BalanceSheetReport? BalanceSheetReport
{
    get => _balanceSheetReport;
    set { _balanceSheetReport = value; OnPropertyChanged(); }
}

private bool _isBalanceSheetVerificationRunning;
public bool IsBalanceSheetVerificationRunning
{
    get => _isBalanceSheetVerificationRunning;
    set { _isBalanceSheetVerificationRunning = value; OnPropertyChanged(); }
}

public Func<BalanceSheetVerificationRequest, CancellationToken, Task<BalanceSheetReport>>? BalanceSheetVerificationRunner { get; set; }
public ICommand RunBalanceSheetVerificationCommand { get; }
```

Initialize command in constructor:

```csharp
RunBalanceSheetVerificationCommand = new RelayCommand(() => _ = RunBalanceSheetVerificationAsync(), () => !IsBalanceSheetVerificationRunning);
```

Add method:

```csharp
public async Task RunBalanceSheetVerificationAsync()
{
    if (BalanceSheetSelectedCompany == null)
    {
        ShowToast("Select Sync Job", "Choose a Sync Job before running Balance Sheet Verification.", "warn");
        return;
    }

    IsBalanceSheetVerificationRunning = true;
    try
    {
        var request = new BalanceSheetVerificationRequest
        {
            CompanyProfileId = BalanceSheetSelectedCompany.Id,
            FinancialYearStart = BalanceSheetFinancialYearStart.Date,
            AsAtDate = BalanceSheetAsAtDate.Date
        };

        var runner = BalanceSheetVerificationRunner;
        BalanceSheetReport result;
        if (runner != null)
        {
            result = await runner(request, _asyncOpsCts.Token);
        }
        else
        {
            var service = new BalanceSheetVerificationService(_repo);
            result = await service.GenerateAsync(request, _asyncOpsCts.Token);
        }

        InvokeOnDispatcher(() =>
        {
            BalanceSheetReport = result;
            ShowToast("Balance Sheet Ready", result.Status, result.Status == "failed" ? "err" : result.Status == "out_of_balance" ? "warn" : "ok");
        });
    }
    catch (OperationCanceledException)
    {
    }
    catch (Exception ex)
    {
        InvokeOnDispatcher(() => ShowToast("Balance Sheet Failed", ex.Message, "err"));
    }
    finally
    {
        InvokeOnDispatcher(() => IsBalanceSheetVerificationRunning = false);
    }
}
```

Update `LoadConfiguration()` to clear and refill `BalanceSheetVerificationHistory` from `_repo.GetRecentBalanceSheetVerificationRuns(50)`.

- [ ] **Step 4: Run ViewModel test**

Run:

```powershell
dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore --filter RunBalanceSheetVerificationAsync_UsesSelectedCompanyAndStoresReport
```

Expected: pass.

- [ ] **Step 5: Commit**

```powershell
git add src/TallyDbLoader.Wpf/MainViewModel.cs tests/TallyDbLoader.Tests/MainViewModelTests.cs
git commit -m "feat: wire balance sheet verification view model"
```

---

### Task 10: WPF Balance Sheet Page and Navigation

**Files:**
- Create: `src/TallyDbLoader.Wpf/Views/BalanceSheetVerificationPage.xaml`
- Create: `src/TallyDbLoader.Wpf/Views/BalanceSheetVerificationPage.xaml.cs`
- Modify: `src/TallyDbLoader.Wpf/MainWindow.xaml`
- Modify: `src/TallyDbLoader.Wpf/MainWindow.xaml.cs`
- Modify: `src/TallyDbLoader.Wpf/Converters/StatusToToneConverter.cs`

- [ ] **Step 1: Add the page**

Create `src/TallyDbLoader.Wpf/Views/BalanceSheetVerificationPage.xaml`:

```xml
<Page x:Class="TallyDbLoader.Wpf.Views.BalanceSheetVerificationPage"
      xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      DataContext="{Binding}"
      Title="Balance Sheet Verification">
    <Grid Margin="24">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
        </Grid.RowDefinitions>

        <StackPanel Grid.Row="0" Margin="0,0,0,16">
            <TextBlock Text="Balance Sheet Verification" Style="{StaticResource DisplayTextStyle}"/>
            <TextBlock Text="Compute a Tally-style Balance Sheet from the synced target database." Style="{StaticResource CaptionTextStyle}"/>
        </StackPanel>

        <Border Grid.Row="1" Background="{DynamicResource Layer2Brush}" BorderBrush="{DynamicResource BorderBrush}" BorderThickness="1" CornerRadius="4" Padding="12" Margin="0,0,0,16">
            <Grid>
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="2*"/>
                    <ColumnDefinition Width="Auto"/>
                    <ColumnDefinition Width="Auto"/>
                    <ColumnDefinition Width="Auto"/>
                </Grid.ColumnDefinitions>

                <ComboBox Grid.Column="0" ItemsSource="{Binding Companies}" SelectedItem="{Binding BalanceSheetSelectedCompany}" DisplayMemberPath="Name" MinWidth="260" Margin="0,0,12,0"/>
                <DatePicker Grid.Column="1" SelectedDate="{Binding BalanceSheetFinancialYearStart}" Width="150" Margin="0,0,12,0"/>
                <DatePicker Grid.Column="2" SelectedDate="{Binding BalanceSheetAsAtDate}" Width="150" Margin="0,0,12,0"/>
                <Button Grid.Column="3" Content="Run" Command="{Binding RunBalanceSheetVerificationCommand}" Style="{StaticResource PrimaryButtonStyle}"/>
            </Grid>
        </Border>

        <Grid Grid.Row="2">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*"/>
                <ColumnDefinition Width="*"/>
            </Grid.ColumnDefinitions>

            <Border Grid.Column="0" Style="{StaticResource FluentCardStyle}" Margin="0,0,8,0">
                <DockPanel>
                    <TextBlock DockPanel.Dock="Top" Text="Liabilities" Style="{StaticResource SubtitleTextStyle}" FontSize="18" Margin="0,0,0,12"/>
                    <TextBlock DockPanel.Dock="Bottom" Text="{Binding BalanceSheetReport.LiabilityTotal, Converter={StaticResource IndianCurrencyConverter}}" HorizontalAlignment="Right" FontWeight="Bold" FontSize="16"/>
                    <ItemsControl ItemsSource="{Binding BalanceSheetReport.LiabilitySide.Lines}">
                        <ItemsControl.ItemTemplate>
                            <DataTemplate>
                                <Grid Margin="0,2">
                                    <TextBlock Text="{Binding Name}" FontWeight="SemiBold"/>
                                    <TextBlock Text="{Binding Amount, Converter={StaticResource IndianCurrencyConverter}}" HorizontalAlignment="Right" FontWeight="SemiBold"/>
                                </Grid>
                            </DataTemplate>
                        </ItemsControl.ItemTemplate>
                    </ItemsControl>
                </DockPanel>
            </Border>

            <Border Grid.Column="1" Style="{StaticResource FluentCardStyle}" Margin="8,0,0,0">
                <DockPanel>
                    <TextBlock DockPanel.Dock="Top" Text="Assets" Style="{StaticResource SubtitleTextStyle}" FontSize="18" Margin="0,0,0,12"/>
                    <TextBlock DockPanel.Dock="Bottom" Text="{Binding BalanceSheetReport.AssetTotal, Converter={StaticResource IndianCurrencyConverter}}" HorizontalAlignment="Right" FontWeight="Bold" FontSize="16"/>
                    <ItemsControl ItemsSource="{Binding BalanceSheetReport.AssetSide.Lines}">
                        <ItemsControl.ItemTemplate>
                            <DataTemplate>
                                <Grid Margin="0,2">
                                    <TextBlock Text="{Binding Name}" FontWeight="SemiBold"/>
                                    <TextBlock Text="{Binding Amount, Converter={StaticResource IndianCurrencyConverter}}" HorizontalAlignment="Right" FontWeight="SemiBold"/>
                                </Grid>
                            </DataTemplate>
                        </ItemsControl.ItemTemplate>
                    </ItemsControl>
                </DockPanel>
            </Border>
        </Grid>
    </Grid>
</Page>
```

Create `src/TallyDbLoader.Wpf/Views/BalanceSheetVerificationPage.xaml.cs`:

```csharp
using System.Windows.Controls;

namespace TallyDbLoader.Wpf.Views
{
    public partial class BalanceSheetVerificationPage : Page
    {
        public BalanceSheetVerificationPage() => InitializeComponent();
    }
}
```

- [ ] **Step 2: Add navigation route and button**

In `src/TallyDbLoader.Wpf/MainWindow.xaml`, add a navigation button after `Run History`:

```xml
<Button Content="Balance Sheet" Style="{StaticResource NavButtonStyle}" Command="{Binding NavigateCommand}" CommandParameter="{x:Static local:RouteScreen.BalanceSheetVerification}"/>
```

In `src/TallyDbLoader.Wpf/MainWindow.xaml.cs`, add switch case:

```csharp
case RouteScreen.BalanceSheetVerification:
    page = new BalanceSheetVerificationPage();
    break;
```

In `MainViewModel.ExecuteNavigate`, include `RouteScreen.BalanceSheetVerification` in the reset-stack set.

- [ ] **Step 3: Update status tone converter**

In `src/TallyDbLoader.Wpf/Converters/StatusToToneConverter.cs`, update the status checks:

```csharp
if (status == "ok" || status == "success" || status == "healthy" || status == "running" || status == "completed" || status == "balanced")
    return GetFrozenBrush("#16a34a");
if (status == "warn" || status == "warning" || status == "paused" || status == "stale" || status == "attention_required" || status == "review_required" || status == "out_of_balance")
    return GetFrozenBrush("#d97706");
```

- [ ] **Step 4: Build WPF project**

Run:

```powershell
dotnet build src/TallyDbLoader.sln
```

Expected: build succeeds.

- [ ] **Step 5: Commit**

```powershell
git add src/TallyDbLoader.Wpf/Views/BalanceSheetVerificationPage.xaml src/TallyDbLoader.Wpf/Views/BalanceSheetVerificationPage.xaml.cs src/TallyDbLoader.Wpf/MainWindow.xaml src/TallyDbLoader.Wpf/MainWindow.xaml.cs src/TallyDbLoader.Wpf/MainViewModel.cs src/TallyDbLoader.Wpf/Converters/StatusToToneConverter.cs
git commit -m "feat: add balance sheet verification page"
```

---

### Task 11: Final Test Pass and Documentation Linkage

**Files:**
- Modify: `docs/release-history.md`

- [ ] **Step 1: Add release history note**

Append this bullet under the `Added:` list for `**Version: 2.1.0-beta [17-Jun-2026]**<br>` in `docs/release-history.md`:

```markdown
* Added manual Balance Sheet Verification for the WPF app. The report computes a Tally-style top-level Balance Sheet from synced target database data with adjustable Financial Year Start and As At Date.
```

- [ ] **Step 2: Run full fast test suite**

Run:

```powershell
dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore
```

Expected: all default tests pass. Any opt-in live provider tests remain skipped when environment variables are not configured.

- [ ] **Step 3: Build solution**

Run:

```powershell
dotnet build src/TallyDbLoader.sln
```

Expected: build succeeds with no new errors.

- [ ] **Step 4: Commit**

```powershell
git add docs/release-history.md
git commit -m "docs: note balance sheet verification feature"
```

- [ ] **Step 5: Final status check**

Run:

```powershell
git status --short
```

Expected: only unrelated pre-existing untracked files remain, such as runtime logs or screenshots.

---

## Plan Self-Review

Spec coverage:

- Manual WPF report: Task 9 and Task 10.
- Adjustable Financial Year Start and As At Date: Task 9 and Task 10.
- Core-only calculation logic: Task 6 and Task 7.
- SQLite history through `IConfigRepository`: Task 2 and Task 7.
- Provider adapters for SQLite, MSSQL, PostgreSQL, MySQL: Task 5.
- Identifier validation for schema/prefix/table names: Task 4 and Task 5.
- Async execution and cancellation: Task 3, Task 5, Task 7, Task 9.
- Indian number formatting: Task 8 and Task 10.
- Investments group: Task 6.
- Stock-in-Hand and Profit & Loss rules: Task 6.
- Balance tolerance: Task 1 and Task 6.
- Tests and final verification: Task 11.

Execution notes:

- Keep commits task-sized.
- Do not add automatic post-sync gating.
- Do not write to synced target tables.
- Do not introduce drilldown or Tally-side statement comparison in this implementation.
