using System;
using System.Threading;
using System.Windows;

namespace TallyDbLoader.Wpf
{
    public partial class App : System.Windows.Application
    {
        private const string AppMutexName = "Global\\TallyToDbLoaderMutex_662bd342-d285-4831-a40d";
        private static Mutex? _mutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            _mutex = new Mutex(true, AppMutexName, out bool isNewInstance);

            if (!isNewInstance)
            {
                System.Windows.MessageBox.Show("Another instance of Tally-to-Database Sync is already running.", 
                                "Already Running", MessageBoxButton.OK, MessageBoxImage.Warning);
                System.Windows.Application.Current.Shutdown();
                return;
            }

            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (_mutex != null)
            {
                try
                {
                    _mutex.ReleaseMutex();
                }
                catch (ObjectDisposedException) { }
                catch (ApplicationException) { }
                catch (AbandonedMutexException) { }
                _mutex.Dispose();
            }
            base.OnExit(e);
        }
    }
}
