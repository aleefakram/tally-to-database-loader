using System;
using System.Text;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.Threading;
using System.Windows.Input;
using System.Windows.Threading;
using TallyDbLoader.Wpf.ViewModels;
using TallyDbLoader.Core.Models;
using TallyDbLoader.Core.Data;
using TallyDbLoader.Core.Sync;
using TallyDbLoader.Core.Tally;

namespace TallyDbLoader.Wpf
{
    public enum RouteScreen
    {
        Dashboard,
        Companies,
        CompanyProfile,
        Databases,
        Log,
        History,
        Settings,
        Wizard
    }

    public class NavigationRoute
    {
        public RouteScreen Screen { get; set; }
        public int? ParameterId { get; set; }
    }

    public class ToastModel : BaseViewModel
    {
        private string _title = string.Empty;
        public string Title
        {
            get => _title;
            set { _title = value; OnPropertyChanged(); }
        }

        private string _body = string.Empty;
        public string Body
        {
            get => _body;
            set { _body = value; OnPropertyChanged(); }
        }

        private string _kind = "info"; // "info" | "warn" | "err" | "ok"
        public string Kind
        {
            get => _kind;
            set { _kind = value; OnPropertyChanged(); }
        }
    }

    public enum EngineState
    {
        Idle,
        Running,
        Paused
    }

    public class MainViewModel : BaseViewModel, IDisposable
    {
        private readonly IConfigRepository _repo;
        private BackgroundSyncWorker? _worker;
        private readonly DispatcherTimer _logBatchTimer;
        private readonly ConcurrentQueue<string> _logQueue = new ConcurrentQueue<string>();
        private readonly List<string> _logLines = new List<string>(2000);
        private readonly CancellationTokenSource _asyncOpsCts = new CancellationTokenSource();

        // Navigation callback for opening Dialog from View Model
        public Func<List<TallyCompanyInfo>, TallyCompanyInfo?>? CompanySelector { get; set; }
        public Func<string, string?>? SafetyResolveReasonPrompter { get; set; }

        public Func<string, int, TallyClient>? TallyClientFactory { get; set; }
        public Action<string, string, System.Windows.MessageBoxButton, System.Windows.MessageBoxImage>? MessageBoxShowHandler { get; set; }
        public bool DisableDispatcher { get; set; } = false;

        // Navigation properties
        private NavigationRoute _currentRoute;
        public NavigationRoute CurrentRoute
        {
            get => _currentRoute;
            set { _currentRoute = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanGoBack)); }
        }
        public Stack<NavigationRoute> RouteStack { get; } = new Stack<NavigationRoute>();
        public bool CanGoBack => RouteStack.Count > 1;

        // Collections
        public ObservableCollection<DatabaseProfile> DatabaseProfiles { get; } = new ObservableCollection<DatabaseProfile>();
        public ObservableCollection<CompanyProfile> Companies { get; } = new ObservableCollection<CompanyProfile>();
        public ObservableCollection<SyncRun> RunHistory { get; } = new ObservableCollection<SyncRun>();
        public ObservableCollection<SyncRun> SelectedCompanyRecentRuns { get; } = new ObservableCollection<SyncRun>();
        public ObservableCollection<TallyCompanyInfo> UnlinkedTallyCompanies { get; } = new ObservableCollection<TallyCompanyInfo>();
        public ObservableCollection<ToastModel> Toasts { get; } = new ObservableCollection<ToastModel>();
        public ObservableCollection<CompanyProfile> CompaniesUsingSelectedDb { get; } = new ObservableCollection<CompanyProfile>();

        // Selected items
        private CompanyProfile? _selectedCompany;
        public CompanyProfile? SelectedCompany
        {
            get => _selectedCompany;
            set
            {
                if (_selectedCompany == value) return;
                _selectedCompany = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanResolveSelectedCompanySafetyBlock));
            }
        }

        private DatabaseProfile? _selectedDatabaseProfile;
        public DatabaseProfile? SelectedDatabaseProfile
        {
            get => _selectedDatabaseProfile;
            set
            {
                if (_selectedDatabaseProfile == value) return;
                _selectedDatabaseProfile = value;
                OnPropertyChanged();
                CompaniesUsingSelectedDb.Clear();
                if (value != null)
                {
                    foreach (var c in Companies.Where(cp => cp.DbProfileId == value.Id))
                    {
                        CompaniesUsingSelectedDb.Add(c);
                    }
                    DbName = value.Name;
                    DbTech = value.Technology;
                    DbServer = value.Server;
                    DbPort = value.Port;
                    DbUsername = value.Username;
                    DbPassword = value.Password;
                    DbFormHeader = $"Edit Connection - {value.Name}";
                    DbSaveButtonText = "Update profile";
                    IsEditingDbProfile = true;
                }
            }
        }

        private SyncRun? _selectedRun;
        public SyncRun? SelectedRun
        {
            get => _selectedRun;
            set { _selectedRun = value; OnPropertyChanged(); }
        }

        // Global Settings Properties
        private string _tallyServer = "localhost";
        public string TallyServer
        {
            get => _tallyServer;
            set { _tallyServer = value; OnPropertyChanged(); }
        }

        private int _tallyPort = 9000;
        public int TallyPort
        {
            get => _tallyPort;
            set { _tallyPort = Math.Clamp(value, 1, 65535); OnPropertyChanged(); }
        }

        private string _tallyExePath = string.Empty;
        public string TallyExePath
        {
            get => _tallyExePath;
            set { _tallyExePath = value; OnPropertyChanged(); }
        }

        private string _tallyIniPath = string.Empty;
        public string TallyIniPath
        {
            get => _tallyIniPath;
            set { _tallyIniPath = value; OnPropertyChanged(); }
        }

        private bool _autoStartTally;
        public bool AutoStartTally
        {
            get => _autoStartTally;
            set { _autoStartTally = value; OnPropertyChanged(); }
        }

        // Database Profile Editor Scratch Properties
        private string _dbName = string.Empty;
        public string DbName
        {
            get => _dbName;
            set { _dbName = value; OnPropertyChanged(); }
        }

        private string _dbTech = "postgres";
        public string DbTech
        {
            get => _dbTech;
            set { _dbTech = value; OnPropertyChanged(); }
        }

        private string _dbServer = "localhost";
        public string DbServer
        {
            get => _dbServer;
            set { _dbServer = value; OnPropertyChanged(); }
        }

        private int _dbPort = 5432;
        public int DbPort
        {
            get => _dbPort;
            set { _dbPort = Math.Clamp(value, 1, 65535); OnPropertyChanged(); }
        }

        private string _dbUsername = string.Empty;
        public string DbUsername
        {
            get => _dbUsername;
            set { _dbUsername = value; OnPropertyChanged(); }
        }

        private string _dbPassword = string.Empty;
        public string DbPassword
        {
            get => _dbPassword;
            set { _dbPassword = value; OnPropertyChanged(); }
        }

        private string _dbFormHeader = "New Database Connection";
        public string DbFormHeader
        {
            get => _dbFormHeader;
            set { _dbFormHeader = value; OnPropertyChanged(); }
        }

        private string _dbSaveButtonText = "Save profile";
        public string DbSaveButtonText
        {
            get => _dbSaveButtonText;
            set { _dbSaveButtonText = value; OnPropertyChanged(); }
        }

        private bool _isEditingDbProfile = false;
        public bool IsEditingDbProfile
        {
            get => _isEditingDbProfile;
            set { _isEditingDbProfile = value; OnPropertyChanged(); }
        }

        // Company Profile / Sync Job Editor Scratch Properties
        private string _jobCompany = string.Empty;
        public string JobCompany
        {
            get => _jobCompany;
            set { _jobCompany = value; OnPropertyChanged(); }
        }

        private DatabaseProfile? _jobSelectedProfile;
        public DatabaseProfile? JobSelectedProfile
        {
            get => _jobSelectedProfile;
            set { _jobSelectedProfile = value; OnPropertyChanged(); }
        }

        private string _jobTargetCatalog = string.Empty;
        public string JobTargetCatalog
        {
            get => _jobTargetCatalog;
            set { _jobTargetCatalog = value; OnPropertyChanged(); }
        }

        private string _jobSchema = "public";
        public string JobSchema
        {
            get => _jobSchema;
            set { _jobSchema = value; OnPropertyChanged(); }
        }

        private string _jobTablePrefix = "tally_";
        public string JobTablePrefix
        {
            get => _jobTablePrefix;
            set { _jobTablePrefix = value; OnPropertyChanged(); }
        }

        private string _jobSyncMode = "full";
        public string JobSyncMode
        {
            get => _jobSyncMode;
            set { _jobSyncMode = value; OnPropertyChanged(); }
        }

        private int _jobInterval = 15;
        public int JobInterval
        {
            get => _jobInterval;
            set { _jobInterval = value; OnPropertyChanged(); }
        }

        private bool _jobEnabled = true;
        public bool JobEnabled
        {
            get => _jobEnabled;
            set { _jobEnabled = value; OnPropertyChanged(); }
        }

        private bool _jobNotifyOnError = true;
        public bool JobNotifyOnError
        {
            get => _jobNotifyOnError;
            set { _jobNotifyOnError = value; OnPropertyChanged(); }
        }

        private bool _jobPauseOnTallyClose = false;
        public bool JobPauseOnTallyClose
        {
            get => _jobPauseOnTallyClose;
            set { _jobPauseOnTallyClose = value; OnPropertyChanged(); }
        }

        private string _jobFormHeader = "New Sync Profile";
        public string JobFormHeader
        {
            get => _jobFormHeader;
            set { _jobFormHeader = value; OnPropertyChanged(); }
        }

        private string _jobSaveButtonText = "Save profile";
        public string JobSaveButtonText
        {
            get => _jobSaveButtonText;
            set { _jobSaveButtonText = value; OnPropertyChanged(); }
        }

        private bool _isEditingJob = false;
        public bool IsEditingJob
        {
            get => _isEditingJob;
            set { _isEditingJob = value; OnPropertyChanged(); }
        }

        // Entity check scratch variables
        private bool _jobSyncVouchers = true;
        public bool JobSyncVouchers
        {
            get => _jobSyncVouchers;
            set { _jobSyncVouchers = value; OnPropertyChanged(); }
        }

        private bool _jobSyncLedgers = true;
        public bool JobSyncLedgers
        {
            get => _jobSyncLedgers;
            set { _jobSyncLedgers = value; OnPropertyChanged(); }
        }

        private bool _jobSyncStockItems = true;
        public bool JobSyncStockItems
        {
            get => _jobSyncStockItems;
            set { _jobSyncStockItems = value; OnPropertyChanged(); }
        }

        private bool _jobSyncGroups = true;
        public bool JobSyncGroups
        {
            get => _jobSyncGroups;
            set { _jobSyncGroups = value; OnPropertyChanged(); }
        }

        private bool _jobSyncCostCentres = false;
        public bool JobSyncCostCentres
        {
            get => _jobSyncCostCentres;
            set { _jobSyncCostCentres = value; OnPropertyChanged(); }
        }

        private bool _jobSyncCurrencies = false;
        public bool JobSyncCurrencies
        {
            get => _jobSyncCurrencies;
            set { _jobSyncCurrencies = value; OnPropertyChanged(); }
        }

        // Connection string paste properties
        private string _connectionStringPasteText = string.Empty;
        public string ConnectionStringPasteText
        {
            get => _connectionStringPasteText;
            set
            {
                _connectionStringPasteText = value;
                OnPropertyChanged();
                if (!string.IsNullOrEmpty(value))
                {
                    TryParseConnectionString(value);
                }
            }
        }

        // Wizard Properties
        private int _wizardStepIndex = 0;
        public int WizardStepIndex
        {
            get => _wizardStepIndex;
            set { _wizardStepIndex = value; OnPropertyChanged(); }
        }

        // Engine State
        private EngineState _state = EngineState.Idle;
        public EngineState State
        {
            get => _state;
            set 
            { 
                _state = value; 
                OnPropertyChanged(); 
                OnPropertyChanged(nameof(IsSyncRunning));
                OnPropertyChanged(nameof(IsSyncNotRunning));
                OnPropertyChanged(nameof(StateText));
            }
        }

        public bool IsSyncRunning => State == EngineState.Running;
        public bool IsSyncNotRunning => !IsSyncRunning;
        public string StateText => State.ToString();

        public bool CanResolveSelectedCompanySafetyBlock =>
            SelectedCompany != null &&
            (SelectedCompany.Status == "review_required" ||
             SelectedCompany.Status == "attention_required" ||
             SelectedCompany.Status == "unknown");

        private string _logOutput = string.Empty;
        public string LogOutput
        {
            get => _logOutput;
            set { _logOutput = value; OnPropertyChanged(); }
        }

        // Commands
        public ICommand NavigateCommand { get; }
        public ICommand BackCommand { get; }
        public ICommand StartSyncEngineCommand { get; }
        public ICommand PauseSyncEngineCommand { get; }
        public ICommand ResumeSyncEngineCommand { get; }
        public ICommand StopSyncEngineCommand { get; }
        public ICommand RunCompanyCommand { get; }
        public ICommand SaveTallySettingsCommand { get; }
        public ICommand OpenCompanyPickerCommand { get; }
        public ICommand StartEditingCompanyCommand { get; }
        public ICommand SaveCompanyProfileCommand { get; }
        public ICommand DeleteCompanyProfileCommand { get; }
        public ICommand StartEditingDbProfileCommand { get; }
        public ICommand SaveDatabaseProfileCommand { get; }
        public ICommand DeleteDatabaseProfileCommand { get; }
        public ICommand TestDatabaseConnectionCommand { get; }
        public ICommand TestTallyConnectionCommand { get; }
        public ICommand DetectActiveCompaniesCommand { get; }
        public ICommand RefreshCommand { get; }
        public ICommand ExportLogCommand { get; }
        public ICommand ClearLogCommand { get; }
        public ICommand CancelDbEditCommand { get; }
        public ICommand CancelJobEditCommand { get; }
        public ICommand ResolveSafetyBlockCommand { get; }

        public MainViewModel(string dbPath)
        {
            DatabaseHelper.InitializeDatabase(dbPath);
            _repo = new ConfigRepository(dbPath);

            // Initialize Routing
            _currentRoute = new NavigationRoute { Screen = RouteScreen.Dashboard };
            RouteStack.Push(_currentRoute);

            // Command bindings
            NavigateCommand = new RelayCommand<object?>(ExecuteNavigate);
            BackCommand = new RelayCommand(GoBack);
            StartSyncEngineCommand = new RelayCommand(StartSyncEngine);
            PauseSyncEngineCommand = new RelayCommand(PauseEngine);
            ResumeSyncEngineCommand = new RelayCommand(ResumeEngine);
            StopSyncEngineCommand = new RelayCommand(StopSyncEngine);
            RunCompanyCommand = new RelayCommand<object?>(RunCompany);
            SaveTallySettingsCommand = new RelayCommand(SaveTallySettings);
            OpenCompanyPickerCommand = new RelayCommand(DetectActiveCompanies);
            StartEditingCompanyCommand = new RelayCommand<object?>(StartEditingCompany);
            SaveCompanyProfileCommand = new RelayCommand(SaveCompanyProfile);
            DeleteCompanyProfileCommand = new RelayCommand<object?>(DeleteCompanyProfile);
            StartEditingDbProfileCommand = new RelayCommand<object?>(StartEditingDbProfile);
            SaveDatabaseProfileCommand = new RelayCommand(SaveDatabaseProfile);
            DeleteDatabaseProfileCommand = new RelayCommand<object?>(DeleteDatabaseProfile);
            TestDatabaseConnectionCommand = new RelayCommand(TestDatabaseConnection);
            TestTallyConnectionCommand = new RelayCommand(TestTallyConnection);
            DetectActiveCompaniesCommand = new RelayCommand(DetectActiveCompanies);
            RefreshCommand = new RelayCommand(LoadConfiguration);
            ExportLogCommand = new RelayCommand(ExportLog);
            ClearLogCommand = new RelayCommand(ClearLog);
            CancelDbEditCommand = new RelayCommand(() => StartEditingDbProfile(null));
            CancelJobEditCommand = new RelayCommand(() => GoBack());
            ResolveSafetyBlockCommand = new RelayCommand<object?>(ResolveSafetyBlock);

            LoadConfiguration();

            // Set up log batching timer
            _logBatchTimer = new DispatcherTimer();
            _logBatchTimer.Interval = TimeSpan.FromMilliseconds(100);
            _logBatchTimer.Tick += FlushLogs;
            _logBatchTimer.Start();
        }

        public void ShowToast(string title, string body, string kind = "info")
        {
            var toast = new ToastModel { Title = title, Body = body, Kind = kind };
            Toasts.Add(toast);
            if (Toasts.Count > 5)
            {
                Toasts.RemoveAt(0);
            }

            var dismissTimer = new DispatcherTimer();
            dismissTimer.Interval = TimeSpan.FromMilliseconds(4500);
            dismissTimer.Tick += (s, e) =>
            {
                Toasts.Remove(toast);
                dismissTimer.Stop();
            };
            dismissTimer.Start();
        }

        private void InvokeOnDispatcher(Action action)
        {
            if (DisableDispatcher)
            {
                action();
                return;
            }

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null)
            {
                if (dispatcher.CheckAccess())
                {
                    action();
                }
                else
                {
                    dispatcher.Invoke(action);
                }
            }
            else
            {
                action();
            }
        }

        public void StartSyncEngine()
        {
            if (State == EngineState.Running) return;
            if (_worker == null)
            {
                _worker = new BackgroundSyncWorker(_repo, TallyServer, TallyPort);
                _worker.OnLogMessage += message => _logQueue.Enqueue($"{DateTime.Now:HH:mm:ss} {message}{Environment.NewLine}");
                _worker.OnSyncCompleted += () => InvokeOnDispatcher(() => LoadConfiguration());
            }
            _worker.Start();
            State = EngineState.Running;
            ShowToast("Engine started", "Background worker spinning up...", "info");
        }

        private void PauseEngine()
        {
            if (_worker != null && State == EngineState.Running)
            {
                _worker.Pause();
                State = EngineState.Paused;
                ShowToast("Engine paused", "", "warn");
            }
        }

        private void ResumeEngine()
        {
            if (_worker != null && State == EngineState.Paused)
            {
                _worker.Resume();
                State = EngineState.Running;
                ShowToast("Engine resumed", "", "ok");
            }
        }

        public void StopSyncEngine()
        {
            if (_worker != null)
            {
                _worker.Stop();
                _worker.Dispose();
                _worker = null;
                State = EngineState.Idle;
                ShowToast("Engine stopped", "Background worker disposed.", "warn");
            }
        }

        public void TriggerManualSync()
        {
            if (State == EngineState.Idle)
            {
                ShowToast("Engine is idle — start it to sync.", "", "info");
                return;
            }
            if (_worker != null)
            {
                var result = _worker.TryRequestManualSyncAll();
                if (result.Accepted)
                {
                    ShowToast("Sync queued", result.Message, "ok");
                }
                else
                {
                    ShowToast("Sync rejected", $"{result.Message} ({result.ReasonCode})", "err");
                }
            }
        }

        public void Dispose()
        {
            _asyncOpsCts.Cancel();
            StopSyncEngine();
            _logBatchTimer?.Stop();
            _asyncOpsCts.Dispose();
        }

        private void RunCompany(object? parameter)
        {
            if (State == EngineState.Idle)
            {
                ShowToast("Engine is idle — start it to sync.", "", "info");
                return;
            }
            if (_worker != null)
            {
                int? companyId = parameter as int?;
                var result = companyId.HasValue 
                    ? _worker.TryRequestManualSync(companyId.Value) 
                    : _worker.TryRequestManualSyncAll();
                
                if (result.Accepted)
                {
                    string name = "Company";
                    if (companyId.HasValue)
                    {
                        var company = Companies.FirstOrDefault(c => c.Id == companyId.Value);
                        if (company != null) name = company.Name;
                    }
                    ShowToast("Sync queued", $"{name} will run on the next worker tick.", "ok");
                }
                else
                {
                    ShowToast("Sync rejected", $"{result.Message} ({result.ReasonCode})", "err");
                }
            }
        }

        private void ResolveSafetyBlock(object? parameter)
        {
            var company = parameter as CompanyProfile;
            if (company == null) return;

            string? reason = null;
            if (SafetyResolveReasonPrompter != null)
            {
                reason = SafetyResolveReasonPrompter(company.Name);
                if (string.IsNullOrWhiteSpace(reason)) return; // Cancelled or empty
            }
            else
            {
                // In non-interactive contexts (e.g. tests)
                reason = "Resolved via automation script.";
            }

            // Resolve actor via hierarchy inside a guarded try-catch block
            string actor = "unknown-user";
            try
            {
                string? winIdentity = System.Security.Principal.WindowsIdentity.GetCurrent()?.Name;
                if (!string.IsNullOrWhiteSpace(winIdentity))
                {
                    actor = winIdentity;
                }
                else
                {
                    string? envUser = Environment.UserName;
                    if (!string.IsNullOrWhiteSpace(envUser)) actor = envUser;
                }
            }
            catch
            {
                try
                {
                    string? envUser = Environment.UserName;
                    if (!string.IsNullOrWhiteSpace(envUser)) actor = envUser;
                }
                catch { }
            }

            try
            {
                _repo.ResolveCompanyProfileSafetyState(company.Id, actor, reason, DateTime.Now);
                LoadConfiguration();
                ShowToast("Block Resolved", $"Safety block on '{company.Name}' successfully resolved.", "ok");
            }
            catch (Exception ex)
            {
                ShowToast("Resolution Failed", ex.Message, "err");
                _logQueue.Enqueue($"{DateTime.Now:HH:mm:ss} [error] Failed to resolve safety block: {ex.Message}{Environment.NewLine}");
            }
        }

        private void ExecuteNavigate(object? parameter)
        {
            if (parameter is RouteScreen screen)
            {
                Navigate(screen, resetStack: (screen == RouteScreen.Dashboard || screen == RouteScreen.Companies || screen == RouteScreen.Databases || screen == RouteScreen.Log || screen == RouteScreen.History || screen == RouteScreen.Settings));
            }
            else if (parameter is string actionStr)
            {
                if (actionStr == "WizardNext")
                {
                    if (WizardStepIndex < 5)
                    {
                        WizardStepIndex++;
                    }
                    else
                    {
                        // Save everything!
                        SaveTallySettings();
                        
                        if (!string.IsNullOrEmpty(DbName))
                        {
                            var db = new DatabaseProfile
                            {
                                Name = DbName,
                                Technology = DbTech,
                                Server = DbServer,
                                Port = DbPort,
                                Username = DbUsername,
                                Password = DbPassword
                            };
                            _repo.SaveDatabaseProfile(db);
                            
                            var dbs = _repo.GetAllDatabaseProfiles();
                            var savedDb = dbs.Find(d => d.Name == DbName);
                            
                            if (savedDb != null && !string.IsNullOrEmpty(JobCompany))
                            {
                                int flags = 0;
                                if (JobSyncVouchers) flags |= (int)EntityFlags.Vouchers;
                                if (JobSyncLedgers) flags |= (int)EntityFlags.Ledgers;
                                if (JobSyncStockItems) flags |= (int)EntityFlags.StockItems;
                                if (JobSyncGroups) flags |= (int)EntityFlags.Groups;
                                if (JobSyncCostCentres) flags |= (int)EntityFlags.CostCentres;
                                if (JobSyncCurrencies) flags |= (int)EntityFlags.Currencies;

                                var job = new CompanyProfile
                                {
                                    Name = JobCompany,
                                    DbProfileId = savedDb.Id,
                                    TargetCatalog = JobTargetCatalog,
                                    Schema = JobSchema,
                                    TablePrefix = JobTablePrefix,
                                    Mode = JobSyncMode,
                                    IntervalMinutes = JobInterval,
                                    Enabled = JobEnabled,
                                    NotifyOnError = JobNotifyOnError,
                                    PauseOnTallyClose = JobPauseOnTallyClose,
                                    EntityFlags = flags,
                                    Status = "idle"
                                };
                                _repo.SaveCompanyProfile(job);
                            }
                        }
                        
                        LoadConfiguration();
                        WizardStepIndex = 0;
                        Navigate(RouteScreen.Dashboard, resetStack: true);
                        ShowToast("Setup Complete", "Initial sync profile created.", "ok");
                    }
                }
                else if (actionStr == "WizardBack")
                {
                    if (WizardStepIndex > 0)
                    {
                        WizardStepIndex--;
                    }
                }
                else if (actionStr == "Dashboard")
                {
                    Navigate(RouteScreen.Dashboard, resetStack: true);
                }
                else if (Enum.TryParse<RouteScreen>(actionStr, ignoreCase: true, out var parsedScreen))
                {
                    Navigate(parsedScreen, resetStack: (parsedScreen == RouteScreen.Dashboard || parsedScreen == RouteScreen.Companies || parsedScreen == RouteScreen.Databases || parsedScreen == RouteScreen.Log || parsedScreen == RouteScreen.History || parsedScreen == RouteScreen.Settings));
                }
            }
        }

        public void Navigate(RouteScreen screen, int? parameterId = null, bool resetStack = false)
        {
            var route = new NavigationRoute { Screen = screen, ParameterId = parameterId };
            if (resetStack)
            {
                RouteStack.Clear();
            }
            RouteStack.Push(route);
            CurrentRoute = route;

            if (screen == RouteScreen.CompanyProfile)
            {
                int id = parameterId ?? 0;
                var profile = Companies.FirstOrDefault(c => c.Id == id);
                if (profile == null)
                {
                    SelectedCompany = new CompanyProfile();
                    JobCompany = string.Empty;
                    JobSelectedProfile = DatabaseProfiles.FirstOrDefault();
                    JobTargetCatalog = string.Empty;
                    JobSchema = "public";
                    JobTablePrefix = "tally_";
                    JobSyncMode = "full";
                    JobInterval = 15;
                    JobEnabled = true;
                    JobNotifyOnError = true;
                    JobPauseOnTallyClose = false;

                    JobSyncVouchers = true;
                    JobSyncLedgers = true;
                    JobSyncStockItems = true;
                    JobSyncGroups = true;
                    JobSyncCostCentres = false;
                    JobSyncCurrencies = false;

                    JobFormHeader = "New Sync Profile";
                    JobSaveButtonText = "Save profile";
                    IsEditingJob = false;

                    SelectedCompanyRecentRuns.Clear();
                }
                else
                {
                    SelectedCompany = profile;
                    JobCompany = profile.Name;
                    JobSelectedProfile = DatabaseProfiles.FirstOrDefault(d => d.Id == profile.DbProfileId);
                    JobTargetCatalog = profile.TargetCatalog;
                    JobSchema = profile.Schema;
                    JobTablePrefix = profile.TablePrefix;
                    JobSyncMode = profile.Mode;
                    JobInterval = profile.IntervalMinutes;
                    JobEnabled = profile.Enabled;
                    JobNotifyOnError = profile.NotifyOnError;
                    JobPauseOnTallyClose = profile.PauseOnTallyClose;

                    EntityFlags flags = (EntityFlags)profile.EntityFlags;
                    JobSyncVouchers = flags.HasFlag(EntityFlags.Vouchers);
                    JobSyncLedgers = flags.HasFlag(EntityFlags.Ledgers);
                    JobSyncStockItems = flags.HasFlag(EntityFlags.StockItems);
                    JobSyncGroups = flags.HasFlag(EntityFlags.Groups);
                    JobSyncCostCentres = flags.HasFlag(EntityFlags.CostCentres);
                    JobSyncCurrencies = flags.HasFlag(EntityFlags.Currencies);

                    JobFormHeader = $"Edit Profile - {profile.Name}";
                    JobSaveButtonText = "Update profile";
                    IsEditingJob = true;

                    var runs = _repo.GetSyncRunsForCompany(id, 6);
                    SelectedCompanyRecentRuns.Clear();
                    foreach (var r in runs) SelectedCompanyRecentRuns.Add(r);
                }
            }
        }

        public void GoBack()
        {
            if (RouteStack.Count > 1)
            {
                RouteStack.Pop();
                CurrentRoute = RouteStack.Peek();
            }
        }

        public void LoadConfiguration()
        {
            int? prevCompanyId = _selectedCompany?.Id;
            int? prevDbProfileId = _selectedDatabaseProfile?.Id;
            int? prevJobProfileId = _jobSelectedProfile?.Id;

            DatabaseProfiles.Clear();
            Companies.Clear();
            RunHistory.Clear();

            var settings = _repo.GetTallySettings();
            TallyServer = settings.Server;
            TallyPort = settings.Port;
            TallyExePath = settings.TallyExePath ?? string.Empty;
            TallyIniPath = settings.TallyIniPath ?? string.Empty;
            AutoStartTally = settings.AutoStartTally;

            var profiles = _repo.GetAllDatabaseProfiles();
            foreach (var profile in profiles) DatabaseProfiles.Add(profile);

            var companyProfiles = _repo.GetAllCompanyProfiles();
            foreach (var company in companyProfiles) Companies.Add(company);

            var runs = _repo.GetRecentSyncRuns(50);
            foreach (var run in runs) RunHistory.Add(run);

            // Re-select previously selected items to preserve bindings
            if (prevCompanyId.HasValue)
                _selectedCompany = Companies.FirstOrDefault(c => c.Id == prevCompanyId.Value);
            if (prevDbProfileId.HasValue)
                _selectedDatabaseProfile = DatabaseProfiles.FirstOrDefault(d => d.Id == prevDbProfileId.Value);
            if (prevJobProfileId.HasValue)
                _jobSelectedProfile = DatabaseProfiles.FirstOrDefault(d => d.Id == prevJobProfileId.Value);
            OnPropertyChanged(nameof(SelectedCompany));
            OnPropertyChanged(nameof(CanResolveSelectedCompanySafetyBlock));
            OnPropertyChanged(nameof(SelectedDatabaseProfile));
            OnPropertyChanged(nameof(JobSelectedProfile));
        }

        private bool GuardEngineRunning(string operation)
        {
            if (IsSyncRunning)
            {
                _logQueue.Enqueue($"{DateTime.Now:HH:mm:ss} [guard] {operation} skipped — engine running{Environment.NewLine}");
                ShowToast("Engine is running", "Stop the engine to save changes.", "warn");
                return true;
            }
            return false;
        }

        public void SaveTallySettings()
        {
            if (GuardEngineRunning("SaveTallySettings")) return;
            var settings = new TallySettings
            {
                Server = TallyServer,
                Port = TallyPort,
                TallyExePath = TallyExePath,
                TallyIniPath = TallyIniPath,
                AutoStartTally = AutoStartTally
            };
            _repo.SaveTallySettings(settings);
            ShowToast("Saved", "Tally connection settings updated.", "ok");
        }

        private void StartEditingCompany(object? parameter)
        {
            int id = 0;
            if (parameter is int intId) id = intId;
            else if (parameter is string strId && int.TryParse(strId, out var parsedId)) id = parsedId;
            
            Navigate(RouteScreen.CompanyProfile, id == 0 ? null : id);
        }

        private void SaveCompanyProfile()
        {
            if (GuardEngineRunning("SaveCompanyProfile")) return;
            if (string.IsNullOrWhiteSpace(JobCompany))
            {
                ShowToast("Validation Error", "Company name is required.", "err");
                return;
            }
            if (JobSelectedProfile == null)
            {
                ShowToast("Validation Error", "Database profile is required.", "err");
                return;
            }
            if (string.IsNullOrWhiteSpace(JobTargetCatalog))
            {
                ShowToast("Validation Error", "Target catalog name is required.", "err");
                return;
            }
            
            var profile = SelectedCompany ?? new CompanyProfile();
            profile.Name = JobCompany;
            profile.DbProfileId = JobSelectedProfile.Id;
            profile.TargetCatalog = JobTargetCatalog;
            profile.Schema = JobSchema;
            profile.TablePrefix = JobTablePrefix;
            profile.Mode = JobSyncMode;
            profile.IntervalMinutes = JobInterval;
            profile.Enabled = JobEnabled;
            profile.NotifyOnError = JobNotifyOnError;
            profile.PauseOnTallyClose = JobPauseOnTallyClose;
            
            int entityFlags = 0;
            if (JobSyncVouchers) entityFlags |= (int)EntityFlags.Vouchers;
            if (JobSyncLedgers) entityFlags |= (int)EntityFlags.Ledgers;
            if (JobSyncStockItems) entityFlags |= (int)EntityFlags.StockItems;
            if (JobSyncGroups) entityFlags |= (int)EntityFlags.Groups;
            if (JobSyncCostCentres) entityFlags |= (int)EntityFlags.CostCentres;
            if (JobSyncCurrencies) entityFlags |= (int)EntityFlags.Currencies;
            profile.EntityFlags = entityFlags;
            
            _repo.SaveCompanyProfile(profile);
            LoadConfiguration();
            ShowToast("Profile Saved", $"{profile.Name} profile settings updated.", "ok");
            Navigate(RouteScreen.Companies);
        }

        private void DeleteCompanyProfile(object? parameter)
        {
            if (GuardEngineRunning("DeleteCompanyProfile")) return;
            int id = 0;
            if (parameter is int intId) id = intId;
            if (id > 0)
            {
                _repo.DeleteCompanyProfile(id);
                LoadConfiguration();
                ShowToast("Profile Deleted", "Company profile removed successfully.", "ok");
                Navigate(RouteScreen.Companies);
            }
        }

        private void StartEditingDbProfile(object? parameter)
        {
            int id = 0;
            if (parameter is int intId) id = intId;
            else if (parameter is DatabaseProfile dp) id = dp.Id;
            
            var profile = DatabaseProfiles.FirstOrDefault(d => d.Id == id);
            if (profile == null)
            {
                _selectedDatabaseProfile = null;
                OnPropertyChanged(nameof(SelectedDatabaseProfile));
                DbName = string.Empty;
                DbTech = "postgres";
                DbServer = "localhost";
                DbPort = 5432;
                DbUsername = string.Empty;
                DbPassword = string.Empty;
                
                DbFormHeader = "New Database Connection";
                DbSaveButtonText = "Save profile";
                IsEditingDbProfile = false;
            }
            else
            {
                _selectedDatabaseProfile = profile;
                OnPropertyChanged(nameof(SelectedDatabaseProfile));
                DbName = profile.Name;
                DbTech = profile.Technology;
                DbServer = profile.Server;
                DbPort = profile.Port;
                DbUsername = profile.Username;
                DbPassword = profile.Password;
                
                DbFormHeader = $"Edit Connection - {profile.Name}";
                DbSaveButtonText = "Update profile";
                IsEditingDbProfile = true;
            }
        }

        public void SaveDatabaseProfile()
        {
            if (GuardEngineRunning("SaveDatabaseProfile")) return;
            if (string.IsNullOrWhiteSpace(DbName))
            {
                ShowToast("Validation Error", "Profile name is required.", "err");
                return;
            }
            if (string.IsNullOrWhiteSpace(DbServer))
            {
                ShowToast("Validation Error", "Server address is required.", "err");
                return;
            }
            if (string.IsNullOrWhiteSpace(DbUsername))
            {
                ShowToast("Validation Error", "Username is required.", "err");
                return;
            }
            
            var profile = SelectedDatabaseProfile ?? new DatabaseProfile();
            profile.Name = DbName;
            profile.Technology = DbTech;
            profile.Server = DbServer;
            profile.Port = DbPort;
            profile.Username = DbUsername;
            profile.Password = DbPassword;
            
            _repo.SaveDatabaseProfile(profile);
            LoadConfiguration();
            ShowToast("Profile Saved", $"Database profile '{profile.Name}' updated.", "ok");
            StartEditingDbProfile(null);
        }

        private void DeleteDatabaseProfile(object? parameter)
        {
            if (GuardEngineRunning("DeleteDatabaseProfile")) return;
            int id = 0;
            if (parameter is int intId) id = intId;
            else if (SelectedDatabaseProfile != null) id = SelectedDatabaseProfile.Id;
            
            if (id > 0)
            {
                var profile = DatabaseProfiles.FirstOrDefault(d => d.Id == id);
                if (profile != null && profile.UsedByCount > 0)
                {
                    ShowToast("Cannot Delete", $"This connection is currently used by {profile.UsedByCount} companies.", "err");
                    return;
                }
                _repo.DeleteDatabaseProfile(id);
                LoadConfiguration();
                ShowToast("Profile Deleted", "Database profile removed successfully.", "ok");
                StartEditingDbProfile(null);
            }
        }

        private void TestDatabaseConnection()
        {
            if (GuardEngineRunning("TestDatabaseConnection")) return;
            
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var start = DateTime.Now;
                    bool success = false;
                    if (DbTech.ToLower().Contains("postgres"))
                    {
                        var builder = new Npgsql.NpgsqlConnectionStringBuilder
                        {
                            Host = DbServer,
                            Port = DbPort,
                            Username = DbUsername,
                            Password = DbPassword,
                            Database = "postgres",
                            Timeout = 5
                        };
                        using (var conn = new Npgsql.NpgsqlConnection(builder.ConnectionString))
                        {
                            conn.Open();
                            success = true;
                        }
                    }
                    else
                    {
                        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder
                        {
                            DataSource = $"{DbServer},{DbPort}",
                            UserID = DbUsername,
                            Password = DbPassword,
                            InitialCatalog = "master",
                            ConnectTimeout = 5
                        };
                        using (var conn = new Microsoft.Data.SqlClient.SqlConnection(builder.ConnectionString))
                        {
                            conn.Open();
                            success = true;
                        }
                    }
                    var ms = (int)(DateTime.Now - start).TotalMilliseconds;
                    
                    InvokeOnDispatcher(() =>
                    {
                        if (success)
                            ShowToast("Connection OK", $"{DbName} responded in {ms}ms.", "ok");
                        else
                            ShowToast("Connection failed", "Unknown connection failure.", "err");
                    });
                }
                catch (Exception ex)
                {
                    InvokeOnDispatcher(() =>
                    {
                        ShowToast("Connection failed", $"{ex.Message.Substring(0, Math.Min(120, ex.Message.Length))}", "err");
                    });
                }
            }, _asyncOpsCts.Token);
        }

        private void TestTallyConnection()
        {
            System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    var client = new TallyClient(TallyServer, TallyPort);
                    var companies = await client.FetchActiveCompaniesAsync();
                    InvokeOnDispatcher(() =>
                    {
                        ShowToast("Tally Reachable", $"Active Companies: {companies.Count}", "ok");
                    });
                }
                catch (Exception ex)
                {
                    InvokeOnDispatcher(() =>
                    {
                        ShowToast("Tally Unreachable", ex.Message, "err");
                    });
                }
            }, _asyncOpsCts.Token);
        }

        private void DetectActiveCompanies()
        {
            _ = DetectActiveCompaniesAsync();
        }

        public async Task DetectActiveCompaniesAsync()
        {
            if (GuardEngineRunning("DetectActiveCompanies")) return;
            
            try
            {
                var client = TallyClientFactory != null ? TallyClientFactory(TallyServer, TallyPort) : new TallyClient(TallyServer, TallyPort);
                var details = await client.FetchActiveCompaniesDetailedAsync();
                
                InvokeOnDispatcher(() =>
                {
                    UnlinkedTallyCompanies.Clear();
                    foreach (var info in details)
                    {
                        if (!Companies.Any(c => c.Name.Equals(info.Name, StringComparison.OrdinalIgnoreCase)))
                        {
                            UnlinkedTallyCompanies.Add(info);
                        }
                    }
                    
                    if (UnlinkedTallyCompanies.Count == 0)
                    {
                        ShowToast("No active companies", "Open a company in Tally Prime, then try again.", "warn");
                    }
                    else if (UnlinkedTallyCompanies.Count == 1)
                    {
                        var single = UnlinkedTallyCompanies[0];
                        
                        Navigate(RouteScreen.CompanyProfile);
                        PopulateNewCompanyForm(single);
                        
                        ShowToast("Company linked", $"{single.Name} is now linked.", "ok");
                        MessageBoxShowHandler?.Invoke($"Selected Company: {single.Name}", "Link Company", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                    }
                    else
                    {
                        if (CompanySelector != null)
                        {
                            var selected = CompanySelector(UnlinkedTallyCompanies.ToList());
                            if (selected != null)
                            {
                                Navigate(RouteScreen.CompanyProfile);
                                PopulateNewCompanyForm(selected);
                                
                                ShowToast("Company linked", $"{selected.Name} is now linked.", "ok");
                                MessageBoxShowHandler?.Invoke($"Selected Company: {selected.Name}", "Link Company", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                            }
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                InvokeOnDispatcher(() =>
                {
                    ShowToast("Detection Failed", ex.Message, "err");
                });
            }
        }

        private void PopulateNewCompanyForm(TallyCompanyInfo info)
        {
            SelectedCompany = new CompanyProfile { Name = info.Name, TallyGuid = info.Guid, BooksFrom = info.BooksFrom, BooksTo = info.BooksTo, Consolidated = info.IsGroup };
            JobCompany = info.Name;
            JobSelectedProfile = DatabaseProfiles.FirstOrDefault();
            JobTargetCatalog = string.Empty;
            JobSchema = "public";
            JobTablePrefix = "tally_";
            JobSyncMode = "full";
            JobInterval = 15;
            JobEnabled = true;

            JobSyncVouchers = true;
            JobSyncLedgers = true;
            JobSyncStockItems = true;
            JobSyncGroups = true;
            JobSyncCostCentres = false;
            JobSyncCurrencies = false;

            JobFormHeader = "New Sync Profile (Auto-detected)";
            JobSaveButtonText = "Save profile";
            IsEditingJob = false;
        }

        private void TryParseConnectionString(string input)
        {
            try
            {
                if (input.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase) || input.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase))
                {
                    var uri = new Uri(input);
                    DbTech = "postgres";
                    DbServer = uri.Host;
                    DbPort = uri.Port > 0 ? uri.Port : 5432;
                    var userInfo = uri.UserInfo.Split(':');
                    if (userInfo.Length > 0) DbUsername = userInfo[0];
                    if (userInfo.Length > 1) DbPassword = userInfo[1];
                    if (uri.AbsolutePath.Length > 1) JobTargetCatalog = uri.AbsolutePath.TrimStart('/');
                    ShowToast("Connection string detected", "Filled 5 fields.", "info");
                }
                else if (input.Contains("Server=") || input.Contains("Host=") || input.Contains("Database=") || input.Contains("Initial Catalog="))
                {
                    var builder = new System.Data.Common.DbConnectionStringBuilder();
                    builder.ConnectionString = input;
                    
                    if (builder.ContainsKey("Server") || builder.ContainsKey("Host") || builder.ContainsKey("Data Source"))
                    {
                        var srv = (builder.ContainsKey("Server") ? builder["Server"] : (builder.ContainsKey("Host") ? builder["Host"] : builder["Data Source"])).ToString() ?? string.Empty;
                        var parts = srv.Split(',');
                        DbServer = parts[0];
                        if (parts.Length > 1 && int.TryParse(parts[1], out var p)) DbPort = p;
                    }
                    if (builder.ContainsKey("Database") || builder.ContainsKey("Initial Catalog"))
                    {
                        JobTargetCatalog = (builder.ContainsKey("Database") ? builder["Database"] : builder["Initial Catalog"]).ToString() ?? string.Empty;
                    }
                    if (builder.ContainsKey("User Id") || builder.ContainsKey("User ID") || builder.ContainsKey("Username") || builder.ContainsKey("Uid"))
                    {
                        DbUsername = (builder.ContainsKey("User Id") ? builder["User Id"] : (builder.ContainsKey("User ID") ? builder["User ID"] : (builder.ContainsKey("Username") ? builder["Username"] : builder["Uid"]))).ToString() ?? string.Empty;
                    }
                    if (builder.ContainsKey("Password") || builder.ContainsKey("Pwd"))
                    {
                        DbPassword = (builder.ContainsKey("Password") ? builder["Password"] : builder["Pwd"]).ToString() ?? string.Empty;
                    }
                    
                    DbTech = input.Contains("postgres", StringComparison.OrdinalIgnoreCase) ? "postgres" : "mssql";
                    ShowToast("Connection string detected", "Filled connection parameters.", "info");
                }
            }
            catch
            {
                ShowToast("Auto-parse Failed", "Could not parse connection string format.", "warn");
            }
        }

        private void ExportLog()
        {
            try
            {
                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = "Log Files (*.log)|*.log|Text Files (*.txt)|*.txt|All Files (*.*)|*.*",
                    FileName = "tally_sync_log.log"
                };
                if (dialog.ShowDialog() == true)
                {
                    File.WriteAllText(dialog.FileName, LogOutput);
                    ShowToast("Export Succeeded", "Log file saved successfully.", "ok");
                }
            }
            catch (Exception ex)
            {
                ShowToast("Export Failed", ex.Message, "err");
            }
        }

        private void ClearLog()
        {
            _logLines.Clear();
            LogOutput = string.Empty;
            ShowToast("Log Cleared", "Console output buffer cleared.", "info");
        }

        private void FlushLogs(object? sender, EventArgs e)
        {
            if (_logQueue.IsEmpty) return;

            while (_logQueue.TryDequeue(out var line))
            {
                var cleanLine = line.TrimEnd('\r', '\n');
                _logLines.Add(cleanLine);
            }

            if (_logLines.Count > 2000)
            {
                _logLines.RemoveRange(0, _logLines.Count - 2000);
            }

            LogOutput = string.Join(Environment.NewLine, _logLines) + Environment.NewLine;
        }
    }
}
