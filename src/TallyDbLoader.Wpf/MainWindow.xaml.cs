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
            DataContext = new MainViewModel("config.db");
        }

        protected override void OnStateChanged(EventArgs e)
        {
            if (WindowState == WindowState.Minimized)
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

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm)
            {
                vm.StartSyncEngine();
            }
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm)
            {
                vm.StopSyncEngine();
            }
        }
        private void SaveTallyButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm)
            {
                vm.SaveTallySettings();
            }
        }

        private void SaveDbProfileButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm)
            {
                vm.SaveDatabaseProfile();
            }
        }

        private void AddSyncJobButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm)
            {
                vm.AddSyncJob();
            }
        }

        private void DeleteJobButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm)
            {
                if (vm.SelectedSyncJob != null)
                {
                    vm.DeleteSyncJob(vm.SelectedSyncJob);
                }
            }
        }

        private void DeleteDbProfileButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm)
            {
                if (vm.SelectedDatabaseProfile != null)
                {
                    vm.DeleteDatabaseProfile(vm.SelectedDatabaseProfile);
                }
            }
        }

        private void TestConnectionButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm)
            {
                vm.TestDatabaseConnection();
            }
        }

        private async void DetectCompaniesButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm)
            {
                await vm.DetectActiveCompaniesAsync();
            }
        }

        private void EditJobButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm)
            {
                if (vm.SelectedSyncJob != null)
                {
                    vm.StartEditingSyncJob(vm.SelectedSyncJob);
                    MainTabControl.SelectedIndex = 1;
                }
            }
        }

        private void EditDbProfileButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm)
            {
                if (vm.SelectedDatabaseProfile != null)
                {
                    vm.StartEditingDbProfile(vm.SelectedDatabaseProfile);
                    MainTabControl.SelectedIndex = 1;
                }
            }
        }

        private void CancelDbEditButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm)
            {
                vm.CancelDbEdit();
            }
        }

        private void CancelJobEditButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm)
            {
                vm.CancelJobEdit();
            }
        }
    }
}