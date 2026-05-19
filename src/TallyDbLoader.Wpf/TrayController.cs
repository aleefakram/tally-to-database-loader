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
