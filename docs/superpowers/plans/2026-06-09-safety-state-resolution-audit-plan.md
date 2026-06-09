# Safety State Resolution & Audit Trail Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement Phase 1 safety state recovery by letting operators explicitly resolve blocked Company Profiles back to 'idle' with immutable local SQLite logging.

**Architecture:** Database migrations add the `config_audit_log` table. The Core repository performs transactional update & audit insert with strict exception guarantees. The WPF UI prompts the operator for a reason via a custom dialog and submits it using a guarded identity actor.

**Tech Stack:** .NET 8.0, C#, WPF, SQLite, Dapper, System.Text.Json.

---

### Task 1: SQLite Audit Schema Migration

**Files:**
- Modify: `src/TallyDbLoader.Core/Data/DatabaseHelper.cs`

- [ ] **Step 1: Add user_version 4 migration logic in `DatabaseHelper.cs`**
  Modify the `InitializeDatabase` method to run the SQLite script creating the `config_audit_log` table and its indexes when `version < 4`. Bump `PRAGMA user_version = 4;`.

  Insert this block directly below the `if (version < 3)` migration block (around line 208):

  ```csharp
                          if (version < 4)
                          {
                              conn.Execute(@"
                                  CREATE TABLE IF NOT EXISTS config_audit_log (
                                      id INTEGER PRIMARY KEY AUTOINCREMENT,
                                      created_at TEXT NOT NULL,
                                      actor TEXT NOT NULL,
                                      action TEXT NOT NULL,
                                      entity_type TEXT NOT NULL,
                                      entity_id INTEGER NOT NULL,
                                      entity_name TEXT NULL,
                                      before_json TEXT NOT NULL,
                                      after_json TEXT NOT NULL,
                                      reason TEXT NOT NULL
                                  );", null, transaction);

                              conn.Execute("CREATE INDEX IF NOT EXISTS ix_config_audit_log_created_at ON config_audit_log(created_at DESC);", null, transaction);
                              conn.Execute("CREATE INDEX IF NOT EXISTS ix_config_audit_log_entity ON config_audit_log(entity_type, entity_id, created_at DESC);", null, transaction);

                              conn.Execute("PRAGMA user_version = 4;", null, transaction);
                          }
  ```

- [ ] **Step 2: Verify compiling the project**
  Run: `dotnet build src/TallyDbLoader.sln`
  Expected: Build succeeds.

- [ ] **Step 3: Commit changes**
  ```bash
  git add src/TallyDbLoader.Core/Data/DatabaseHelper.cs
  git commit -m "feat(db): add user_version 4 migration to create config_audit_log table and indexes"
  ```

---

### Task 2: Core API Repository Contract & Implementation

**Files:**
- Modify: `src/TallyDbLoader.Core/Data/IConfigRepository.cs`
- Modify: `src/TallyDbLoader.Core/Data/ConfigRepository.cs`

- [ ] **Step 1: Add the signature to `IConfigRepository.cs`**
  Add the `ResolveCompanyProfileSafetyState` declaration below the `ReconcileStaleRuns` method (around line 33):

  ```csharp
          long ResolveCompanyProfileSafetyState(
              int companyProfileId,
              string actor,
              string reason,
              System.DateTime resolvedAt);
  ```

- [ ] **Step 2: Implement the logic in `ConfigRepository.cs`**
  Add `using System.Text.Json;` to the top import lines.
  Implement the method with strict transactional boundaries, state checking, JSON payload serialization, and exception mapping.

  ```csharp
          public long ResolveCompanyProfileSafetyState(
              int companyProfileId,
              string actor,
              string reason,
              System.DateTime resolvedAt)
          {
              if (string.IsNullOrWhiteSpace(actor))
                  throw new ArgumentException("Actor cannot be null or empty.", nameof(actor));
              if (string.IsNullOrWhiteSpace(reason))
                  throw new ArgumentException("Reason cannot be null or empty.", nameof(reason));

              using (var conn = new SqliteConnection(_connectionString))
              {
                  conn.Open();
                  conn.Execute("PRAGMA foreign_keys = ON;");
                  using (var transaction = conn.BeginTransaction())
                  {
                      try
                      {
                          // 2. Load the company profile
                          var profile = conn.QuerySingleOrDefault<CompanyProfile>(
                              "SELECT id, name, status FROM company_profiles WHERE id = @Id", 
                              new { Id = companyProfileId }, transaction);

                          if (profile == null)
                              throw new KeyNotFoundException($"Company profile with ID {companyProfileId} was not found.");

                          // 3. Verify status eligibility
                          if (profile.Status != "review_required" && 
                              profile.Status != "attention_required" && 
                              profile.Status != "unknown")
                          {
                              throw new InvalidOperationException($"Cannot resolve safety state. Company profile status is '{profile.Status}', which is not a safety-blocked state.");
                          }

                          // 4. Build compact snapshots
                          var beforeSnapshot = new { id = profile.Id, name = profile.Name, status = profile.Status };
                          var afterSnapshot = new { id = profile.Id, name = profile.Name, status = "idle" };

                          string beforeJson = JsonSerializer.Serialize(beforeSnapshot);
                          string afterJson = JsonSerializer.Serialize(afterSnapshot);

                          // 5. Update company status to idle
                          int affected = conn.Execute(@"
                              UPDATE company_profiles
                              SET status = 'idle'
                              WHERE id = @Id;", new { Id = companyProfileId }, transaction);

                          if (affected != 1)
                              throw new InvalidOperationException($"Expected exactly 1 row to be updated, but affected {affected} rows.");

                          // 7. Insert audit log row
                          long auditId;
                          try
                          {
                              conn.Execute(@"
                                  INSERT INTO config_audit_log (created_at, actor, action, entity_type, entity_id, entity_name, before_json, after_json, reason)
                                  VALUES (@CreatedAt, @Actor, @Action, @EntityType, @EntityId, @EntityName, @BeforeJson, @AfterJson, @Reason);",
                                  new
                                  {
                                      CreatedAt = resolvedAt.ToString("o"),
                                      Actor = actor.Trim(),
                                      Action = "resolve_safety_state",
                                      EntityType = "company_profile",
                                      EntityId = companyProfileId,
                                      EntityName = profile.Name,
                                      BeforeJson = beforeJson,
                                      AfterJson = afterJson,
                                      Reason = reason.Trim()
                                  }, transaction);
                              auditId = conn.QuerySingle<long>("SELECT last_insert_rowid();", null, transaction);
                          }
                          catch (Exception ex)
                          {
                              throw new InvalidOperationException("Failed to write to the config audit log table.", ex);
                          }

                          // 8. Commit and return ID
                          transaction.Commit();
                          return auditId;
                      }
                      catch
                      {
                          transaction.Rollback();
                          throw;
                      }
                  }
              }
          }
  ```

- [ ] **Step 3: Compile the project**
  Run: `dotnet build src/TallyDbLoader.sln`
  Expected: Build succeeds with no new warnings or compilation errors introduced by this slice.

- [ ] **Step 4: Commit changes**
  ```bash
  git add src/TallyDbLoader.Core/Data/IConfigRepository.cs src/TallyDbLoader.Core/Data/ConfigRepository.cs
  git commit -m "feat(core): implement transactional ResolveCompanyProfileSafetyState repository API"
  ```

---

### Task 3: Core Integration Testing

**Files:**
- Modify: `tests/TallyDbLoader.Tests/SyncLifecycleSafetyTests.cs`

- [ ] **Step 1: Implement integration tests for the safety state resolution logic**
  Add tests inside `SyncLifecycleSafetyTests.cs` covering success, validation exceptions, invalid state transition rejections, and transaction rollback.

  Add these test methods to the `SyncLifecycleSafetyTests` class:

  ```csharp
          [Fact]
          public void ResolveCompanyProfileSafetyState_Success_UpdatesStatusAndLogsAudit()
          {
              var profile = SeedCompany("attention_required");
              DateTime resolvedAt = DateTime.Now;

              long auditId = _repo.ResolveCompanyProfileSafetyState(profile.Id, "OperatorName", "Resolved network issue", resolvedAt);
              Assert.True(auditId > 0);

              // Assert status was updated to idle
              var updated = _repo.GetAllCompanyProfiles().Find(x => x.Id == profile.Id);
              Assert.Equal("idle", updated.Status);

              // Assert audit log entry exists and contains correct information
              using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
              {
                  var row = conn.QuerySingle<dynamic>(
                      "SELECT * FROM config_audit_log WHERE id = @Id", new { Id = auditId });

                  Assert.Equal("OperatorName", row.actor);
                  Assert.Equal("resolve_safety_state", row.action);
                  Assert.Equal("company_profile", row.entity_type);
                  Assert.Equal((long)profile.Id, row.entity_id);
                  Assert.Equal(profile.Name, row.entity_name);
                  Assert.Equal("Resolved network issue", row.reason);
                  Assert.Equal(resolvedAt.ToString("o"), row.created_at);
                  Assert.Contains("\"status\":\"attention_required\"", (string)row.before_json);
                  Assert.Contains("\"status\":\"idle\"", (string)row.after_json);
              }
          }

          [Fact]
          public void ResolveCompanyProfileSafetyState_EmptyInputs_ThrowsArgumentException()
          {
              var profile = SeedCompany("attention_required");

              Assert.Throws<ArgumentException>(() => 
                  _repo.ResolveCompanyProfileSafetyState(profile.Id, "   ", "Reason", DateTime.Now));

              Assert.Throws<ArgumentException>(() => 
                  _repo.ResolveCompanyProfileSafetyState(profile.Id, "Operator", "", DateTime.Now));
          }

          [Fact]
          public void ResolveCompanyProfileSafetyState_InvalidStatus_ThrowsInvalidOperationException()
          {
              var profile = SeedCompany("idle");

              Assert.Throws<InvalidOperationException>(() => 
                  _repo.ResolveCompanyProfileSafetyState(profile.Id, "Operator", "Reason", DateTime.Now));
          }

          [Fact]
          public void ResolveCompanyProfileSafetyState_AuditInsertFailure_RollsBackTransaction()
          {
              var profile = SeedCompany("review_required");

              using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
              {
                  conn.Open();
                  // Drop config_audit_log table temporarily inside the DB connection
                  conn.Execute("DROP TABLE config_audit_log;");
              }

              // The insert will fail because table is dropped, throwing InvalidOperationException
              var ex = Assert.Throws<InvalidOperationException>(() => 
                  _repo.ResolveCompanyProfileSafetyState(profile.Id, "Operator", "Reason", DateTime.Now));
              
              Assert.NotNull(ex.InnerException);

              // Assert company profile status remains review_required (rolled back)
              var updated = _repo.GetAllCompanyProfiles().Find(x => x.Id == profile.Id);
              Assert.Equal("review_required", updated.Status);
          }
  ```

- [ ] **Step 2: Run the tests to verify correctness**
  Run: `dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --filter "FullyQualifiedName~SyncLifecycleSafetyTests"`
  Expected: All tests pass.

- [ ] **Step 3: Commit changes**
  ```bash
  git add tests/TallyDbLoader.Tests/SyncLifecycleSafetyTests.cs
  git commit -m "test(core): add integration tests for safety state resolution and rollback"
  ```

---

### Task 4: UI Resolution Dialog Window

**Files:**
- Create: `src/TallyDbLoader.Wpf/Views/ResolveSafetyBlockWindow.xaml`
- Create: `src/TallyDbLoader.Wpf/Views/ResolveSafetyBlockWindow.xaml.cs`

- [ ] **Step 1: Create `ResolveSafetyBlockWindow.xaml`**
  Create this WPF Window file representing the modal prompt dialog.

  ```xml
  <Window x:Class="TallyDbLoader.Wpf.Views.ResolveSafetyBlockWindow"
          xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
          xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
          Title="Resolve Safety Block" Height="220" Width="420"
          WindowStartupLocation="CenterOwner"
          Background="#121212" Foreground="#FFFFFF"
          BorderBrush="#333333" BorderThickness="1"
          ResizeMode="NoResize" ShowInTaskbar="False">
      <Window.Resources>
          <Style TargetType="Button">
              <Setter Property="Background" Value="#4A90E2"/>
              <Setter Property="Foreground" Value="White"/>
              <Setter Property="Padding" Value="15,6"/>
              <Setter Property="BorderThickness" Value="0"/>
              <Setter Property="Cursor" Value="Hand"/>
              <Setter Property="Margin" Value="5"/>
              <Style.Triggers>
                  <Trigger Property="IsMouseOver" Value="True">
                      <Setter Property="Background" Value="#357ABD"/>
                  </Trigger>
              </Style.Triggers>
          </Style>
      </Window.Resources>
      <Grid Margin="15">
          <Grid.RowDefinitions>
              <RowDefinition Height="Auto"/>
              <RowDefinition Height="*"/>
              <RowDefinition Height="Auto"/>
          </Grid.RowDefinitions>

          <StackPanel Grid.Row="0" Margin="0,0,0,12">
              <TextBlock Text="Resolve Safety Block" FontSize="14" FontWeight="Bold" Foreground="#4A90E2"/>
              <TextBlock x:Name="SubtitleText" Text="Please enter the reason for resolving the safety block to continue. An immutable audit record will be logged." 
                         TextWrapping="Wrap" Margin="0,4,0,0" Foreground="#CCCCCC" FontSize="11"/>
          </StackPanel>

          <TextBox Grid.Row="1" x:Name="ReasonTextBox" Background="#1E1E1E" Foreground="White" 
                   BorderBrush="#333333" BorderThickness="1" Padding="6" FontSize="12" AcceptsReturn="True" 
                   TextWrapping="Wrap" VerticalAlignment="Stretch" Margin="0,0,0,12"/>

          <StackPanel Grid.Row="2" Orientation="Horizontal" HorizontalAlignment="Right">
              <Button Content="Cancel" Click="CancelButton_Click" Background="#555555"/>
              <Button Content="Resolve Block" Click="ResolveButton_Click" Width="130"/>
          </StackPanel>
      </Grid>
  </Window>
  ```

- [ ] **Step 2: Create `ResolveSafetyBlockWindow.xaml.cs`**
  Create the code-behind for the window.

  ```csharp
  using System.Windows;

  namespace TallyDbLoader.Wpf.Views
  {
      public partial class ResolveSafetyBlockWindow : Window
      {
          public string Reason { get; private set; } = string.Empty;

          public ResolveSafetyBlockWindow(string companyName)
          {
              InitializeComponent();
              SubtitleText.Text = $"Please enter the reason for resolving the safety block on '{companyName}' to continue. An immutable audit record will be logged.";
              ReasonTextBox.Focus();
          }

          private void ResolveButton_Click(object sender, RoutedEventArgs e)
          {
              string txt = ReasonTextBox.Text?.Trim() ?? string.Empty;
              if (string.IsNullOrWhiteSpace(txt))
              {
                  MessageBox.Show("Reason is required to resolve a safety block.", "Reason Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                  return;
              }
              Reason = txt;
              DialogResult = true;
              Close();
          }

          private void CancelButton_Click(object sender, RoutedEventArgs e)
          {
              DialogResult = false;
              Close();
          }
      }
  }
  ```

- [ ] **Step 3: Compile the project**
  Run: `dotnet build src/TallyDbLoader.sln`
  Expected: Build succeeds.

- [ ] **Step 4: Commit changes**
  ```bash
  git add src/TallyDbLoader.Wpf/Views/ResolveSafetyBlockWindow.xaml src/TallyDbLoader.Wpf/Views/ResolveSafetyBlockWindow.xaml.cs
  git commit -m "feat(wpf): create ResolveSafetyBlockWindow view dialog for prompting operator reasons"
  ```

---

### Task 5: WPF ViewModel Integration

**Files:**
- Modify: `src/TallyDbLoader.Wpf/MainViewModel.cs`
- Modify: `src/TallyDbLoader.Wpf/MainWindow.xaml.cs`

- [ ] **Step 1: Add callback property and command in `MainViewModel.cs`**
  Add the prompter callback property (around line 81):

  ```csharp
          public Func<string, string?>? SafetyResolveReasonPrompter { get; set; }
  ```

  Add the Command property (around line 440):

  ```csharp
          public ICommand ResolveSafetyBlockCommand { get; }
  ```

  Bind the command inside the constructor (around line 475):

  ```csharp
              ResolveSafetyBlockCommand = new RelayCommand<object?>(ResolveSafetyBlock);
  ```

- [ ] **Step 2: Add MVVM validation property `CanResolveSelectedCompanySafetyBlock`**
  Add the property below the `IsSyncRunning` helpers (around line 428):

  ```csharp
          public bool CanResolveSelectedCompanySafetyBlock =>
              SelectedCompany != null &&
              (SelectedCompany.Status == "review_required" ||
               SelectedCompany.Status == "attention_required" ||
               SelectedCompany.Status == "unknown");
  ```

  Trigger its PropertyChanged notification in:
  1. The setter of `SelectedCompany` (inside `SelectedCompany` property, line 113):
     ```csharp
                     OnPropertyChanged();
                     OnPropertyChanged(nameof(CanResolveSelectedCompanySafetyBlock));
     ```
  2. Inside `LoadConfiguration` (around line 860):
     ```csharp
                 OnPropertyChanged(nameof(SelectedCompany));
                 OnPropertyChanged(nameof(CanResolveSelectedCompanySafetyBlock));
     ```

- [ ] **Step 3: Implement the `ResolveSafetyBlock` method in `MainViewModel.cs`**
  Add the command execution implementation using a guarded identity actor lookup.

  Add this method to `MainViewModel.cs` (around line 655):

  ```csharp
          private void ResolveSafetyBlock(object? parameter)
          {
              var company = parameter as CompanyProfile;
              if (company == null) return;

              if (SafetyResolveReasonPrompter == null) return;

              string? reason = SafetyResolveReasonPrompter(company.Name);
              if (string.IsNullOrWhiteSpace(reason)) return; // Cancelled or empty

              // Resolve actor via hierarchy inside a guarded try-catch block
              string actor = "unknown-user";
              try
              {
                  string? winIdentity = System.Security.Principal.WindowsIdentity.GetCurrent()?.Name;
                  if (!string.IsNullOrWhiteSpace(winIdentity))
                  {
                      actor = winIdentity;
                  }
                  else
                  {
                      string? envUser = Environment.UserName;
                      if (!string.IsNullOrWhiteSpace(envUser)) actor = envUser;
                  }
              }
              catch
              {
                  try
                  {
                      string? envUser = Environment.UserName;
                      if (!string.IsNullOrWhiteSpace(envUser)) actor = envUser;
                  }
                  catch { }
              }

              try
              {
                  _repo.ResolveCompanyProfileSafetyState(company.Id, actor, reason, DateTime.Now);
                  LoadConfiguration();
                  ShowToast("Block Resolved", $"Safety block on '{company.Name}' successfully resolved.", "ok");
              }
              catch (Exception ex)
              {
                  ShowToast("Resolution Failed", ex.Message, "err");
              }
          }
  ```

- [ ] **Step 4: Register Callback in `MainWindow.xaml.cs`**
  Bind the prompter callback to the new custom dialog window (around line 36):

  ```csharp
              _vm.SafetyResolveReasonPrompter = (companyName) =>
              {
                  var dialog = new TallyDbLoader.Wpf.Views.ResolveSafetyBlockWindow(companyName);
                  dialog.Owner = this;
                  if (dialog.ShowDialog() == true)
                  {
                      return dialog.Reason;
                  }
                  return null;
              };
  ```

- [ ] **Step 5: Compile the project**
  Run: `dotnet build src/TallyDbLoader.sln`
  Expected: Build succeeds.

- [ ] **Step 6: Commit changes**
  ```bash
  git add src/TallyDbLoader.Wpf/MainViewModel.cs src/TallyDbLoader.Wpf/MainWindow.xaml.cs
  git commit -m "feat(wpf): integrate ResolveSafetyBlockCommand, validation property, and dialog callback in view model"
  ```

---

### Task 6: View UI Updates

**Files:**
- Modify: `src/TallyDbLoader.Wpf/Views/CompaniesPage.xaml`

- [ ] **Step 1: Add the "Resolve Safety Block" button to `CompaniesPage.xaml`**
  Add the button to the CommandBar StackPanel, setting its visibility or enabled binding to `CanResolveSelectedCompanySafetyBlock`.

  Replace the StackPanel in the CommandBar section (lines 30-34):

  ```xml
              <StackPanel Orientation="Horizontal" VerticalAlignment="Center">
                  <Button Content="New Company Profile" Command="{Binding StartEditingCompanyCommand}" CommandParameter="0" Style="{StaticResource PrimaryButtonStyle}" Margin="0,0,8,0"/>
                  <Button Content="Configure Selected" Command="{Binding StartEditingCompanyCommand}" CommandParameter="{Binding SelectedCompany.Id}" IsEnabled="{Binding SelectedCompany, Converter={StaticResource NullToBoolConverter}}" Style="{StaticResource StandardButtonStyle}" Margin="0,0,8,0"/>
                  <Button Content="Resolve Safety Block" Command="{Binding ResolveSafetyBlockCommand}" CommandParameter="{Binding SelectedCompany}" IsEnabled="{Binding CanResolveSelectedCompanySafetyBlock}" Style="{StaticResource StandardButtonStyle}" Margin="0,0,8,0" Background="#B45309" Foreground="White"/>
                  <Button Content="Delete Selected" Command="{Binding DeleteCompanyProfileCommand}" CommandParameter="{Binding SelectedCompany.Id}" IsEnabled="{Binding SelectedCompany, Converter={StaticResource NullToBoolConverter}}" Style="{StaticResource StandardButtonStyle}" Foreground="#EF4444"/>
              </StackPanel>
  ```

- [ ] **Step 2: Compile the project**
  Run: `dotnet build src/TallyDbLoader.sln`
  Expected: Build succeeds.

- [ ] **Step 3: Commit changes**
  ```bash
  git add src/TallyDbLoader.Wpf/Views/CompaniesPage.xaml
  git commit -m "feat(view): add Resolve Safety Block button to Companies page toolbar"
  ```

---

### Task 7: ViewModel Unit Testing

**Files:**
- Modify: `tests/TallyDbLoader.Tests/MainViewModelTests.cs`

- [ ] **Step 1: Implement ViewModel command tests**
  Add unit tests validating the resolution command flow, dialog cancellation, success routing, and actor fallback.

  Add these test methods to the `MainViewModelTests` class:

  ```csharp
          [Fact]
          public void Test_ResolveSafetyBlockCommand_Cancelled()
          {
              string dbPath = "vm_test_resolve_cancel.db";
              if (File.Exists(dbPath)) File.Delete(dbPath);
              DatabaseHelper.InitializeDatabase(dbPath);

              var vm = new MainViewModel(dbPath);
              vm.DisableDispatcher = true;

              // Seed blocked company
              var repo = new ConfigRepository(dbPath);
              var dbProfile = new DatabaseProfile { Name = "TestDb", Technology = "sqlite" };
              repo.SaveDatabaseProfile(dbProfile);
              var dbFromDb = repo.GetDatabaseProfileByName("TestDb");

              var company = new CompanyProfile
              {
                  Name = "BlockedCo",
                  DbProfileId = dbFromDb.Id,
                  TargetCatalog = "test",
                  Status = "attention_required"
              };
              repo.SaveCompanyProfile(company);
              vm.LoadConfiguration();

              // Select the company
              vm.SelectedCompany = vm.Companies.First(c => c.Name == "BlockedCo");
              Assert.True(vm.CanResolveSelectedCompanySafetyBlock);

              // Cancel dialog callback
              vm.SafetyResolveReasonPrompter = (name) => null;

              // Execute
              vm.ResolveSafetyBlockCommand.Execute(vm.SelectedCompany);

              // Assert status remains blocked
              Assert.Equal("attention_required", vm.SelectedCompany.Status);

              Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
              if (File.Exists(dbPath)) File.Delete(dbPath);
          }

          [Fact]
          public void Test_ResolveSafetyBlockCommand_Success()
          {
              string dbPath = "vm_test_resolve_ok.db";
              if (File.Exists(dbPath)) File.Delete(dbPath);
              DatabaseHelper.InitializeDatabase(dbPath);

              var vm = new MainViewModel(dbPath);
              vm.DisableDispatcher = true;

              var repo = new ConfigRepository(dbPath);
              var dbProfile = new DatabaseProfile { Name = "TestDb", Technology = "sqlite" };
              repo.SaveDatabaseProfile(dbProfile);
              var dbFromDb = repo.GetDatabaseProfileByName("TestDb");

              var company = new CompanyProfile
              {
                  Name = "BlockedCo",
                  DbProfileId = dbFromDb.Id,
                  TargetCatalog = "test",
                  Status = "unknown"
              };
              repo.SaveCompanyProfile(company);
              vm.LoadConfiguration();

              vm.SelectedCompany = vm.Companies.First(c => c.Name == "BlockedCo");

              // Reason mock
              vm.SafetyResolveReasonPrompter = (name) => "operator manual override reason";

              // Execute
              vm.ResolveSafetyBlockCommand.Execute(vm.SelectedCompany);

              // Assert status is now idle and command is disabled
              Assert.Equal("idle", vm.SelectedCompany.Status);
              Assert.False(vm.CanResolveSelectedCompanySafetyBlock);

              // Verify audit trail exists
              using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
              {
                  conn.Open();
                  var auditCount = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM config_audit_log");
                  Assert.Equal(1, auditCount);
              }

              Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
              if (File.Exists(dbPath)) File.Delete(dbPath);
          }
  ```

- [ ] **Step 2: Run all tests in the solution**
  Run: `dotnet test src/TallyDbLoader.sln`
  Expected: All tests pass.

- [ ] **Step 3: Commit changes**
  ```bash
  git add tests/TallyDbLoader.Tests/MainViewModelTests.cs
  git commit -m "test(wpf): add unit tests for ResolveSafetyBlockCommand on MainViewModel"
  ```
