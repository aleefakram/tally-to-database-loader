# WPF Export Tools Wiring Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Expose sanitized configuration export and diagnostic backup ZIP creation in the WPF SettingsPage UI using testable, thread-safe background command flows and injectable dialog delegates.

**Architecture:** Extend `MainViewModel` with delegates for file/folder/prompt dialogs and commands calling Core's `ConfigExportService` and `DiagnosticBackupService`. Bind WPF `MainWindow` to trigger native dialogs, place buttons on the `SettingsPage`, and verify end-to-end command states through asynchronous unit tests.

**Tech Stack:** C#, WPF, .NET 8, Windows Forms (for FolderBrowserDialog), xUnit

---

### File Structure Changes

- Modify: `src/TallyDbLoader.Wpf/MainViewModel.cs` — Define dialog delegates, `_dbPath` tracking, `DiagnosticsBaseDirectory` override property, `GetActorName()` / `GetApplicationVersion()` helpers, commands, and `ExportSanitizedConfig` / `CreateDiagnosticBackupAsync` command handlers.
- Modify: `src/TallyDbLoader.Wpf/MainWindow.xaml.cs` — Wire up WPF/WinForms concrete dialogs to the ViewModel delegates on application startup.
- Modify: `src/TallyDbLoader.Wpf/Views/SettingsPage.xaml` — Add the "Configuration & Support Exports" UI card and bind its buttons to the new commands.
- Modify: `tests/TallyDbLoader.Tests/MainViewModelTests.cs` — Add tests for success, cancellation, and error cases for both commands, using delegate mocks.

---

### Task 1: View-Model Setup, Delegates & Helpers

**Files:**
- Modify: `src/TallyDbLoader.Wpf/MainViewModel.cs:68-80`
- Modify: `src/TallyDbLoader.Wpf/MainViewModel.cs:471-513`

- [ ] **Step 1: Declare private fields, public delegates, and overridable base directory**
  Add `_dbPath` private field, the three UI dialog delegates, and the `DiagnosticsBaseDirectory` property.

  ```csharp
  // Modify near line 70
  private readonly string _dbPath;

  // Modify near line 80
  public Func<string, string, string?>? SaveFileDialogHandler { get; set; }
  public Func<string?>? FolderBrowserDialogHandler { get; set; }
  public Func<string, string, bool>? ConfirmationPromptHandler { get; set; }

  // Expose test-overridable diagnostics directory to prevent environment-sensitive tests
  public string DiagnosticsBaseDirectory { get; set; } = AppDomain.CurrentDomain.BaseDirectory;
  ```

- [ ] **Step 2: Initialize dbPath and define helper methods**
  Assign `_dbPath = dbPath` in the constructor. Define `GetActorName()` and `GetApplicationVersion()` methods. Use `typeof(MainViewModel).Assembly` for the application version helper to ensure test compatibility.

  ```csharp
  // Inside MainViewModel constructor near line 473:
  _dbPath = dbPath;

  // Add at the end of MainViewModel class:
  private string GetActorName()
  {
      try
      {
          string? winIdentity = System.Security.Principal.WindowsIdentity.GetCurrent()?.Name;
          if (!string.IsNullOrWhiteSpace(winIdentity))
          {
              return winIdentity;
          }
      }
      catch { }

      try
      {
          string? envUser = Environment.UserName;
          if (!string.IsNullOrWhiteSpace(envUser))
          {
              return envUser;
          }
      }
      catch { }

      return "unknown";
  }

  private string GetApplicationVersion()
  {
      try
      {
          var assembly = typeof(MainViewModel).Assembly;
          var infoVersionAttr = (System.Reflection.AssemblyInformationalVersionAttribute?)Attribute.GetCustomAttribute(assembly, typeof(System.Reflection.AssemblyInformationalVersionAttribute));
          if (infoVersionAttr != null && !string.IsNullOrWhiteSpace(infoVersionAttr.InformationalVersion))
          {
              return infoVersionAttr.InformationalVersion;
          }
          var version = assembly.GetName().Version;
          if (version != null)
          {
              return version.ToString();
          }
      }
      catch { }
      return "dev";
  }
  ```

- [ ] **Step 3: Refactor ResolveSafetyBlock to use GetActorName()**
  Replace the inline actor detection logic in `ResolveSafetyBlock` with the new helper.

  ```csharp
  // In ResolveSafetyBlock near line 683-707:
  string actor = GetActorName();
  ```

- [ ] **Step 4: Run existing tests to verify zero regressions**
  Run: `dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore`
  Expected: PASS

- [ ] **Step 5: Commit changes**
  ```bash
  git add src/TallyDbLoader.Wpf/MainViewModel.cs
  git commit -m "feat: add wpf dialog delegates and actor resolution helper"
  ```

---

### Task 2: Implement Config Export and Diagnostic Backup Commands

**Files:**
- Modify: `src/TallyDbLoader.Wpf/MainViewModel.cs:445-470`
- Modify: `src/TallyDbLoader.Wpf/MainViewModel.cs:475-513`
- Modify: `src/TallyDbLoader.Wpf/MainViewModel.cs:1340-1390`

- [ ] **Step 1: Declare commands**
  Add the command properties in the properties region.

  ```csharp
  // Near line 465
  public ICommand ExportSanitizedConfigCommand { get; }
  public ICommand CreateDiagnosticBackupCommand { get; }
  ```

- [ ] **Step 2: Bind commands in MainViewModel constructor**
  Initialize commands using `RelayCommand` inside constructor. `CreateDiagnosticBackupCommand` triggers the asynchronous handler.

  ```csharp
  // Near line 500
  ExportSanitizedConfigCommand = new RelayCommand(ExportSanitizedConfig);
  CreateDiagnosticBackupCommand = new RelayCommand(CreateDiagnosticBackup);
  ```

- [ ] **Step 3: Implement command methods**
  Add `ExportSanitizedConfig` command handler, `CreateDiagnosticBackup()` wrapper, and the awaitable `CreateDiagnosticBackupAsync()` method. Wrap the entire body in a try-catch block to handle errors before or inside `Task.Run`.

  ```csharp
  // Add near end of MainViewModel class
  private void ExportSanitizedConfig()
  {
      try
      {
          string defaultFilename = "tally-sync-config.json";
          string filter = "JSON Files (*.json)|*.json|All Files (*.*)|*.*";
          string? filePath = SaveFileDialogHandler != null
              ? SaveFileDialogHandler(defaultFilename, filter)
              : null;

          if (string.IsNullOrWhiteSpace(filePath))
          {
              return;
          }

          string version = GetApplicationVersion();
          var service = new ConfigExportService(_repo, version);
          string json = service.ExportJson(DateTimeOffset.Now);
          File.WriteAllText(filePath, json);

          ShowToast("Export Succeeded", $"Configuration saved to {Path.GetFileName(filePath)}", "ok");
      }
      catch (Exception ex)
      {
          ShowToast("Export Failed", ex.Message, "err");
      }
  }

  private void CreateDiagnosticBackup()
  {
      _ = CreateDiagnosticBackupAsync();
  }

  public async Task CreateDiagnosticBackupAsync()
  {
      try
      {
          string? outputDir = FolderBrowserDialogHandler != null ? FolderBrowserDialogHandler() : null;
          if (string.IsNullOrWhiteSpace(outputDir))
          {
              return;
          }

          bool includeRawXml = false;
          string baseDir = DiagnosticsBaseDirectory;
          string rawXmlPath = Path.Combine(baseDir, "raw_xml");
          bool rawXmlFolderExists = Directory.Exists(rawXmlPath);

          if (ConfirmationPromptHandler != null && ConfirmationPromptHandler("Would you like to include raw XML diagnostic payloads in the backup?", "Include Raw XML?"))
          {
              if (rawXmlFolderExists)
              {
                  includeRawXml = true;
              }
              else
              {
                  ShowToast("Folder Missing", "Raw XML diagnostics directory is missing. Proceeding without XML payloads.", "warn");
              }
          }

          string logPath = Path.Combine(baseDir, "logs");
          string dbPath = _dbPath;
          string version = GetApplicationVersion();
          string actor = GetActorName();

          // Propagate token to Task.Run to handle shutdown gracefully
          await System.Threading.Tasks.Task.Run(() =>
          {
              var request = new DiagnosticBackupRequest
              {
                  ConfigDatabasePath = dbPath,
                  LogDirectoryPath = logPath,
                  RawXmlDirectoryPath = rawXmlFolderExists ? rawXmlPath : null,
                  OutputDirectoryPath = outputDir,
                  ApplicationVersion = version,
                  Actor = actor,
                  Reason = "User requested diagnostic backup from WPF settings",
                  IncludeRawXml = includeRawXml,
                  CreatedAt = DateTimeOffset.Now
              };

              var service = new DiagnosticBackupService(_repo);
              var result = service.CreateBackup(request);

              InvokeOnDispatcher(() =>
              {
                  ShowToast("Backup Created", $"Diagnostic backup saved: {result.FileName}", "ok");
              });
          }, _asyncOpsCts.Token);
      }
      catch (OperationCanceledException)
      {
          // App is shutting down or canceled; exit silently without posting error toasts
      }
      catch (Exception ex)
      {
          InvokeOnDispatcher(() =>
          {
              ShowToast("Backup Failed", ex.Message, "err");
          });
      }
  }
  ```

- [ ] **Step 4: Run existing tests to verify code builds successfully**
  Run: `dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore`
  Expected: PASS

- [ ] **Step 5: Commit changes**
  ```bash
  git add src/TallyDbLoader.Wpf/MainViewModel.cs
  git commit -m "feat: implement export sanitized config and diagnostic backup commands"
  ```

---

### Task 3: MainWindow Delegate Hookups

**Files:**
- Modify: `src/TallyDbLoader.Wpf/MainWindow.xaml.cs:25-35`

- [ ] **Step 1: Add Win32 and WinForms dialog wire-ups**
  Wire up `SaveFileDialogHandler`, `FolderBrowserDialogHandler`, and `ConfirmationPromptHandler` delegates in the `MainWindow` constructor.

  ```csharp
  // In MainWindow constructor, near line 25:
  _vm.SaveFileDialogHandler = (defaultFilename, filter) =>
  {
      var dialog = new Microsoft.Win32.SaveFileDialog
      {
          FileName = defaultFilename,
          Filter = filter
      };
      if (dialog.ShowDialog() == true)
      {
          return dialog.FileName;
      }
      return null;
  };

  _vm.FolderBrowserDialogHandler = () =>
  {
      using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
      {
          if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
          {
              return dialog.SelectedPath;
          }
      }
      return null;
  };

  _vm.ConfirmationPromptHandler = (message, title) =>
  {
      var result = MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question);
      return result == MessageBoxResult.Yes;
  };
  ```

- [ ] **Step 2: Verify compilation**
  Run: `dotnet build src/TallyDbLoader.sln`
  Expected: Success without errors.

- [ ] **Step 3: Commit changes**
  ```bash
  git add src/TallyDbLoader.Wpf/MainWindow.xaml.cs
  git commit -m "feat: hook up concrete dialog delegates in MainWindow"
  ```

---

### Task 4: Add Settings UI Card Controls

**Files:**
- Modify: `src/TallyDbLoader.Wpf/Views/SettingsPage.xaml:60-72`

- [ ] **Step 1: Add UI Card to SettingsPage.xaml**
  Insert the `Configuration & Support Exports` border card underneath the `Executable File Paths` card.

  ```xml
                  <!-- Tally Exe Paths Card -->
                  <Border Style="{StaticResource FluentCardStyle}">
                      <StackPanel>
                          <TextBlock Text="Executable File Paths" Style="{StaticResource SubtitleTextStyle}" Margin="0,0,0,12"/>

                          <TextBlock Text="Path to Tally.exe" Style="{StaticResource CaptionMuteTextStyle}" Margin="0,0,0,4"/>
                          <TextBox Text="{Binding TallyExePath, Mode=TwoWay}" Style="{StaticResource AccentTextBoxStyle}" Margin="0,0,0,12"/>

                          <TextBlock Text="Path to Tally.ini Configuration" Style="{StaticResource CaptionMuteTextStyle}" Margin="0,0,0,4"/>
                          <TextBox Text="{Binding TallyIniPath, Mode=TwoWay}" Style="{StaticResource AccentTextBoxStyle}"/>
                      </StackPanel>
                  </Border>

                  <!-- Configuration & Support Exports Card -->
                  <Border Style="{StaticResource FluentCardStyle}" Margin="0,16,0,0">
                      <StackPanel>
                          <TextBlock Text="Configuration &amp; Support Exports" Style="{StaticResource SubtitleTextStyle}" Margin="0,0,0,12"/>

                          <TextBlock Text="Export settings for backups, support tickets, and environment migration." Style="{StaticResource CaptionMuteTextStyle}" Margin="0,0,0,12" TextWrapping="Wrap"/>

                          <StackPanel Orientation="Horizontal" Margin="0,0,0,8">
                              <Button Content="Export Sanitized Config" Command="{Binding ExportSanitizedConfigCommand}" Style="{StaticResource StandardButtonStyle}" Margin="0,0,12,0" ToolTip="Export database and company configuration without passwords."/>
                              <Button Content="Create Diagnostic Backup" Command="{Binding CreateDiagnosticBackupCommand}" Style="{StaticResource StandardButtonStyle}" ToolTip="Generate a ZIP file with system information, logs, and settings."/>
                          </StackPanel>
                      </StackPanel>
                  </Border>
  ```

- [ ] **Step 2: Build and verify XML markup**
  Run: `dotnet build src/TallyDbLoader.sln`
  Expected: Success.

- [ ] **Step 3: Commit changes**
  ```bash
  git add src/TallyDbLoader.Wpf/Views/SettingsPage.xaml
  git commit -m "feat: add Configuration & Support Exports card to SettingsPage"
  ```

---

### Task 5: Add Unit Tests

**Files:**
- Modify: `tests/TallyDbLoader.Tests/MainViewModelTests.cs:360-364`

- [ ] **Step 1: Add config export tests**
  Write tests in `MainViewModelTests.cs` for sanitized config export success, cancellation, and failure paths. Assert that cancellation creates no files and posts no toasts.

  ```csharp
          [Fact]
          public void Test_ExportSanitizedConfig_Success()
          {
              string dbPath = $"vm_test_export_ok_{Guid.NewGuid():N}.db";
              string exportPath = $"vm_test_export_out_{Guid.NewGuid():N}.json";
              try
              {
                  DatabaseHelper.InitializeDatabase(dbPath);
                  var vm = new MainViewModel(dbPath);
                  vm.DisableDispatcher = true;

                  // Mock save file dialog
                  vm.SaveFileDialogHandler = (defaultName, filter) => exportPath;

                  // Run config export
                  vm.ExportSanitizedConfigCommand.Execute(null);

                  // Assert file exists and contains expected metadata
                  Assert.True(File.Exists(exportPath));
                  string content = File.ReadAllText(exportPath);
                  Assert.Contains("tally-db-loader.config-export", content);
              }
              finally
              {
                  Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                  if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
                  if (File.Exists(exportPath)) try { File.Delete(exportPath); } catch { }
              }
          }

          [Fact]
          public void Test_ExportSanitizedConfig_Cancelled()
          {
              string dbPath = $"vm_test_export_cancel_{Guid.NewGuid():N}.db";
              string exportPath = $"vm_test_export_cancel_out_{Guid.NewGuid():N}.json";
              try
              {
                  DatabaseHelper.InitializeDatabase(dbPath);
                  var vm = new MainViewModel(dbPath);
                  vm.DisableDispatcher = true;

                  // Mock user cancelled
                  vm.SaveFileDialogHandler = (defaultName, filter) => null;

                  vm.ExportSanitizedConfigCommand.Execute(null);

                  // Assert no file created and no toasts registered
                  Assert.False(File.Exists(exportPath));
                  Assert.Empty(vm.Toasts);
              }
              finally
              {
                  Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                  if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
              }
          }

          [Fact]
          public void Test_ExportSanitizedConfig_Failure()
          {
              string dbPath = $"vm_test_export_fail_{Guid.NewGuid():N}.db";
              try
              {
                  DatabaseHelper.InitializeDatabase(dbPath);
                  var vm = new MainViewModel(dbPath);
                  vm.DisableDispatcher = true;

                  // Provide an invalid path that will cause write failure
                  vm.SaveFileDialogHandler = (defaultName, filter) => "invalid:\\path/to/nonexistent/file.json";

                  vm.ExportSanitizedConfigCommand.Execute(null);

                  // Toast collection should contain failure message
                  Assert.Contains(vm.Toasts, t => t.Kind == "err");
              }
              finally
              {
                  Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                  if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
              }
          }
  ```

- [ ] **Step 2: Add diagnostic backup tests**
  Write tests for diagnostic backup success, cancellation, handler exception, and missing XML folder. Use an isolated `DiagnosticsBaseDirectory` for each test to make them environment-independent. Assert that cancellation creates no ZIP, triggers no confirmation prompt, and posts no toasts. Explicitly verify missing XML backup zip manifest details and handler failure toast creation.

  ```csharp
          [Fact]
          public async Task Test_CreateDiagnosticBackup_Success()
          {
              string dbPath = $"vm_test_diag_ok_{Guid.NewGuid():N}.db";
              string outputDir = Path.Combine(Path.GetTempPath(), $"vm_diag_out_{Guid.NewGuid():N}");
              string tempBaseDir = Path.Combine(Path.GetTempPath(), $"vm_diag_base_ok_{Guid.NewGuid():N}");
              Directory.CreateDirectory(outputDir);
              Directory.CreateDirectory(tempBaseDir);
              try
              {
                  DatabaseHelper.InitializeDatabase(dbPath);
                  var vm = new MainViewModel(dbPath);
                  vm.DisableDispatcher = true;
                  vm.DiagnosticsBaseDirectory = tempBaseDir;

                  vm.FolderBrowserDialogHandler = () => outputDir;
                  vm.ConfirmationPromptHandler = (msg, title) => false; // No XML

                  await vm.CreateDiagnosticBackupAsync();

                  var files = Directory.GetFiles(outputDir, "*.zip");
                  Assert.Single(files);
                  Assert.Contains(vm.Toasts, t => t.Kind == "ok");
              }
              finally
              {
                  Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                  if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
                  if (Directory.Exists(outputDir)) try { Directory.Delete(outputDir, true); } catch { }
                  if (Directory.Exists(tempBaseDir)) try { Directory.Delete(tempBaseDir, true); } catch { }
              }
          }

          [Fact]
          public void Test_CreateDiagnosticBackup_Cancelled()
          {
              string dbPath = $"vm_test_diag_cancel_{Guid.NewGuid():N}.db";
              string outputDir = Path.Combine(Path.GetTempPath(), $"vm_diag_cancel_out_{Guid.NewGuid():N}");
              try
              {
                  DatabaseHelper.InitializeDatabase(dbPath);
                  var vm = new MainViewModel(dbPath);
                  vm.DisableDispatcher = true;

                  bool confirmationCalled = false;
                  vm.FolderBrowserDialogHandler = () => null;
                  vm.ConfirmationPromptHandler = (msg, title) => { confirmationCalled = true; return false; };

                  vm.CreateDiagnosticBackupCommand.Execute(null);

                  // Assert cancellation doesn't trigger prompts or output files or toasts
                  Assert.False(confirmationCalled);
                  Assert.Empty(vm.Toasts);
                  if (Directory.Exists(outputDir))
                  {
                      Assert.Empty(Directory.GetFiles(outputDir, "*.zip"));
                  }
              }
              finally
              {
                  Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                  if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
              }
          }

          [Fact]
          public async Task Test_CreateDiagnosticBackup_HandlerException()
          {
              string dbPath = $"vm_test_diag_exc_{Guid.NewGuid():N}.db";
              try
              {
                  DatabaseHelper.InitializeDatabase(dbPath);
                  var vm = new MainViewModel(dbPath);
                  vm.DisableDispatcher = true;

                  // Mock handler that throws an exception
                  vm.FolderBrowserDialogHandler = () => throw new InvalidOperationException("Simulated dialog failure");

                  await vm.CreateDiagnosticBackupAsync();

                  // Assert failure toast was posted
                  Assert.Contains(vm.Toasts, t => t.Kind == "err" && t.Body.Contains("Simulated dialog failure"));
              }
              finally
              {
                  Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                  if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
              }
          }

          [Fact]
          public async Task Test_CreateDiagnosticBackup_MissingXmlDirectory()
          {
              string dbPath = $"vm_test_diag_xml_missing_{Guid.NewGuid():N}.db";
              string outputDir = Path.Combine(Path.GetTempPath(), $"vm_diag_xml_out_{Guid.NewGuid():N}");
              string tempBaseDir = Path.Combine(Path.GetTempPath(), $"vm_diag_base_xml_{Guid.NewGuid():N}");
              Directory.CreateDirectory(outputDir);
              Directory.CreateDirectory(tempBaseDir); // Kept empty so raw_xml won't exist
              try
              {
                  DatabaseHelper.InitializeDatabase(dbPath);
                  var vm = new MainViewModel(dbPath);
                  vm.DisableDispatcher = true;
                  vm.DiagnosticsBaseDirectory = tempBaseDir;

                  vm.FolderBrowserDialogHandler = () => outputDir;
                  vm.ConfirmationPromptHandler = (msg, title) => true; // User asks for XML

                  await vm.CreateDiagnosticBackupAsync();

                  // Toast collection should contain warning message about folder missing
                  Assert.Contains(vm.Toasts, t => t.Kind == "warn" && t.Title.Contains("Folder Missing"));

                  // Verify ZIP manifest.json states include_raw_xml = false
                  var files = Directory.GetFiles(outputDir, "*.zip");
                  Assert.Single(files);
                  using (var archive = System.IO.Compression.ZipFile.OpenRead(files[0]))
                  {
                      var manifestEntry = archive.GetEntry("manifest.json");
                      Assert.NotNull(manifestEntry);
                      using (var reader = new StreamReader(manifestEntry.Open()))
                      {
                          string json = reader.ReadToEnd();
                          Assert.Contains("\"include_raw_xml\": false", json);
                      }
                      Assert.Empty(archive.Entries.Where(e => e.FullName.StartsWith("raw_xml/")));
                  }
              }
              finally
              {
                  Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                  if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
                  if (Directory.Exists(outputDir)) try { Directory.Delete(outputDir, true); } catch { }
                  if (Directory.Exists(tempBaseDir)) try { Directory.Delete(tempBaseDir, true); } catch { }
              }
          }
  ```

- [ ] **Step 3: Add test for engine running safety rules**
  Write a test ensuring both read/export actions succeed when the sync engine is actively running. Set `vm.State = EngineState.Running` directly instead of calling the real background worker.

  ```csharp
          [Fact]
          public async Task Test_ExportAndBackup_AllowedWhileEngineRunning()
          {
              string dbPath = $"vm_test_engine_run_{Guid.NewGuid():N}.db";
              string exportPath = $"vm_test_engine_run_out_{Guid.NewGuid():N}.json";
              string outputDir = Path.Combine(Path.GetTempPath(), $"vm_diag_engine_run_out_{Guid.NewGuid():N}");
              string tempBaseDir = Path.Combine(Path.GetTempPath(), $"vm_diag_base_run_{Guid.NewGuid():N}");
              Directory.CreateDirectory(outputDir);
              Directory.CreateDirectory(tempBaseDir);
              try
              {
                  DatabaseHelper.InitializeDatabase(dbPath);
                  var vm = new MainViewModel(dbPath);
                  vm.DisableDispatcher = true;
                  vm.DiagnosticsBaseDirectory = tempBaseDir;

                  // Force State to Running directly without starting background threads
                  vm.State = EngineState.Running;
                  Assert.True(vm.IsSyncRunning);

                  // 1. Verify Config Export is allowed
                  vm.SaveFileDialogHandler = (defaultName, filter) => exportPath;
                  vm.ExportSanitizedConfigCommand.Execute(null);
                  Assert.True(File.Exists(exportPath));
                  Assert.Contains(vm.Toasts, t => t.Kind == "ok" && t.Title.Contains("Export Succeeded"));

                  // 2. Verify Diagnostic Backup is allowed
                  vm.FolderBrowserDialogHandler = () => outputDir;
                  vm.ConfirmationPromptHandler = (msg, title) => false;
                  await vm.CreateDiagnosticBackupAsync();

                  var files = Directory.GetFiles(outputDir, "*.zip");
                  Assert.Single(files);
                  Assert.Contains(vm.Toasts, t => t.Kind == "ok" && t.Title.Contains("Backup Created"));
              }
              finally
              {
                  Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                  if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
                  if (File.Exists(exportPath)) try { File.Delete(exportPath); } catch { }
                  if (Directory.Exists(outputDir)) try { Directory.Delete(outputDir, true); } catch { }
                  if (Directory.Exists(tempBaseDir)) try { Directory.Delete(tempBaseDir, true); } catch { }
              }
          }
  ```

- [ ] **Step 4: Run target tests to verify all tests pass**
  Run: `dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore --filter "FullyQualifiedName~MainViewModelTests"`
  Expected: PASS

- [ ] **Step 5: Run the entire test suite**
  Run: `dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore`
  Expected: PASS

- [ ] **Step 6: Commit changes**
  ```bash
  git add tests/TallyDbLoader.Tests/MainViewModelTests.cs
  git commit -m "test: add MainViewModel tests for config export and diagnostic backup commands"
  ```
