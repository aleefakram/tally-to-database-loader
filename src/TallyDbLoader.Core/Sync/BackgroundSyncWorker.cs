using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System.Data.Common;
using TallyDbLoader.Core.Data;
using TallyDbLoader.Core.Tally;
using TallyDbLoader.Core.DatabaseLoaders;
using TallyDbLoader.Core.Models;

namespace TallyDbLoader.Core.Sync
{
    public class BackgroundSyncWorker : IDisposable
    {
        private readonly IConfigRepository _repo;
        private readonly string _tallyServer;
        private readonly int _tallyPort;
        private CancellationTokenSource? _cts;
        private Task? _runTask;
        private TallyClient? _tallyClient;

        private readonly object _syncLock = new object();
        private bool _forceSyncOnce = false;
        private int? _manualCompanyProfileId = null;
        private CancellationTokenSource _wakeUpCts = new CancellationTokenSource();
        private bool _disposed = false;
        private bool _isPaused = false;

        public event Action<string>? OnLogMessage;
        public event Action? OnSyncCompleted;

        public bool IsRunning => _cts != null;
        public bool IsPaused => _isPaused;
        public bool IsBlocked { get; private set; }

        public BackgroundSyncWorker(IConfigRepository repo, string tallyServer, int tallyPort)
        {
            _repo = repo ?? throw new ArgumentNullException(nameof(repo));
            _tallyServer = tallyServer;
            _tallyPort = tallyPort <= 0 ? 9000 : tallyPort;
        }

        public void SetTallyClientForTest(TallyClient client)
        {
            _tallyClient = client;
        }

        private void Log(string message)
        {
            OnLogMessage?.Invoke(message);
            TallyDbLoader.Core.Logging.FileLogger.LogMessage(message);
        }

        public void Start(bool startScheduler = true)
        {
            lock (_syncLock)
            {
                if (IsRunning) return;
                _isPaused = !startScheduler; // Start paused if scheduler is not started
                IsBlocked = false;
                
                // Reconcile stale runs before spawning the background worker thread
                try
                {
                    _repo.ReconcileStaleRuns(DateTime.Now);
                    Log("[Engine] Startup reconciliation of stale runs completed.");
                }
                catch (Exception ex)
                {
                    IsBlocked = true;
                    Log($"[Engine ERROR] Startup reconciliation failed: {ex.Message}. Scheduler will not start.");
                    return; // Fail-closed: do not set _cts, do not spawn task.
                }

                _cts = new CancellationTokenSource();
                if (startScheduler)
                {
                    var token = _cts.Token;
                    _runTask = Task.Run(() => WorkerLoop(token));
                    Log("Background Sync Engine started.");
                }
                else
                {
                    Log("Background Sync Engine initialized in mock preflight-only mode.");
                }
            }
        }

        public void Pause()
        {
            lock (_syncLock)
            {
                if (!IsRunning) return;
                _isPaused = true;
                Log($"[Engine] Paused at {DateTime.Now:HH:mm:ss}");
            }
        }

        public void Resume()
        {
            lock (_syncLock)
            {
                if (!IsRunning) return;
                _isPaused = false;
                Log($"[Engine] Resumed at {DateTime.Now:HH:mm:ss}");
                TriggerWakeUp();
            }
        }

        public void Stop()
        {
            CancellationTokenSource? localCts = null;
            Task? localTask = null;

            lock (_syncLock)
            {
                if (!IsRunning) return;
                localCts = _cts;
                localTask = _runTask;
                _cts = null;
                _runTask = null;
                _isPaused = false;
                IsBlocked = false;
            }

            localCts?.Cancel();
            if (localTask != null)
            {
                var completed = localTask.Wait(TimeSpan.FromSeconds(5));
                if (!completed)
                {
                    Log("[Engine] Force stopped - long-running operations might have been aborted.");
                }
            }
            localCts?.Dispose();
            Log("Background Sync Engine stopped.");
        }

        public SyncStartResult TryRequestManualSync(int companyProfileId)
        {
            lock (_syncLock)
            {
                if (_disposed)
                {
                    return new SyncStartResult
                    {
                        Accepted = false,
                        ReasonCode = "Disposed",
                        Message = "Engine is disposed."
                    };
                }

                if (!IsRunning)
                {
                    return new SyncStartResult
                    {
                        Accepted = false,
                        ReasonCode = "EngineNotRunning",
                        Message = "Background sync engine is stopped."
                    };
                }

                if (IsBlocked)
                {
                    return new SyncStartResult
                    {
                        Accepted = false,
                        ReasonCode = "SafetyBlocked",
                        Message = "Scheduler is currently blocked due to a safety violation or initialization failure."
                    };
                }

                if (_forceSyncOnce)
                {
                    return new SyncStartResult
                    {
                        Accepted = false,
                        ReasonCode = "WorkerBusy",
                        Message = "Another manual run is already pending dispatch."
                    };
                }

                // Retrieve active profile state directly from DB
                var profiles = _repo.GetAllCompanyProfiles();
                var profile = profiles.Find(p => p.Id == companyProfileId);

                if (profile == null)
                {
                    return new SyncStartResult
                    {
                        Accepted = false,
                        ReasonCode = "NotFound",
                        Message = "Company profile not found."
                    };
                }

                if (!profile.Enabled)
                {
                    return new SyncStartResult
                    {
                        Accepted = false,
                        ReasonCode = "Disabled",
                        Message = "This sync job is disabled."
                    };
                }

                if (profile.Status == "running")
                {
                    return new SyncStartResult
                    {
                        Accepted = false,
                        ReasonCode = "AlreadyRunning",
                        Message = "This sync job is already executing."
                    };
                }

                if (profile.Status != "idle" && profile.Status != "completed" && profile.Status != "failed")
                {
                    return new SyncStartResult
                    {
                        Accepted = false,
                        ReasonCode = "SafetyBlocked",
                        Message = $"Sync is blocked because current status is '{profile.Status}'."
                    };
                }

                // Set the target request & notify the worker thread loop
                _manualCompanyProfileId = companyProfileId;
                _forceSyncOnce = true;
                TriggerWakeUp();

                return new SyncStartResult
                {
                    Accepted = true,
                    ReasonCode = "PendingDispatch",
                    Message = "Manual run request accepted and pending dispatch."
                };
            }
        }

        public SyncStartResult TryRequestManualSyncAll()
        {
            lock (_syncLock)
            {
                if (_disposed)
                {
                    return new SyncStartResult
                    {
                        Accepted = false,
                        ReasonCode = "Disposed",
                        Message = "Engine is disposed."
                    };
                }

                if (!IsRunning)
                {
                    return new SyncStartResult
                    {
                        Accepted = false,
                        ReasonCode = "EngineNotRunning",
                        Message = "Background sync engine is stopped."
                    };
                }

                if (IsBlocked)
                {
                    return new SyncStartResult
                    {
                        Accepted = false,
                        ReasonCode = "SafetyBlocked",
                        Message = "Scheduler is currently blocked due to a safety violation or initialization failure."
                    };
                }

                if (_forceSyncOnce)
                {
                    return new SyncStartResult
                    {
                        Accepted = false,
                        ReasonCode = "WorkerBusy",
                        Message = "Another manual run is already pending dispatch."
                    };
                }

                // Accept request for all eligible companies
                _manualCompanyProfileId = null; // null signifies sync all
                _forceSyncOnce = true;
                TriggerWakeUp();

                return new SyncStartResult
                {
                    Accepted = true,
                    ReasonCode = "PendingDispatch",
                    Message = "Manual run request for all profiles accepted and pending dispatch."
                };
            }
        }

        [Obsolete("Use TryRequestManualSync or TryRequestManualSyncAll instead")]
        public SyncStartResult TriggerManualSync(int? companyId = null)
        {
            if (companyId.HasValue)
            {
                return TryRequestManualSync(companyId.Value);
            }
            return TryRequestManualSyncAll();
        }

        private void TriggerWakeUp()
        {
            lock (_syncLock)
            {
                try
                {
                    _wakeUpCts.Cancel();
                }
                catch (ObjectDisposedException) { }
            }
        }

        public void Dispose()
        {
            lock (_syncLock)
            {
                if (_disposed) return;
                _disposed = true;
            }
            Stop();
            try
            {
                _wakeUpCts.Dispose();
            }
            catch (ObjectDisposedException) { }
        }

        private async Task WorkerLoop(CancellationToken token)
        {
            var client = _tallyClient ?? new TallyClient(_tallyServer, _tallyPort);

            while (!token.IsCancellationRequested)
            {
                lock (_syncLock)
                {
                    try
                    {
                        if (_wakeUpCts.IsCancellationRequested)
                        {
                            _wakeUpCts.Dispose();
                            _wakeUpCts = new CancellationTokenSource();
                        }
                    }
                    catch (ObjectDisposedException)
                    {
                        _wakeUpCts = new CancellationTokenSource();
                    }
                }
                if (_isPaused)
                {
                    bool hasManualRun = false;
                    lock (_syncLock)
                    {
                        hasManualRun = _forceSyncOnce;
                    }
                    if (!hasManualRun)
                    {
                        try
                        {
                            await Task.Delay(1000, _wakeUpCts.Token);
                        }
                        catch { }
                        continue;
                    }
                }

                if (IsBlocked)
                {
                    Log("[Engine] Scheduler is blocked due to a fatal metadata or reconciliation error. Skipping cycle.");
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(60), _wakeUpCts.Token);
                    }
                    catch (TaskCanceledException) { }
                    continue;
                }

                try
                {
                    var settings = _repo.GetTallySettings();
                    if (settings.AutoStartTally && !string.IsNullOrEmpty(settings.TallyExePath))
                    {
                        if (!TallyLauncher.IsTallyRunning())
                        {
                            Log("[Engine] Auto-start Tally: Tally is not running. Launching...");
                            try
                            {
                                TallyLauncher.LaunchTally(settings.TallyExePath);
                                Log("[Engine] Tally launched successfully.");
                                await Task.Delay(TimeSpan.FromSeconds(5), token);
                            }
                            catch (Exception ex)
                            {
                                Log($"[Engine ERROR] Auto-start Tally failed: {ex.Message}");
                            }
                        }
                    }

                    bool runManualSync;
                    int? manualCompanyId;
                    lock (_syncLock)
                    {
                        runManualSync = _forceSyncOnce;
                        manualCompanyId = _manualCompanyProfileId;
                        _forceSyncOnce = false;
                        _manualCompanyProfileId = null; // Clear request
                    }

                    var companies = _repo.GetAllCompanyProfiles();
                    bool manualTargetSeen = false;
                    foreach (var company in companies)
                    {
                        if (token.IsCancellationRequested) break;

                        bool shouldSync = false;
                        string skipReason = string.Empty;

                        if (runManualSync)
                        {
                            // Authoritative check on the manual target profile
                            if (manualCompanyId.HasValue)
                            {
                                if (manualCompanyId.Value == company.Id)
                                {
                                    manualTargetSeen = true;
                                    if (!company.Enabled)
                                    {
                                        skipReason = "JobDisabled";
                                    }
                                    else if (company.Status == "running")
                                    {
                                        skipReason = "AlreadyRunning";
                                    }
                                    else if (company.Status != "idle" && company.Status != "completed" && company.Status != "failed")
                                    {
                                        skipReason = "SafetyBlocked";
                                    }
                                    else
                                    {
                                        shouldSync = true;
                                    }
                                }
                            }
                            else
                            {
                                // Manual sync all
                                if (!company.Enabled)
                                {
                                    skipReason = "JobDisabled";
                                }
                                else if (company.Status == "running")
                                {
                                    skipReason = "AlreadyRunning";
                                }
                                else if (company.Status != "idle" && company.Status != "completed" && company.Status != "failed")
                                {
                                    skipReason = "SafetyBlocked";
                                }
                                else
                                {
                                    shouldSync = true;
                                }
                            }
                        }
                        else
                        {
                            // Scheduler evaluation rules:
                            if (!company.Enabled)
                            {
                                skipReason = "JobDisabled";
                            }
                            else if (company.Status == "running")
                            {
                                skipReason = "AlreadyRunning";
                            }
                            else if (company.Status != "idle" && company.Status != "completed" && company.Status != "failed")
                            {
                                skipReason = "SafetyBlocked";
                            }
                            else if (!SyncOrchestrator.ShouldRun(company, DateTime.Now))
                            {
                                skipReason = "IntervalNotMet";
                            }
                            else
                            {
                                shouldSync = true;
                            }
                        }

                        if (shouldSync)
                        {
                            await SyncCompany(company, client, token);
                        }
                        else if (!string.IsNullOrEmpty(skipReason) && skipReason != "IntervalNotMet")
                        {
                            Log($"[Sync Skipped] Skipping job '{company.Name}' (Reason: {skipReason}, Current Status: {company.Status})");
                        }
                    }

                    if (runManualSync && manualCompanyId.HasValue && !manualTargetSeen)
                    {
                        Log($"[Engine WARNING] Manual trigger targets company ID {manualCompanyId.Value}, but it was not found in active profiles (ManualTriggerDropped/NotFound).");
                    }
                }
                catch (Exception ex)
                {
                    Log($"[Engine Error] Cycle execution error: {ex.Message}");
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(60), _wakeUpCts.Token);
                }
                catch (TaskCanceledException)
                {
                    // Woken up
                }
            }
        }

        private async Task SyncCompany(CompanyProfile company, TallyClient client, CancellationToken token)
        {
            Log($"[Sync] Initiating sync execution for company '{company.Name}'...");
            Log($"[Sync] Starting sync for company '{company.Name}' (Target: '{company.TargetCatalog}')...");

            if (string.IsNullOrWhiteSpace(company.TargetCatalog))
            {
                _repo.MarkCompanyProfileUnknown(company.Id, "Target catalog empty", DateTime.Now);
                Log($"[Sync ERROR] Company '{company.Name}' failed: Target database name is empty.");
                OnSyncCompleted?.Invoke();
                return;
            }

            // 1. Authoritative check & atomic state transition
            bool started = _repo.TryStartCompanyProfile(company.Id);
            if (!started)
            {
                Log($"[Sync Skipped] Company '{company.Name}' is currently running or safety blocked.");
                return;
            }

            // 2. Create and insert SyncRun record
            var run = new SyncRun
            {
                CompanyId = company.Id,
                CompanyName = company.Name,
                StartedAt = DateTime.Now,
                Mode = company.Mode,
                Status = "running",
                ResultSummary = "Sync execution started."
            };

            try
            {
                _repo.AddSyncRun(run); // Populates run.Id
            }
            catch (Exception ex)
            {
                // Crucial Safety Gate: Revert company profile to unknown if SyncRun metadata save fails
                Log($"[Sync ERROR] Metadata write error during run initiation. Failing closed: {ex.Message}");
                try
                {
                    _repo.MarkCompanyProfileUnknown(company.Id, "Metadata run registration failed", DateTime.Now);
                }
                catch (Exception revertEx)
                {
                    Log($"[Sync FATAL] Failed to revert company status to unknown after run registration failure: {revertEx.Message}. PERSISTED SAFETY STATE IS COMPROMISED. Blocking scheduler.");
                    IsBlocked = true;
                }
                OnSyncCompleted?.Invoke();
                return;
            }

            string finalStatus = "completed";
            string summary = "Sync completed successfully.";
            string? logExcerpt = null;
            long totalRows = 0;
            bool isError = false;

            try
            {
                var dbProfile = company.Db ?? _repo.GetDatabaseProfileById(company.DbProfileId);
                if (dbProfile == null)
                {
                    throw new InvalidOperationException("Target database profile not found.");
                }

                // Verify Tally connectivity & open company
                List<string> activeCompanies;
                try
                {
                    activeCompanies = await client.FetchActiveCompaniesAsync();
                }
                catch (Exception ex)
                {
                    // Wrap Tally connection issues into a distinct exception type
                    throw new TimeoutException("Tally Prime server is offline or unreachable.", ex);
                }

                if (!activeCompanies.Contains(company.Name))
                {
                    throw new InvalidOperationException($"Target company '{company.Name}' is not loaded in Tally Prime.");
                }

                IDatabaseLoader dbLoader;
                string connStr;
                var tech = dbProfile.Technology.ToLower();
                if (tech.Contains("postgres") || tech.Contains("npgsql"))
                {
                    var builder = new Npgsql.NpgsqlConnectionStringBuilder
                    {
                        Host = dbProfile.Server,
                        Port = dbProfile.Port,
                        Username = dbProfile.Username,
                        Password = dbProfile.Password,
                        Database = company.TargetCatalog
                    };
                    if (!dbProfile.Server.Equals("localhost", StringComparison.OrdinalIgnoreCase) && 
                        !dbProfile.Server.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase))
                    {
                        builder.SslMode = Npgsql.SslMode.Require;
                        builder.TrustServerCertificate = true;
                    }
                    connStr = builder.ConnectionString;
                    dbLoader = new PostgreSqlLoader(connStr);
                }
                else if (tech.Contains("mssql") || tech.Contains("sqlserver"))
                {
                    var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder
                    {
                        DataSource = $"{dbProfile.Server},{dbProfile.Port}",
                        UserID = dbProfile.Username,
                        Password = dbProfile.Password,
                        InitialCatalog = company.TargetCatalog,
                        TrustServerCertificate = true
                    };
                    connStr = builder.ConnectionString;
                    dbLoader = new MSSqlLoader(connStr);
                }
                else if (tech.Contains("mysql"))
                {
                    connStr = $"Server={dbProfile.Server};Port={dbProfile.Port};User Id={dbProfile.Username};Password={dbProfile.Password};Database={company.TargetCatalog};";
                    dbLoader = new MySqlLoader(connStr);
                }
                else if (tech.Contains("sqlite"))
                {
                    string dbFile = company.TargetCatalog.EndsWith(".db") ? company.TargetCatalog : $"{company.TargetCatalog}.db";
                    connStr = $"Data Source={dbFile}";
                    dbLoader = new TallyDbLoader.Core.DatabaseLoaders.SqliteLoader(connStr);
                }
                else
                {
                    throw new NotSupportedException($"Technology '{dbProfile.Technology}' not supported.");
                }

                var configFilename = (company.Mode?.ToLowerInvariant() == "incremental")
                    ? "tally-export-config-incremental.yaml"
                    : "tally-export-config.yaml";

                var yamlPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, configFilename);
                if (!System.IO.File.Exists(yamlPath))
                {
                    yamlPath = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), configFilename);
                }

                if (!System.IO.File.Exists(yamlPath))
                {
                    throw new FileNotFoundException($"Tally definition file '{yamlPath}' not found.");
                }

                var yamlContent = System.IO.File.ReadAllText(yamlPath);
                var config = YamlConfigParser.Parse(yamlContent);

                DbConnection targetConn;
                if (tech.Contains("postgres") || tech.Contains("npgsql"))
                {
                    targetConn = new Npgsql.NpgsqlConnection(connStr);
                }
                else if (tech.Contains("mssql") || tech.Contains("sqlserver"))
                {
                    targetConn = new Microsoft.Data.SqlClient.SqlConnection(connStr);
                }
                else if (tech.Contains("mysql"))
                {
                    targetConn = new MySqlConnector.MySqlConnection(connStr);
                }
                else if (tech.Contains("sqlite"))
                {
                    targetConn = new Microsoft.Data.Sqlite.SqliteConnection(connStr);
                }
                else
                {
                    throw new NotSupportedException($"Technology '{dbProfile.Technology}' not supported.");
                }

                using (targetConn)
                {
                    await targetConn.OpenAsync(token);

                    var staging = new StagingTableManager(targetConn);
                    await staging.EnsureStagingTablesAsync();

                    long? prevMaster = null;
                    long? prevTxn = null;
                    if (company.Mode?.ToLowerInvariant() == "incremental")
                    {
                        var repo = new WatermarkRepository(targetConn);
                        var (m, t) = await repo.ReadAsync();
                        prevMaster = m;
                        prevTxn = t;
                    }

                    var fetcher = new CompanyInfoFetcher(client);
                    var companyInfo = await fetcher.FetchAndPersist(company.Name, targetConn);
                    var fromDate = companyInfo.BooksFrom ?? new DateTime(2000, 1, 1);
                    var toDate = companyInfo.BooksTo ?? DateTime.Today;

                    if (company.Mode?.ToLowerInvariant() == "incremental")
                    {
                        var runner = new IncrementalSyncRunner(client, dbLoader);
                        // IncrementalSyncRunner internally updates the watermark only on success
                        await runner.RunAsync(config, company.Name, fromDate, toDate, targetConn, prevMaster, prevTxn);
                        totalRows = 0;
                    }
                    else
                    {
                        IFullSyncTablePromoter promoter;
                        if (tech.Contains("sqlite")) promoter = new SqliteFullSyncTablePromoter();
                        else if (tech.Contains("postgres") || tech.Contains("npgsql")) promoter = new PostgresFullSyncTablePromoter();
                        else if (tech.Contains("mssql") || tech.Contains("sqlserver")) promoter = new MssqlFullSyncTablePromoter();
                        else if (tech.Contains("mysql")) promoter = new MysqlFullSyncTablePromoter();
                        else throw new NotSupportedException($"Technology '{dbProfile.Technology}' not supported.");

                        var runner = new FullSyncRunner(client, promoter);
                        totalRows = await runner.Run(config, company.Name, fromDate, toDate, targetConn);
                    }
                }

                finalStatus = "completed";
                summary = $"Sync completed successfully. Wrote {totalRows} records.";
            }
            catch (OperationCanceledException)
            {
                finalStatus = "unknown";
                summary = "Sync operation was cancelled or interrupted.";
                isError = true;
            }
            catch (TimeoutException ex)
            {
                // Tally unavailable / offline -> attention_required
                finalStatus = "attention_required";
                summary = ex.Message;
                logExcerpt = ex.StackTrace;
                isError = true;
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("not loaded in Tally"))
            {
                // Company mismatch / not loaded -> attention_required
                finalStatus = "attention_required";
                summary = ex.Message;
                logExcerpt = ex.StackTrace;
                isError = true;
            }
            catch (FileNotFoundException ex)
            {
                // Missing config/definition files -> review_required
                finalStatus = "review_required";
                summary = ex.Message;
                logExcerpt = ex.StackTrace;
                isError = true;
            }
            catch (Exception ex)
            {
                // Other exceptions, validation errors -> failed
                finalStatus = "failed";
                summary = ex.Message;
                logExcerpt = ex.StackTrace;
                isError = true;
            }
            finally
            {
                // 4. Update the SyncRun execution ledger
                run.EndedAt = DateTime.Now;
                run.Status = finalStatus;
                run.ResultSummary = summary;
                run.LogExcerpt = logExcerpt;
                run.RowsIn = totalRows;
                run.RowsWritten = totalRows;

                try
                {
                    _repo.UpdateSyncRun(run);
                }
                catch (Exception ex)
                {
                    Log($"[Sync ERROR] Failed to update SyncRun record: {ex.Message}");
                    finalStatus = "unknown"; // Post-commit metadata failure -> fail-closed to unknown. The SyncRun row may remain 'running' until startup reconciliation recovers it.
                    isError = true;          // Force isError = true to increment error count
                }

                // 5. Update Company Profile runtime status safely (Targeted repository method)
                try
                {
                    int durationMs = (int)(run.EndedAt - run.StartedAt).TotalMilliseconds;
                    _repo.CompleteCompanyProfileRun(company.Id, finalStatus, run.EndedAt, durationMs, totalRows, isError);
                }
                catch (Exception ex)
                {
                    Log($"[Sync ERROR] Failed to save runtime status update for company {company.Id}: {ex.Message}");
                    try
                    {
                        _repo.MarkCompanyProfileUnknown(company.Id, $"Runtime status update failed: {ex.Message}", DateTime.Now);
                    }
                    catch (Exception revertEx)
                    {
                        Log($"[Sync FATAL] Failed to revert company status to unknown: {revertEx.Message}. PERSISTED SAFETY STATE IS COMPROMISED. Blocking scheduler.");
                        IsBlocked = true;
                    }
                }

                Log($"[Sync Finished] Result: {finalStatus}. Wrote {totalRows} records.");
                if (finalStatus == "completed")
                {
                    Log($"[Sync SUCCESS] Company '{company.Name}' sync finished. Wrote {totalRows} rows.");
                }
                OnSyncCompleted?.Invoke();
            }
        }

        private bool ShouldSyncTable(TableConfig table, EntityFlags flags)
        {
            var name = table.Name.ToLowerInvariant();
            if (name.Contains("group")) return flags.HasFlag(EntityFlags.Groups);
            if (name.Contains("ledger")) return flags.HasFlag(EntityFlags.Ledgers);
            if (name.Contains("voucher") || name.Contains("sales") || name.Contains("purchase") || name.Contains("receipt") || name.Contains("payment") || name.Contains("journal") || name.Contains("contra")) return flags.HasFlag(EntityFlags.Vouchers);
            if (name.Contains("stock") || name.Contains("item")) return flags.HasFlag(EntityFlags.StockItems);
            return true;
        }

        private async Task<(DateTime fromDate, DateTime toDate)> GetCompanyDatesAsync(TallyClient client, string companyName)
        {
            var companies = await client.FetchActiveCompaniesDetailedAsync();
            var info = companies.FirstOrDefault(c => c.Name.Equals(companyName, StringComparison.OrdinalIgnoreCase));
            var defaultFrom = new DateTime(2000, 1, 1);
            var defaultTo = DateTime.Today;
            if (info == null) return (defaultFrom, defaultTo);
            var from = info.BooksFrom ?? defaultFrom;
            var to = info.BooksTo ?? defaultTo;
            return (from, to);
        }
    }

    public sealed class SyncStartResult
    {
        public bool Accepted { get; init; }
        public string ReasonCode { get; init; } = "";
        public string Message { get; init; } = "";
    }
}
