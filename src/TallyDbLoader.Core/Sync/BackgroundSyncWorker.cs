using System;
using System.Threading;
using System.Threading.Tasks;
using TallyDbLoader.Core.Data;
using TallyDbLoader.Core.Tally;

namespace TallyDbLoader.Core.Sync
{
    public class BackgroundSyncWorker
    {
        private readonly ConfigRepository _repo;
        private readonly string _tallyServer;
        private readonly int _tallyPort;
        private CancellationTokenSource? _cts;
        private Task? _runTask;

        public event Action<string>? OnLogMessage;
        public event Action? OnSyncCompleted;

        public bool IsRunning => _cts != null;

        public BackgroundSyncWorker(ConfigRepository repo, string tallyServer, int tallyPort)
        {
            _repo = repo ?? throw new ArgumentNullException(nameof(repo));
            _tallyServer = tallyServer;
            _tallyPort = portCheck(tallyPort);
        }

        private static int portCheck(int port) => port <= 0 ? 9000 : port;

        private void Log(string message)
        {
            OnLogMessage?.Invoke(message);
            TallyDbLoader.Core.Logging.FileLogger.LogMessage(message);
        }

        public void Start()
        {
            if (IsRunning) return;
            _cts = new CancellationTokenSource();
            _runTask = Task.Run(() => WorkerLoop(_cts.Token));
            Log("Background Sync Engine started.");
        }

        public void Stop()
        {
            if (!IsRunning) return;
            _cts?.Cancel();
            try { _runTask?.Wait(); } catch { }
            _cts = null;
            Log("Background Sync Engine stopped.");
        }

        private async Task WorkerLoop(CancellationToken token)
        {
            var client = new TallyClient(_tallyServer, _tallyPort);
            
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var settings = _repo.GetTallySettings();
                    if (settings.AutoStartTally == 1 && !string.IsNullOrEmpty(settings.TallyExePath))
                    {
                        if (!TallyLauncher.IsTallyRunning())
                        {
                            Log("Auto-start Tally: Tally is not running. Launching...");
                            try
                            {
                                TallyLauncher.LaunchTally(settings.TallyExePath);
                                Log("Tally launched successfully.");
                                await Task.Delay(TimeSpan.FromSeconds(5), token);
                            }
                            catch (Exception ex)
                            {
                                Log($"Auto-start Tally failed: {ex.Message}");
                                TallyDbLoader.Core.Logging.FileLogger.LogError("Auto-start Tally", ex);
                            }
                        }
                    }

                    var jobs = _repo.GetAllSyncJobs();
                    foreach (var job in jobs)
                    {
                        if (token.IsCancellationRequested) break;
                        
                        if (SyncOrchestrator.ShouldRun(job, DateTime.Now))
                        {
                            Log($"Starting job '{job.CompanyName}' (Target: '{job.TargetCatalog}')...");

                            if (string.IsNullOrWhiteSpace(job.TargetCatalog))
                            {
                                job.Status = "Failed";
                                _repo.SaveSyncJob(job);
                                Log($"Job '{job.CompanyName}' failed: Target database catalog name cannot be empty. Please configure a target database name.");
                                OnSyncCompleted?.Invoke();
                                continue;
                            }
                            
                            job.Status = "Running";
                            _repo.SaveSyncJob(job);
                            OnSyncCompleted?.Invoke();
                            
                            try
                            {
                                // Fetch and parse ledgers
                                Log($"[SyncJob] Fetching ledgers XML from Tally for company '{job.CompanyName}'...");
                                var ledgersXml = await client.FetchLedgersXmlAsync(job.CompanyName);
                                Log($"[SyncJob] Received Tally XML response of size {ledgersXml.Length} bytes.");
                                
                                var ledgers = TallyXmlParser.ParseLedgers(ledgersXml);
                                Log($"[SyncJob] Successfully parsed {ledgers.Count} ledgers from Tally XML.");
                                
                                // Find database profile
                                var dbProfile = _repo.GetDatabaseProfileById(job.DbProfileId);
                                
                                if (dbProfile != null)
                                {
                                    Log($"[SyncJob] Target database technology: {dbProfile.Technology} on server '{dbProfile.Server}:{dbProfile.Port}'.");
                                    Log($"[SyncJob] Initializing/verifying target catalog and tables '{job.TargetCatalog}'...");
                                    DatabaseWriter.InitializeTargetTables(dbProfile, job.TargetCatalog);
                                    Log($"[SyncJob] Database structures verified. Writing {ledgers.Count} ledgers to target database...");
                                    DatabaseWriter.WriteLedgers(dbProfile, job.TargetCatalog, ledgers);
                                    Log($"[SyncJob] Ledger data written successfully.");
                                    
                                    job.Status = "Idle";
                                    job.LastRunTime = DateTime.UtcNow.ToString("o");
                                    Log($"Job '{job.CompanyName}' completed successfully.");
                                }
                                else
                                {
                                    job.Status = "Failed";
                                    Log($"Job '{job.CompanyName}' failed: Database profile ID {job.DbProfileId} not found in configuration.");
                                }
                            }
                            catch (Exception ex)
                            {
                                job.Status = "Failed";
                                Log($"Job '{job.CompanyName}' failed: {ex.Message}");
                                TallyDbLoader.Core.Logging.FileLogger.LogError($"Job '{job.CompanyName}'", ex);
                            }
                            
                            _repo.SaveSyncJob(job);
                            OnSyncCompleted?.Invoke();
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"Loop error: {ex.Message}");
                    TallyDbLoader.Core.Logging.FileLogger.LogError("WorkerLoop Main Check", ex);
                }
                
                try { await Task.Delay(TimeSpan.FromSeconds(60), token); } catch { break; }
            }
        }
    }
}
