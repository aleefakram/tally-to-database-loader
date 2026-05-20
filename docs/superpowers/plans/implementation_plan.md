# Implementation Plan: Tally WPF Front-End Redesign and Safety Enhancements

This plan details the implementation steps to redesign the WPF desktop app, resolve key bugs (tray exit, manual sync, and settings updates), enforce database password security via DPAPI, and ensure background engine command safety.

---

## 1. Core Cryptography & DPAPI Encryption

### 1.1 Dependency Update
- Add `<PackageReference Include="System.Security.Cryptography.ProtectedData" Version="8.0.0" />` to [TallyDbLoader.Core.csproj](file:///c:/Users/user/Desktop/tally-to-database-loader/src/TallyDbLoader.Core/TallyDbLoader.Core.csproj).

### 1.2 Repository Password Security
- Modify `ConfigRepository.cs` to encrypt/decrypt database profile passwords inside:
  - `SaveDatabaseProfile(DatabaseProfile profile)`
  - `GetAllDatabaseProfiles()`
  - `GetDatabaseProfileById(int id)`
  - `GetDatabaseProfileByName(string name)`
- **Encryption Logic**:
  - If password is empty or null, save as empty.
  - Otherwise, encrypt plain text using `ProtectedData.Protect` under `DataProtectionScope.CurrentUser`.
  - Base64 encode the output and prefix it with `dpapi:`.
- **Decryption Logic**:
  - If the stored password starts with `dpapi:`:
    - Strip the prefix, base64 decode, and decrypt using `ProtectedData.Unprotect`.
    - If base64 decoding or decryption fails due to invalid/corrupted format, **log the error and return an empty string (`string.Empty`)** to ensure UI resilience and prevent application startup failure.
  - If the stored password does not start with `dpapi:`, treat it as legacy plain text (supporting automatic on-save migration).

---

## 2. Application Exit and System Tray Fixes

### 2.1 MainWindow Lifecycle Fixes
- Rename private field `_isExplicitShutdown` to `_isExiting` in [MainWindow.xaml.cs](file:///c:/Users/user/Desktop/tally-to-database-loader/src/TallyDbLoader.Wpf/MainWindow.xaml.cs).
- Implement a public `ExitApplication()` method:
  ```csharp
  public void ExitApplication()
  {
      _isExiting = true;
      System.Windows.Application.Current.Shutdown();
  }
  ```
- Subscribe to the `System.Windows.Application.Current.SessionEnding` event inside `MainWindow` constructor to set `_isExiting = true` when Windows is shutting down.
- Update `OnClosing` logic to intercept close and hide the window only when `_isExiting == false`.

### 2.2 System Tray Exit Routing
- Modify `TrayController.cs` to route the context menu's **Exit** option to `_mainWindow.ExitApplication()` instead of calling `Application.Current.Shutdown()` directly.

---

## 3. Manual Sync Execution ("Sync Now")

### 3.1 Background Sync Worker Wake & Thread Safety
- Implement `IDisposable` interface on `BackgroundSyncWorker` in [BackgroundSyncWorker.cs](file:///c:/Users/user/Desktop/tally-to-database-loader/src/TallyDbLoader.Core/Sync/BackgroundSyncWorker.cs):
  ```csharp
  public void Dispose()
  {
      Stop();
      _wakeUpCts.Dispose();
  }
  ```
- Update `Stop()` method to ensure that if a task is running, `_cts` is canceled, waited on, and then disposed before setting the reference to null:
  ```csharp
  public void Stop()
  {
      if (!IsRunning) return;
      _cts?.Cancel();
      try { _runTask?.Wait(); } catch { }
      _cts?.Dispose();
      _cts = null;
      Log("Background Sync Engine stopped.");
  }
  ```
- Introduce thread safety for manual sync signaling. Create a private `object _syncLock = new object();` locking primitive, a private boolean `_forceSyncOnce`, and a private `CancellationTokenSource _wakeUpCts = new CancellationTokenSource();`.
- Implement `TriggerManualSync()` method protected by the lock:
  ```csharp
  public void TriggerManualSync()
  {
      lock (_syncLock)
      {
          if (!IsRunning)
          {
              Log("Manual sync ignored: Sync engine is not running.");
              return;
          }
          _forceSyncOnce = true;
          var oldCts = _wakeUpCts;
          _wakeUpCts = new CancellationTokenSource();
          oldCts.Cancel();
          oldCts.Dispose();
      }
  }
  ```
- In `WorkerLoop`:
  - At the start of each iteration, copy `_forceSyncOnce` to a local variable `bool runManualSync` and reset `_forceSyncOnce = false` under `lock (_syncLock)`.
  - Copy the `CancellationToken` value from `_wakeUpCts.Token` inside the same lock block (the worker must never access the `_wakeUpCts` field directly outside of locks, only the copied `CancellationToken` struct).
  - Run the sync job if `runManualSync || SyncOrchestrator.ShouldRun(job, DateTime.Now)`.
  - Sleep for 60 seconds (or until signaled) using a linked token source combining the worker cancellation token and the copied wake cancellation token.

### 3.2 MainViewModel and Tray Menu Wiring
- Expose `TriggerManualSync()` in `MainViewModel.cs`. If the worker is running, delegate the call to `_worker.TriggerManualSync()`.
- Wire `TrayController.TriggerManualSync()` to call `vm.TriggerManualSync()`.

---

## 4. Sync Settings Renewal & Mutation Guards

### 4.1 Worker Disposal and Re-creation
- Modify `StopSyncEngine()` in `MainViewModel.cs` to dispose and nullify the worker:
  ```csharp
  public void StopSyncEngine()
  {
      if (_worker != null)
      {
          _worker.Dispose();
          _worker = null;
      }
  }
  ```
  This guarantees that restarting the sync engine recreates `BackgroundSyncWorker` using the updated Tally configuration settings.

### 4.2 Mutex/Safety Properties
- Expose `IsSyncRunning` and `IsSyncNotRunning` properties in `MainViewModel.cs` representing whether `_worker?.IsRunning == true`.
- Raise `PropertyChanged` notifications when starting or stopping the engine.

### 4.3 UI Controls Disabling
- Bind `IsEnabled="{Binding IsSyncNotRunning}"` in [MainWindow.xaml](file:///c:/Users/user/Desktop/tally-to-database-loader/src/TallyDbLoader.Wpf/MainWindow.xaml) to:
  - Global Tally config inputs and the **Save Tally Settings** button.
  - Database target profile fields, **Test Connection**, and CRUD buttons.
  - Sync job editor inputs, **Detect**, and CRUD buttons.

### 4.4 ViewModel Mutation Guards
- Add guards at the top of mutation methods in `MainViewModel.cs` (`DeleteSyncJob`, `DeleteDatabaseProfile`, `SaveDatabaseProfile`, `AddSyncJob`, `TestDatabaseConnection`, `DetectActiveCompaniesAsync`, and `SaveTallySettings`):
  ```csharp
  if (IsSyncRunning)
  {
      Log("Cannot modify configurations while the sync engine is running.");
      return;
  }
  ```

---

## 5. Testing Plan

To verify all components meet standard reliability guidelines, we will write new unit and integration tests under `tests/TallyDbLoader.Tests`:

### 5.1 DPAPI Password Security Tests (`ConfigRepositoryTests.cs`)
- **Round-Trip Encryption Test**: Verifies saving a profile encrypts the password with `dpapi:` prefix and reading it decrypts it back to plain text.
- **Legacy Plain-Text Compatibility Test**: Inserts a raw plaintext password directly into the database (no `dpapi:` prefix) and verifies `ConfigRepository` reads it as plain text without throwing.
- **Malformed DPAPI Prefix Test**: Inserts a profile with an invalid/malformed `dpapi:corrupted_bytes` password and verifies that reading it logs the error and returns `string.Empty` (ensuring UI resilience).
- **Automatic Migration Test**: Reads a legacy plaintext profile, calls `SaveDatabaseProfile` on it, and verifies that it is now persisted in encrypted format with the `dpapi:` prefix.

### 5.2 Worker Settings and Manual Sync Tests (`BackgroundSyncWorkerTests.cs`)
- **Worker Configuration Refresh Test**: Verifies that when settings are changed, stopping and restarting the sync engine successfully initializes the next sync task using the updated Tally host/port parameters.
- **Thread-Safe Manual Sync Trigger Test**: Verifies calling `TriggerManualSync` on a running worker wakes it from sleep immediately and executes scheduled jobs.

### 5.3 Safety Guards & Properties (`MainViewModelTests.cs`)
- **Mutation Guard Block Test**: Verifies that calling configuration mutations (`SaveTallySettings`, `SaveDatabaseProfile`, `AddSyncJob`, `DeleteSyncJob`, `TestDatabaseConnection`, `DetectActiveCompaniesAsync`) **early-returns and logs** without modifying the database when `IsSyncRunning` is `true`.
- **UI Binding Properties Test**: Verifies that `IsSyncRunning` and `IsSyncNotRunning` transition correctly and emit property change notifications when sync engine is started and stopped.
