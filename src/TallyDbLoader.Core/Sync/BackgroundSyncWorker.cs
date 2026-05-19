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

        public void Start()
        {
            if (IsRunning) return;
            _cts = new CancellationTokenSource();
            _runTask = Task.Run(() => WorkerLoop(_cts.Token));
            OnLogMessage?.Invoke("Background Sync Engine started.");
        }

        public void Stop()
        {
            if (!IsRunning) return;
            _cts?.Cancel();
            try { _runTask?.Wait(); } catch { }
            _cts = null;
            OnLogMessage?.Invoke("Background Sync Engine stopped.");
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
                            OnLogMessage?.Invoke("Auto-start Tally: Tally is not running. Launching...");
                            try
                            {
                                TallyLauncher.LaunchTally(settings.TallyExePath);
                                OnLogMessage?.Invoke("Tally launched successfully.");
                                await Task.Delay(TimeSpan.FromSeconds(5), token);
                            }
                            catch (Exception ex)
                            {
                                OnLogMessage?.Invoke($"Auto-start Tally failed: {ex.Message}");
                            }
                        }
                    }

                    var jobs = _repo.GetAllSyncJobs();
                    foreach (var job in jobs)
                      {
                        if (token.IsCancellationRequested) break;
                        
                        if (SyncOrchestrator.ShouldRun(job, DateTime.Now))
                        {
                            OnLogMessage?.Invoke($"Starting job '{job.CompanyName}'...");
                            
                            job.Status = "Running";
                            _repo.SaveSyncJob(job);
                            OnSyncCompleted?.Invoke();
                            
                            try
                            {
                                // Fetch and parse ledgers
                                var ledgersXml = await client.FetchLedgersXmlAsync(job.CompanyName);
                                var ledgers = TallyXmlParser.ParseLedgers(ledgersXml);
                                
                                // Find database profile
                                var dbProfile = _repo.GetDatabaseProfileById(job.DbProfileId);
                                
                                if (dbProfile != null)
                                {
                                    DatabaseWriter.InitializeTargetTables(dbProfile, job.TargetCatalog);
                                    DatabaseWriter.WriteLedgers(dbProfile, job.TargetCatalog, ledgers);
                                    
                                    job.Status = "Idle";
                                    job.LastRunTime = DateTime.UtcNow.ToString("o");
                                    OnLogMessage?.Invoke($"Job '{job.CompanyName}' completed successfully.");
                                }
                                else
                                {
                                    job.Status = "Failed";
                                    OnLogMessage?.Invoke($"Job '{job.CompanyName}' failed: Database profile not found.");
                                }
                            }
                            catch (Exception ex)
                            {
                                job.Status = "Failed";
                                OnLogMessage?.Invoke($"Job '{job.CompanyName}' failed: {ex.Message}");
                            }
                            
                            _repo.SaveSyncJob(job);
                            OnSyncCompleted?.Invoke();
                        }
                    }
                }
                catch (Exception ex)
                {
                    OnLogMessage?.Invoke($"Loop error: {ex.Message}");
                }
                
                try { await Task.Delay(TimeSpan.FromSeconds(60), token); } catch { break; }
            }
        }
    }
}
