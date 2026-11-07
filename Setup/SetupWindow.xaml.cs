using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace WinIslandSetup
{
    public partial class SetupWindow : Window
    {
        readonly bool _uninstallMode;
        readonly string _snapshotDir;
        bool _busy, _donePending, _indeterminate;
        FrameworkElement _page;
        int _lastPct = -1;
        string _lastText;

        /// <summary>Папка, которую надо стереть после выхода (деинсталлятор лежал в ней сам).</summary>
        public string CleanupDir { get; private set; }

        public SetupWindow(bool uninstall, string snapshotDir)
        {
            _uninstallMode = uninstall;
            _snapshotDir = snapshotDir;
            InitializeComponent();

            Title = uninstall ? "Удаление WinIsland" : "Установка WinIsland";
            Subtitle.Text = $"Динамический остров для Windows · версия {Installer.Version}";
            PathText.Text = Installer.InstallDir;
            PathText.ToolTip = Installer.InstallDir;

            string installed = null;
            try { installed = Installer.InstalledVersion; } catch { }
            if (installed != null)
            {
                // обновление: сохраняем прежний выбор пользователя
                InstallBtn.Content = installed == Installer.Version ? "Переустановить" : $"Обновить до {Installer.Version}";
                DesktopSwitch.IsChecked = Installer.HasDesktopLink;
                AutostartSwitch.IsChecked = Installer.AutostartEnabled;
            }

            bool needRuntime = !Installer.HasRuntime();
            RuntimeNote.Visibility = needRuntime ? Visibility.Visible : Visibility.Collapsed;
            RuntimeText.Text = $"Для работы нужен .NET Desktop Runtime {Installer.RuntimeMajor} — установщик сам скачает его " +
                               "с сайта Microsoft (≈ 60 МБ) и попросит подтверждение Windows.";

            ShowPage(uninstall ? PageUninstall : PageWelcome, false);

            Loaded += OnLoaded;
            Closing += (_, e) =>
            {
                if (_busy) { e.Cancel = true; return; }
                if (_donePending) ApplyDone(); // закрыли крестиком — выбор всё равно применяем
            };

            if (snapshotDir != null)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = -4000;
                Top = 0;
                ShowInTaskbar = false;
            }
        }

        // ===================================================================
        //  Анимации
        // ===================================================================

        async void OnLoaded(object sender, RoutedEventArgs e)
        {
            // остров «распускается» из точки, как при запуске приложения
            var spring = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.45 };
            HeroPill.BeginAnimation(WidthProperty, Anim(236, 750, spring, 150));
            HeroPill.BeginAnimation(HeightProperty, Anim(46, 750, spring, 150));
            HeroContent.BeginAnimation(OpacityProperty, Anim(1, 400, null, 450));

            double[] dur = { 0.42, 0.31, 0.50, 0.36 }, peak = { 1.0, 0.72, 0.92, 0.62 }, low = { 0.30, 0.22, 0.36, 0.26 };
            for (int i = 0; i < Eq.Children.Count; i++)
            {
                var bar = (Rectangle)Eq.Children[i];
                var st = new ScaleTransform(1, low[i]);
                bar.RenderTransform = st;
                bar.RenderTransformOrigin = new Point(0.5, 0.5);
                var a = new DoubleAnimation(low[i], peak[i], TimeSpan.FromSeconds(dur[i]))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                    BeginTime = TimeSpan.FromMilliseconds(i * 60)
                };
                Timeline.SetDesiredFrameRate(a, 30);
                st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
            }

            if (_snapshotDir != null) await RunSnapshots();
        }

        static DoubleAnimation Anim(double to, double ms, IEasingFunction ease, double delayMs = 0) =>
            new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
            {
                EasingFunction = ease ?? new CubicEase { EasingMode = EasingMode.EaseOut },
                BeginTime = TimeSpan.FromMilliseconds(delayMs)
            };

        void ShowPage(FrameworkElement page, bool animate = true)
        {
            foreach (FrameworkElement p in new[] { PageWelcome, PageProgress, PageDone, PageUninstall, PageRemoved, PageError })
                if (p != page) p.Visibility = Visibility.Collapsed;

            _page = page;
            page.Visibility = Visibility.Visible;
            if (!animate) return;

            var shift = new TranslateTransform(0, 12);
            page.RenderTransform = shift;
            page.Opacity = 0;
            page.BeginAnimation(OpacityProperty, Anim(1, 260, null));
            shift.BeginAnimation(TranslateTransform.YProperty, Anim(0, 420, new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 }));
            UpdateLayout();
        }

        void SetBusy(bool busy)
        {
            _busy = busy;
            CloseBtn.IsEnabled = !busy;
        }

        // ---------- полоса прогресса ----------

        void Report(double? f, string text)
        {
            if (text != null && text != _lastText) { _lastText = text; ProgressText.Text = text; }
            double w = BarTrack.ActualWidth;
            if (w <= 0) return;

            if (f == null)
            {
                if (_indeterminate) return;
                _indeterminate = true;
                BarFill.BeginAnimation(WidthProperty, null);
                BarFill.Width = w * 0.3;
                var a = new DoubleAnimation(-w * 0.3, w, TimeSpan.FromSeconds(1.25))
                {
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                };
                BarShift.BeginAnimation(TranslateTransform.XProperty, a);
                return;
            }

            if (_indeterminate)
            {
                _indeterminate = false;
                BarShift.BeginAnimation(TranslateTransform.XProperty, null);
                BarShift.X = 0;
                BarFill.BeginAnimation(WidthProperty, null);
                BarFill.Width = 0;
            }
            double v = Math.Max(0, Math.Min(1, f.Value));
            BarFill.BeginAnimation(WidthProperty, Anim(v * w, 220, null));
        }

        /// <summary>Отчёт о прогрессе из фонового потока.</summary>
        void ReportFromWorker(double? f, string text)
        {
            int pct = f.HasValue ? (int)(f.Value * 100) : -1;
            if (pct == _lastPct && text == _lastText) return;
            _lastPct = pct;
            Dispatcher.BeginInvoke(new Action(() => Report(f, text)));
        }

        // ===================================================================
        //  Кнопки
        // ===================================================================

        void Card_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) { try { DragMove(); } catch { } }
        }

        void Close_Click(object sender, RoutedEventArgs e) => Close();

        void Retry_Click(object sender, RoutedEventArgs e) => ShowPage(_uninstallMode ? PageUninstall : PageWelcome);

        async void Install_Click(object sender, RoutedEventArgs e)
        {
            SetBusy(true);
            ProgressTitle.Text = InstallBtn.Content is string s && s.StartsWith("Обновить") ? "Обновляю WinIsland…" : "Устанавливаю WinIsland…";
            ShowPage(PageProgress);
            Report(null, "Подготовка…");
            try
            {
                if (!Installer.HasRuntime())
                    await Installer.InstallRuntimeAsync(Report);

                bool desktop = DesktopSwitch.IsChecked == true;
                await Task.Run(() => Installer.Install(desktop, ReportFromWorker));
                await Task.Delay(250);

                SetBusy(false);
                _donePending = true;
                ShowPage(PageDone);
            }
            catch (Exception ex)
            {
                SetBusy(false);
                ShowError(ex);
            }
        }

        void Done_Click(object sender, RoutedEventArgs e) => Close(); // выбор применится в Closing

        /// <summary>Применить выбор с последнего экрана: автозагрузка и запуск.</summary>
        void ApplyDone()
        {
            _donePending = false;
            try { Installer.SetAutostart(AutostartSwitch.IsChecked == true); }
            catch (Exception ex) { MessageBox.Show("Не удалось настроить автозапуск: " + ex.Message, "WinIsland"); }

            if (LaunchSwitch.IsChecked == true)
            {
                try { Installer.Launch(); }
                catch (Exception ex) { MessageBox.Show("Не удалось запустить WinIsland: " + ex.Message, "WinIsland"); }
            }
        }

        async void Uninstall_Click(object sender, RoutedEventArgs e)
        {
            SetBusy(true);
            ProgressTitle.Text = "Удаляю WinIsland…";
            ShowPage(PageProgress);
            Report(null, "Подготовка…");
            try
            {
                CleanupDir = await Task.Run(() => Installer.Uninstall(ReportFromWorker));
                await Task.Delay(250);
                SetBusy(false);
                ShowPage(PageRemoved);
            }
            catch (Exception ex)
            {
                SetBusy(false);
                ShowError(ex);
            }
        }

        void ShowError(Exception ex)
        {
            ErrorText.Text = ex is InstallException ? ex.Message : "Ошибка: " + ex.Message;
            ShowPage(PageError);
        }

        // ===================================================================
        //  --snapshot <папка>: все экраны в PNG (проверка дизайна)
        // ===================================================================

        async Task RunSnapshots()
        {
            Directory.CreateDirectory(_snapshotDir);
            await Task.Delay(1300);
            RuntimeNote.Visibility = Visibility.Visible;
            ShowPage(PageWelcome, false);
            Shot("1-welcome");

            ProgressTitle.Text = "Устанавливаю WinIsland…";
            ShowPage(PageProgress, false);
            UpdateLayout();
            Report(0.62, "Скачиваю .NET Desktop Runtime 10 · 37 из 57 МБ");
            await Task.Delay(500);
            Shot("2-progress");

            ShowPage(PageDone, false);
            await Task.Delay(600);
            Shot("3-done");

            ShowPage(PageUninstall, false);
            await Task.Delay(300);
            Shot("4-uninstall");

            ShowPage(PageRemoved, false);
            await Task.Delay(300);
            Shot("5-removed");

            ErrorText.Text = "Установка .NET отменена.";
            ShowPage(PageError, false);
            await Task.Delay(300);
            Shot("6-error");

            Application.Current.Shutdown();
        }

        void Shot(string name)
        {
            UpdateLayout();
            int w = (int)ActualWidth, h = (int)ActualHeight;
            var rtb = new RenderTargetBitmap(w * 2, h * 2, 192, 192, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
                dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x8E, 0xA6, 0xD8), Color.FromRgb(0xD6, 0xD6, 0xDE), 90), null, new Rect(0, 0, w, h));
            rtb.Render(dv);
            rtb.Render(Root);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = File.Create(System.IO.Path.Combine(_snapshotDir, name + ".png")))
                enc.Save(fs);
        }
    }
}
