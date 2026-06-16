using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using TallyDbLoader.Wpf.Views;
using TallyDbLoader.Core.Models;

namespace TallyDbLoader.Wpf
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _vm;
        private TrayController? _trayController;
        private bool _isExiting = false;

        public MainWindow()
        {
            InitializeComponent();

            // Instantiate VM directly — do NOT rely on DataContext being set by XAML
            _vm = new MainViewModel("config.db");
            DataContext = _vm;

            _vm.PropertyChanged += OnVmPropertyChanged;

            // Setup tray controller and company picker callback
            _trayController = new TrayController(this);
            _vm.CompanySelector = (companies) =>
            {
                var dialog = new CompanySelectionWindow(companies);
                dialog.Owner = this;
                if (dialog.ShowDialog() == true)
                {
                    return dialog.SelectedCompany;
                }
                return null;
            };

            _vm.SafetyResolveReasonPrompter = (companyName) =>
            {
                var dialog = new ResolveSafetyBlockWindow(companyName);
                dialog.Owner = this;
                if (dialog.ShowDialog() == true)
                {
                    return dialog.Reason;
                }
                return null;
            };

            _vm.SaveFileDialogHandler = (defaultFilename, filter) =>
            {
                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    FileName = defaultFilename,
                    Filter = filter
                };
                if (dialog.ShowDialog() == true)
                {
                    return dialog.FileName;
                }
                return null;
            };

            _vm.OpenFileDialogHandler = (filter) =>
            {
                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Filter = filter
                };
                if (dialog.ShowDialog() == true)
                {
                    return dialog.FileName;
                }
                return null;
            };

            _vm.PasswordPromptHandler = (preview) =>
            {
                var dialog = new ImportPasswordPromptWindow(preview.DatabaseProfiles.Where(d => d.HasPassword));
                dialog.Owner = this;
                if (dialog.ShowDialog() == true)
                {
                    return dialog.Results;
                }
                return null;
            };

            _vm.FolderBrowserDialogHandler = () =>
            {
                using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
                {
                    if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    {
                        return dialog.SelectedPath;
                    }
                }
                return null;
            };

            _vm.ConfirmationPromptHandler = (message, title) =>
            {
                var result = System.Windows.MessageBox.Show(this, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question);
                return result == MessageBoxResult.Yes;
            };

            // Session ending handler
            if (System.Windows.Application.Current != null)
            {
                System.Windows.Application.Current.SessionEnding += App_SessionEnding;
            }

            NavigateToRoute(_vm.CurrentRoute);
        }

        private void App_SessionEnding(object sender, SessionEndingCancelEventArgs e)
        {
            if (_isExiting) return;
            _isExiting = true;
            _vm.Dispose();
            _trayController?.Dispose();
        }

        public void ExitApplication()
        {
            if (_isExiting) return;
            _isExiting = true;
            _vm.Dispose();
            _trayController?.Dispose();
            System.Windows.Application.Current?.Shutdown();
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
            if (!_isExiting)
            {
                e.Cancel = true;
                Hide();
                _trayController?.ShowNotification("Minimized", "The Tally loader utility is running in the background.");
            }
            base.OnClosing(e);
        }

        private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.CurrentRoute))
            {
                NavigateToRoute(_vm.CurrentRoute);
            }
        }

        private void NavigateToRoute(NavigationRoute route)
        {
            var frame = (Frame)FindName("NavigationFrame");
            if (frame == null) return;

            Page? page = null;
            switch (route.Screen)
            {
                case RouteScreen.Dashboard:
                    page = new DashboardPage();
                    break;
                case RouteScreen.Companies:
                    page = new CompaniesPage();
                    break;
                case RouteScreen.CompanyProfile:
                    page = new CompanyProfilePage();
                    break;
                case RouteScreen.Databases:
                    page = new DatabasesPage();
                    break;
                case RouteScreen.Log:
                    page = new LogPage();
                    break;
                case RouteScreen.History:
                    page = new HistoryPage();
                    break;
                case RouteScreen.Settings:
                    page = new SettingsPage();
                    break;
                case RouteScreen.Wizard:
                    page = new SetupWizardPage();
                    break;
            }

            if (page != null)
            {
                page.DataContext = _vm;
                frame.Navigate(page);
            }
        }
    }
}