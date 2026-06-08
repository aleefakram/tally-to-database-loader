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
        private int? _manualSyncCompanyId = null;
        private CancellationTokenSource _wakeUpCts = new CancellationTokenSource();
        private bool _disposed = false;
        private bool _isPaused = false;

        public event Action<string>? OnLogMessage;
        public event Action? OnSyncCompleted;

        public bool IsRunning => _cts != null;
        public bool IsPaused => _isPaused;

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

        public void Start()
        {
            lock (_syncLock)
            {
                if (IsRunning) return;
                _isPaused = false;
                _cts = new CancellationTokenSource();
                var token = _cts.Token;
                _runTask = Task.Run(() => WorkerLoop(token));
                Log("Background Sync Engine started.");
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
            }

            localCts?.Cancel();
            if (localTask != null)
            {
                // Wait up to 5 seconds asynchronously to avoid hard blocking
                var completed = localTask.Wait(TimeSpan.FromSeconds(5));
                if (!completed)
                {
                    Log("[Engine] Force stopped - long-running operations might have been aborted.");
                }
            }
            localCts?.Dispose();
            Log("Background Sync Engine stopped.");
        }

        public void TriggerManualSync(int? companyId = null)
        {
            lock (_syncLock)
            {
                if (_disposed || !IsRunning)
                {
                    Log("[Sync warning] Sync engine is not running or disposed.");
                    return;
                }
                _forceSyncOnce = true;
                _manualSyncCompanyId = companyId;
                TriggerWakeUp();
            }
        }

        private void TriggerWakeUp()
        {
            var oldCts = _wakeUpCts;
            _wakeUpCts = new CancellationTokenSource();
            oldCts.Cancel();
            oldCts.Dispose();
        }

        public void Dispose()
        {
            lock (_syncLock)
            {
                if (_disposed) return;
                _disposed = true;
            }
            Stop();
            _wakeUpCts.Dispose();
        }

        private async Task WorkerLoop(CancellationToken token)
        {
            var client = _tallyClient ?? new TallyClient(_tallyServer, _tallyPort);

            while (!token.IsCancellationRequested)
            {
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
                        manualCompanyId = _manualSyncCompanyId;
                        _forceSyncOnce = false;
                        _manualSyncCompanyId = null;
                    }

                    var companies = _repo.GetAllCompanyProfiles();
                    foreach (var company in companies)
                    {
                        if (token.IsCancellationRequested) break;

                        bool shouldSync = false;
                        if (runManualSync)
                        {
                            shouldSync = !manualCompanyId.HasValue || manualCompanyId.Value == company.Id;
                        }
                        else
                        {
                            shouldSync = SyncOrchestrator.ShouldRun(company, DateTime.Now);
                        }

                        if (shouldSync)
                        {
                            await SyncCompany(company, client, token);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"[Engine Error] Cycle execution error: {ex.Message}");
                }

                try
                {
                    // Sleep for 60 seconds unless woken up earlier by manual trigger
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
            Log($"[Sync] Starting sync for company '{company.Name}' (Target: '{company.TargetCatalog}')...");

            if (string.IsNullOrWhiteSpace(company.TargetCatalog))
            {
                company.Status = "err";
                _repo.SaveCompanyProfile(company);
                Log($"[Sync ERROR] Company '{company.Name}' failed: Target database name is empty.");
                OnSyncCompleted?.Invoke();
                return;
            }

            company.Status = "running";
            _repo.SaveCompanyProfile(company);
            OnSyncCompleted?.Invoke();

            var run = new SyncRun
            {
                CompanyId = company.Id,
                CompanyName = company.Name,
                StartedAt = DateTime.Now,
                Mode = company.Mode,
                Status = "ok"
            };

            try
            {
                var dbProfile = company.Db ?? _repo.GetDatabaseProfileById(company.DbProfileId);
                if (dbProfile == null)
                {
                    throw new Exception("Target database profile not found.");
                }

                // Verify company in Tally
                var activeCompanies = await client.FetchActiveCompaniesAsync();
                if (!activeCompanies.Contains(company.Name))
                {
                    throw new Exception("Company is not open in Tally Prime.");
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

                // Use existing dynamic table pipeline (YAML config → TDL XML → DataTable → Bulk Load)
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
                    throw new System.IO.FileNotFoundException($"Tally definition file '{yamlPath}' not found.");
                }

                var yamlContent = System.IO.File.ReadAllText(yamlPath);
                var config = YamlConfigParser.Parse(yamlContent);

                long totalRows = 0;

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

                    // Ensure configuration and staging tables exist before persisting info
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
                        await runner.RunAsync(config, company.Name, fromDate, toDate, targetConn, prevMaster, prevTxn);
                        totalRows = 0; // Incremental tracks watermark, rows count is secondary
                    }
                    else
                    {
                        IFullSyncTablePromoter promoter = tech.Contains("sqlite")
                            ? new SqliteFullSyncTablePromoter()
                            : new UnsupportedFullSyncTablePromoter();
                        var runner = new FullSyncRunner(client, promoter);
                        totalRows = await runner.Run(config, company.Name, fromDate, toDate, targetConn);
                    }
                }

                // Complete SyncRun
                run.EndedAt = DateTime.Now;
                run.RowsIn = totalRows;
                run.RowsWritten = totalRows;
                run.Status = "ok";
                run.ResultSummary = $"Sync completed successfully. Wrote {totalRows} records.";
                _repo.AddSyncRun(run);

                // Update Profile
                company.Status = "ok";
                company.LastRunAt = DateTime.Now;
                company.LastDurationMs = (int)(run.EndedAt - run.StartedAt).TotalMilliseconds;
                company.LastRowsWritten = totalRows;
                company.ErrorCount24h = 0;
                _repo.SaveCompanyProfile(company);

                Log($"[Sync SUCCESS] Company '{company.Name}' sync finished. Wrote {totalRows} rows.");
            }
            catch (Exception ex)
            {
                run.EndedAt = DateTime.Now;
                run.Status = "err";
                run.ResultSummary = ex.Message;
                run.LogExcerpt = ex.StackTrace;
                _repo.AddSyncRun(run);

                company.Status = "err";
                company.ErrorCount24h++;
                _repo.SaveCompanyProfile(company);

                Log($"[Sync ERROR] Sync failed for '{company.Name}': {ex.Message}");
            }
            finally
            {
                OnSyncCompleted?.Invoke();
            }
        }

        private bool ShouldSyncTable(TableConfig table, EntityFlags flags)
        {
            // Map table names to entity flags
            var name = table.Name.ToLowerInvariant();
            if (name.Contains("group")) return flags.HasFlag(EntityFlags.Groups);
            if (name.Contains("ledger")) return flags.HasFlag(EntityFlags.Ledgers);
            if (name.Contains("voucher") || name.Contains("sales") || name.Contains("purchase") || name.Contains("receipt") || name.Contains("payment") || name.Contains("journal") || name.Contains("contra")) return flags.HasFlag(EntityFlags.Vouchers);
            if (name.Contains("stock") || name.Contains("item")) return flags.HasFlag(EntityFlags.StockItems);
            return true; // Default: sync if flag unknown
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
}
