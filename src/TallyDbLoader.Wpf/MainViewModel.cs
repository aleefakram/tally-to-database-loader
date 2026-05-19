using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using TallyDbLoader.Core.Models;
using TallyDbLoader.Core.Data;
using TallyDbLoader.Core.Sync;
using TallyDbLoader.Core.Tally;

namespace TallyDbLoader.Wpf
{
    public class MainViewModel : INotifyPropertyChanged
    {
        private readonly ConfigRepository _repo;
        private string _statusText = "Sync engine is idle.";
        private string _logOutput = "Ready to start sync loop.";
        private BackgroundSyncWorker? _worker;

        public ObservableCollection<DatabaseProfile> DatabaseProfiles { get; set; }
        public ObservableCollection<SyncJob> SyncJobs { get; set; }

        public string StatusText
        {
            get => _statusText;
            set { _statusText = value; OnPropertyChanged(); }
        }

        public string LogOutput
        {
            get => _logOutput;
            set { _logOutput = value; OnPropertyChanged(); }
        }

        private void Log(string message)
        {
            LogOutput = $"[{DateTime.Now:HH:mm:ss}] {message}\n" + LogOutput;
            TallyDbLoader.Core.Logging.FileLogger.LogMessage(message);
        }

        // Tally Settings Form
        private string _tallyServer = "localhost";
        private int _tallyPort = 9000;
        private string _tallyExePath = string.Empty;
        private string _tallyIniPath = string.Empty;
        private bool _autoStartTally = false;

        public string TallyServer
        {
            get => _tallyServer;
            set { _tallyServer = value; OnPropertyChanged(); }
        }

        public int TallyPort
        {
            get => _tallyPort;
            set { _tallyPort = value; OnPropertyChanged(); }
        }

        public string TallyExePath
        {
            get => _tallyExePath;
            set { _tallyExePath = value; OnPropertyChanged(); }
        }

        public string TallyIniPath
        {
            get => _tallyIniPath;
            set { _tallyIniPath = value; OnPropertyChanged(); }
        }

        public bool AutoStartTally
        {
            get => _autoStartTally;
            set { _autoStartTally = value; OnPropertyChanged(); }
        }

        // Database Profile CRUD Form
        private string _dbName = string.Empty;
        private string _dbTech = "postgres"; // default
        private string _dbServer = "localhost";
        private int _dbPort = 5432;
        private string _dbUsername = string.Empty;
        private string _dbPassword = string.Empty;

        public string DbName
        {
            get => _dbName;
            set { _dbName = value; OnPropertyChanged(); }
        }

        public string DbTech
        {
            get => _dbTech;
            set { _dbTech = value; OnPropertyChanged(); }
        }

        public string DbServer
        {
            get => _dbServer;
            set 
            { 
                _dbServer = value; 
                OnPropertyChanged(); 
                TryParseConnectionString(value);
            }
        }

        private bool _isParsing = false;
        private void TryParseConnectionString(string input)
        {
            if (string.IsNullOrWhiteSpace(input) || _isParsing) return;
            _isParsing = true;
            try
            {
                if (input.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase) || 
                    input.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase))
                {
                    var uri = new Uri(input);
                    DbTech = "postgres";
                    
                    _dbServer = uri.Host;
                    OnPropertyChanged(nameof(DbServer));
                    
                    DbPort = uri.Port > 0 ? uri.Port : 5432;
                    
                    var userInfo = uri.UserInfo.Split(':');
                    if (userInfo.Length >= 1) DbUsername = Uri.UnescapeDataString(userInfo[0]);
                    if (userInfo.Length >= 2) DbPassword = Uri.UnescapeDataString(userInfo[1]);
                    
                    var path = uri.AbsolutePath.TrimStart('/');
                    if (!string.IsNullOrEmpty(path))
                    {
                        JobTargetCatalog = Uri.UnescapeDataString(path);
                    }
                }
                else if (input.Contains("Server=", StringComparison.OrdinalIgnoreCase) || 
                         input.Contains("Host=", StringComparison.OrdinalIgnoreCase) || 
                         input.Contains("Database=", StringComparison.OrdinalIgnoreCase))
                {
                    var builder = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = input };
                    
                    if (builder.TryGetValue("Server", out var s) || builder.TryGetValue("Host", out s) || builder.TryGetValue("Data Source", out s))
                    {
                        var hostPort = s.ToString()?.Split(',');
                        if (hostPort?.Length >= 1)
                        {
                            _dbServer = hostPort[0].Trim();
                            OnPropertyChanged(nameof(DbServer));
                        }
                        if (hostPort?.Length >= 2 && int.TryParse(hostPort[1], out int p)) DbPort = p;
                    }
                    if (builder.TryGetValue("User Id", out var u) || builder.TryGetValue("Username", out u) || builder.TryGetValue("Uid", out u))
                    {
                        DbUsername = u.ToString() ?? "";
                    }
                    if (builder.TryGetValue("Password", out var pass) || builder.TryGetValue("Pwd", out pass))
                    {
                        DbPassword = pass.ToString() ?? "";
                    }
                    if (builder.TryGetValue("Port", out var portStr) && int.TryParse(portStr.ToString(), out int parsedPort))
                    {
                        DbPort = parsedPort;
                    }
                    if (builder.TryGetValue("Database", out var db) || builder.TryGetValue("Initial Catalog", out db))
                    {
                        JobTargetCatalog = db.ToString() ?? "";
                    }
                }
            }
            catch {}
            finally
            {
                _isParsing = false;
            }
        }

        public int DbPort
        {
            get => _dbPort;
            set { _dbPort = value; OnPropertyChanged(); }
        }

        public string DbUsername
        {
            get => _dbUsername;
            set { _dbUsername = value; OnPropertyChanged(); }
        }

        public string DbPassword
        {
            get => _dbPassword;
            set { _dbPassword = value; OnPropertyChanged(); }
        }

        // Sync Job CRUD Form
        private string _jobCompany = string.Empty;
        private DatabaseProfile? _jobSelectedProfile;
        private string _jobTargetCatalog = string.Empty;
        private int _jobInterval = 15;

        public string JobCompany
        {
            get => _jobCompany;
            set { _jobCompany = value; OnPropertyChanged(); }
        }

        public DatabaseProfile? JobSelectedProfile
        {
            get => _jobSelectedProfile;
            set { _jobSelectedProfile = value; OnPropertyChanged(); }
        }

        public string JobTargetCatalog
        {
            get => _jobTargetCatalog;
            set { _jobTargetCatalog = value; OnPropertyChanged(); }
        }

        public int JobInterval
        {
            get => _jobInterval;
            set { _jobInterval = value; OnPropertyChanged(); }
        }

        public MainViewModel(string dbPath)
        {
            DatabaseHelper.InitializeDatabase(dbPath);
            _repo = new ConfigRepository(dbPath);
            
            DatabaseProfiles = new ObservableCollection<DatabaseProfile>();
            SyncJobs = new ObservableCollection<SyncJob>();
            
            LoadConfiguration();
        }

        public void LoadConfiguration()
        {
            DatabaseProfiles.Clear();
            SyncJobs.Clear();

            // Load Tally Settings
            var settings = _repo.GetTallySettings();
            TallyServer = settings.Server;
            TallyPort = settings.Port;
            TallyExePath = settings.TallyExePath ?? string.Empty;
            TallyIniPath = settings.TallyIniPath ?? string.Empty;
            AutoStartTally = settings.AutoStartTally == 1;
            
            // Load DB Profiles
            var profiles = _repo.GetAllDatabaseProfiles();
            foreach (var profile in profiles)
            {
                DatabaseProfiles.Add(profile);
            }

            // Load Sync Jobs
            var jobs = _repo.GetAllSyncJobs();
            foreach (var job in jobs)
            {
                SyncJobs.Add(job);
            }
        }

        // We will call repo methods to save
        public void SaveTallySettings()
        {
            var settings = new TallySettings
            {
                Server = TallyServer,
                Port = TallyPort,
                TallyExePath = TallyExePath,
                TallyIniPath = TallyIniPath,
                AutoStartTally = AutoStartTally ? 1 : 0
            };
            _repo.SaveTallySettings(settings);
            Log("Saved Tally configuration.");
        }

        private int _editingDbProfileId = 0;
        private int _editingSyncJobId = 0;

        public string DbFormHeader => _editingDbProfileId == 0 ? "Create Database Target Profile" : $"Edit Database Target Profile (ID: {_editingDbProfileId})";
        public string DbSaveButtonText => _editingDbProfileId == 0 ? "Create DB Target" : "Update Target";
        
        public string JobFormHeader => _editingSyncJobId == 0 ? "Create Sync Schedule / Job" : $"Edit Sync Schedule / Job (ID: {_editingSyncJobId})";
        public string JobSaveButtonText => _editingSyncJobId == 0 ? "Create Sync Schedule" : "Update Schedule";

        public System.Windows.Visibility IsEditingDbProfileVisibility => 
            _editingDbProfileId != 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

        public System.Windows.Visibility IsEditingJobVisibility => 
            _editingSyncJobId != 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

        public void StartEditingDbProfile(DatabaseProfile profile)
        {
            if (profile == null) return;
            _editingDbProfileId = profile.Id;
            DbName = profile.Name;
            DbTech = profile.Technology;
            DbServer = profile.Server;
            DbPort = profile.Port;
            DbUsername = profile.Username;
            DbPassword = profile.Password;
            
            OnPropertyChanged(nameof(DbFormHeader));
            OnPropertyChanged(nameof(DbSaveButtonText));
            OnPropertyChanged(nameof(IsEditingDbProfileVisibility));
        }

        public void CancelDbEdit()
        {
            _editingDbProfileId = 0;
            DbName = string.Empty;
            DbTech = "postgres";
            DbServer = "localhost";
            DbPort = 5432;
            DbUsername = string.Empty;
            DbPassword = string.Empty;
            
            OnPropertyChanged(nameof(DbFormHeader));
            OnPropertyChanged(nameof(DbSaveButtonText));
            OnPropertyChanged(nameof(IsEditingDbProfileVisibility));
        }

        public void StartEditingSyncJob(SyncJob job)
        {
            if (job == null) return;
            _editingSyncJobId = job.Id;
            JobCompany = job.CompanyName;
            JobTargetCatalog = job.TargetCatalog;
            JobInterval = job.SyncIntervalMinutes ?? 15;
            
            foreach (var profile in DatabaseProfiles)
            {
                if (profile.Id == job.DbProfileId)
                {
                    JobSelectedProfile = profile;
                    break;
                }
            }
            
            OnPropertyChanged(nameof(JobFormHeader));
            OnPropertyChanged(nameof(JobSaveButtonText));
            OnPropertyChanged(nameof(IsEditingJobVisibility));
        }

        public void CancelJobEdit()
        {
            _editingSyncJobId = 0;
            JobCompany = string.Empty;
            JobTargetCatalog = string.Empty;
            JobInterval = 15;
            JobSelectedProfile = null;
            
            OnPropertyChanged(nameof(JobFormHeader));
            OnPropertyChanged(nameof(JobSaveButtonText));
            OnPropertyChanged(nameof(IsEditingJobVisibility));
        }

        public void SaveDatabaseProfile()
        {
            if (string.IsNullOrWhiteSpace(DbName)) return;

            var profile = new DatabaseProfile
            {
                Id = _editingDbProfileId,
                Name = DbName,
                Technology = DbTech,
                Server = DbServer,
                Port = DbPort,
                Username = DbUsername,
                Password = DbPassword
            };
            _repo.SaveDatabaseProfile(profile);
            
            string msg = _editingDbProfileId == 0 
                ? $"Created Database Profile '{DbName}'." 
                : $"Updated Database Profile '{DbName}' (ID: {_editingDbProfileId}).";
            
            CancelDbEdit();
            LoadConfiguration();
            Log(msg);
        }

        public void DeleteDatabaseProfile(DatabaseProfile profile)
        {
            if (profile == null) return;
            _repo.DeleteDatabaseProfile(profile.Id);
            LoadConfiguration();
            Log($"Deleted Database Profile '{profile.Name}'.");
        }

        public void AddSyncJob()
        {
            if (string.IsNullOrWhiteSpace(JobCompany) || JobSelectedProfile == null) return;

            var job = new SyncJob
            {
                Id = _editingSyncJobId,
                CompanyName = JobCompany,
                DbProfileId = JobSelectedProfile.Id,
                TargetCatalog = JobTargetCatalog,
                SyncIntervalMinutes = JobInterval,
                Status = "Idle"
            };
            _repo.SaveSyncJob(job);
            
            string msg = _editingSyncJobId == 0 
                ? $"Created Sync Job for '{JobCompany}'." 
                : $"Updated Sync Job for '{JobCompany}' (ID: {_editingSyncJobId}).";
            
            CancelJobEdit();
            LoadConfiguration();
            Log(msg);
        }

        public void DeleteSyncJob(SyncJob job)
        {
            if (job == null) return;
            _repo.DeleteSyncJob(job.Id);
            LoadConfiguration();
            Log($"Deleted Sync Job for '{job.CompanyName}'.");
        }

        private SyncJob? _selectedSyncJob;
        public SyncJob? SelectedSyncJob
        {
            get => _selectedSyncJob;
            set { _selectedSyncJob = value; OnPropertyChanged(); }
        }

        private DatabaseProfile? _selectedDatabaseProfile;
        public DatabaseProfile? SelectedDatabaseProfile
        {
            get => _selectedDatabaseProfile;
            set { _selectedDatabaseProfile = value; OnPropertyChanged(); }
        }

        public void TestDatabaseConnection()
        {
            if (string.IsNullOrWhiteSpace(DbName))
            {
                Log("Connection test failed: Profile form is incomplete.");
                return;
            }

            Log($"Testing database connection to '{DbServer}'...");

            try
            {
                if (DbTech.Equals("postgres", StringComparison.OrdinalIgnoreCase))
                {
                    string sslParam = "";
                    if (!DbServer.Equals("localhost", StringComparison.OrdinalIgnoreCase) && 
                        !DbServer.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase))
                    {
                        sslParam = "SslMode=Require;TrustServerCertificate=True;";
                    }
                    using (var conn = new Npgsql.NpgsqlConnection($"Host={DbServer};Port={DbPort};Username={DbUsername};Password={DbPassword};Database=postgres;Timeout=5;{sslParam}"))
                    {
                        conn.Open();
                    }
                }
                else
                {
                    using (var conn = new Microsoft.Data.SqlClient.SqlConnection($"Server={DbServer},{DbPort};User Id={DbUsername};Password={DbPassword};TrustServerCertificate=True;Connection Timeout=5"))
                    {
                        conn.Open();
                    }
                }
                Log("Database connection SUCCESSFUL!");
                System.Windows.MessageBox.Show("Connection Successful!", "Database Test", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Log($"Database connection FAILED: {ex.Message}");
                TallyDbLoader.Core.Logging.FileLogger.LogError("TestDatabaseConnection", ex);
                System.Windows.MessageBox.Show($"Connection Failed:\n{ex.Message}", "Database Test", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }

        public async System.Threading.Tasks.Task DetectActiveCompaniesAsync()
        {
            Log($"Querying running Tally instance at http://{TallyServer}:{TallyPort}...");
            
            try
            {
                var client = new TallyClient(TallyServer, TallyPort);
                var companies = await client.FetchActiveCompaniesDetailedAsync();
                
                if (companies != null && companies.Count > 0)
                {
                    JobCompany = companies[0].Name;
                    var companyStrings = new System.Collections.Generic.List<string>();
                    foreach (var c in companies)
                    {
                        companyStrings.Add(c.ToString());
                    }
                    Log($"Success! Detected {companies.Count} company/companies: {string.Join(", ", companyStrings)}");
                    System.Windows.MessageBox.Show($"Detected Company: {companies[0]}" + 
                        (companies.Count > 1 ? $"\n(And {companies.Count - 1} other open companies. Check log output for full list)" : ""), 
                        "Tally Company Detection", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                }
                else
                {
                    Log("No open companies found. Please open a company in Tally Prime first.");
                    System.Windows.MessageBox.Show("No active companies found. Please ensure a company is open in Tally Prime.", 
                        "Tally Company Detection", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                Log($"Connection to Tally failed: {ex.Message}");
                TallyDbLoader.Core.Logging.FileLogger.LogError("DetectActiveCompanies", ex);
                System.Windows.MessageBox.Show($"Failed to connect to Tally XML API at {TallyServer}:{TallyPort}.\nEnsure Tally Prime is running and ODBC/XML features are enabled.", 
                    "Tally Connection Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }

        public void StartSyncEngine()
        {
            if (_worker == null)
            {
                _worker = new BackgroundSyncWorker(_repo, TallyServer, TallyPort);
                _worker.OnLogMessage += (msg) => {
                    LogOutput = $"[{DateTime.Now:HH:mm:ss}] {msg}\n" + LogOutput;
                    StatusText = msg;
                };
                _worker.OnSyncCompleted += () => {
                    System.Windows.Application.Current.Dispatcher.Invoke(() => LoadConfiguration());
                };
            }
            _worker.Start();
        }

        public void StopSyncEngine()
        {
            _worker?.Stop();
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
