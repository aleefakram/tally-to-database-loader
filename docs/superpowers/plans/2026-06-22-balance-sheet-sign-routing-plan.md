# Balance Sheet Dynamic Sign Routing and Opening Balance Difference Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Correct the WPF app's Balance Sheet display by implementing sign-based group routing and computing the report-period opening balance difference, achieving perfect parity with Tally.

**Architecture:** Calculate the opening balance difference at the start of the report calculation, dynamically route recognized primary groups to the Assets or Liabilities side based on their net balance sign, fail on unrecognized groups, and sort the sides deterministically.

**Tech Stack:** C# 10, .NET 6, xUnit, SQLite, Dapper

---

### Task 1: Update BalanceSheetCalculator Logic

**Files:**
- Modify: `src/TallyDbLoader.Core/Reports/BalanceSheetCalculator.cs:100-200`

- [ ] **Step 1: Compute totalOpening and dynamic routing**
  Replace lines 135 to 190 in `src/TallyDbLoader.Core/Reports/BalanceSheetCalculator.cs` with the updated sign-based routing, validation failure on unrecognized groups, and deterministic line sorting.

  Code changes:
  ```csharp
              // 1. Calculate report-period opening balance difference
              decimal totalOpening = raw.Ledgers.Sum(l =>
              {
                  bool hasCycle = false;
                  string primaryGroup = l.PrimaryGroup;
                  if (string.IsNullOrWhiteSpace(primaryGroup) && !l.IsRevenue && !l.LedgerName.Equals(request.Options.ProfitAndLossLedgerName, StringComparison.OrdinalIgnoreCase))
                  {
                      primaryGroup = ResolvePrimaryGroup(l.ParentGroupName, groupMap, new HashSet<string>(StringComparer.OrdinalIgnoreCase), ref hasCycle);
                  }
                  if (hasCycle || string.IsNullOrWhiteSpace(primaryGroup) || 
                      (!LiabilityGroups.Contains(primaryGroup) && !AssetGroups.Contains(primaryGroup) && !l.IsRevenue && !l.LedgerName.Equals(request.Options.ProfitAndLossLedgerName, StringComparison.OrdinalIgnoreCase)))
                  {
                      return 0m;
                  }

                  if (NormalizeGroup(primaryGroup).Equals("Stock-in-hand", StringComparison.OrdinalIgnoreCase))
                  {
                      return l.HasOpeningStockValue ? l.OpeningStockValue : l.OpeningBalance;
                  }
                  return l.OpeningBalance;
              });

              var grouped = raw.Ledgers
                  .Where(l => !l.IsRevenue)
                  .Where(l => !l.LedgerName.Equals(request.Options.ProfitAndLossLedgerName, StringComparison.OrdinalIgnoreCase))
                  .GroupBy(l => NormalizeGroup(l.PrimaryGroup));

              foreach (var group in grouped)
              {
                  bool isLiability = LiabilityGroups.Contains(group.Key);
                  bool isAsset = AssetGroups.Contains(group.Key);

                  if (!isLiability && !isAsset)
                  {
                      return Fail(report, $"Unrecognized primary group '{group.Key}' was detected.");
                  }

                  decimal signedBalance = group.Sum(l =>
                  {
                      if (NormalizeGroup(group.Key).Equals("Stock-in-hand", StringComparison.OrdinalIgnoreCase) && l.HasClosingStockValue)
                      {
                          return -l.ClosingStockValue;
                      }
                      return l.OpeningBalance + l.PrePeriodMovement + l.CurrentPeriodMovement;
                  });

                  if (signedBalance == 0m) continue;

                  // Credit balance (> 0) goes to Liabilities, Debit balance (< 0) goes to Assets
                  if (signedBalance > 0m)
                  {
                      report.LiabilitySide.Lines.Add(new BalanceSheetLine
                      {
                          Name = group.Key,
                          Amount = signedBalance,
                          IsEmphasis = true
                      });
                  }
                  else
                  {
                      report.AssetSide.Lines.Add(new BalanceSheetLine
                      {
                          Name = group.Key,
                          Amount = -signedBalance,
                          IsEmphasis = true
                      });
                  }
              }

              if (report.ProfitAndLoss.NetAmount > 0m)
              {
                  report.LiabilitySide.Lines.Add(CreateProfitAndLossLine(
                      request.Options.ProfitAndLossLedgerName,
                      report.ProfitAndLoss.NetAmount,
                      report.ProfitAndLoss));
              }
              else if (report.ProfitAndLoss.NetAmount < 0m)
              {
                  report.AssetSide.Lines.Add(CreateProfitAndLossLine(
                      request.Options.ProfitAndLossLedgerName,
                      Math.Abs(report.ProfitAndLoss.NetAmount),
                      report.ProfitAndLoss));
              }

              // Inject Difference in opening balances
              if (totalOpening > 0m)
              {
                  report.AssetSide.Lines.Add(new BalanceSheetLine
                  {
                      Name = "Difference in opening balances",
                      Amount = totalOpening,
                      IsEmphasis = true
                  });
              }
              else if (totalOpening < 0m)
              {
                  report.LiabilitySide.Lines.Add(new BalanceSheetLine
                  {
                      Name = "Difference in opening balances",
                      Amount = -totalOpening,
                      IsEmphasis = true
                  });
              }

              // Sort lines deterministically
              var liabilityOrder = new List<string> { "Capital Account", "Loans (Liability)", "Current Liabilities", request.Options.ProfitAndLossLedgerName, "Difference in opening balances" };
              var assetOrder = new List<string> { "Fixed Assets", "Investments", "Current Assets", "Branch / Divisions", "Misc. Expenses (ASSET)", "Suspense A/c", request.Options.ProfitAndLossLedgerName, "Difference in opening balances" };

              report.LiabilitySide.Lines = report.LiabilitySide.Lines
                  .OrderBy(l =>
                  {
                      int index = liabilityOrder.FindIndex(x => x.Equals(l.Name, StringComparison.OrdinalIgnoreCase));
                      return index >= 0 ? index : int.MaxValue;
                  })
                  .ToList();

              report.AssetSide.Lines = report.AssetSide.Lines
                  .OrderBy(l =>
                  {
                      int index = assetOrder.FindIndex(x => x.Equals(l.Name, StringComparison.OrdinalIgnoreCase));
                      return index >= 0 ? index : int.MaxValue;
                  })
                  .ToList();
  ```

- [ ] **Step 2: Compile to verify syntax**
  Run: `dotnet build src/TallyDbLoader.sln`
  Expected: Build succeeds with 0 errors.

---

### Task 2: Update Calculator Unit Tests

**Files:**
- Modify: `tests/TallyDbLoader.Tests/BalanceSheetCalculatorTests.cs`

- [ ] **Step 1: Implement test cases in `BalanceSheetCalculatorTests.cs`**
  Add unit tests validating:
  1. Unrecognized group validation failure.
  2. Debit-balanced liabilities correctly routed to Assets side.
  3. Difference in opening balances computed and routed correctly on both sides.

  Code changes:
  ```csharp
          [Fact]
          public void Calculate_UnrecognizedGroup_FailsVerification()
          {
              var request = new BalanceSheetVerificationRequest
              {
                  CompanyProfileId = 1,
                  FinancialYearStart = new DateTime(2025, 4, 1),
                  AsAtDate = new DateTime(2025, 6, 1)
              };

              var raw = new BalanceSheetRawData
              {
                  Groups = new List<BalanceSheetGroupRow>(),
                  Ledgers = new List<BalanceSheetLedgerRow>
                  {
                      new BalanceSheetLedgerRow
                      {
                          LedgerName = "Unknown Ledger",
                          ParentGroupName = "Some Group",
                          PrimaryGroup = "Not A Valid Group",
                          OpeningBalance = -1000m
                      }
                  }
              };

              var report = BalanceSheetCalculator.Calculate("Test Company", raw, request);

              Assert.Equal("failed", report.Status);
              Assert.Contains("Unrecognized primary group", report.ErrorSummary);
          }

          [Fact]
          public void Calculate_DebitBalancedLiability_RoutesToAssets()
          {
              var request = new BalanceSheetVerificationRequest
              {
                  CompanyProfileId = 1,
                  FinancialYearStart = new DateTime(2025, 4, 1),
                  AsAtDate = new DateTime(2025, 6, 1)
              };

              var raw = new BalanceSheetRawData
              {
                  Groups = new List<BalanceSheetGroupRow>(),
                  Ledgers = new List<BalanceSheetLedgerRow>
                  {
                      new BalanceSheetLedgerRow
                      {
                          LedgerName = "Overdraft Liab",
                          ParentGroupName = "Current Liabilities",
                          PrimaryGroup = "Current Liabilities",
                          OpeningBalance = -5000m // Debit (Asset-like)
                      }
                  }
              };

              var report = BalanceSheetCalculator.Calculate("Test Company", raw, request);

              var assetLine = report.AssetSide.Lines.FirstOrDefault(l => l.Name == "Current Liabilities");
              var liabilityLine = report.LiabilitySide.Lines.FirstOrDefault(l => l.Name == "Current Liabilities");

              Assert.NotNull(assetLine);
              Assert.Null(liabilityLine);
              Assert.Equal(5000m, assetLine.Amount);
          }

          [Fact]
          public void Calculate_OpeningBalanceDifference_InjectsCorrectSide()
          {
              var request = new BalanceSheetVerificationRequest
              {
                  CompanyProfileId = 1,
                  FinancialYearStart = new DateTime(2025, 4, 1),
                  AsAtDate = new DateTime(2025, 6, 1)
              };

              // Trial balance difference of credit 1000m
              var raw = new BalanceSheetRawData
              {
                  Groups = new List<BalanceSheetGroupRow>(),
                  Ledgers = new List<BalanceSheetLedgerRow>
                  {
                      new BalanceSheetLedgerRow
                      {
                          LedgerName = "Capital Ledger",
                          ParentGroupName = "Capital Account",
                          PrimaryGroup = "Capital Account",
                          OpeningBalance = 1000m // Credit
                      }
                  }
              };

              var report = BalanceSheetCalculator.Calculate("Test Company", raw, request);

              var diffAssetLine = report.AssetSide.Lines.FirstOrDefault(l => l.Name == "Difference in opening balances");
              Assert.NotNull(diffAssetLine);
              Assert.Equal(1000m, diffAssetLine.Amount);
          }
  ```

- [ ] **Step 2: Run tests to verify they pass**
  Run: `dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore --filter "FullyQualifiedName~BalanceSheetCalculatorTests"`
  Expected: All calculator tests pass.

---

### Task 3: Implement Query Integration Tests

**Files:**
- Modify: `tests/TallyDbLoader.Tests/BalanceSheetVerificationServiceTests.cs`

- [ ] **Step 1: Write integration tests**
  Add a service integration test to `tests/TallyDbLoader.Tests/BalanceSheetVerificationServiceTests.cs` validating:
  1. Query adapter correctly parses positive stock_value to signed negative OpeningStockValue.
  2. Integration flow computes opening difference and routes inverted groups dynamically.

  Code changes:
  ```csharp
          [Fact]
          public async Task GenerateAsync_SeededDb_ComputesOpeningDifferenceAndDynamicRouting()
          {
              // Arrange
              var (repo, dbConnectionFactory) = CreateTestRepositories();
              var service = new BalanceSheetVerificationService(repo, dbConnectionFactory);

              // Setup db tables and profiles
              using (var connection = dbConnectionFactory.CreateConnection())
              {
                  connection.Open();
                  // Seed tables: company_profiles, database_profiles, trn_closingstock_ledger, mst_ledger, trn_accounting, etc.
                  connection.Execute("CREATE TABLE IF NOT EXISTS company_profiles (id INTEGER PRIMARY KEY, name TEXT, db_profile_id INTEGER, is_active INTEGER);");
                  connection.Execute("CREATE TABLE IF NOT EXISTS database_profiles (id INTEGER PRIMARY KEY, name TEXT, technology TEXT, server TEXT, port INTEGER, username TEXT, password TEXT, last_test_result TEXT, last_tested_at TEXT);");
                  connection.Execute("CREATE TABLE IF NOT EXISTS mst_ledger (name TEXT PRIMARY KEY, parent TEXT, opening_balance REAL);");
                  connection.Execute("CREATE TABLE IF NOT EXISTS mst_group (name TEXT PRIMARY KEY, parent TEXT, primary_group TEXT, is_revenue INTEGER);");
                  connection.Execute("CREATE TABLE IF NOT EXISTS trn_accounting (guid TEXT, ledger TEXT, amount REAL);");
                  connection.Execute("CREATE TABLE IF NOT EXISTS trn_voucher (guid TEXT PRIMARY KEY, date TEXT, is_order_voucher INTEGER, is_inventory_voucher INTEGER);");
                  connection.Execute("CREATE TABLE IF NOT EXISTS trn_closingstock_ledger (ledger TEXT, stock_date TEXT, stock_value REAL);");

                  connection.Execute("INSERT INTO company_profiles (id, name, db_profile_id, is_active) VALUES (1, 'Test Company', 1, 1);");
                  connection.Execute("INSERT INTO database_profiles (id, name, technology, server, port, username, password) VALUES (1, 'LocalDb', 'sqlite', '', 0, '', '');");

                  connection.Execute("INSERT INTO mst_group (name, parent, primary_group, is_revenue) VALUES ('Capital', '', 'Capital Account', 0);");
                  connection.Execute("INSERT INTO mst_group (name, parent, primary_group, is_revenue) VALUES ('Stock', '', 'Stock-in-hand', 0);");
                  
                  connection.Execute("INSERT INTO mst_ledger (name, parent, opening_balance) VALUES ('Equity', 'Capital', 5000.00);"); // Credit
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

              // Act
              var report = await service.GenerateAsync(request, CancellationToken.None);

              // Assert
              Assert.NotNull(report);
              Assert.Equal("balanced", report.Status);

              // Opening Difference: Credit 5000 - Debit 2000 (stock_value) = 3000 Credit surplus. Should show as 3000 Debit Difference on Assets.
              var diffLine = report.AssetSide.Lines.FirstOrDefault(l => l.Name == "Difference in opening balances");
              Assert.NotNull(diffLine);
              Assert.Equal(3000m, diffLine.Amount);
          }
  ```

- [ ] **Step 2: Run all tests**
  Run: `dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore`
  Expected: All 260+ tests pass successfully.

---

### Task 4: Final Validation and Commit

- [ ] **Step 1: Verify WPF Application**
  Run the WPF application locally. Inspect the "AR Foods" company balance sheet with May 1, 2025 to June 1, 2025 dates, verifying it matches the Tally report:
  - Assets side lists "Current Liabilities" with `9,10,983.13`
  - Assets side lists "Difference in opening balances" with `1,95,390.00`
  - Report status is "balanced" with 0.00 difference.

- [ ] **Step 2: Commit all changes**
  ```powershell
  git add src/TallyDbLoader.Core/Reports/BalanceSheetCalculator.cs tests/TallyDbLoader.Tests/BalanceSheetCalculatorTests.cs tests/TallyDbLoader.Tests/BalanceSheetVerificationServiceTests.cs docs/superpowers/specs/2026-06-22-balance-sheet-sign-routing-design.md
  git commit -m "feat: implement dynamic sign routing and opening balance difference for balance sheet"
  ```
