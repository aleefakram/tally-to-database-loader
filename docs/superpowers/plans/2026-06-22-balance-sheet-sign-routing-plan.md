# Balance Sheet Dynamic Sign Routing and Opening Balance Difference Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Correct the WPF app's Balance Sheet display by implementing sign-based group routing and computing the report-period opening balance difference, achieving perfect parity with Tally.

**Architecture:** Calculate the opening balance difference on Tally report-period opening basis (including `PrePeriodMovement` and P&L opening) after resolving ledgers, dynamically route recognized primary groups to the Assets or Liabilities side based on their net balance sign, fail immediately with `ErrorSummary` on unrecognized groups (clearing all lines so failed history totals are zero), and sort both sides deterministically using a unified order.

**Tech Stack:** C# 10, .NET 6, xUnit, SQLite, Dapper

---

### Task 1: Update BalanceSheetCalculator Logic

**Files:**
- Modify: `src/TallyDbLoader.Core/Reports/BalanceSheetCalculator.cs`

- [ ] **Step 1: Update Fail method**
  Update the private static `Fail` method in `src/TallyDbLoader.Core/Reports/BalanceSheetCalculator.cs` to clear lines on failure.
  ```csharp
          private static BalanceSheetReport Fail(BalanceSheetReport report, string error)
          {
              report.Status = "failed";
              report.ErrorSummary = error;
              report.LiabilitySide.Lines.Clear();
              report.AssetSide.Lines.Clear();
              return report;
          }
  ```

- [ ] **Step 2: Update stock logic, dynamic routing, and sorting**
  Update the rest of `Calculate` method in `src/TallyDbLoader.Core/Reports/BalanceSheetCalculator.cs` (from where totalOpening is calculated onwards).

  Code changes:
  ```csharp
              // ... Existing loop for ledger group resolution (lines 50-91) ...

              // 1. Calculate resolved ledgers opening sum (excluding revenue and P&L)
              decimal resolvedOpening = raw.Ledgers.Sum(l =>
              {
                  if (l.IsRevenue || l.LedgerName.Equals(request.Options.ProfitAndLossLedgerName, StringComparison.OrdinalIgnoreCase))
                  {
                      return 0m;
                  }

                  bool hasCycle = false;
                  string primaryGroup = l.PrimaryGroup;
                  if (string.IsNullOrWhiteSpace(primaryGroup))
                  {
                      primaryGroup = ResolvePrimaryGroup(l.ParentGroupName, groupMap, new HashSet<string>(StringComparer.OrdinalIgnoreCase), ref hasCycle);
                  }
                  if (hasCycle || string.IsNullOrWhiteSpace(primaryGroup) || 
                      (!LiabilityGroups.Contains(primaryGroup) && !AssetGroups.Contains(primaryGroup)))
                  {
                      return 0m;
                  }

                  if (NormalizeGroup(primaryGroup).Equals("Stock-in-hand", StringComparison.OrdinalIgnoreCase))
                  {
                      return l.HasOpeningStockValue ? l.OpeningStockValue : l.OpeningBalance + l.PrePeriodMovement;
                  }
                  return l.OpeningBalance + l.PrePeriodMovement;
              });

              // 2. Compute stockOpening and stockClosing (signed DB basis)
              decimal stockOpening = raw.Ledgers
                  .Where(l => l.PrimaryGroup.Equals("Stock-in-hand", StringComparison.OrdinalIgnoreCase))
                  .Sum(l => l.HasOpeningStockValue
                      ? l.OpeningStockValue
                      : l.OpeningBalance + l.PrePeriodMovement);

              var stockLedgers = raw.Ledgers
                  .Where(l => l.PrimaryGroup.Equals("Stock-in-hand", StringComparison.OrdinalIgnoreCase))
                  .ToList();

              decimal stockClosing = stockLedgers.Sum(l => l.HasClosingStockValue
                  ? l.ClosingStockValue
                  : l.OpeningBalance + l.PrePeriodMovement + l.CurrentPeriodMovement);

              // 3. Compute Profit & Loss Breakdown using consistent signs
              report.ProfitAndLoss.OpeningBalance = pnlOpening + revenuePrePeriod - stockOpening;
              report.ProfitAndLoss.CurrentPeriod = revenueCurrent - (stockClosing - stockOpening);
              report.ProfitAndLoss.LessTransferred = pnlLessTransferred;

              // 4. Calculate total opening (including P&L opening)
              decimal totalOpening = resolvedOpening + report.ProfitAndLoss.OpeningBalance;

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
                          return l.ClosingStockValue; // already negative/debit
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

              // Unified primary group display order for sorting
              var unifiedOrder = new List<string>
              {
                  "Capital Account",
                  "Loans (Liability)",
                  "Current Liabilities",
                  "Fixed Assets",
                  "Investments",
                  "Current Assets",
                  "Stock-in-hand",
                  "Branch / Divisions",
                  "Misc. Expenses (ASSET)",
                  "Suspense A/c",
                  request.Options.ProfitAndLossLedgerName,
                  "Difference in opening balances"
              };

              report.LiabilitySide.Lines = report.LiabilitySide.Lines
                  .OrderBy(l =>
                  {
                      int index = unifiedOrder.FindIndex(x => x.Equals(l.Name, StringComparison.OrdinalIgnoreCase));
                      return index >= 0 ? index : int.MaxValue;
                  })
                  .ToList();

              report.AssetSide.Lines = report.AssetSide.Lines
                  .OrderBy(l =>
                  {
                      int index = unifiedOrder.FindIndex(x => x.Equals(l.Name, StringComparison.OrdinalIgnoreCase));
                      return index >= 0 ? index : int.MaxValue;
                  })
                  .ToList();
  ```

- [ ] **Step 3: Compile to verify syntax**
  Run: `dotnet build src/TallyDbLoader.sln`
  Expected: Build succeeds with 0 errors.

---

### Task 2: Update Calculator Unit Tests

**Files:**
- Modify: `tests/TallyDbLoader.Tests/BalanceSheetCalculatorTests.cs`

- [ ] **Step 1: Modify existing stock and tolerance tests**
  In `tests/TallyDbLoader.Tests/BalanceSheetCalculatorTests.cs`:
  - In `Calculate_ProfitAndLoss_CurrentPeriod_IncludesStockDelta`, change `ClosingStockValue = 300m` to `ClosingStockValue = -300m`.
  - In `Calculate_PartialStockValuation_AppliesPerLedgerFallback`, change `ClosingStockValue = 150m` to `ClosingStockValue = -150m`.
  - Replace `Calculate_SmallDifferenceWithinTolerance_IsBalanced` to exercise tolerance via current period movement mismatch:
    ```csharp
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
                        new() { LedgerName = "Cash", PrimaryGroup = "Current Assets", OpeningBalance = -100m, CurrentPeriodMovement = 0.02m }
                    }
                };

                var report = BalanceSheetCalculator.Calculate("Demo Co", raw, request);

                Assert.Equal("balanced", report.Status);
                Assert.Equal(0.02m, report.Difference);
            }
    ```

- [ ] **Step 2: Add new unit test cases**
  Add unit tests validating unrecognized group failure with cleared lines, debit-balanced liabilities, credit-balanced assets, opening difference with pre-period movements, opening difference debit surplus on Liabilities, P&L stock breakdown correctness with negative stock values, and unified display sorting.

  Code changes:
  ```csharp
          [Fact]
          public void Calculate_UnrecognizedGroup_FailsVerificationAndPopulatesErrorSummary()
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
              Assert.NotNull(report.ErrorSummary);
              Assert.Contains("Unrecognized primary group", report.ErrorSummary);
              Assert.Empty(report.AssetSide.Lines);
              Assert.Empty(report.LiabilitySide.Lines);
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
          public void Calculate_CreditBalancedAsset_RoutesToLiabilities()
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
                          LedgerName = "Bank Overdraft",
                          ParentGroupName = "Current Assets",
                          PrimaryGroup = "Current Assets",
                          OpeningBalance = 3000m // Credit (Liability-like)
                      }
                  }
              };

              var report = BalanceSheetCalculator.Calculate("Test Company", raw, request);

              var assetLine = report.AssetSide.Lines.FirstOrDefault(l => l.Name == "Current Assets");
              var liabilityLine = report.LiabilitySide.Lines.FirstOrDefault(l => l.Name == "Current Assets");

              Assert.Null(assetLine);
              Assert.NotNull(liabilityLine);
              Assert.Equal(3000m, liabilityLine.Amount);
          }

          [Fact]
          public void Calculate_OpeningBalanceDifference_WithPrePeriod_InjectsCorrectSide()
          {
              var request = new BalanceSheetVerificationRequest
              {
                  CompanyProfileId = 1,
                  FinancialYearStart = new DateTime(2025, 4, 1),
                  AsAtDate = new DateTime(2025, 6, 1)
              };

              // Trial balance difference with pre-period movements:
              // Capital Ledger: Opening = 1000m, PrePeriod = 200m -> Total Opening credit = 1200m.
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
                          OpeningBalance = 1000m,
                          PrePeriodMovement = 200m
                      }
                  }
              };

              var report = BalanceSheetCalculator.Calculate("Test Company", raw, request);

              var diffAssetLine = report.AssetSide.Lines.FirstOrDefault(l => l.Name == "Difference in opening balances");
              Assert.NotNull(diffAssetLine);
              Assert.Equal(1200m, diffAssetLine.Amount);
          }

          [Fact]
          public void Calculate_OpeningBalanceDifference_DebitSurplus_InjectsLiabilities()
          {
              var request = new BalanceSheetVerificationRequest
              {
                  CompanyProfileId = 1,
                  FinancialYearStart = new DateTime(2025, 4, 1),
                  AsAtDate = new DateTime(2025, 6, 1)
              };

              // Trial balance difference: Cash = -1000m (Debit) -> totalOpening = -1000m (Debit surplus)
              var raw = new BalanceSheetRawData
              {
                  Groups = new List<BalanceSheetGroupRow>(),
                  Ledgers = new List<BalanceSheetLedgerRow>
                  {
                      new BalanceSheetLedgerRow
                      {
                          LedgerName = "Cash",
                          ParentGroupName = "Current Assets",
                          PrimaryGroup = "Current Assets",
                          OpeningBalance = -1000m
                      }
                  }
              };

              var report = BalanceSheetCalculator.Calculate("Test Company", raw, request);

              var diffLiabilityLine = report.LiabilitySide.Lines.FirstOrDefault(l => l.Name == "Difference in opening balances");
              Assert.NotNull(diffLiabilityLine);
              Assert.Equal(1000m, diffLiabilityLine.Amount);
          }

          [Fact]
          public void Calculate_PnLBreakdownWithStock_ComputesCorrectly()
          {
              var request = new BalanceSheetVerificationRequest
              {
                  CompanyProfileId = 1,
                  FinancialYearStart = new DateTime(2025, 5, 1),
                  AsAtDate = new DateTime(2025, 6, 1)
              };

              var raw = new BalanceSheetRawData
              {
                  Groups = new List<BalanceSheetGroupRow>(),
                  Ledgers = new List<BalanceSheetLedgerRow>
                  {
                      new BalanceSheetLedgerRow
                      {
                          LedgerName = "Profit & Loss A/c",
                          ParentGroupName = "",
                          PrimaryGroup = "",
                          OpeningBalance = 5000m // Credit
                      },
                      new BalanceSheetLedgerRow
                      {
                          LedgerName = "Inventory Ledger",
                          ParentGroupName = "Stock-in-hand",
                          PrimaryGroup = "Stock-in-hand",
                          HasOpeningStockValue = true,
                          OpeningStockValue = -2000m, // Debit (Opening Stock)
                          HasClosingStockValue = true,
                          ClosingStockValue = -3000m // Debit (Closing Stock)
                      },
                      new BalanceSheetLedgerRow
                      {
                          LedgerName = "Sales",
                          ParentGroupName = "Sales Accounts",
                          PrimaryGroup = "Sales Accounts",
                          IsRevenue = true,
                          CurrentPeriodMovement = 10000m // Credit (Revenue)
                      }
                  }
              };

              var report = BalanceSheetCalculator.Calculate("Test Company", raw, request);

              // Opening P&L: 5000 (pnlOpening) - (-2000 stockOpening) = 7000 (credit)
              Assert.Equal(7000m, report.ProfitAndLoss.OpeningBalance);

              // Current P&L: 10000 (revenueCurrent) - (-3000 stockClosing - (-2000 stockOpening)) = 10000 - (-1000) = 11000 (credit)
              Assert.Equal(11000m, report.ProfitAndLoss.CurrentPeriod);
          }

          [Fact]
          public void Calculate_UnifiedOrdering_SortsDeterministically()
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
                      // Mix groups to test sorting
                      new BalanceSheetLedgerRow { LedgerName = "Suspense", ParentGroupName = "Suspense A/c", PrimaryGroup = "Suspense A/c", OpeningBalance = -100m },
                      new BalanceSheetLedgerRow { LedgerName = "Capital", ParentGroupName = "Capital Account", PrimaryGroup = "Capital Account", OpeningBalance = 500m },
                      new BalanceSheetLedgerRow { LedgerName = "Asset", ParentGroupName = "Current Assets", PrimaryGroup = "Current Assets", OpeningBalance = -300m }
                  }
              };

              var report = BalanceSheetCalculator.Calculate("Test Company", raw, request);

              // Assets Side Order should be: Current Assets, then Suspense A/c, then Difference in opening balances
              var assetNames = report.AssetSide.Lines.Select(l => l.Name).ToList();
              Assert.Equal("Current Assets", assetNames[0]);
              Assert.Equal("Suspense A/c", assetNames[1]);
              Assert.Equal("Difference in opening balances", assetNames[2]);
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
  In `tests/TallyDbLoader.Tests/BalanceSheetVerificationServiceTests.cs`, add integration tests that initialize a temp target SQLite DB and temp config DB using the ConfigRepository, seed the tables, and call the query adapter and verification service.

  Code changes:
  ```csharp
          [Fact]
          public async Task GenerateAsync_Adapter_ParsesPositiveStockValueToSignedNegative()
          {
              string targetPath = Path.Combine(Path.GetTempPath(), $"bs_target_{Guid.NewGuid()}.db");
              try
              {
                  using (var connection = new SqliteConnection($"Data Source={targetPath}"))
                  {
                      connection.Open();
                      connection.Execute("CREATE TABLE IF NOT EXISTS mst_ledger (name TEXT PRIMARY KEY, parent TEXT, opening_balance REAL);");
                      connection.Execute("CREATE TABLE IF NOT EXISTS mst_group (name TEXT PRIMARY KEY, parent TEXT, primary_group TEXT, is_revenue INTEGER);");
                      connection.Execute("CREATE TABLE IF NOT EXISTS trn_accounting (guid TEXT, ledger TEXT, amount REAL);");
                      connection.Execute("CREATE TABLE IF NOT EXISTS trn_voucher (guid TEXT PRIMARY KEY, date TEXT, is_order_voucher INTEGER, is_inventory_voucher INTEGER);");
                      connection.Execute("CREATE TABLE IF NOT EXISTS trn_closingstock_ledger (ledger TEXT, stock_date TEXT, stock_value REAL);");

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
                      connection.Execute("CREATE TABLE IF NOT EXISTS mst_ledger (name TEXT PRIMARY KEY, parent TEXT, opening_balance REAL);");
                      connection.Execute("CREATE TABLE IF NOT EXISTS mst_group (name TEXT PRIMARY KEY, parent TEXT, primary_group TEXT, is_revenue INTEGER);");
                      connection.Execute("CREATE TABLE IF NOT EXISTS trn_accounting (guid TEXT, ledger TEXT, amount REAL);");
                      connection.Execute("CREATE TABLE IF NOT EXISTS trn_voucher (guid TEXT PRIMARY KEY, date TEXT, is_order_voucher INTEGER, is_inventory_voucher INTEGER);");
                      connection.Execute("CREATE TABLE IF NOT EXISTS trn_closingstock_ledger (ledger TEXT, stock_date TEXT, stock_value REAL);");

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
                  Assert.Equal("balanced", report.Status);

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
                  Assert.Equal("balanced", report.Status);
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
                      connection.Execute("CREATE TABLE IF NOT EXISTS mst_ledger (name TEXT PRIMARY KEY, parent TEXT, opening_balance REAL);");
                      connection.Execute("CREATE TABLE IF NOT EXISTS mst_group (name TEXT PRIMARY KEY, parent TEXT, primary_group TEXT, is_revenue INTEGER);");
                      connection.Execute("CREATE TABLE IF NOT EXISTS trn_accounting (guid TEXT, ledger TEXT, amount REAL);");
                      connection.Execute("CREATE TABLE IF NOT EXISTS trn_voucher (guid TEXT PRIMARY KEY, date TEXT, is_order_voucher INTEGER, is_inventory_voucher INTEGER);");
                      connection.Execute("CREATE TABLE IF NOT EXISTS trn_closingstock_ledger (ledger TEXT, stock_date TEXT, stock_value REAL);");

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
                  Assert.Equal("balanced", report.Status);

                  // Total Opening = 0. No difference line should exist.
                  var diffLine = report.AssetSide.Lines.FirstOrDefault(l => l.Name == "Difference in opening balances");
                  Assert.Null(diffLine);
              }
              finally
              {
                  SqliteConnection.ClearAllPools();
                  if (File.Exists(configPath)) try { File.Delete(configPath); } catch { }
                  if (File.Exists(targetPath)) try { File.Delete(targetPath); } catch { }
              }
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
  git add src/TallyDbLoader.Core/Reports/BalanceSheetCalculator.cs tests/TallyDbLoader.Tests/BalanceSheetCalculatorTests.cs tests/TallyDbLoader.Tests/BalanceSheetVerificationServiceTests.cs docs/superpowers/specs/2026-06-22-balance-sheet-sign-routing-design.md docs/superpowers/plans/2026-06-22-balance-sheet-sign-routing-plan.md
  git commit -m "feat: implement dynamic sign routing and opening balance difference for balance sheet"
  ```
