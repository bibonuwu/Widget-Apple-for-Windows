using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace WinIsland
{
    public partial class App : Application
    {
        /// <summary>Именованное событие: установщик/деинсталлятор просит запущенную копию закрыться.</summary>
        public const string ExitSignalName = @"Local\WinIsland.Exit";

        Mutex _mutex;
        EventWaitHandle _exitSignal;
        MainWindow _win;
        Forms.NotifyIcon _tray;
        ContextMenu _menu;
        MenuItem _autoItem, _hideItem, _darkItem, _lightItem;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // --snapshot <папка> — отрисовать все состояния в PNG и выйти (для проверки дизайна)
            string snapshotDir = null;
            for (int i = 0; i < e.Args.Length - 1; i++)
                if (e.Args[i] == "--snapshot") snapshotDir = e.Args[i + 1];

            if (snapshotDir == null)
            {
                _mutex = new Mutex(true, @"Local\WinIsland.SingleInstance", out bool created);
                if (!created) { Shutdown(); return; }
            }

            DispatcherUnhandledException += (_, a) => { Log(a.Exception); a.Handled = true; };
            AppDomain.CurrentDomain.UnhandledException += (_, a) => Log(a.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (_, a) => { Log(a.Exception); a.SetObserved(); };

            Theme.Init();
            if (snapshotDir == null) ListenForExitSignal();

            _win = new MainWindow(snapshotDir);
            _win.Show();

            if (snapshotDir == null)
            {
                BuildMenu();
                BuildTray();
            }
        }

        void BuildMenu()
        {
            _menu = new ContextMenu();

            var header = new MenuItem { Header = "WinIsland", IsEnabled = false };

            _autoItem = new MenuItem { Header = "Запускать вместе с Windows", IsCheckable = true };
            _autoItem.Click += (_, _) => Autostart.Set(_autoItem.IsChecked);

            _hideItem = new MenuItem { Header = "Спрятать остров", IsCheckable = true };
            _hideItem.Click += (_, _) => _win.SetUserHidden(_hideItem.IsChecked);

            _darkItem = new MenuItem { Header = "Тёмная тема" };
            _darkItem.Click += (_, _) => SetTheme(false);

            _lightItem = new MenuItem { Header = "Светлая тема" };
            _lightItem.Click += (_, _) => SetTheme(true);

            var exit = new MenuItem { Header = "Выход" };
            exit.Click += (_, _) => Shutdown();

            _menu.Items.Add(header);
            _menu.Items.Add(new Separator());
            _menu.Items.Add(_autoItem);
            _menu.Items.Add(_hideItem);
            _menu.Items.Add(new Separator());
            _menu.Items.Add(_darkItem);
            _menu.Items.Add(_lightItem);
            _menu.Items.Add(new Separator());
            _menu.Items.Add(exit);

            // чтобы меню закрывалось по клику в любом другом месте
            _menu.Opened += (_, _) =>
            {
                if (PresentationSource.FromVisual(_menu) is HwndSource src)
                    Native.SetForegroundWindow(src.Handle);
            };
        }

        void BuildTray()
        {
            _tray = new Forms.NotifyIcon
            {
                Icon = TrayIcon.Create(),
                Text = "WinIsland",
                Visible = true
            };
            _tray.MouseUp += (_, a) =>
            {
                if (a.Button == Forms.MouseButtons.Right) ShowMenu();
                else if (a.Button == Forms.MouseButtons.Left) _win.TrayActivate();
            };
        }

        public void ShowMenu()
        {
            if (_menu == null) return;
            _autoItem.IsChecked = Autostart.IsEnabled;
            _hideItem.IsChecked = _win.UserHidden;
            _darkItem.IsChecked = !Theme.IsLight;
            _lightItem.IsChecked = Theme.IsLight;
            _menu.Placement = PlacementMode.MousePoint;
            _menu.IsOpen = true;
        }

        void SetTheme(bool light)
        {
            if (Theme.IsLight == light) return;
            Theme.Set(light);
            _win.Peek(); // остров выезжает на пару секунд — видно новую тему
        }

        void ListenForExitSignal()
        {
            try
            {
                _exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ExitSignalName);
                _exitSignal.Reset(); // мы единственная копия (мьютекс) — старый сигнал не наш
                ThreadPool.RegisterWaitForSingleObject(_exitSignal,
                    (_, _) => Dispatcher.BeginInvoke(new Action(() => Shutdown())), null, Timeout.Infinite, true);
            }
            catch (Exception ex) { Log(ex); }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
                _win?.Cleanup();
                _mutex?.ReleaseMutex();
                _mutex?.Dispose();
                _exitSignal?.Dispose();
            }
            catch { }
            base.OnExit(e);
        }

        internal static void Log(Exception ex)
        {
            if (ex == null) return;
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinIsland");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "error.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\r\n\r\n");
            }
            catch { }
        }
    }
}
