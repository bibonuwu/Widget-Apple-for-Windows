using System;
using System.Windows;

namespace WinIslandSetup
{
    public partial class App : Application
    {
        SetupWindow _win;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            bool uninstall = false;
            string snapshotDir = null;
            for (int i = 0; i < e.Args.Length; i++)
            {
                string a = e.Args[i].ToLowerInvariant();
                if (a == "/uninstall" || a == "--uninstall") uninstall = true;
                else if (a == "--snapshot" && i + 1 < e.Args.Length) snapshotDir = e.Args[++i];
            }

            DispatcherUnhandledException += (_, a) =>
            {
                a.Handled = true;
                MessageBox.Show(a.Exception.Message, "WinIsland", MessageBoxButton.OK, MessageBoxImage.Error);
            };

            _win = new SetupWindow(uninstall, snapshotDir);
            MainWindow = _win;
            _win.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // деинсталлятор не может удалить сам себя, пока работает, — стираем папку после выхода
            if (_win?.CleanupDir != null) Installer.DeleteAfterExit(_win.CleanupDir);
            base.OnExit(e);
        }
    }
}
