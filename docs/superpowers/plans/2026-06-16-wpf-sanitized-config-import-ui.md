# WPF Sanitized Config Import UI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a thin WPF entry point for importing sanitized configuration JSON files, prompting users for required passwords via secure dialogs, and blocking imports if the engine is running or conflicts are found.

**Architecture:** Create a Core-side preview API to identify required password prompts and conflicts without duplicating JSON schema structures in WPF. Use local injectable handlers in the WPF MainViewModel for dialog delegation, ensuring testability and keeping cleartext passwords strictly in short-lived local memory. Extract shared private helper methods for both envelope/structural validations and conflict detection/matching in `ConfigImportService` to eliminate drift between preview and import execution.

**Tech Stack:** .NET Core 8.0, C#, WPF, xUnit, SQLite, Dapper

---

## File Structure

### Core Layer (New/Modified)
* **Create:** `src/TallyDbLoader.Core/Models/ConfigImportPreview.cs` (preview structures)
* **Modify:** `src/TallyDbLoader.Core/Data/ConfigImportService.cs` (implements preview extraction, and extracts private validation/conflict helpers)
* **Test:** `tests/TallyDbLoader.Tests/ConfigImportServiceTests.cs` (verifies preview validation, conflict checks, missing fields, and conflict parity tests)

### WPF Layer (New/Modified)
* **Create:** `src/TallyDbLoader.Wpf/Views/ImportPasswordPromptWindow.xaml` (secure modal layout)
* **Create:** `src/TallyDbLoader.Wpf/Views/ImportPasswordPromptWindow.xaml.cs` (dialog code-behind using programmatically created controls to avoid virtualization issues)
* **Modify:** `src/TallyDbLoader.Wpf/MainViewModel.cs` (command wiring and execution guard flow)
* **Modify:** `src/TallyDbLoader.Wpf/MainWindow.xaml.cs` (hooking dialog and password prompts, adding necessary usings)
* **Modify:** `src/TallyDbLoader.Wpf/Views/SettingsPage.xaml` (settings page UI button)
* **Test:** `tests/TallyDbLoader.Tests/MainViewModelTests.cs` (verifies WPF command flow, guards, delegates, and database row-count state invariants)

---

## Tasks

### Task 1: Core Preview Models and Method

**Files:**
* Create: `src/TallyDbLoader.Core/Models/ConfigImportPreview.cs`
* Modify: `src/TallyDbLoader.Core/Data/ConfigImportService.cs`
* Test: `tests/TallyDbLoader.Tests/ConfigImportServiceTests.cs`

- [ ] **Step 1: Write preview model classes**
Create `src/TallyDbLoader.Core/Models/ConfigImportPreview.cs`:
```csharp
using System;
using System.Collections.Generic;

namespace TallyDbLoader.Core.Models
{
    public sealed class ConfigImportPreview
    {
        public IReadOnlyList<ConfigImportPreviewDatabaseProfile> DatabaseProfiles { get; init; } = Array.Empty<ConfigImportPreviewDatabaseProfile>();
        public IReadOnlyList<ConfigImportPreviewCompanyProfile> CompanyProfiles { get; init; } = Array.Empty<ConfigImportPreviewCompanyProfile>();
        public IReadOnlyList<string> ValidationErrors { get; init; } = Array.Empty<string>();
        public bool HasConflicts { get; init; }
        public bool IsValid => ValidationErrors.Count == 0;
    }

    public sealed class ConfigImportPreviewDatabaseProfile
    {
        public int SourceId { get; init; }
        public string Name { get; init; } = "";
        public bool HasPassword { get; init; }
        public bool HasConflict { get; init; }
    }

    public sealed class ConfigImportPreviewCompanyProfile
    {
        public int SourceId { get; init; }
        public string Name { get; init; } = "";
        public bool HasConflict { get; init; }
    }
}
```

- [ ] **Step 2: Add focused tests for PreviewJson**
Add targeted tests to `tests/TallyDbLoader.Tests/ConfigImportServiceTests.cs`:
```csharp
        [Fact]
        public void PreviewJson_WithInvalidJson_ReturnsValidationErrors()
        {
            var fake = new FakeConfigRepository();
            var service = new ConfigImportService(fake);

            var preview = service.PreviewJson("invalid json");
            Assert.False(preview.IsValid);
            Assert.Contains("Invalid JSON content", preview.ValidationErrors[0]);
        }

        [Fact]
        public void PreviewJson_WithValidPayload_ReturnsProfiles()
        {
            var fake = new FakeConfigRepository();
            var service = new ConfigImportService(fake);

            string json = @"{
                ""format"": ""tally-db-loader.config-export"",
                ""schema_version"": 1,
                ""application_version"": ""2.0.0"",
                ""payload"": {
                    ""database_profiles"": [
                        { ""id"": 2, ""name"": ""NewDb"", ""technology"": ""postgres"", ""server"": ""localhost"", ""has_password"": false }
                    ],
                    ""company_profiles"": [
                        { ""id"": 11, ""name"": ""NewComp"", ""db_profile_id"": 2, ""target_catalog"": ""catalog"" }
                    ]
                }
            }";

            var preview = service.PreviewJson(json);
            Assert.True(preview.IsValid);
            Assert.False(preview.HasConflicts);

            var db = Assert.Single(preview.DatabaseProfiles);
            Assert.Equal(2, db.SourceId);
            Assert.Equal("NewDb", db.Name);
            Assert.False(db.HasConflict);
            Assert.False(db.HasPassword);

            var comp = Assert.Single(preview.CompanyProfiles);
            Assert.Equal(11, comp.SourceId);
            Assert.Equal("NewComp", comp.Name);
            Assert.False(comp.HasConflict);
        }

        [Fact]
        public void PreviewJson_WithDbConflict_SetsHasConflicts()
        {
            var fake = new FakeConfigRepository();
            fake.DatabaseProfiles.Add(new DatabaseProfile { Name = "ConflictingDb" });
            var service = new ConfigImportService(fake);

            string json = @"{
                ""format"": ""tally-db-loader.config-export"",
                ""schema_version"": 1,
                ""application_version"": ""2.0.0"",
                ""payload"": {
                    ""database_profiles"": [
                        { ""id"": 1, ""name"": ""ConflictingDb"", ""technology"": ""postgres"", ""server"": ""localhost"", ""has_password"": true }
                    ],
                    ""company_profiles"": []
                }
            }";

            var preview = service.PreviewJson(json);
            Assert.True(preview.IsValid);
            Assert.True(preview.HasConflicts);
            Assert.True(preview.DatabaseProfiles[0].HasConflict);
        }

        [Fact]
        public void PreviewJson_WithCompanyConflict_SetsHasConflicts()
        {
            var fake = new FakeConfigRepository();
            fake.CompanyProfiles.Add(new CompanyProfile { Name = "ConflictingComp" });
            var service = new ConfigImportService(fake);

            string json = @"{
                ""format"": ""tally-db-loader.config-export"",
                ""schema_version"": 1,
                ""application_version"": ""2.0.0"",
                ""payload"": {
                    ""database_profiles"": [
                        { ""id"": 1, ""name"": ""MyDB"", ""technology"": ""postgres"", ""server"": ""localhost"", ""has_password"": false }
                    ],
                    ""company_profiles"": [
                        { ""id"": 10, ""name"": ""ConflictingComp"", ""db_profile_id"": 1, ""target_catalog"": ""catalog"" }
                    ]
                }
            }";

            var preview = service.PreviewJson(json);
            Assert.True(preview.IsValid);
            Assert.True(preview.HasConflicts);
            Assert.True(preview.CompanyProfiles[0].HasConflict);
        }

        [Fact]
        public void PreviewJson_WithMissingHasPassword_ReturnsValidationError()
        {
            var fake = new FakeConfigRepository();
            var service = new ConfigImportService(fake);

            string json = @"{
                ""format"": ""tally-db-loader.config-export"",
                ""schema_version"": 1,
                ""application_version"": ""2.0.0"",
                ""payload"": {
                    ""database_profiles"": [
                        { ""id"": 1, ""name"": ""MyDB"", ""technology"": ""postgres"", ""server"": ""localhost"" }
                    ],
                    ""company_profiles"": []
                }
            }";

            var preview = service.PreviewJson(json);
            Assert.False(preview.IsValid);
            Assert.Contains("missing has_password flag", preview.ValidationErrors[0]);
        }

        [Fact]
        public void PreviewJson_WithBrokenDbProfileIdReference_ReturnsValidationError()
        {
            var fake = new FakeConfigRepository();
            var service = new ConfigImportService(fake);

            string json = @"{
                ""format"": ""tally-db-loader.config-export"",
                ""schema_version"": 1,
                ""application_version"": ""2.0.0"",
                ""payload"": {
                    ""database_profiles"": [],
                    ""company_profiles"": [
                        { ""id"": 10, ""name"": ""OrphanComp"", ""db_profile_id"": 99, ""target_catalog"": ""catalog"" }
                    ]
                }
            }";

            var preview = service.PreviewJson(json);
            Assert.False(preview.IsValid);
            Assert.Contains("references database profile ID 99 which is not present in the import payload", preview.ValidationErrors[0]);
        }

        [Fact]
        public void ImportAndPreview_HaveParity_ForConflicts()
        {
            var fake = new FakeConfigRepository();
            fake.DatabaseProfiles.Add(new DatabaseProfile { Name = "ConflictingDb" });
            fake.CompanyProfiles.Add(new CompanyProfile { Name = "ConflictingComp" });
            var service = new ConfigImportService(fake);

            string json = @"{
                ""format"": ""tally-db-loader.config-export"",
                ""schema_version"": 1,
                ""application_version"": ""2.0.0"",
                ""payload"": {
                    ""database_profiles"": [
                        { ""id"": 1, ""name"": ""ConflictingDb"", ""technology"": ""postgres"", ""server"": ""localhost"", ""has_password"": false }
                    ],
                    ""company_profiles"": [
                        { ""id"": 10, ""name"": ""ConflictingComp"", ""db_profile_id"": 1, ""target_catalog"": ""catalog"" }
                    ]
                }
            }";

            // 1. Verify Preview detects conflict
            var preview = service.PreviewJson(json);
            Assert.True(preview.IsValid);
            Assert.True(preview.HasConflicts);
            Assert.True(preview.DatabaseProfiles[0].HasConflict);
            Assert.True(preview.CompanyProfiles[0].HasConflict);

            // 2. Verify ImportJson throws validation exception with matching error message
            var decision = new ImportDecision(); // No strategy given
            var importEx = Assert.Throws<ConfigImportValidationException>(() =>
                service.ImportJson(json, decision, "system", "reason"));

            Assert.Contains("Conflict detected for database profile 'ConflictingDb'", importEx.Errors[0]);
        }
```

- [ ] **Step 3: Run tests and ensure they fail**
Run: `dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore --filter "ConfigImportServiceTests"`
Expected: Compilation failure (PreviewJson is missing).

- [ ] **Step 4: Extract validation and conflict matching logic and implement PreviewJson**
Modify `src/TallyDbLoader.Core/Data/ConfigImportService.cs` by extracting the envelope, structural validation, and conflict-matching algorithms to shared private helper methods.

First, add these helper methods:
```csharp
        private void ValidateEnvelopeAndPayload(ExportEnvelope envelope, List<string> errors)
        {
            if (envelope.Format != "tally-db-loader.config-export")
            {
                errors.Add("Unsupported or invalid format string.");
            }
            if (envelope.Schema_Version != 1)
            {
                errors.Add("Unsupported schema version. Only version 1 is supported.");
            }
            if (string.IsNullOrWhiteSpace(envelope.Application_Version))
            {
                errors.Add("Application version must be a non-empty string.");
            }
            if (envelope.Payload == null)
            {
                errors.Add("Configuration payload is missing or empty.");
                return;
            }

            var payload = envelope.Payload;
            var dbProfiles = payload.Database_Profiles ?? new List<ExportDatabaseProfile>();
            var companyProfiles = payload.Company_Profiles ?? new List<ExportCompanyProfile>();

            foreach (var db in dbProfiles)
            {
                if (db == null)
                {
                    errors.Add("Database profile element is null.");
                    continue;
                }
                if (db.Id <= 0)
                {
                    errors.Add("Database profile has an invalid or missing ID.");
                }
                if (string.IsNullOrWhiteSpace(db.Name))
                {
                    errors.Add($"Database profile ID {db.Id} is missing a name.");
                }
                if (string.IsNullOrWhiteSpace(db.Technology))
                {
                    errors.Add($"Database profile '{db.Name}' (ID {db.Id}) is missing technology.");
                }
                if (string.IsNullOrWhiteSpace(db.Server))
                {
                    errors.Add($"Database profile '{db.Name}' (ID {db.Id}) is missing server host.");
                }
                if (db.Has_Password == null)
                {
                    errors.Add($"Database profile '{db.Name}' (ID {db.Id}) is missing has_password flag.");
                }
            }

            foreach (var comp in companyProfiles)
            {
                if (comp == null)
                {
                    errors.Add("Company profile element is null.");
                    continue;
                }
                if (comp.Id <= 0)
                {
                    errors.Add("Company profile has an invalid or missing ID.");
                }
                if (string.IsNullOrWhiteSpace(comp.Name))
                {
                    errors.Add($"Company profile ID {comp.Id} is missing a name.");
                }
                if (comp.Db_Profile_Id <= 0)
                {
                    errors.Add($"Company profile '{comp.Name}' (ID {comp.Id}) is missing db_profile_id.");
                }
                if (string.IsNullOrWhiteSpace(comp.Target_Catalog))
                {
                    errors.Add($"Company profile '{comp.Name}' (ID {comp.Id}) is missing target_catalog.");
                }
            }

            if (errors.Count > 0) return;

            var dbSourceIds = new HashSet<int>();
            foreach (var db in dbProfiles)
            {
                if (!dbSourceIds.Add(db.Id))
                    errors.Add($"Duplicate database profile source ID: {db.Id}");
            }

            var compSourceIds = new HashSet<int>();
            foreach (var comp in companyProfiles)
            {
                if (!compSourceIds.Add(comp.Id))
                    errors.Add($"Duplicate company profile source ID: {comp.Id}");
            }
        }

        private DatabaseProfile? FindExistingDatabaseConflict(ExportDatabaseProfile sourceDb, List<DatabaseProfile> existingDbs)
        {
            var sourceNameNorm = sourceDb.Name.Trim().ToLowerInvariant();
            return existingDbs.FirstOrDefault(e => e.Name.Trim().ToLowerInvariant() == sourceNameNorm);
        }

        private CompanyProfile? FindExistingCompanyConflict(ExportCompanyProfile sourceComp, List<CompanyProfile> existingComps, List<string> errors)
        {
            var sourceNameNorm = sourceComp.Name.Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(sourceComp.Tally_Guid))
            {
                var matchByGuid = existingComps.FirstOrDefault(e => e.TallyGuid == sourceComp.Tally_Guid);
                var matchByName = existingComps.FirstOrDefault(e => e.Name.Trim().ToLowerInvariant() == sourceNameNorm);

                if (matchByGuid != null && matchByName != null && matchByGuid.Id != matchByName.Id)
                {
                    errors.Add($"Ambiguous conflict for company profile '{sourceComp.Name}': matches GUID with one profile and Name with another. Import blocked.");
                    return null;
                }

                return matchByGuid ?? matchByName;
            }
            else
            {
                return existingComps.FirstOrDefault(e => e.Name.Trim().ToLowerInvariant() == sourceNameNorm);
            }
        }
```

Now, replace the validation and conflict resolution matching blocks in `ImportJson` to use these helpers:
Replace `ImportJson` lines 74-185 (the basic validation) with:
```csharp
            ExportEnvelope envelope;
            try
            {
                envelope = JsonSerializer.Deserialize<ExportEnvelope>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                           ?? throw new InvalidOperationException("Failed to deserialize JSON.");
            }
            catch (Exception ex)
            {
                throw new ConfigImportValidationException(new[] { $"Invalid JSON content: {ex.Message}" });
            }

            var errors = new List<string>();
            ValidateEnvelopeAndPayload(envelope, errors);

            if (errors.Count > 0)
                throw new ConfigImportValidationException(errors);
```

Modify the loop that resolves database conflicts (in `ImportJson`) to use `FindExistingDatabaseConflict`:
```csharp
            // 4. Resolve Database Conflicts & Passwords
            foreach (var sourceDb in dbProfiles)
            {
                var existingMatch = FindExistingDatabaseConflict(sourceDb, existingDbs);
                if (existingMatch != null)
                {
                    ...
```

Modify the loop that resolves company conflicts (in `ImportJson`) to use `FindExistingCompanyConflict`:
```csharp
            // 5. Resolve Company Conflicts & skipped DB profiles validation
            foreach (var sourceComp in companyProfiles)
            {
                // A company profile must only reference a DB profile in the payload
                var dbInPayload = dbProfiles.FirstOrDefault(d => d.Id == sourceComp.Db_Profile_Id);
                if (dbInPayload == null)
                {
                    errors.Add($"Company profile '{sourceComp.Name}' references database profile ID {sourceComp.Db_Profile_Id} which is not present in the import payload.");
                    continue;
                }

                // If referenced DB profile is skipped, company MUST also be skipped
                bool dbIsSkipped = skippedDbIds.Contains(sourceComp.Db_Profile_Id);

                var existingMatch = FindExistingCompanyConflict(sourceComp, existingComps, errors);
                ...
```

Then, implement `PreviewJson` using these exact same helpers to avoid validation/conflict drift:
```csharp
        public TallyDbLoader.Core.Models.ConfigImportPreview PreviewJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new ArgumentException("JSON content cannot be null or empty.", nameof(json));

            ExportEnvelope envelope;
            try
            {
                envelope = JsonSerializer.Deserialize<ExportEnvelope>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                           ?? throw new InvalidOperationException("Failed to deserialize JSON.");
            }
            catch (Exception ex)
            {
                return new TallyDbLoader.Core.Models.ConfigImportPreview
                {
                    ValidationErrors = new[] { $"Invalid JSON content: {ex.Message}" }
                };
            }

            var errors = new List<string>();
            ValidateEnvelopeAndPayload(envelope, errors);

            if (errors.Count > 0)
            {
                return new TallyDbLoader.Core.Models.ConfigImportPreview { ValidationErrors = errors };
            }

            var payload = envelope.Payload!;
            var dbProfiles = payload.Database_Profiles ?? new List<ExportDatabaseProfile>();
            var companyProfiles = payload.Company_Profiles ?? new List<ExportCompanyProfile>();

            var existingDbs = _repository.GetAllDatabaseProfiles() ?? new List<DatabaseProfile>();
            var existingComps = _repository.GetAllCompanyProfiles() ?? new List<CompanyProfile>();

            var dbPreviews = new List<TallyDbLoader.Core.Models.ConfigImportPreviewDatabaseProfile>();
            var compPreviews = new List<TallyDbLoader.Core.Models.ConfigImportPreviewCompanyProfile>();
            bool hasConflicts = false;

            foreach (var sourceDb in dbProfiles)
            {
                var existingMatch = FindExistingDatabaseConflict(sourceDb, existingDbs);
                var isConflict = existingMatch != null;
                if (isConflict) hasConflicts = true;

                dbPreviews.Add(new TallyDbLoader.Core.Models.ConfigImportPreviewDatabaseProfile
                {
                    SourceId = sourceDb.Id,
                    Name = sourceDb.Name,
                    HasPassword = sourceDb.Has_Password.GetValueOrDefault(),
                    HasConflict = isConflict
                });
            }

            foreach (var sourceComp in companyProfiles)
            {
                var dbInPayload = dbProfiles.FirstOrDefault(d => d.Id == sourceComp.Db_Profile_Id);
                if (dbInPayload == null)
                {
                    errors.Add($"Company profile '{sourceComp.Name}' references database profile ID {sourceComp.Db_Profile_Id} which is not present in the import payload.");
                    continue;
                }

                var existingMatch = FindExistingCompanyConflict(sourceComp, existingComps, errors);
                var isConflict = existingMatch != null;
                if (isConflict) hasConflicts = true;

                compPreviews.Add(new TallyDbLoader.Core.Models.ConfigImportPreviewCompanyProfile
                {
                    SourceId = sourceComp.Id,
                    Name = sourceComp.Name,
                    HasConflict = isConflict
                });
            }

            if (errors.Count > 0)
            {
                return new TallyDbLoader.Core.Models.ConfigImportPreview { ValidationErrors = errors };
            }

            return new TallyDbLoader.Core.Models.ConfigImportPreview
            {
                DatabaseProfiles = dbPreviews,
                CompanyProfiles = compPreviews,
                HasConflicts = hasConflicts,
                ValidationErrors = Array.Empty<string>()
            };
        }
```

- [ ] **Step 5: Run tests and ensure they pass**
Run: `dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore --filter "ConfigImportServiceTests"`
Expected: PASS

- [ ] **Step 6: Commit**
```bash
git add src/TallyDbLoader.Core/Models/ConfigImportPreview.cs src/TallyDbLoader.Core/Data/ConfigImportService.cs tests/TallyDbLoader.Tests/ConfigImportServiceTests.cs
git commit -m "feat: implement JSON preview validation and conflict checks in ConfigImportService"
```

---

### Task 2: Secure WPF Password collection dialog

**Files:**
* Create: `src/TallyDbLoader.Wpf/Views/ImportPasswordPromptWindow.xaml`
* Create: `src/TallyDbLoader.Wpf/Views/ImportPasswordPromptWindow.xaml.cs`

- [ ] **Step 1: Write WPF XAML Layout**
Create `src/TallyDbLoader.Wpf/Views/ImportPasswordPromptWindow.xaml`. Avoid ItemsControl to prevent Visual Tree container virtualization issues, utilizing a StackPanel inside a ScrollViewer for deterministic rows.
```xml
<Window x:Class="TallyDbLoader.Wpf.Views.ImportPasswordPromptWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Database Profile Passwords" Height="280" Width="420"
        WindowStartupLocation="CenterOwner" ResizeMode="NoResize" ShowInTaskbar="False">
    <Grid Margin="16">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>

        <StackPanel Grid.Row="0" Margin="0,0,0,16">
            <TextBlock Text="Credentials Required" FontSize="16" FontWeight="SemiBold" Margin="0,0,0,4"/>
            <TextBlock Text="Enter passwords for database profiles included in this configuration." FontSize="11" Foreground="#666" TextWrapping="Wrap"/>
        </StackPanel>

        <ScrollViewer Grid.Row="1" VerticalScrollBarVisibility="Auto" Margin="0,0,0,16">
            <StackPanel Name="PasswordsStackPanel"/>
        </ScrollViewer>

        <StackPanel Grid.Row="2" Orientation="Horizontal" HorizontalAlignment="Right">
            <Button Content="Cancel" Click="Cancel_Click" Width="80" Height="28" Margin="0,0,12,0"/>
            <Button Content="Import Settings" Click="Submit_Click" IsDefault="True" Width="110" Height="28"/>
        </StackPanel>
    </Grid>
</Window>
```

- [ ] **Step 2: Write WPF Code-Behind**
Create `src/TallyDbLoader.Wpf/Views/ImportPasswordPromptWindow.xaml.cs`. Bind profiles programmatically and store references in a dictionary to prevent visual tree generator bugs.
```csharp
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using TallyDbLoader.Core.Models;

namespace TallyDbLoader.Wpf.Views
{
    public partial class ImportPasswordPromptWindow : Window
    {
        private readonly Dictionary<int, PasswordBox> _passwordBoxes = new();
        public Dictionary<int, string>? Results { get; private set; }

        public ImportPasswordPromptWindow(List<ConfigImportPreviewDatabaseProfile> targetProfiles)
        {
            InitializeComponent();

            foreach (var db in targetProfiles)
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 12) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var label = new TextBlock
                {
                    Text = db.Name,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontWeight = FontWeights.Medium,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                Grid.SetColumn(label, 0);
                row.Children.Add(label);

                var passwordBox = new PasswordBox
                {
                    Height = 28,
                    VerticalContentAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(passwordBox, 1);
                row.Children.Add(passwordBox);

                _passwordBoxes[db.SourceId] = passwordBox;
                PasswordsStackPanel.Children.Add(row);
            }
        }

        private void Submit_Click(object sender, RoutedEventArgs e)
        {
            var dict = new Dictionary<int, string>();

            foreach (var kvp in _passwordBoxes)
            {
                string password = kvp.Value.Password;
                if (string.IsNullOrWhiteSpace(password))
                {
                    MessageBox.Show(this, "All listed database profile passwords are required.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                dict[kvp.Key] = password;
            }

            Results = dict;
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
```

- [ ] **Step 3: Commit**
```bash
git add src/TallyDbLoader.Wpf/Views/ImportPasswordPromptWindow.xaml src/TallyDbLoader.Wpf/Views/ImportPasswordPromptWindow.xaml.cs
git commit -m "feat: implement ImportPasswordPromptWindow secure dialog using programmatically mapped PasswordBoxes"
```

---

### Task 3: ViewModel command integration and testing

**Files:**
* Modify: `src/TallyDbLoader.Wpf/MainViewModel.cs`
* Modify: `tests/TallyDbLoader.Tests/MainViewModelTests.cs`

- [ ] **Step 1: Write MainViewModel tests for sanitized configuration import**
Add tests to `tests/TallyDbLoader.Tests/MainViewModelTests.cs`. Ensure they assert that no database modifications occurred on blocked/cancelled paths.
```csharp
        [Fact]
        public void Test_ImportSanitizedConfig_CancelledFileDialog_ExitsSilently()
        {
            string dbPath = $"vm_test_import_cancel_{Guid.NewGuid():N}.db";
            try
            {
                DatabaseHelper.InitializeDatabase(dbPath);
                var vm = new MainViewModel(dbPath);
                vm.DisableDispatcher = true;

                // Dialog handler returns null
                vm.OpenFileDialogHandler = (filter) => null;

                vm.ImportSanitizedConfigCommand.Execute(null);

                // Assert no toasts and no DB configurations written
                Assert.Empty(vm.Toasts);
                var repo = new ConfigRepository(dbPath);
                Assert.Empty(repo.GetAllDatabaseProfiles());
                Assert.Empty(repo.GetAllCompanyProfiles());
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
            }
        }

        [Fact]
        public void Test_ImportSanitizedConfig_PasswordPromptCancelled_ExitsSilently()
        {
            string dbPath = $"vm_test_import_pw_cancel_{Guid.NewGuid():N}.db";
            string importFile = $"vm_test_import_pw_cancel_{Guid.NewGuid():N}.json";
            try
            {
                DatabaseHelper.InitializeDatabase(dbPath);
                var vm = new MainViewModel(dbPath);
                vm.DisableDispatcher = true;

                string jsonContent = @"{
                    ""format"": ""tally-db-loader.config-export"",
                    ""schema_version"": 1,
                    ""application_version"": ""2.0.0"",
                    ""payload"": {
                        ""database_profiles"": [
                            { ""id"": 1, ""name"": ""TargetDB"", ""technology"": ""postgres"", ""server"": ""localhost"", ""has_password"": true }
                        ],
                        ""company_profiles"": []
                    }
                }";
                File.WriteAllText(importFile, jsonContent);
                vm.OpenFileDialogHandler = (filter) => importFile;

                // Password Prompt returns null (User clicked Cancel)
                vm.PasswordPromptHandler = (preview) => null;

                vm.ImportSanitizedConfigCommand.Execute(null);

                // Assert no toasts and no DB configurations written
                Assert.Empty(vm.Toasts);
                var repo = new ConfigRepository(dbPath);
                Assert.Empty(repo.GetAllDatabaseProfiles());
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
                if (File.Exists(importFile)) try { File.Delete(importFile); } catch { }
            }
        }

        [Fact]
        public void Test_ImportSanitizedConfig_InvalidJson_ShowsErrorToastAndDoesNotImport()
        {
            string dbPath = $"vm_test_import_invalid_{Guid.NewGuid():N}.db";
            string importFile = $"vm_test_import_invalid_{Guid.NewGuid():N}.json";
            try
            {
                DatabaseHelper.InitializeDatabase(dbPath);
                var vm = new MainViewModel(dbPath);
                vm.DisableDispatcher = true;

                File.WriteAllText(importFile, "invalid-json-content");
                vm.OpenFileDialogHandler = (filter) => importFile;

                vm.ImportSanitizedConfigCommand.Execute(null);

                Assert.Contains(vm.Toasts, t => t.Kind == "err" && t.Body.Contains("Invalid JSON content"));
                var repo = new ConfigRepository(dbPath);
                Assert.Empty(repo.GetAllDatabaseProfiles());
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
                if (File.Exists(importFile)) try { File.Delete(importFile); } catch { }
            }
        }

        [Fact]
        public void Test_ImportSanitizedConfig_Conflicts_BlocksImportAndToastsWarning()
        {
            string dbPath = $"vm_test_import_conflict_{Guid.NewGuid():N}.db";
            string importFile = $"vm_test_import_conflict_{Guid.NewGuid():N}.json";
            try
            {
                DatabaseHelper.InitializeDatabase(dbPath);
                var repo = new ConfigRepository(dbPath);
                repo.SaveDatabaseProfile(new DatabaseProfile { Name = "ConflictingDb", Technology = "postgres" });

                var vm = new MainViewModel(dbPath);
                vm.DisableDispatcher = true;

                // Payload contains database profile with Name "ConflictingDb", creating a conflict
                string jsonContent = @"{
                    ""format"": ""tally-db-loader.config-export"",
                    ""schema_version"": 1,
                    ""application_version"": ""2.0.0"",
                    ""payload"": {
                        ""database_profiles"": [
                            { ""id"": 1, ""name"": ""ConflictingDb"", ""technology"": ""postgres"", ""server"": ""localhost"", ""has_password"": false }
                        ],
                        ""company_profiles"": []
                    }
                }";
                File.WriteAllText(importFile, jsonContent);
                vm.OpenFileDialogHandler = (filter) => importFile;

                vm.ImportSanitizedConfigCommand.Execute(null);

                Assert.Contains(vm.Toasts, t => t.Kind == "err" && t.Body.Contains("this version only supports new profiles"));

                // Verify the database has only the pre-existing profile and nothing else was written
                Assert.Single(repo.GetAllDatabaseProfiles());
                Assert.Empty(repo.GetAllCompanyProfiles());
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
                if (File.Exists(importFile)) try { File.Delete(importFile); } catch { }
            }
        }

        [Fact]
        public void Test_ImportSanitizedConfig_MissingPassword_BlocksImport()
        {
            string dbPath = $"vm_test_import_password_missing_{Guid.NewGuid():N}.db";
            string importFile = $"vm_test_import_password_missing_{Guid.NewGuid():N}.json";
            try
            {
                DatabaseHelper.InitializeDatabase(dbPath);
                var vm = new MainViewModel(dbPath);
                vm.DisableDispatcher = true;

                string jsonContent = @"{
                    ""format"": ""tally-db-loader.config-export"",
                    ""schema_version"": 1,
                    ""application_version"": ""2.0.0"",
                    ""payload"": {
                        ""database_profiles"": [
                            { ""id"": 1, ""name"": ""NewDb"", ""technology"": ""postgres"", ""server"": ""localhost"", ""has_password"": true }
                        ],
                        ""company_profiles"": []
                    }
                }";
                File.WriteAllText(importFile, jsonContent);
                vm.OpenFileDialogHandler = (filter) => importFile;

                // Password Prompt returns empty dictionary (missing passwords)
                vm.PasswordPromptHandler = (preview) => new Dictionary<int, string>();

                vm.ImportSanitizedConfigCommand.Execute(null);

                Assert.Contains(vm.Toasts, t => t.Kind == "err" && t.Body.Contains("requires a password"));
                var repo = new ConfigRepository(dbPath);
                Assert.Empty(repo.GetAllDatabaseProfiles());
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
                if (File.Exists(importFile)) try { File.Delete(importFile); } catch { }
            }
        }

        [Fact]
        public void Test_ImportSanitizedConfig_EngineRunning_IsBlocked()
        {
            string dbPath = $"vm_test_import_blocked_engine_{Guid.NewGuid():N}.db";
            try
            {
                DatabaseHelper.InitializeDatabase(dbPath);
                var vm = new MainViewModel(dbPath);
                vm.DisableDispatcher = true;
                vm.State = EngineState.Running; // Engine is running

                vm.ImportSanitizedConfigCommand.Execute(null);

                Assert.Contains(vm.Toasts, t => t.Kind == "warn" && t.Title.Contains("Engine is running"));
                var repo = new ConfigRepository(dbPath);
                Assert.Empty(repo.GetAllDatabaseProfiles());
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
            }
        }

        [Fact]
        public void Test_ImportSanitizedConfig_Success()
        {
            string dbPath = $"vm_test_import_ok_{Guid.NewGuid():N}.db";
            string importFile = $"vm_test_import_ok_{Guid.NewGuid():N}.json";
            try
            {
                DatabaseHelper.InitializeDatabase(dbPath);
                var vm = new MainViewModel(dbPath);
                vm.DisableDispatcher = true;

                string jsonContent = @"{
                    ""format"": ""tally-db-loader.config-export"",
                    ""schema_version"": 1,
                    ""application_version"": ""2.0.0"",
                    ""payload"": {
                        ""database_profiles"": [
                            { ""id"": 1, ""name"": ""TargetDB"", ""technology"": ""postgres"", ""server"": ""localhost"", ""has_password"": true }
                        ],
                        ""company_profiles"": [
                            { ""id"": 10, ""name"": ""TargetComp"", ""db_profile_id"": 1, ""target_catalog"": ""catalog"", ""enabled"": true }
                        ]
                    }
                }";
                File.WriteAllText(importFile, jsonContent);
                vm.OpenFileDialogHandler = (filter) => importFile;

                vm.PasswordPromptHandler = (preview) => new Dictionary<int, string> { { 1, "secret-pass" } };

                vm.ImportSanitizedConfigCommand.Execute(null);

                Assert.Contains(vm.Toasts, t => t.Kind == "ok" && t.Title.Contains("Import Succeeded"));

                // Verify loaded configuration in ViewModel collections
                Assert.Single(vm.DatabaseProfiles);
                Assert.Equal("TargetDB", vm.DatabaseProfiles[0].Name);

                Assert.Single(vm.Companies);
                Assert.Equal("TargetComp", vm.Companies[0].Name);
                Assert.False(vm.Companies[0].Enabled); // Must be disabled by default
                Assert.Equal("review_required", vm.Companies[0].Status); // Must be review_required
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
                if (File.Exists(importFile)) try { File.Delete(importFile); } catch { }
            }
        }
```

- [ ] **Step 2: Run tests and verify failure**
Run: `dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore --filter "MainViewModelTests"`
Expected: Compile error because `ImportSanitizedConfigCommand` is not defined in `MainViewModel`.

- [ ] **Step 3: Modify MainViewModel to add fields and delegates**
Modify `src/TallyDbLoader.Wpf/MainViewModel.cs`:
In fields area:
```csharp
        private readonly ConfigImportService _importService;
```
In delegate declarations:
```csharp
        public Func<string, string?>? OpenFileDialogHandler { get; set; }
        public Func<TallyDbLoader.Core.Models.ConfigImportPreview, Dictionary<int, string>?>? PasswordPromptHandler { get; set; }
```
In commands:
```csharp
        public ICommand ImportSanitizedConfigCommand { get; }
```

- [ ] **Step 4: Initialize ConfigImportService and commands in constructor**
In `MainViewModel` constructor:
```csharp
            _importService = new ConfigImportService(_repo);
```
In command bindings:
```csharp
            ImportSanitizedConfigCommand = new RelayCommand(ImportSanitizedConfig);
```

- [ ] **Step 5: Implement ImportSanitizedConfig method in MainViewModel**
Add this method to `src/TallyDbLoader.Wpf/MainViewModel.cs` (right after `ExportSanitizedConfig`):
```csharp
        private void ImportSanitizedConfig()
        {
            if (GuardEngineRunning("ImportSanitizedConfig")) return;

            string filter = "JSON Files (*.json)|*.json|All Files (*.*)|*.*";
            string? filePath = OpenFileDialogHandler != null
                ? OpenFileDialogHandler(filter)
                : null;

            if (string.IsNullOrWhiteSpace(filePath))
            {
                return;
            }

            try
            {
                string json = File.ReadAllText(filePath);
                var preview = _importService.PreviewJson(json);

                if (!preview.IsValid)
                {
                    ShowToast("Import Blocked", string.Join(Environment.NewLine, preview.ValidationErrors), "err");
                    return;
                }

                if (preview.HasConflicts)
                {
                    ShowToast("Import Blocked", "Import blocked: this version only supports new profiles. Rename or remove conflicting profiles before importing.", "err");
                    return;
                }

                var requiredPasswordProfiles = preview.DatabaseProfiles.Where(d => d.HasPassword).ToList();
                var decision = new ImportDecision();

                if (requiredPasswordProfiles.Count > 0)
                {
                    if (PasswordPromptHandler == null)
                    {
                        ShowToast("Import Failed", "Password collector interface is missing.", "err");
                        return;
                    }

                    var passwords = PasswordPromptHandler(preview);
                    if (passwords == null)
                    {
                        return; // Cancellation
                    }

                    foreach (var reqDb in requiredPasswordProfiles)
                    {
                        if (!passwords.TryGetValue(reqDb.SourceId, out var pass) || string.IsNullOrEmpty(pass))
                        {
                            ShowToast("Import Failed", $"Database profile '{reqDb.Name}' requires a password.", "err");
                            return;
                        }
                    }

                    decision.DatabasePasswords = passwords;
                }

                string actor = GetActorName();
                string reason = "User imported sanitized configuration from WPF settings";

                _importService.ImportJson(json, decision, actor, reason);

                LoadConfiguration();
                ShowToast("Import Succeeded", $"Imported {preview.DatabaseProfiles.Count} database profiles and {preview.CompanyProfiles.Count} company profiles.", "ok");
            }
            catch (ConfigImportValidationException valEx)
            {
                ShowToast("Import Failed", string.Join(Environment.NewLine, valEx.Errors), "err");
            }
            catch (Exception ex)
            {
                ShowToast("Import Failed", ex.Message, "err");
            }
        }
```

- [ ] **Step 6: Run tests and ensure they pass**
Run: `dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore --filter "MainViewModelTests"`
Expected: PASS

- [ ] **Step 7: Commit**
```bash
git add src/TallyDbLoader.Wpf/MainViewModel.cs tests/TallyDbLoader.Tests/MainViewModelTests.cs
git commit -m "feat: wire ImportSanitizedConfigCommand in MainViewModel with tests"
```

---

### Task 4: UI Button integration and handler hookup

**Files:**
* Modify: `src/TallyDbLoader.Wpf/MainWindow.xaml.cs` (register delegate handlers and add required `using` directives)
* Modify: `src/TallyDbLoader.Wpf/Views/SettingsPage.xaml` (add settings UI button link)

- [ ] **Step 1: Wire up delegates and usings in MainWindow**
Modify `src/TallyDbLoader.Wpf/MainWindow.xaml.cs`.
Add usings at the top of the file:
```csharp
using System.Collections.Generic;
using TallyDbLoader.Core.Models;
```

And configure delegates inside the MainWindow constructor:
```csharp
            _vm.OpenFileDialogHandler = (filter) =>
            {
                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Filter = filter
                };
                if (dialog.ShowDialog() == true)
                {
                    return dialog.FileName;
                }
                return null;
            };

            _vm.PasswordPromptHandler = (preview) =>
            {
                var targetList = new List<ConfigImportPreviewDatabaseProfile>();
                foreach (var db in preview.DatabaseProfiles)
                {
                    if (db.HasPassword)
                    {
                        targetList.Add(db);
                    }
                }

                var dialog = new ImportPasswordPromptWindow(targetList);
                dialog.Owner = this;
                if (dialog.ShowDialog() == true)
                {
                    return dialog.Results;
                }
                return null;
            };
```

- [ ] **Step 2: Add import button to SettingsPage XAML**
Modify `src/TallyDbLoader.Wpf/Views/SettingsPage.xaml`:
```xml
                        <StackPanel Orientation="Horizontal" Margin="0,0,0,8">
                            <Button Content="Export Sanitized Config" Command="{Binding ExportSanitizedConfigCommand}" Style="{StaticResource StandardButtonStyle}" Margin="0,0,12,0" ToolTip="Export database and company configuration without passwords."/>
                            <Button Content="Import Sanitized Config" Command="{Binding ImportSanitizedConfigCommand}" Style="{StaticResource StandardButtonStyle}" Margin="0,0,12,0" ToolTip="Import database and company configuration. Stop the engine before importing."/>
                            <Button Content="Create Diagnostic Backup" Command="{Binding CreateDiagnosticBackupCommand}" Style="{StaticResource StandardButtonStyle}" ToolTip="Generate a ZIP file with system information, logs, and settings."/>
                        </StackPanel>
```

- [ ] **Step 3: Run the full test suite**
Run: `dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore`
Expected: PASS

- [ ] **Step 4: Check git status and compile the project**
Run: `dotnet build src/TallyDbLoader.sln`
Expected: Zero compilation errors.

- [ ] **Step 5: Verify git diff**
Run: `git diff --check`
Expected: No trailing whitespaces or diff formatting violations.

- [ ] **Step 6: Commit**
```bash
git add src/TallyDbLoader.Wpf/MainWindow.xaml.cs src/TallyDbLoader.Wpf/Views/SettingsPage.xaml
git commit -m "ui: integrate Import Sanitized Config button and link OpenFileDialog/PasswordPrompt dialog handlers in MainWindow"
```
