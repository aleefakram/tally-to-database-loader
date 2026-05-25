# .NET Sync Parity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Bring `TallyDbLoader.Core` sync engine to behavioral parity with the Node loader for full and incremental sync, with no data loss.

**Architecture:** Split sync into `FullSyncRunner` and `IncrementalSyncRunner`, with shared helpers `CompanyInfoFetcher`, `WatermarkRepository`, `StagingTableManager`, `WatermarkXmlBuilder`. `BackgroundSyncWorker.SyncCompany` becomes a thin dispatcher branching on `company.Mode`. Mirror Node's `_diff`/`_delete` staging-table algorithm from `src/tally.ts:88-308`.

**Tech Stack:** C# / .NET 8, xUnit, Microsoft.Data.Sqlite (test target DB), Npgsql / MySqlConnector / Microsoft.Data.SqlClient (production DB drivers), YamlDotNet.

**Spec:** `docs/superpowers/specs/2026-05-25-dotnet-sync-parity-design.md`

---

## Task Index

- Task 1: `WatermarkXmlBuilder` — TDL XML for `$AltMstId,$AltVchId`
- Task 2: `TallyClient.FetchCompanyInfoAsync` — populate `BooksFrom`/`BooksTo`/AlterIDs
- Task 3: `CompanyInfoFetcher` — issue request, parse, persist to target `config` table
- Task 4: `WatermarkRepository` — read/write `Last AlterID *` rows
- Task 5: `StagingTableManager` — create/truncate `_diff`, `_delete`, `_vchnumber`, `config`
- Task 6: `IDatabaseLoader` per-DB SQL methods (`TruncateSql`, `CascadeUpdateSql`, `VoucherNumberUpdateSql`, `CountAutoNumberVoucherTypesSql`)
- Task 7: `FakeTallyClient` test helper
- Task 8: `FullSyncRunner` — truncate + reload, no duplicates
- Task 9: `IncrementalSyncRunner` part A — diff/delete/cascade-delete phase
- Task 10: `IncrementalSyncRunner` part B — refetch with `$AlterID >` filter
- Task 11: `IncrementalSyncRunner` part C — cascade-update + voucher number refresh
- Task 12: `IncrementalSyncRunner` part D — watermark commit + staging truncate
- Task 13: `BackgroundSyncWorker.SyncCompany` — dispatch on `company.Mode`
- Task 14: End-to-end scenario tests (insert/update/delete/cascade/auto-number/failure)

Each task = TDD cycle (failing test → impl → green → commit). Commit after each task.

---

## File Map

**New files:**
- `src/TallyDbLoader.Core/Tally/WatermarkXmlBuilder.cs`
- `src/TallyDbLoader.Core/Tally/CompanyInfoFetcher.cs`
- `src/TallyDbLoader.Core/Sync/WatermarkRepository.cs`
- `src/TallyDbLoader.Core/Sync/StagingTableManager.cs`
- `src/TallyDbLoader.Core/Sync/FullSyncRunner.cs`
- `src/TallyDbLoader.Core/Sync/IncrementalSyncRunner.cs`
- `tests/TallyDbLoader.Tests/Fakes/FakeTallyClient.cs`
- `tests/TallyDbLoader.Tests/WatermarkXmlBuilderTests.cs`
- `tests/TallyDbLoader.Tests/CompanyInfoFetcherTests.cs`
- `tests/TallyDbLoader.Tests/WatermarkRepositoryTests.cs`
- `tests/TallyDbLoader.Tests/StagingTableManagerTests.cs`
- `tests/TallyDbLoader.Tests/FullSyncRunnerTests.cs`
- `tests/TallyDbLoader.Tests/IncrementalSyncRunnerTests.cs`

**Modified files:**
- `src/TallyDbLoader.Core/Tally/TallyClient.cs` — add `FetchCompanyInfoAsync`, extract `ITallyClient`
- `src/TallyDbLoader.Core/Tally/TallyCompanyInfo.cs` — add `AltMstId`, `AltVchId` properties (BooksFrom/BooksTo already exist)
- `src/TallyDbLoader.Core/DatabaseLoaders/IDatabaseLoader.cs` — add per-DB SQL methods
- `src/TallyDbLoader.Core/DatabaseLoaders/MSSqlLoader.cs`, `MySqlLoader.cs`, `PostgreSqlLoader.cs`, `SqliteLoader.cs` — implement new methods
- `src/TallyDbLoader.Core/Sync/BackgroundSyncWorker.cs` — replace `SyncCompany` body with dispatch

---

### Task 1: WatermarkXmlBuilder

**Files:**
- Create: `src/TallyDbLoader.Core/Tally/WatermarkXmlBuilder.cs`
- Test: `tests/TallyDbLoader.Tests/WatermarkXmlBuilderTests.cs`

The reference XML is from `src/tally.ts:416`. Output a single static string; if a company name is supplied, substitute `##SVCurrentCompany` with `"escapedName"`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/TallyDbLoader.Tests/WatermarkXmlBuilderTests.cs
using Xunit;
using TallyDbLoader.Core.Tally;

public class WatermarkXmlBuilderTests
{
    [Fact]
    public void Build_NoCompanyName_KeepsSvCurrentCompany()
    {
        var xml = WatermarkXmlBuilder.Build(null);
        Assert.Contains("##SVCurrentCompany", xml);
        Assert.Contains("$AltMstId", xml);
        Assert.Contains("$AltVchId", xml);
        Assert.Contains("ASCII (Comma Delimited)", xml);
    }

    [Fact]
    public void Build_WithCompanyName_SubstitutesAndEscapes()
    {
        var xml = WatermarkXmlBuilder.Build("Acme & Co");
        Assert.DoesNotContain("##SVCurrentCompany", xml);
        Assert.Contains("\"Acme &amp; Co\"", xml);
    }
}
```

- [ ] **Step 2: Run test, expect FAIL (class not defined)**

Run: `dotnet test tests/TallyDbLoader.Tests --filter WatermarkXmlBuilderTests`
Expected: compile error / class not found.

- [ ] **Step 3: Implement**

```csharp
// src/TallyDbLoader.Core/Tally/WatermarkXmlBuilder.cs
using System.Net;

namespace TallyDbLoader.Core.Tally
{
    public static class WatermarkXmlBuilder
    {
        private const string Template =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><ENVELOPE><HEADER><VERSION>1</VERSION>" +
            "<TALLYREQUEST>Export</TALLYREQUEST><TYPE>Data</TYPE><ID>MyReport</ID></HEADER>" +
            "<BODY><DESC><STATICVARIABLES><SVEXPORTFORMAT>ASCII (Comma Delimited)</SVEXPORTFORMAT>" +
            "</STATICVARIABLES><TDL><TDLMESSAGE><REPORT NAME=\"MyReport\"><FORMS>MyForm</FORMS></REPORT>" +
            "<FORM NAME=\"MyForm\"><PARTS>MyPart</PARTS></FORM>" +
            "<PART NAME=\"MyPart\"><LINES>MyLine</LINES><REPEAT>MyLine : MyCollection</REPEAT>" +
            "<SCROLLED>Vertical</SCROLLED></PART>" +
            "<LINE NAME=\"MyLine\"><FIELDS>FldAlterMaster,FldAlterTransaction</FIELDS></LINE>" +
            "<FIELD NAME=\"FldAlterMaster\"><SET>$AltMstId</SET></FIELD>" +
            "<FIELD NAME=\"FldAlterTransaction\"><SET>$AltVchId</SET></FIELD>" +
            "<COLLECTION NAME=\"MyCollection\"><TYPE>Company</TYPE>" +
            "<FILTER>FilterActiveCompany</FILTER></COLLECTION>" +
            "<SYSTEM TYPE=\"Formulae\" NAME=\"FilterActiveCompany\">$$IsEqual:##SVCurrentCompany:$Name</SYSTEM>" +
            "</TDLMESSAGE></TDL></DESC></BODY></ENVELOPE>";

        public static string Build(string? companyName)
        {
            if (string.IsNullOrEmpty(companyName)) return Template;
            var escaped = WebUtility.HtmlEncode(companyName);
            return Template.Replace("##SVCurrentCompany", $"\"{escaped}\"");
        }
    }
}
```

- [ ] **Step 4: Run test, expect PASS**

Run: `dotnet test tests/TallyDbLoader.Tests --filter WatermarkXmlBuilderTests`
Expected: 2 passed.

- [ ] **Step 5: Commit**

```bash
git add src/TallyDbLoader.Core/Tally/WatermarkXmlBuilder.cs tests/TallyDbLoader.Tests/WatermarkXmlBuilderTests.cs
git commit -m "feat(sync): add WatermarkXmlBuilder for AltMstId/AltVchId query"
```

---

### Task 2: TallyClient.FetchCompanyInfoAsync + ITallyClient extraction

**Files:**
- Modify: `src/TallyDbLoader.Core/Tally/TallyClient.cs`
- Modify: `src/TallyDbLoader.Core/Tally/TallyCompanyInfo.cs`
- Create: `src/TallyDbLoader.Core/Tally/ITallyClient.cs`
- Test: `tests/TallyDbLoader.Tests/TallyClientTests.cs` (extend)

`TallyDatabaseLoaderReport` XML reference is `src/tally.ts:578`. Returns one CSV-ish row terminated by `,"†",\r\n`. Fields in order: `Guid, Name, BooksFromYYYYMMDD, LastVoucherDateYYYYMMDD, AltMstId, AltVchId`.

- [ ] **Step 1: Add fields to TallyCompanyInfo**

Open `src/TallyDbLoader.Core/Tally/TallyCompanyInfo.cs` and add:

```csharp
public long AltMstId { get; set; }
public long AltVchId { get; set; }
```

(`BooksFrom`, `BooksTo`, `Name`, `IsGroup` already exist per existing usage in `BackgroundSyncWorker.cs:433-434`.)

- [ ] **Step 2: Extract ITallyClient interface**

```csharp
// src/TallyDbLoader.Core/Tally/ITallyClient.cs
using System.Collections.Generic;
using System.Threading.Tasks;

namespace TallyDbLoader.Core.Tally
{
    public interface ITallyClient
    {
        Task<string> PostXMLAsync(string xmlRequest);
        Task<List<TallyCompanyInfo>> FetchActiveCompaniesDetailedAsync();
        Task<List<string>> FetchActiveCompaniesAsync();
        Task<TallyCompanyInfo> FetchCompanyInfoAsync(string? companyName);
    }
}
```

Make `TallyClient : ITallyClient`.

- [ ] **Step 3: Write failing test for FetchCompanyInfoAsync**

```csharp
// add to tests/TallyDbLoader.Tests/TallyClientTests.cs
[Fact]
public async Task FetchCompanyInfoAsync_ParsesAllSixFields()
{
    var sampleResponse =
        "\"abc-guid\",\"Acme Ltd\",\"20240401\",\"20250320\",\"12345\",\"67890\",\"†\",\r\n";
    var handler = new StubHttpMessageHandler(sampleResponse);
    var client = new TallyClient(new HttpClient(handler), "localhost", 9000);

    var info = await client.FetchCompanyInfoAsync(null);

    Assert.Equal("Acme Ltd", info.Name);
    Assert.Equal(new DateTime(2024, 4, 1), info.BooksFrom);
    Assert.Equal(new DateTime(2025, 3, 20), info.BooksTo);
    Assert.Equal(12345, info.AltMstId);
    Assert.Equal(67890, info.AltVchId);
}

[Fact]
public async Task FetchCompanyInfoAsync_EmptyResponse_Throws()
{
    var handler = new StubHttpMessageHandler("");
    var client = new TallyClient(new HttpClient(handler), "localhost", 9000);

    await Assert.ThrowsAsync<InvalidOperationException>(
        () => client.FetchCompanyInfoAsync(null));
}

// StubHttpMessageHandler: minimal HttpMessageHandler that returns a fixed string body.
private sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly string _body;
    public StubHttpMessageHandler(string body) { _body = body; }
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
        => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(_body, System.Text.Encoding.Unicode, "text/xml")
        });
}
```

- [ ] **Step 4: Run test, expect FAIL**

Run: `dotnet test tests/TallyDbLoader.Tests --filter FetchCompanyInfoAsync`
Expected: method not defined.

- [ ] **Step 5: Implement FetchCompanyInfoAsync**

Add to `TallyClient.cs`:

```csharp
public async Task<TallyCompanyInfo> FetchCompanyInfoAsync(string? companyName)
{
    var xml = BuildCompanyInfoXml(companyName);
    var response = await PostXMLAsync(xml);

    if (!response.EndsWith(",\"†\",\r\n"))
        throw new InvalidOperationException(
            string.IsNullOrEmpty(companyName)
                ? "No company open in Tally"
                : $"Specified company \"{companyName}\" is closed in Tally");

    var trimmed = response.Replace("\",\"†\",\r\n", "").Substring(1);
    var parts = trimmed.Split(new[] { "\",\"" }, StringSplitOptions.None);
    if (parts.Length < 6)
        throw new InvalidOperationException("Unexpected company info response shape");

    return new TallyCompanyInfo
    {
        Name = parts[1].Replace("'", "\\\""),
        BooksFrom = ParseYyyymmdd(parts[2]),
        BooksTo = ParseYyyymmdd(parts[3]),
        AltMstId = long.TryParse(parts[4], out var m) ? m : 0,
        AltVchId = long.TryParse(parts[5], out var v) ? v : 0
    };
}

private static DateTime? ParseYyyymmdd(string s)
{
    if (s.Length != 8) return null;
    if (!int.TryParse(s.Substring(0, 4), out var y)) return null;
    if (!int.TryParse(s.Substring(4, 2), out var mo)) return null;
    if (!int.TryParse(s.Substring(6, 2), out var d)) return null;
    try { return new DateTime(y, mo, d); } catch { return null; }
}

private static string BuildCompanyInfoXml(string? companyName)
{
    const string template = /* exact XML from src/tally.ts:578, single line */ "...";
    if (string.IsNullOrEmpty(companyName)) return template;
    var escaped = System.Net.WebUtility.HtmlEncode(companyName);
    return template.Replace("##SVCurrentCompany", $"\"{escaped}\"");
}
```

The full template string is the XML literal from `src/tally.ts:578` — copy verbatim, replacing TypeScript backticks with C# `"..."` and escaping inner double-quotes. (Same shape as the WatermarkXmlBuilder template but with the seven `Fld*` fields from the Node source.)

- [ ] **Step 6: Run tests, expect PASS**

Run: `dotnet test tests/TallyDbLoader.Tests --filter TallyClient`
Expected: all green.

- [ ] **Step 7: Commit**

```bash
git add src/TallyDbLoader.Core/Tally/ITallyClient.cs src/TallyDbLoader.Core/Tally/TallyClient.cs src/TallyDbLoader.Core/Tally/TallyCompanyInfo.cs tests/TallyDbLoader.Tests/TallyClientTests.cs
git commit -m "feat(sync): TallyClient.FetchCompanyInfoAsync with AlterID + dates"
```

---

### Task 3: CompanyInfoFetcher

**Files:**
- Create: `src/TallyDbLoader.Core/Tally/CompanyInfoFetcher.cs`
- Test: `tests/TallyDbLoader.Tests/CompanyInfoFetcherTests.cs`

Responsibility: call `ITallyClient.FetchCompanyInfoAsync`, then persist `Update Timestamp`, `Company Name`, `Period From`, `Period To`, `Last AlterID Master`, `Last AlterID Transaction` rows into target DB's `config` table. Mirrors `src/tally.ts:595-599`.

- [ ] **Step 1: Failing test**

```csharp
// tests/TallyDbLoader.Tests/CompanyInfoFetcherTests.cs
using Microsoft.Data.Sqlite;
using TallyDbLoader.Core.Tally;
using TallyDbLoader.Core.Sync;
using TallyDbLoader.Tests.Fakes;   // FakeTallyClient — created in Task 7; for now stub locally

public class CompanyInfoFetcherTests
{
    [Fact]
    public async Task FetchAndPersist_WritesAllSixConfigRows()
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "CREATE TABLE config (name TEXT PRIMARY KEY, value TEXT)";
            cmd.ExecuteNonQuery();
        }

        var fake = new InlineFakeTallyClient(new TallyCompanyInfo
        {
            Name = "Acme",
            BooksFrom = new DateTime(2024, 4, 1),
            BooksTo = new DateTime(2025, 3, 20),
            AltMstId = 1000,
            AltVchId = 2000
        });

        var fetcher = new CompanyInfoFetcher(fake);
        var info = await fetcher.FetchAndPersist("Acme", conn);

        Assert.Equal(1000, info.AltMstId);
        using var read = conn.CreateCommand();
        read.CommandText = "SELECT value FROM config WHERE name = 'Last AlterID Master'";
        Assert.Equal("1000", read.ExecuteScalar()?.ToString());

        read.CommandText = "SELECT value FROM config WHERE name = 'Period From'";
        Assert.Equal("2024-04-01", read.ExecuteScalar()?.ToString());
    }

    private sealed class InlineFakeTallyClient : ITallyClient
    {
        private readonly TallyCompanyInfo _info;
        public InlineFakeTallyClient(TallyCompanyInfo info) { _info = info; }
        public Task<TallyCompanyInfo> FetchCompanyInfoAsync(string? n) => Task.FromResult(_info);
        public Task<string> PostXMLAsync(string x) => Task.FromResult("");
        public Task<List<TallyCompanyInfo>> FetchActiveCompaniesDetailedAsync() => Task.FromResult(new List<TallyCompanyInfo>());
        public Task<List<string>> FetchActiveCompaniesAsync() => Task.FromResult(new List<string>());
    }
}
```

- [ ] **Step 2: Run test, expect FAIL** (`CompanyInfoFetcher` not defined)

Run: `dotnet test tests/TallyDbLoader.Tests --filter CompanyInfoFetcherTests`

- [ ] **Step 3: Implement**

```csharp
// src/TallyDbLoader.Core/Tally/CompanyInfoFetcher.cs
using System.Data.Common;
using System.Threading.Tasks;

namespace TallyDbLoader.Core.Sync
{
    public class CompanyInfoFetcher
    {
        private readonly TallyDbLoader.Core.Tally.ITallyClient _tally;
        public CompanyInfoFetcher(TallyDbLoader.Core.Tally.ITallyClient tally) { _tally = tally; }

        public async Task<TallyDbLoader.Core.Tally.TallyCompanyInfo> FetchAndPersist(
            string companyName, DbConnection targetConn)
        {
            var info = await _tally.FetchCompanyInfoAsync(companyName);

            using var del = targetConn.CreateCommand();
            del.CommandText = "DELETE FROM config";
            del.ExecuteNonQuery();

            void Insert(string name, string value)
            {
                using var ins = targetConn.CreateCommand();
                ins.CommandText = "INSERT INTO config(name, value) VALUES ($n, $v)";
                var pn = ins.CreateParameter(); pn.ParameterName = "$n"; pn.Value = name; ins.Parameters.Add(pn);
                var pv = ins.CreateParameter(); pv.ParameterName = "$v"; pv.Value = value; ins.Parameters.Add(pv);
                ins.ExecuteNonQuery();
            }

            Insert("Update Timestamp", System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            Insert("Company Name", info.Name ?? "");
            Insert("Period From", info.BooksFrom?.ToString("yyyy-MM-dd") ?? "");
            Insert("Period To", info.BooksTo?.ToString("yyyy-MM-dd") ?? "");
            Insert("Last AlterID Master", info.AltMstId.ToString());
            Insert("Last AlterID Transaction", info.AltVchId.ToString());

            return info;
        }
    }
}
```

- [ ] **Step 4: Run, expect PASS**

- [ ] **Step 5: Commit**

```bash
git add src/TallyDbLoader.Core/Tally/CompanyInfoFetcher.cs tests/TallyDbLoader.Tests/CompanyInfoFetcherTests.cs
git commit -m "feat(sync): CompanyInfoFetcher persists company metadata + watermarks"
```

---

### Task 4: WatermarkRepository

**Files:**
- Create: `src/TallyDbLoader.Core/Sync/WatermarkRepository.cs`
- Test: `tests/TallyDbLoader.Tests/WatermarkRepositoryTests.cs`

Read `Last AlterID Master` / `Last AlterID Transaction` from target DB `config` table; write them back at end of sync.

- [ ] **Step 1: Failing test**

```csharp
// tests/TallyDbLoader.Tests/WatermarkRepositoryTests.cs
using Microsoft.Data.Sqlite;
using TallyDbLoader.Core.Sync;

public class WatermarkRepositoryTests
{
    [Fact]
    public void RoundTrip()
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "CREATE TABLE config (name TEXT PRIMARY KEY, value TEXT)";
            cmd.ExecuteNonQuery();
        }

        var repo = new WatermarkRepository(conn);
        Assert.Equal((0, 0), repo.Read());

        repo.Write(masterAlterId: 42, transactionAlterId: 99);
        Assert.Equal((42L, 99L), repo.Read());

        repo.Write(masterAlterId: 50, transactionAlterId: 99);
        Assert.Equal((50L, 99L), repo.Read());   // upsert behaviour
    }
}
```

- [ ] **Step 2: Run, expect FAIL**

- [ ] **Step 3: Implement**

```csharp
// src/TallyDbLoader.Core/Sync/WatermarkRepository.cs
using System.Data.Common;

namespace TallyDbLoader.Core.Sync
{
    public class WatermarkRepository
    {
        private readonly DbConnection _conn;
        public WatermarkRepository(DbConnection conn) { _conn = conn; }

        public (long master, long transaction) Read()
        {
            return (
                ReadOne("Last AlterID Master"),
                ReadOne("Last AlterID Transaction"));
        }

        public void Write(long masterAlterId, long transactionAlterId)
        {
            Upsert("Last AlterID Master", masterAlterId.ToString());
            Upsert("Last AlterID Transaction", transactionAlterId.ToString());
        }

        private long ReadOne(string name)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM config WHERE name = $n";
            var p = cmd.CreateParameter(); p.ParameterName = "$n"; p.Value = name; cmd.Parameters.Add(p);
            var raw = cmd.ExecuteScalar()?.ToString();
            return long.TryParse(raw, out var v) ? v : 0;
        }

        private void Upsert(string name, string value)
        {
            using var del = _conn.CreateCommand();
            del.CommandText = "DELETE FROM config WHERE name = $n";
            var p1 = del.CreateParameter(); p1.ParameterName = "$n"; p1.Value = name; del.Parameters.Add(p1);
            del.ExecuteNonQuery();

            using var ins = _conn.CreateCommand();
            ins.CommandText = "INSERT INTO config(name, value) VALUES ($n, $v)";
            var p2 = ins.CreateParameter(); p2.ParameterName = "$n"; p2.Value = name; ins.Parameters.Add(p2);
            var p3 = ins.CreateParameter(); p3.ParameterName = "$v"; p3.Value = value; ins.Parameters.Add(p3);
            ins.ExecuteNonQuery();
        }
    }
}
```

- [ ] **Step 4: Run, expect PASS**

- [ ] **Step 5: Commit**

```bash
git add src/TallyDbLoader.Core/Sync/WatermarkRepository.cs tests/TallyDbLoader.Tests/WatermarkRepositoryTests.cs
git commit -m "feat(sync): WatermarkRepository for Last AlterID rows"
```

---

### Task 5: StagingTableManager

**Files:**
- Create: `src/TallyDbLoader.Core/Sync/StagingTableManager.cs`
- Test: `tests/TallyDbLoader.Tests/StagingTableManagerTests.cs`

`EnsureStagingTables()` creates `_diff`, `_delete`, `_vchnumber`, `config` if not present. `TruncateStaging()` empties the three staging tables (not `config`).

- [ ] **Step 1: Failing test**

```csharp
public class StagingTableManagerTests
{
    [Fact]
    public void EnsureCreatesAllFourTables_Idempotent()
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        var mgr = new StagingTableManager(conn);

        mgr.EnsureStagingTables();
        mgr.EnsureStagingTables();   // second call must not throw

        foreach (var t in new[] { "_diff", "_delete", "_vchnumber", "config" })
        {
            using var c = conn.CreateCommand();
            c.CommandText = $"SELECT COUNT(*) FROM {t}";
            Assert.Equal(0L, (long)c.ExecuteScalar()!);
        }
    }

    [Fact]
    public void TruncateStaging_ClearsStagingButPreservesConfig()
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        var mgr = new StagingTableManager(conn);
        mgr.EnsureStagingTables();

        using (var c = conn.CreateCommand())
        {
            c.CommandText = "INSERT INTO _diff(guid, alterid) VALUES ('g', 1); " +
                            "INSERT INTO config(name, value) VALUES ('k', 'v')";
            c.ExecuteNonQuery();
        }

        mgr.TruncateStaging();

        using var c2 = conn.CreateCommand();
        c2.CommandText = "SELECT COUNT(*) FROM _diff";
        Assert.Equal(0L, (long)c2.ExecuteScalar()!);
        c2.CommandText = "SELECT COUNT(*) FROM config";
        Assert.Equal(1L, (long)c2.ExecuteScalar()!);
    }
}
```

- [ ] **Step 2: Run, expect FAIL**

- [ ] **Step 3: Implement**

```csharp
// src/TallyDbLoader.Core/Sync/StagingTableManager.cs
using System.Data.Common;

namespace TallyDbLoader.Core.Sync
{
    public class StagingTableManager
    {
        private readonly DbConnection _conn;
        public StagingTableManager(DbConnection conn) { _conn = conn; }

        public void EnsureStagingTables()
        {
            Exec("CREATE TABLE IF NOT EXISTS config (name VARCHAR(64) PRIMARY KEY, value VARCHAR(1024))");
            Exec("CREATE TABLE IF NOT EXISTS _diff (guid VARCHAR(64) PRIMARY KEY, alterid BIGINT)");
            Exec("CREATE TABLE IF NOT EXISTS _delete (guid VARCHAR(64) PRIMARY KEY)");
            Exec("CREATE TABLE IF NOT EXISTS _vchnumber (guid VARCHAR(64) PRIMARY KEY, voucher_number VARCHAR(64))");
        }

        public void TruncateStaging()
        {
            Exec("DELETE FROM _diff");
            Exec("DELETE FROM _delete");
            Exec("DELETE FROM _vchnumber");
        }

        private void Exec(string sql)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }
}
```

Note: For SQL Server / MySQL / Postgres in production, `CREATE TABLE IF NOT EXISTS` works on MySQL and Postgres; for SQL Server, `IDatabaseLoader.EnsureTableSql(name, ddl)` should be used. Defer SQL Server-specific DDL to Task 6 (route through `IDatabaseLoader.EnsureStagingTablesSql()` rather than hardcoding strings here). For now, the hardcoded SQL works for the test target (SQLite) and Postgres/MySQL; SQL Server adaptation happens in Task 6.

- [ ] **Step 4: Run, expect PASS**

- [ ] **Step 5: Commit**

```bash
git add src/TallyDbLoader.Core/Sync/StagingTableManager.cs tests/TallyDbLoader.Tests/StagingTableManagerTests.cs
git commit -m "feat(sync): StagingTableManager for _diff/_delete/_vchnumber/config"
```

---

### Task 6: Per-DB SQL methods on IDatabaseLoader

**Files:**
- Modify: `src/TallyDbLoader.Core/DatabaseLoaders/IDatabaseLoader.cs`
- Modify: `MSSqlLoader.cs`, `MySqlLoader.cs`, `PostgreSqlLoader.cs`, `SqliteLoader.cs`
- Test: `tests/TallyDbLoader.Tests/DatabaseLoaderTests.cs` (extend)

Reference SQL: `src/tally.ts:239-247` (cascade update per DB), `src/tally.ts:292-300` (voucher number update per DB), `src/tally.ts:256` (auto-number count).

- [ ] **Step 1: Failing tests asserting SQL strings**

Add to `DatabaseLoaderTests.cs`:

```csharp
[Fact]
public void MSSqlLoader_CascadeUpdateSql_MatchesNodeTemplate()
{
    var sut = new MSSqlLoader("Server=.;");
    var sql = sut.CascadeUpdateSql("mst_group", "mst_ledger", "parent");
    Assert.Equal(
        "update t set t.parent = s.name from mst_ledger as t join mst_group as s on s.guid = t._parent ;",
        sql);
}

[Fact]
public void MySqlLoader_CascadeUpdateSql_MatchesNodeTemplate()
{
    var sut = new MySqlLoader("Server=;");
    var sql = sut.CascadeUpdateSql("mst_group", "mst_ledger", "parent");
    Assert.Equal(
        "update mst_ledger as t join mst_group as s on s.guid = t._parent set t.parent = s.name ;",
        sql);
}

[Fact]
public void PostgreSqlLoader_CascadeUpdateSql_MatchesNodeTemplate()
{
    var sut = new PostgreSqlLoader("Host=;");
    var sql = sut.CascadeUpdateSql("mst_group", "mst_ledger", "parent");
    Assert.Equal(
        "update mst_ledger as t set parent = s.name from mst_group as s where s.guid = t._parent ;",
        sql);
}

[Fact]
public void MSSqlLoader_TruncateSql_UsesTruncate()
{
    Assert.Equal("truncate table foo", new MSSqlLoader("Server=.;").TruncateSql("foo"));
}

[Fact]
public void SqliteLoader_TruncateSql_UsesDelete()
{
    Assert.Equal("delete from foo", new SqliteLoader("Data Source=:memory:").TruncateSql("foo"));
}

[Fact]
public void VoucherNumberUpdateSql_PerDb()
{
    Assert.Equal(
        "update t set t.voucher_number = s.voucher_number from trn_voucher as t join _vchnumber as s on s.guid = t.guid;",
        new MSSqlLoader("Server=.;").VoucherNumberUpdateSql());
    Assert.Equal(
        "update trn_voucher as t join _vchnumber as s on s.guid = t.guid set t.voucher_number = s.voucher_number;",
        new MySqlLoader("Server=;").VoucherNumberUpdateSql());
    Assert.Equal(
        "update trn_voucher as t set voucher_number = s.voucher_number from _vchnumber as s where s.guid = t.guid;",
        new PostgreSqlLoader("Host=;").VoucherNumberUpdateSql());
}
```

- [ ] **Step 2: Run, expect FAIL**

- [ ] **Step 3: Extend interface**

```csharp
// src/TallyDbLoader.Core/DatabaseLoaders/IDatabaseLoader.cs
public interface IDatabaseLoader
{
    Task LoadBulkDataAsync(DataTable data, string tableName);   // existing
    string TruncateSql(string tableName);
    string CascadeUpdateSql(string primaryTable, string childTable, string field);
    string VoucherNumberUpdateSql();
    string CountAutoNumberVoucherTypesSql();
}
```

Add to each loader:

```csharp
// MSSqlLoader
public string TruncateSql(string t) => $"truncate table {t}";
public string CascadeUpdateSql(string p, string c, string f) =>
    $"update t set t.{f} = s.name from {c} as t join {p} as s on s.guid = t._{f} ;";
public string VoucherNumberUpdateSql() =>
    "update t set t.voucher_number = s.voucher_number from trn_voucher as t join _vchnumber as s on s.guid = t.guid;";
public string CountAutoNumberVoucherTypesSql() =>
    "select count(*) as c from mst_vouchertype where numbering_method like '%Auto%' ;";
```

```csharp
// MySqlLoader
public string TruncateSql(string t) => $"truncate table {t}";
public string CascadeUpdateSql(string p, string c, string f) =>
    $"update {c} as t join {p} as s on s.guid = t._{f} set t.{f} = s.name ;";
public string VoucherNumberUpdateSql() =>
    "update trn_voucher as t join _vchnumber as s on s.guid = t.guid set t.voucher_number = s.voucher_number;";
public string CountAutoNumberVoucherTypesSql() =>
    "select count(*) as c from mst_vouchertype where numbering_method like '%Auto%' ;";
```

```csharp
// PostgreSqlLoader
public string TruncateSql(string t) => $"truncate table {t}";
public string CascadeUpdateSql(string p, string c, string f) =>
    $"update {c} as t set {f} = s.name from {p} as s where s.guid = t._{f} ;";
public string VoucherNumberUpdateSql() =>
    "update trn_voucher as t set voucher_number = s.voucher_number from _vchnumber as s where s.guid = t.guid;";
public string CountAutoNumberVoucherTypesSql() =>
    "select count(*) as c from mst_vouchertype where numbering_method like '%Auto%' ;";
```

```csharp
// SqliteLoader (no TRUNCATE in SQLite)
public string TruncateSql(string t) => $"delete from {t}";
public string CascadeUpdateSql(string p, string c, string f) =>
    $"update {c} set {f} = (select name from {p} where guid = {c}._{f}) where exists (select 1 from {p} where guid = {c}._{f});";
public string VoucherNumberUpdateSql() =>
    "update trn_voucher set voucher_number = (select voucher_number from _vchnumber where guid = trn_voucher.guid) where exists (select 1 from _vchnumber where guid = trn_voucher.guid);";
public string CountAutoNumberVoucherTypesSql() =>
    "select count(*) as c from mst_vouchertype where numbering_method like '%Auto%' ;";
```

- [ ] **Step 4: Run, expect PASS**

- [ ] **Step 5: Commit**

```bash
git add src/TallyDbLoader.Core/DatabaseLoaders/ tests/TallyDbLoader.Tests/DatabaseLoaderTests.cs
git commit -m "feat(sync): per-DB SQL methods for truncate/cascade-update/voucher-refresh"
```

---

### Task 7: FakeTallyClient test helper

**Files:**
- Create: `tests/TallyDbLoader.Tests/Fakes/FakeTallyClient.cs`

Maps request "shapes" (substring match on a key like `"$AltMstId"` or a YAML table's collection name) to canned response strings. Tracks call count per key for assertions.

- [ ] **Step 1: Implement (no tests — it's a test helper)**

```csharp
// tests/TallyDbLoader.Tests/Fakes/FakeTallyClient.cs
using System.Collections.Generic;
using System.Threading.Tasks;
using TallyDbLoader.Core.Tally;

namespace TallyDbLoader.Tests.Fakes
{
    public class FakeTallyClient : ITallyClient
    {
        private readonly List<(string key, string response)> _responses = new();
        public Dictionary<string, int> CallCounts { get; } = new();
        public List<string> AllRequests { get; } = new();

        public TallyCompanyInfo CompanyInfo { get; set; } = new TallyCompanyInfo
        {
            Name = "TestCo",
            BooksFrom = new System.DateTime(2024, 4, 1),
            BooksTo = new System.DateTime(2025, 3, 31),
            AltMstId = 0,
            AltVchId = 0
        };

        public void Register(string requestKeySubstring, string response)
            => _responses.Add((requestKeySubstring, response));

        public Task<string> PostXMLAsync(string xmlRequest)
        {
            AllRequests.Add(xmlRequest);
            foreach (var (key, resp) in _responses)
            {
                if (xmlRequest.Contains(key))
                {
                    CallCounts[key] = CallCounts.GetValueOrDefault(key) + 1;
                    return Task.FromResult(resp);
                }
            }
            return Task.FromResult("");
        }

        public Task<TallyCompanyInfo> FetchCompanyInfoAsync(string? c) => Task.FromResult(CompanyInfo);
        public Task<List<TallyCompanyInfo>> FetchActiveCompaniesDetailedAsync() => Task.FromResult(new List<TallyCompanyInfo> { CompanyInfo });
        public Task<List<string>> FetchActiveCompaniesAsync() => Task.FromResult(new List<string> { CompanyInfo.Name });
    }
}
```

- [ ] **Step 2: Compile**

Run: `dotnet build tests/TallyDbLoader.Tests`
Expected: success.

- [ ] **Step 3: Commit**

```bash
git add tests/TallyDbLoader.Tests/Fakes/FakeTallyClient.cs
git commit -m "test: add FakeTallyClient helper"
```

---

### Task 8: FullSyncRunner

**Files:**
- Create: `src/TallyDbLoader.Core/Sync/FullSyncRunner.cs`
- Test: `tests/TallyDbLoader.Tests/FullSyncRunnerTests.cs`

Replaces the current inline loop in `BackgroundSyncWorker.SyncCompany:361-375`. Adds the missing `TRUNCATE` before each `LoadBulkDataAsync`.

- [ ] **Step 1: Failing test — re-run does not duplicate**

```csharp
// tests/TallyDbLoader.Tests/FullSyncRunnerTests.cs
[Fact]
public async Task Run_TwiceWithSameData_DoesNotDuplicateRows()
{
    // Setup: in-memory SQLite + a single-table YAML config + FakeTallyClient with one row
    using var conn = new SqliteConnection("Data Source=:memory:");
    conn.Open();
    using (var c = conn.CreateCommand())
    {
        c.CommandText = "CREATE TABLE mst_group (guid TEXT, name TEXT, alterid INTEGER)";
        c.ExecuteNonQuery();
    }

    var loader = new SqliteLoader(conn.ConnectionString);   // shares the same in-memory DB via cache
    // (For in-memory SQLite, use "Data Source=file:test?mode=memory&cache=shared" so loader sees same DB)

    var table = new TableConfig
    {
        Name = "mst_group",
        Collection = "Group",
        Nature = "Primary",
        Fields = new List<FieldConfig>
        {
            new() { Name = "guid", Field = "Guid", Type = "text" },
            new() { Name = "name", Field = "Name", Type = "text" },
            new() { Name = "alterid", Field = "AlterId", Type = "text" }
        }
    };
    var config = new TallyExportConfig { Master = new List<TableConfig> { table }, Transaction = new List<TableConfig>() };

    var fake = new FakeTallyClient();
    fake.Register("Group", "<ENVELOPE><F01>g1</F01><F02>Sundry Debtors</F02><F03>5</F03></ENVELOPE>");

    var runner = new FullSyncRunner(fake, loader);
    await runner.Run(config, "TestCo", new DateTime(2024,4,1), new DateTime(2025,3,31), conn);
    await runner.Run(config, "TestCo", new DateTime(2024,4,1), new DateTime(2025,3,31), conn);

    using var count = conn.CreateCommand();
    count.CommandText = "SELECT COUNT(*) FROM mst_group";
    Assert.Equal(1L, (long)count.ExecuteScalar()!);
}
```

- [ ] **Step 2: Run, expect FAIL**

- [ ] **Step 3: Implement**

```csharp
// src/TallyDbLoader.Core/Sync/FullSyncRunner.cs
using System;
using System.Data.Common;
using System.Threading.Tasks;
using TallyDbLoader.Core.DatabaseLoaders;
using TallyDbLoader.Core.Models;
using TallyDbLoader.Core.Tally;

namespace TallyDbLoader.Core.Sync
{
    public class FullSyncRunner
    {
        private readonly ITallyClient _tally;
        private readonly IDatabaseLoader _loader;

        public FullSyncRunner(ITallyClient tally, IDatabaseLoader loader)
        {
            _tally = tally;
            _loader = loader;
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
```

- [ ] **Step 4: Run, expect PASS**

- [ ] **Step 5: Commit**

```bash
git add src/TallyDbLoader.Core/Sync/FullSyncRunner.cs tests/TallyDbLoader.Tests/FullSyncRunnerTests.cs
git commit -m "feat(sync): FullSyncRunner truncates before bulk load (fixes duplication)"
```

---

### Task 9: IncrementalSyncRunner — diff/delete/cascade-delete phase

**Files:**
- Create: `src/TallyDbLoader.Core/Sync/IncrementalSyncRunner.cs`
- Test: `tests/TallyDbLoader.Tests/IncrementalSyncRunnerTests.cs`

Implement just the "Phase 1" code path mirroring `src/tally.ts:149-192`.

- [ ] **Step 1: Failing test — deletion is detected and propagates cascade**

```csharp
[Fact]
public async Task Phase1_DiffAndDelete_RemovesMissingRowsAndCascades()
{
    using var conn = new SqliteConnection("Data Source=file:phase1?mode=memory&cache=shared");
    conn.Open();
    // Seed source table with two rows; Tally _diff returns only one row
    SetupSchema(conn, "mst_group", "mst_ledger");
    Insert(conn, "INSERT INTO mst_group(guid, name, alterid) VALUES ('g1','A',1),('g2','B',1)");
    Insert(conn, "INSERT INTO mst_ledger(guid, name, _parent, parent, alterid) VALUES ('l1','LA','g1','A',1),('l2','LB','g2','B',1)");

    var loader = new SqliteLoader(conn.ConnectionString);
    var fake = new FakeTallyClient();
    // _diff fetch returns only g1 (g2 was deleted in Tally)
    fake.Register("_diff", "<ENVELOPE><F01>g1</F01><F02>1</F02></ENVELOPE>");

    var groupTable = MakeTable("mst_group", "Group", new[] {
        ("guid","Guid","text"), ("name","Name","text"), ("alterid","AlterId","text") },
        cascadeDelete: new[] { ("mst_ledger","_parent") });

    var runner = new IncrementalSyncRunner(fake, loader);
    await runner.RunPhase1Diff(new[] { groupTable }, "TestCo",
        new DateTime(2024,4,1), new DateTime(2025,3,31), conn);

    Assert.Equal(1L, Scalar(conn, "SELECT COUNT(*) FROM mst_group"));
    Assert.Equal("g1", Scalar(conn, "SELECT guid FROM mst_group").ToString());
    Assert.Equal(1L, Scalar(conn, "SELECT COUNT(*) FROM mst_ledger"));
    Assert.Equal("l1", Scalar(conn, "SELECT guid FROM mst_ledger").ToString());
}
```

(Helper methods `SetupSchema`, `Insert`, `Scalar`, `MakeTable` defined at bottom of test file — straightforward SQL helpers and `TableConfig` builders.)

- [ ] **Step 2: Run, expect FAIL**

- [ ] **Step 3: Implement Phase1**

```csharp
// src/TallyDbLoader.Core/Sync/IncrementalSyncRunner.cs
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading.Tasks;
using TallyDbLoader.Core.DatabaseLoaders;
using TallyDbLoader.Core.Models;
using TallyDbLoader.Core.Tally;

namespace TallyDbLoader.Core.Sync
{
    public class IncrementalSyncRunner
    {
        private readonly ITallyClient _tally;
        private readonly IDatabaseLoader _loader;

        public IncrementalSyncRunner(ITallyClient tally, IDatabaseLoader loader)
        {
            _tally = tally;
            _loader = loader;
        }

        public async Task RunPhase1Diff(IEnumerable<TableConfig> primaryTables, string companyName,
            DateTime fromDate, DateTime toDate, DbConnection conn)
        {
            var staging = new StagingTableManager(conn);
            staging.EnsureStagingTables();

            foreach (var active in primaryTables)
            {
                Exec(conn, _loader.TruncateSql("_diff"));
                Exec(conn, _loader.TruncateSql("_delete"));

                var diffTable = new TableConfig
                {
                    Name = "_diff",
                    Collection = active.Collection,
                    Nature = "",
                    Fields = new List<FieldConfig>
                    {
                        new() { Name = "guid", Field = "Guid", Type = "text" },
                        new() { Name = "alterid", Field = "AlterId", Type = "text" }
                    },
                    Fetch = new List<string> { "AlterId" },
                    Filters = active.Filters ?? new List<string>()
                };

                var xml = DynamicTdlXmlGenerator.GenerateXml(diffTable, companyName,
                    fromDate.ToString("yyyyMMdd"), toDate.ToString("yyyyMMdd"));
                var resp = await _tally.PostXMLAsync(xml);
                var diffData = DynamicXmlParser.ParseXml(resp, diffTable);
                if (diffData.Rows.Count > 0)
                    await _loader.LoadBulkDataAsync(diffData, "_diff");

                Exec(conn, $"INSERT INTO _delete SELECT guid FROM {active.Name} WHERE guid NOT IN (SELECT guid FROM _diff)");
                Exec(conn, $"INSERT INTO _delete SELECT t.guid FROM {active.Name} AS t JOIN _diff AS s ON s.guid = t.guid WHERE s.alterid <> t.alterid");
                Exec(conn, $"DELETE FROM {active.Name} WHERE guid IN (SELECT guid FROM _delete)");

                if (active.CascadeDelete != null)
                    foreach (var cd in active.CascadeDelete)
                        Exec(conn, $"DELETE FROM {cd.Table} WHERE {cd.Field} IN (SELECT guid FROM _delete)");
            }
        }

        private static void Exec(DbConnection conn, string sql)
        {
            using var c = conn.CreateCommand();
            c.CommandText = sql;
            c.ExecuteNonQuery();
        }
    }
}
```

- [ ] **Step 4: Run, expect PASS**

- [ ] **Step 5: Commit**

```bash
git add src/TallyDbLoader.Core/Sync/IncrementalSyncRunner.cs tests/TallyDbLoader.Tests/IncrementalSyncRunnerTests.cs
git commit -m "feat(sync): IncrementalSyncRunner phase 1 (diff + delete + cascade-delete)"
```

---

### Task 10: IncrementalSyncRunner — refetch with $AlterID filter

**Files:**
- Modify: `src/TallyDbLoader.Core/Sync/IncrementalSyncRunner.cs`
- Modify: `tests/TallyDbLoader.Tests/IncrementalSyncRunnerTests.cs`

Mirrors `src/tally.ts:194-228`.

- [ ] **Step 1: Failing test — only rows with AlterID > watermark are re-fetched and re-inserted**

```csharp
[Fact]
public async Task Phase2_Refetch_AppendsAlterIdFilter()
{
    // Setup as before; after Phase1, call RunPhase2Refetch with watermark=5
    // FakeTallyClient asserts that the request XML contains "$AlterID > 5"
    // and returns one new row, which lands in the target table
    var fake = new FakeTallyClient();
    fake.Register("mst_group", "<ENVELOPE><F01>g3</F01><F02>C</F02><F03>10</F03></ENVELOPE>");

    // ... arrange tables, conn, runner ...
    await runner.RunPhase2Refetch(new[] { groupTable }, masterTables: new[] { groupTable },
        transactionTables: new TableConfig[0],
        lastMasterId: 5, lastTransactionId: 0,
        companyName: "TestCo", fromDate, toDate, conn);

    Assert.Contains(fake.AllRequests, r => r.Contains("$AlterID > 5"));
    Assert.Equal(1L, Scalar(conn, "SELECT COUNT(*) FROM mst_group WHERE guid = 'g3'"));
}
```

- [ ] **Step 2: Run, expect FAIL**

- [ ] **Step 3: Implement Phase2**

```csharp
public async Task RunPhase2Refetch(
    IEnumerable<TableConfig> masterTables,
    IEnumerable<TableConfig> transactionTables,
    long lastMasterId, long lastTransactionId,
    string companyName, DateTime fromDate, DateTime toDate, DbConnection conn)
{
    await RefetchTables(masterTables, lastMasterId, companyName, fromDate, toDate);
    await RefetchTables(transactionTables, lastTransactionId, companyName, fromDate, toDate);
}

private async Task RefetchTables(IEnumerable<TableConfig> tables, long watermark,
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
            Name = t.Name, Collection = t.Collection, Nature = t.Nature,
            Fields = t.Fields, Fetch = t.Fetch,
            Filters = filters,
            CascadeUpdate = t.CascadeUpdate, CascadeDelete = t.CascadeDelete
        };
        var xml = DynamicTdlXmlGenerator.GenerateXml(clone, companyName,
            fromDate.ToString("yyyyMMdd"), toDate.ToString("yyyyMMdd"));
        var resp = await _tally.PostXMLAsync(xml);
        var dt = DynamicXmlParser.ParseXml(resp, clone);
        if (dt.Rows.Count > 0)
            await _loader.LoadBulkDataAsync(dt, t.Name);
    }
}
```

- [ ] **Step 4: Run, expect PASS**

- [ ] **Step 5: Commit**

```bash
git add src/TallyDbLoader.Core/Sync/IncrementalSyncRunner.cs tests/TallyDbLoader.Tests/IncrementalSyncRunnerTests.cs
git commit -m "feat(sync): IncrementalSyncRunner phase 2 (refetch with AlterID filter)"
```

---

### Task 11: IncrementalSyncRunner — cascade-update + voucher number refresh

**Files:**
- Modify: `src/TallyDbLoader.Core/Sync/IncrementalSyncRunner.cs`
- Modify: tests

Mirrors `src/tally.ts:230-303`.

- [ ] **Step 1: Failing tests — cascade-update renames flow into child rows; auto-voucher refresh updates voucher numbers**

```csharp
[Fact]
public async Task Phase3_CascadeUpdate_FlowsRenamesIntoChildren()
{
    // Seed mst_group with g1=A; mst_ledger with _parent='g1', parent='OldName'
    // Run cascade update
    await runner.RunPhase3CascadeUpdate(new[] { groupTable }, conn);
    Assert.Equal("A", Scalar(conn, "SELECT parent FROM mst_ledger WHERE guid='l1'").ToString());
}

[Fact]
public async Task Phase3_AutoVoucherNumberRefresh_UpdatesNumbers()
{
    // Seed mst_vouchertype with numbering_method='Automatic'
    // Seed trn_voucher with voucher_number='OLD'
    // Register fake response for _vchnumber fetch with new number
    fake.Register("_vchnumber", "<ENVELOPE><F01>v1</F01><F02>NEW-1</F02></ENVELOPE>");
    await runner.RunPhase3VoucherRefresh(transactionTables, "TestCo", fromDate, toDate, conn);
    Assert.Equal("NEW-1", Scalar(conn, "SELECT voucher_number FROM trn_voucher WHERE guid='v1'").ToString());
}
```

- [ ] **Step 2: Run, expect FAIL**

- [ ] **Step 3: Implement Phase3**

```csharp
public void RunPhase3CascadeUpdate(IEnumerable<TableConfig> primaryTables, DbConnection conn)
{
    foreach (var active in primaryTables)
    {
        if (active.CascadeUpdate == null) continue;
        foreach (var cu in active.CascadeUpdate)
        {
            var sql = _loader.CascadeUpdateSql(active.Name, cu.Table, cu.Field);
            Exec(conn, sql);
        }
    }
}

public async Task RunPhase3VoucherRefresh(IEnumerable<TableConfig> transactionTables,
    string companyName, DateTime fromDate, DateTime toDate, DbConnection conn)
{
    // Count auto-numbering voucher types
    long count;
    using (var c = conn.CreateCommand())
    {
        c.CommandText = _loader.CountAutoNumberVoucherTypesSql();
        count = Convert.ToInt64(c.ExecuteScalar() ?? 0L);
    }
    if (count == 0) return;

    Exec(conn, _loader.TruncateSql("_vchnumber"));

    var voucher = transactionTables.FirstOrDefault(t => t.Name == "trn_voucher");
    if (voucher == null) return;

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
        await _loader.LoadBulkDataAsync(dt, "_vchnumber");

    Exec(conn, _loader.VoucherNumberUpdateSql());
}
```

- [ ] **Step 4: Run, expect PASS**

- [ ] **Step 5: Commit**

```bash
git add src/TallyDbLoader.Core/Sync/IncrementalSyncRunner.cs tests/TallyDbLoader.Tests/IncrementalSyncRunnerTests.cs
git commit -m "feat(sync): IncrementalSyncRunner phase 3 (cascade-update + voucher refresh)"
```

---

### Task 12: IncrementalSyncRunner.Run — orchestrate all phases + watermark commit

**Files:**
- Modify: `src/TallyDbLoader.Core/Sync/IncrementalSyncRunner.cs`
- Modify: tests

Top-level `Run()` checks watermarks, calls Phase1/2/3 conditionally, commits watermark only on success, truncates staging.

- [ ] **Step 1: Failing test — happy path end-to-end + no-change short circuit + failure does not advance watermark**

```csharp
[Fact]
public async Task Run_NoChange_SkipsAllPhases()
{
    // companyInfo.AltMstId == db watermark → expect zero PostXMLAsync calls beyond company-info
    SeedConfigWatermark(conn, master: 100, txn: 200);
    fake.CompanyInfo.AltMstId = 100;
    fake.CompanyInfo.AltVchId = 200;

    await runner.Run(config, "TestCo", fromDate, toDate, conn);
    Assert.Empty(fake.AllRequests);   // company info fetched via FetchCompanyInfoAsync, not PostXMLAsync
}

[Fact]
public async Task Run_HappyPath_AdvancesWatermark()
{
    SeedConfigWatermark(conn, master: 0, txn: 0);
    fake.CompanyInfo.AltMstId = 50;
    fake.CompanyInfo.AltVchId = 75;
    // register canned responses for _diff and table fetches as needed

    await runner.Run(config, "TestCo", fromDate, toDate, conn);

    var repo = new WatermarkRepository(conn);
    Assert.Equal((50L, 75L), repo.Read());
}

[Fact]
public async Task Run_PhaseThrows_WatermarkNotAdvanced()
{
    SeedConfigWatermark(conn, master: 10, txn: 20);
    fake.CompanyInfo.AltMstId = 50;
    // arrange fake to throw on second request

    await Assert.ThrowsAnyAsync<Exception>(
        () => runner.Run(config, "TestCo", fromDate, toDate, conn));

    var repo = new WatermarkRepository(conn);
    Assert.Equal((10L, 20L), repo.Read());
}
```

- [ ] **Step 2: Run, expect FAIL**

- [ ] **Step 3: Implement Run**

```csharp
public async Task Run(TallyExportConfig config, string companyName,
    DateTime fromDate, DateTime toDate, DbConnection conn)
{
    new StagingTableManager(conn).EnsureStagingTables();
    var repo = new WatermarkRepository(conn);
    var (lastMasterDb, lastTxnDb) = repo.Read();

    var companyInfo = await _tally.FetchCompanyInfoAsync(companyName);
    var masterChanged = companyInfo.AltMstId != lastMasterDb;
    var txnChanged = companyInfo.AltVchId != lastTxnDb;

    if (!masterChanged && !txnChanged) return;

    var primary = new List<TableConfig>();
    if (masterChanged) primary.AddRange(config.Master.Where(t => t.Nature == "Primary"));
    if (txnChanged) primary.AddRange(config.Transaction.Where(t => t.Nature == "Primary"));

    await RunPhase1Diff(primary, companyName, fromDate, toDate, conn);

    await RunPhase2Refetch(
        masterChanged ? config.Master : new List<TableConfig>(),
        txnChanged ? config.Transaction : new List<TableConfig>(),
        lastMasterDb, lastTxnDb, companyName, fromDate, toDate, conn);

    if (masterChanged) RunPhase3CascadeUpdate(primary, conn);
    if (txnChanged) await RunPhase3VoucherRefresh(config.Transaction, companyName, fromDate, toDate, conn);

    new StagingTableManager(conn).TruncateStaging();
    repo.Write(companyInfo.AltMstId, companyInfo.AltVchId);
}
```

- [ ] **Step 4: Run, expect PASS**

- [ ] **Step 5: Commit**

```bash
git add src/TallyDbLoader.Core/Sync/IncrementalSyncRunner.cs tests/TallyDbLoader.Tests/IncrementalSyncRunnerTests.cs
git commit -m "feat(sync): IncrementalSyncRunner.Run orchestrates phases + atomic watermark"
```

---

### Task 13: BackgroundSyncWorker dispatch

**Files:**
- Modify: `src/TallyDbLoader.Core/Sync/BackgroundSyncWorker.cs`

Replace the inline loop (lines 359–375) with a dispatch on `company.Mode`. Use `IDatabaseLoader.OpenConnection()` helper if it exists, else create connection inline from `connStr`.

- [ ] **Step 1: Modify SyncCompany**

Locate `BackgroundSyncWorker.SyncCompany`. After the `dbLoader`/`connStr` assignment block (around line 333) and after `dates = await GetCompanyDatesAsync(...)` (line 350), replace lines 351–375 with:

```csharp
// Open a target DB connection for orchestrators
using var targetConn = OpenTargetConnection(tech, connStr);
targetConn.Open();

long totalRows;
if (string.Equals(company.Mode, "incremental", StringComparison.OrdinalIgnoreCase))
{
    // CompanyInfoFetcher persists config and derives dates if needed
    var fetcher = new CompanyInfoFetcher(client);
    var info = await fetcher.FetchAndPersist(company.Name, targetConn);
    var from = info.BooksFrom ?? new DateTime(2000, 1, 1);
    var to = info.BooksTo ?? DateTime.Today;

    var incremental = new IncrementalSyncRunner(client, dbLoader);
    await incremental.Run(config, company.Name, from, to, targetConn);
    totalRows = 0;   // incremental doesn't report row count the same way; could be enriched later
}
else
{
    var fetcher = new CompanyInfoFetcher(client);
    var info = await fetcher.FetchAndPersist(company.Name, targetConn);
    var from = info.BooksFrom ?? new DateTime(2000, 1, 1);
    var to = info.BooksTo ?? DateTime.Today;

    var full = new FullSyncRunner(client, dbLoader);
    totalRows = await full.Run(config, company.Name, from, to, targetConn);
}
```

Add helper:

```csharp
private static DbConnection OpenTargetConnection(string tech, string connStr)
{
    if (tech.Contains("postgres") || tech.Contains("npgsql"))
        return new Npgsql.NpgsqlConnection(connStr);
    if (tech.Contains("mssql") || tech.Contains("sqlserver"))
        return new Microsoft.Data.SqlClient.SqlConnection(connStr);
    if (tech.Contains("mysql"))
        return new MySqlConnector.MySqlConnection(connStr);
    if (tech.Contains("sqlite"))
        return new Microsoft.Data.Sqlite.SqliteConnection(connStr);
    throw new NotSupportedException($"Cannot open connection for tech '{tech}'");
}
```

Remove the now-unused `GetCompanyDatesAsync` method.

`client` type changes from `TallyClient` to `ITallyClient` where it's passed to the new orchestrators. The `WorkerLoop` keeps creating a concrete `TallyClient` and passes it as `ITallyClient`.

- [ ] **Step 2: Build + run all tests**

Run: `dotnet build src/TallyDbLoader.Core && dotnet test tests/TallyDbLoader.Tests`
Expected: all pass.

- [ ] **Step 3: Commit**

```bash
git add src/TallyDbLoader.Core/Sync/BackgroundSyncWorker.cs
git commit -m "feat(sync): BackgroundSyncWorker dispatches to Full/IncrementalSyncRunner"
```

---

### Task 14: End-to-end scenario tests

**Files:**
- Modify: `tests/TallyDbLoader.Tests/IncrementalSyncRunnerTests.cs`

Add full end-to-end scenarios from the spec's testing section that exercise the orchestrated `Run`:

- [ ] **Step 1: Add scenario tests**

```csharp
[Fact]
public async Task Scenario_Insert_NewMasterRowAppears() { /* seed empty, simulate insert in tally, run twice */ }

[Fact]
public async Task Scenario_Update_RowReplacedOthersUntouched() { /* seed with row at alterid=1, change to alterid=2 */ }

[Fact]
public async Task Scenario_Delete_RowRemovedAndCascades() { /* seed parent+child, delete parent */ }

[Fact]
public async Task Scenario_AutoVoucherRenumber_BackdatedVoucherShifts() { /* per spec */ }
```

(Each scenario follows the same pattern: SQLite in-memory with shared cache, FakeTallyClient with registered responses, run, assert.)

- [ ] **Step 2: Run, expect FAIL until impl matches**

- [ ] **Step 3: Iterate on `IncrementalSyncRunner` if any scenario reveals a bug — fix, re-run.**

- [ ] **Step 4: Run all tests, expect PASS**

Run: `dotnet test tests/TallyDbLoader.Tests`

- [ ] **Step 5: Commit**

```bash
git add tests/TallyDbLoader.Tests/IncrementalSyncRunnerTests.cs
git commit -m "test(sync): end-to-end incremental scenarios (insert/update/delete/renumber)"
```

---

## Final verification

After Task 14:

- [ ] `dotnet build src/TallyDbLoader.sln` — clean.
- [ ] `dotnet test tests/TallyDbLoader.Tests` — all green.
- [ ] Manually open `BackgroundSyncWorker.SyncCompany` and confirm: no dead code; `company.Mode` is actually branched on; `GetCompanyDatesAsync` is gone.
- [ ] Run a real sync against a Tally instance for one company in `full` mode; verify no duplicate rows on second run.
- [ ] Switch the company to `incremental` mode; add a master in Tally; run; verify only the new master is inserted and watermark advances.
- [ ] Delete a master in Tally; run; verify it's removed from the DB along with child rows.
