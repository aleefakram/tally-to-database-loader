using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using TallyDbLoader.Core.Models;
using TallyDbLoader.Core.Data;
using TallyDbLoader.Core.Sync;

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
            LogOutput = $"[{DateTime.Now:HH:mm:ss}] Saved Tally configuration.\n" + LogOutput;
        }

        public void SaveDatabaseProfile()
        {
            if (string.IsNullOrWhiteSpace(DbName)) return;

            var profile = new DatabaseProfile
            {
                Name = DbName,
                Technology = DbTech,
                Server = DbServer,
                Port = DbPort,
                Username = DbUsername,
                Password = DbPassword
            };
            _repo.SaveDatabaseProfile(profile);
            LoadConfiguration();
            LogOutput = $"[{DateTime.Now:HH:mm:ss}] Saved Database Profile '{DbName}'.\n" + LogOutput;
        }

        public void DeleteDatabaseProfile(DatabaseProfile profile)
        {
            if (profile == null) return;
            _repo.DeleteDatabaseProfile(profile.Id);
            LoadConfiguration();
            LogOutput = $"[{DateTime.Now:HH:mm:ss}] Deleted Database Profile '{profile.Name}'.\n" + LogOutput;
        }

        public void AddSyncJob()
        {
            if (string.IsNullOrWhiteSpace(JobCompany) || JobSelectedProfile == null) return;

            var job = new SyncJob
            {
                CompanyName = JobCompany,
                DbProfileId = JobSelectedProfile.Id,
                TargetCatalog = JobTargetCatalog,
                SyncIntervalMinutes = JobInterval,
                Status = "Idle"
            };
            _repo.SaveSyncJob(job);
            LoadConfiguration();
            LogOutput = $"[{DateTime.Now:HH:mm:ss}] Created Sync Job for '{JobCompany}'.\n" + LogOutput;
        }

        public void DeleteSyncJob(SyncJob job)
        {
            if (job == null) return;
            _repo.DeleteSyncJob(job.Id);
            LoadConfiguration();
            LogOutput = $"[{DateTime.Now:HH:mm:ss}] Deleted Sync Job for '{job.CompanyName}'.\n" + LogOutput;
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
                LogOutput = $"[{DateTime.Now:HH:mm:ss}] Connection test failed: Profile form is incomplete.\n" + LogOutput;
                return;
            }

            LogOutput = $"[{DateTime.Now:HH:mm:ss}] Testing database connection to '{DbServer}'...\n" + LogOutput;

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
                LogOutput = $"[{DateTime.Now:HH:mm:ss}] Database connection SUCCESSFUL!\n" + LogOutput;
                System.Windows.MessageBox.Show("Connection Successful!", "Database Test", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                LogOutput = $"[{DateTime.Now:HH:mm:ss}] Database connection FAILED: {ex.Message}\n" + LogOutput;
                System.Windows.MessageBox.Show($"Connection Failed:\n{ex.Message}", "Database Test", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
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
