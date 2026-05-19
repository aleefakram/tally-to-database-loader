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
    }
}