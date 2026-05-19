using System;
using System.IO;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using TallyDbLoader.Core.Data;
using TallyDbLoader.Core.Tally;
using TallyDbLoader.Core.DatabaseLoaders;

namespace TallyDbLoader.Core.Sync
{
    public class BackgroundSyncWorker
    {
        private readonly ConfigRepository _repo;
        private readonly string _tallyServer;
        private readonly int _tallyPort;
        private CancellationTokenSource? _cts;
        private Task? _runTask;
        private TallyClient? _tallyClient;

        public void SetTallyClientForTest(TallyClient client)
        {
            _tallyClient = client;
        }

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
            var client = _tallyClient ?? new TallyClient(_tallyServer, _tallyPort);
            
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
                                var yamlPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tally-export-config.yaml");
                                if (!File.Exists(yamlPath))
                                {
                                    yamlPath = Path.Combine(Directory.GetCurrentDirectory(), "tally-export-config.yaml");
                                }
                                
                                if (!File.Exists(yamlPath))
                                {
                                    throw new FileNotFoundException($"Tally definition file '{yamlPath}' not found.");
                                }
                                
                                Log($"[SyncJob] Loading Tally definition file: {yamlPath}");
                                var yamlContent = File.ReadAllText(yamlPath);
                                var config = YamlConfigParser.Parse(yamlContent);
                                Log($"[SyncJob] Parsed YAML configuration: {config.Master.Count} masters, {config.Transaction.Count} transactions.");

                                // Find database profile
                                var dbProfile = _repo.GetDatabaseProfileById(job.DbProfileId);
                                
                                if (dbProfile != null)
                                {
                                    Log($"[SyncJob] Target database technology: {dbProfile.Technology} on server '{dbProfile.Server}:{dbProfile.Port}'.");
                                    
                                    IDatabaseLoader dbLoader;
                                    var tech = dbProfile.Technology.ToLower();
                                    string connStr;
                                    if (tech.Contains("postgres") || tech.Contains("npgsql"))
                                    {
                                        string sslParam = "";
                                        if (!dbProfile.Server.Equals("localhost", StringComparison.OrdinalIgnoreCase) && 
                                            !dbProfile.Server.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase))
                                        {
                                            sslParam = "SslMode=Require;TrustServerCertificate=True;";
                                        }
                                        connStr = $"Host={dbProfile.Server};Port={dbProfile.Port};Username={dbProfile.Username};Password={dbProfile.Password};Database={job.TargetCatalog};{sslParam}";
                                        dbLoader = new PostgreSqlLoader(connStr);
                                    }
                                    else if (tech.Contains("mssql") || tech.Contains("sqlserver"))
                                    {
                                        connStr = $"Server={dbProfile.Server},{dbProfile.Port};User Id={dbProfile.Username};Password={dbProfile.Password};Database={job.TargetCatalog};TrustServerCertificate=True;";
                                        dbLoader = new MSSqlLoader(connStr);
                                    }
                                    else if (tech.Contains("mysql"))
                                    {
                                        connStr = $"Server={dbProfile.Server};Port={dbProfile.Port};User Id={dbProfile.Username};Password={dbProfile.Password};Database={job.TargetCatalog};";
                                        dbLoader = new MySqlLoader(connStr);
                                    }
                                    else
                                    {
                                        throw new NotSupportedException($"Technology '{dbProfile.Technology}' not supported.");
                                    }

                                    var dates = await GetCompanyDatesAsync(client, job.CompanyName);
                                    Log($"[SyncJob] Sync period: {dates.fromDate} to {dates.toDate}");

                                    var tablesToSync = new System.Collections.Generic.List<TableConfig>();
                                    tablesToSync.AddRange(config.Master);
                                    tablesToSync.AddRange(config.Transaction);

                                    foreach (var table in tablesToSync)
                                    {
                                        if (token.IsCancellationRequested) break;
                                        
                                        Log($"[SyncJob] Processing table '{table.Name}'...");
                                        
                                        // 1. Initialize schema
                                        DatabaseWriter.InitializeTargetTableDynamic(dbProfile, job.TargetCatalog, table);
                                        
                                        // 2. Truncate table
                                        using (var conn = DatabaseWriter.GetConnection(dbProfile, job.TargetCatalog))
                                        using (var cmd = conn.CreateCommand())
                                        {
                                            cmd.CommandText = $"TRUNCATE TABLE {table.Name};";
                                            cmd.ExecuteNonQuery();
                                        }
                                        
                                        // 3. Query XML from Tally
                                        var xmlQuery = DynamicTdlXmlGenerator.GenerateXml(table, job.CompanyName, dates.fromDate, dates.toDate);
                                        var responseXml = await client.PostXMLAsync(xmlQuery);
                                        
                                        // 4. Parse into DataTable
                                        var dataTable = DynamicXmlParser.ParseXml(responseXml, table);
                                        Log($"[SyncJob] Parsed {dataTable.Rows.Count} rows for table '{table.Name}'.");
                                        
                                        // 5. Bulk Load into target database
                                        if (dataTable.Rows.Count > 0)
                                        {
                                            await dbLoader.LoadBulkDataAsync(dataTable, table.Name);
                                            Log($"[SyncJob] Successfully bulk loaded {dataTable.Rows.Count} rows into '{table.Name}'.");
                                        }
                                    }

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

        private async Task<(string fromDate, string toDate)> GetCompanyDatesAsync(TallyClient client, string companyName)
        {
            var defaultFrom = "20000101";
            var defaultTo = DateTime.Today.ToString("yyyyMMdd");
            
            try
            {
                var xmlCompany = @"<?xml version=""1.0"" encoding=""utf-8""?><ENVELOPE><HEADER><VERSION>1</VERSION><TALLYREQUEST>Export</TALLYREQUEST><TYPE>Data</TYPE><ID>TallyDatabaseLoaderReport</ID></HEADER><BODY><DESC><STATICVARIABLES><SVEXPORTFORMAT>ASCII (Comma Delimited)</SVEXPORTFORMAT></STATICVARIABLES><TDL><TDLMESSAGE><REPORT NAME=""TallyDatabaseLoaderReport""><FORMS>MyForm</FORMS></REPORT><FORM NAME=""MyForm""><PARTS>MyPart</PARTS></FORM><PART NAME=""MyPart""><LINES>MyLine</LINES><REPEAT>MyLine : MyCollection</REPEAT><SCROLLED>Vertical</SCROLLED></PART><LINE NAME=""MyLine""><FIELDS>FldGuid,FldName,FldBooksFrom,FldLastVoucherDate,FldLastAlterIdMaster,FldLastAlterIdTransaction,FldEOL</FIELDS></LINE><FIELD NAME=""FldGuid""><SET>$Guid</SET></FIELD><FIELD NAME=""FldName""><SET>$$StringFindAndReplace:$Name:'""':'""""'</SET></FIELD><FIELD NAME=""FldBooksFrom""><SET>(($$YearOfDate:$BooksFrom)*10000)+(($$MonthOfDate:$BooksFrom)*100)+(($$DayOfDate:$BooksFrom)*1)</SET></FIELD><FIELD NAME=""FldLastVoucherDate""><SET>(($$YearOfDate:$LastVoucherDate)*10000)+(($$MonthOfDate:$LastVoucherDate)*100)+(($$DayOfDate:$LastVoucherDate)*1)</SET></FIELD><FIELD NAME=""FldLastAlterIdMaster""><SET>$AltMstId</SET></FIELD><FIELD NAME=""FldLastAlterIdTransaction""><SET>$AltVchId</SET></FIELD><FIELD NAME=""FldEOL""><SET>†</SET></FIELD><COLLECTION NAME=""MyCollection""><TYPE>Company</TYPE><FILTER>FilterActiveCompany</FILTER></COLLECTION><SYSTEM TYPE=""Formulae"" NAME=""FilterActiveCompany"">$$IsEqual:##SVCurrentCompany:$Name</SYSTEM></TDLMESSAGE></TDL></DESC></BODY></ENVELOPE>";
                
                xmlCompany = xmlCompany.Replace("##SVCurrentCompany", $"\"{System.Security.SecurityElement.Escape(companyName)}\"");
                
                var response = await client.PostXMLAsync(xmlCompany);
                if (string.IsNullOrEmpty(response)) return (defaultFrom, defaultTo);
                
                var cleaned = response.Replace("\"", "").Trim();
                var parts = cleaned.Split(',');
                if (parts.Length >= 4)
                {
                    var fromPart = parts[2].Trim();
                    var toPart = parts[3].Trim();
                    
                    if (fromPart.Length == 8 && int.TryParse(fromPart, out _))
                    {
                        defaultFrom = fromPart;
                    }
                    if (toPart.Length == 8 && int.TryParse(toPart, out _))
                    {
                        defaultTo = toPart;
                    }
                }
            }
            catch
            {
                // Fallback gracefully
            }
            
            return (defaultFrom, defaultTo);
        }
    }
}
