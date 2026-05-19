# Tally-to-Database Loader .NET Port - Stage 3 (WPF User Interface & System Tray) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement the WPF Dashboard user interface, enable Windows Forms integration for native System Tray notifications, and hook up view models to SQLite config repositories.

**Architecture:** Configure `TallyDbLoader.Wpf.csproj` to support `<UseWindowsForms>true</UseWindowsForms>` so that we can leverage `System.Windows.Forms.NotifyIcon` for low-resource system tray interaction. Design a modern, responsive XAML dashboard in `MainWindow.xaml` styled with dark colors, card structures, and status indicators.

**Tech Stack:** WPF, XAML, C#, WinForms Integration.

---

## Tasks

### Task 8: WinForms Integration & System Tray Lifecycle

**Files:**
- Modify: `src/TallyDbLoader.Wpf/TallyDbLoader.Wpf.csproj`
- Modify: `src/TallyDbLoader.Wpf/App.xaml.cs`
- Create: `src/TallyDbLoader.Wpf/TrayController.cs`

- [ ] **Step 1: Enable Windows Forms in WPF Project**
  
  Modify `src/TallyDbLoader.Wpf/TallyDbLoader.Wpf.csproj` to include:
  ```xml
  <UseWindowsForms>true</UseWindowsForms>
  ```
  
  And verify that the project restores successfully.

- [ ] **Step 2: Implement TrayController**
  
  Create `src/TallyDbLoader.Wpf/TrayController.cs` to manage the lifecycle of the system tray icon:
  ```csharp
  using System;
  using System.Drawing;
  using System.Windows;
  using System.Windows.Forms;
  
  namespace TallyDbLoader.Wpf
  {
      public class TrayController : IDisposable
      {
          private readonly NotifyIcon _notifyIcon;
          private readonly Window _mainWindow;
  
          public TrayController(Window mainWindow)
          {
              _mainWindow = mainWindow ?? throw new ArgumentNullException(nameof(mainWindow));
              
              _notifyIcon = new NotifyIcon
              {
                  Icon = SystemIcons.Application,
                  Text = "Tally-to-Database Sync Utility",
                  Visible = true
              };
              
              _notifyIcon.DoubleClick += (s, e) => RestoreWindow();
              
              var contextMenu = new ContextMenuStrip();
              contextMenu.Items.Add("Open Dashboard", null, (s, e) => RestoreWindow());
              contextMenu.Items.Add("Sync Now", null, (s, e) => TriggerManualSync());
              contextMenu.Items.Add("-");
              contextMenu.Items.Add("Exit", null, (s, e) => ShutdownApplication());
              
              _notifyIcon.ContextMenuStrip = contextMenu;
          }
  
          public void ShowNotification(string title, string message)
          {
              _notifyIcon.ShowBalloonTip(3000, title, message, ToolTipIcon.Info);
          }
  
          private void RestoreWindow()
          {
              _mainWindow.Show();
              _mainWindow.WindowState = WindowState.Normal;
              _mainWindow.Activate();
          }
  
          private void TriggerManualSync()
          {
              ShowNotification("Sync Started", "Manual database synchronization has been triggered.");
          }
  
          private void ShutdownApplication()
          {
              _notifyIcon.Visible = false;
              _notifyIcon.Dispose();
              System.Windows.Application.Current.Shutdown();
          }
  
          public void Dispose()
          {
              _notifyIcon?.Dispose();
          }
      }
  }
  ```

- [ ] **Step 3: Integrate TrayController in MainWindow**
  
  Modify `src/TallyDbLoader.Wpf/MainWindow.xaml.cs` to bind tray hooks and handle minimize/close state:
  ```csharp
  using System;
  using System.ComponentModel;
  using System.Windows;
  
  namespace TallyDbLoader.Wpf
  {
      public partial class MainWindow : Window
      {
          private TrayController? _trayController;
          private bool _isExplicitShutdown = false;
  
          public MainWindow()
          {
              InitializeComponent();
              Loaded += MainWindow_Loaded;
          }
  
          private void MainWindow_Loaded(object sender, RoutedEventArgs e)
          {
              _trayController = new TrayController(this);
          }
  
          protected override void OnStateChanged(EventArgs e)
          {
              if (WindowState == WindowStateMinimized)
              {
                  Hide();
              }
              base.OnStateChanged(e);
          }
  
          protected override void OnClosing(CancelEventArgs e)
          {
              if (!_isExplicitShutdown)
              {
                  e.Cancel = true;
                  Hide();
                  _trayController?.ShowNotification("Minimized", "The Tally loader utility is running in the background.");
              }
              base.OnClosing(e);
          }
      }
  }
  ```

- [ ] **Step 4: Verify Compilation**
  
  Run: `dotnet build src/TallyDbLoader.sln`
  Expected: Successful compile with exit code 0.

- [ ] **Step 5: Commit**
  
  Run:
  ```bash
  git add src/
  git commit -m "feat: integrate WinForms notify icon for native WPF system tray support"
  ```

---

### Task 9: XAML Settings Dashboard & Data Binding

**Files:**
- Modify: `src/TallyDbLoader.Wpf/MainWindow.xaml`
- Create: `src/TallyDbLoader.Wpf/MainViewModel.cs`

- [ ] **Step 1: Implement MainViewModel**
  
  Create `src/TallyDbLoader.Wpf/MainViewModel.cs` that links SQLite repo configuration to the UI components:
  ```csharp
  using System.Collections.ObjectModel;
  using System.ComponentModel;
  using System.Runtime.CompilerServices;
  using TallyDbLoader.Core.Models;
  using TallyDbLoader.Core.Data;
  
  namespace TallyDbLoader.Wpf
  {
      public class MainViewModel : INotifyPropertyChanged
      {
          private readonly ConfigRepository _repo;
          private string _statusText = "Sync engine is idle.";
  
          public ObservableCollection<DatabaseProfile> DatabaseProfiles { get; set; }
          public ObservableCollection<SyncJob> SyncJobs { get; set; }
  
          public string StatusText
          {
              get => _statusText;
              set { _statusText = value; OnPropertyChanged(); }
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
              
              // Seed default values for demonstration if empty
              var postgresProfile = _repo.GetDatabaseProfileByName("PostgreSqlLocal");
              if (postgresProfile == null)
              {
                  postgresProfile = new DatabaseProfile
                  {
                      Name = "PostgreSqlLocal",
                      Technology = "postgres",
                      Server = "localhost",
                      Port = 5432,
                      Username = "postgres",
                      Password = "password"
                  };
                  _repo.SaveDatabaseProfile(postgresProfile);
              }
              
              DatabaseProfiles.Add(postgresProfile);
              
              var jobs = _repo.GetAllSyncJobs();
              if (jobs.Count == 0)
              {
                  var defaultJob = new SyncJob
                  {
                      CompanyName = "Demo Kitchen Central",
                      DbProfileId = postgresProfile.Id,
                      TargetCatalog = "kitchen_central",
                      SyncIntervalMinutes = 15,
                      Status = "Idle"
                  };
                  _repo.SaveSyncJob(defaultJob);
                  jobs = _repo.GetAllSyncJobs();
              }
              
              foreach (var job in jobs)
              {
                  SyncJobs.Add(job);
              }
          }
  
          public event PropertyChangedEventHandler? PropertyChanged;
          protected void OnPropertyChanged([CallerMemberName] string? name = null)
          {
              PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
          }
      }
  }
  ```

- [ ] **Step 2: Bind Data Context in MainWindow**
  
  Modify `src/TallyDbLoader.Wpf/MainWindow.xaml.cs` to set the ViewDataContext:
  ```csharp
  private void MainWindow_Loaded(object sender, RoutedEventArgs e)
  {
      _trayController = new TrayController(this);
      DataContext = new MainViewModel("config.db");
  }
  ```

- [ ] **Step 3: Update MainWindow XAML Layout**
  
  Modify `src/TallyDbLoader.Wpf/MainWindow.xaml` to feature a beautiful, high-fidelity UI layout including card boards, status lights, and responsive data tables:
  ```xml
  <Window x:Class="TallyDbLoader.Wpf.MainWindow"
          xmlns="http://schemas.microsoft.com/winfx/2000/xaml/presentation"
          xmlns:x="http://schemas.microsoft.com/winfx/2000/xaml"
          Title="Tally-to-Database Sync Dashboard" Height="480" Width="800"
          Background="#1E1E1E" Foreground="#FFFFFF" FontFamily="Segoe UI">
      <Grid Margin="20">
          <Grid.RowDefinitions>
              <RowDefinition Height="Auto"/>
              <RowDefinition Height="*"/>
              <RowDefinition Height="Auto"/>
          </Grid.RowDefinitions>
  
          <!-- Header -->
          <StackPanel Grid.Row="0" Margin="0,0,0,20">
              <TextBlock Text="Tally-to-Database Dashboard" FontSize="24" FontWeight="Bold" Foreground="#4A90E2"/>
              <TextBlock Text="Manage background database loaders and execution schedules" FontSize="12" Foreground="#888888" Margin="0,2,0,0"/>
          </StackPanel>
  
          <!-- Main Panels -->
          <Grid Grid.Row="1">
              <Grid.ColumnDefinitions>
                  <ColumnDefinition Width="*"/>
                  <ColumnDefinition Width="*"/>
              </Grid.ColumnDefinitions>
  
              <!-- Sync Jobs Card -->
              <Border Grid.Column="0" Background="#2D2D2D" CornerRadius="8" Padding="15" Margin="0,0,10,0">
                  <Grid>
                      <Grid.RowDefinitions>
                          <RowDefinition Height="Auto"/>
                          <RowDefinition Height="*"/>
                      </Grid.RowDefinitions>
                      <TextBlock Grid.Row="0" Text="Sync Jobs" FontSize="16" FontWeight="SemiBold" Foreground="#4A90E2" Margin="0,0,0,10"/>
                      <DataGrid Grid.Row="1" ItemsSource="{Binding SyncJobs}" AutoGenerateColumns="False" 
                                Background="Transparent" RowBackground="#333333" AlternatingRowBackground="#2A2A2A" 
                                BorderBrush="Transparent" Foreground="#FFFFFF" GridLinesVisibility="None" IsReadOnly="True">
                          <DataGrid.Columns>
                              <DataGridTextColumn Header="Company" Binding="{Binding CompanyName}" Width="*"/>
                              <DataGridTextColumn Header="Interval" Binding="{Binding SyncIntervalMinutes}" Width="60"/>
                              <DataGridTextColumn Header="Status" Binding="{Binding Status}" Width="80"/>
                          </DataGrid.Columns>
                      </DataGrid>
                  </Grid>
              </Border>
  
              <!-- Database Profiles Card -->
              <Border Grid.Column="1" Background="#2D2D2D" CornerRadius="8" Padding="15" Margin="10,0,0,0">
                  <Grid>
                      <Grid.RowDefinitions>
                          <RowDefinition Height="Auto"/>
                          <RowDefinition Height="*"/>
                      </Grid.RowDefinitions>
                      <TextBlock Grid.Row="0" Text="Database Connections" FontSize="16" FontWeight="SemiBold" Foreground="#4A90E2" Margin="0,0,0,10"/>
                      <DataGrid Grid.Row="1" ItemsSource="{Binding DatabaseProfiles}" AutoGenerateColumns="False" 
                                Background="Transparent" RowBackground="#333333" AlternatingRowBackground="#2A2A2A" 
                                BorderBrush="Transparent" Foreground="#FFFFFF" GridLinesVisibility="None" IsReadOnly="True">
                          <DataGrid.Columns>
                              <DataGridTextColumn Header="Name" Binding="{Binding Name}" Width="*"/>
                              <DataGridTextColumn Header="Type" Binding="{Binding Technology}" Width="80"/>
                              <DataGridTextColumn Header="Server" Binding="{Binding Server}" Width="*"/>
                          </DataGrid.Columns>
                      </DataGrid>
                  </Grid>
              </Border>
          </Grid>
  
          <!-- Status Bar Footer -->
          <Border Grid.Row="2" Background="#111111" CornerRadius="4" Padding="8,5" Margin="0,15,0,0">
              <Grid>
                  <Grid.ColumnDefinitions>
                      <ColumnDefinition Width="Auto"/>
                      <ColumnDefinition Width="*"/>
                  </Grid.ColumnDefinitions>
                  <Ellipse Grid.Column="0" Width="8" Height="8" Fill="#4CAF50" VerticalAlignment="Center" Margin="5,0,10,0"/>
                  <TextBlock Grid.Column="1" Text="{Binding StatusText}" FontSize="12" Foreground="#888888" VerticalAlignment="Center"/>
              </Grid>
          </Border>
      </Grid>
  </Window>
  ```

- [ ] **Step 4: Verify Compilation and Run Application**
  
  Run: `dotnet build src/TallyDbLoader.sln`
  Expected: Successful compilation, 0 errors.

- [ ] **Step 5: Commit**
  
  Run:
  ```bash
  git add src/
  git commit -m "feat: complete modern dark-theme dashboard UI using card layouts and Dapper bindings"
  ```
