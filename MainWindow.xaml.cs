using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinIsland.Controls;
using WinIsland.Services;

namespace WinIsland
{
    public enum IslandState { Collapsed, Compact, Hud, Banner, Expanded }

    public partial class MainWindow : Window
    {
        // размеры острова (в DIP)
        const double CollapsedW = 124, PillH = 32, CompactW = 250, CompactH = 36, HudW = 272;
        const double BannerW = 364, BannerH = 80, ExpandedW = 400, HudBarW = 170;

        static readonly Color Blue = Color.FromRgb(0x0A, 0x84, 0xFF);
        static readonly Color Red = Color.FromRgb(0xFF, 0x45, 0x3A);
        static readonly Color DefaultAccent = Color.FromRgb(0x30, 0xD1, 0x58);

        readonly MediaService _media = new MediaService();
        readonly AudioService _audio = new AudioService();
        readonly BrightnessService _bright = new BrightnessService();
        readonly RadioService _radio = new RadioService();

        readonly SolidColorBrush _accent = new SolidColorBrush(DefaultAccent);
        readonly GradientStop _glowStop = new GradientStop(Color.FromArgb(0x55, 0x30, 0xD1, 0x58), 0);
        readonly RectangleGeometry _islandClip = new RectangleGeometry();

        /// <summary>Круглый переключатель: фон и иконка зависят от состояния и темы.</summary>
        sealed class Tog
        {
            public readonly SolidColorBrush Bg = new SolidColorBrush(Theme.ToggleOff);
            public readonly SolidColorBrush Fg = new SolidColorBrush(Theme.ToggleGlyph);
            public Color? On; // null — выключен
        }
        readonly Tog _wifi = new Tog(), _bt = new Tog(), _air = new Tog(), _mute = new Tog(), _snip = new Tog();

        readonly DispatcherTimer _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
        readonly DispatcherTimer _tempTimer = new DispatcherTimer();
        readonly DispatcherTimer _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        readonly DispatcherTimer _fsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
        readonly DispatcherTimer _mouseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };

        // остров спрятан (уехал вверх, клики проходят к вкладкам), пока на него не наведут мышь
        bool _revealed = true, _dwellBlocked;
        long _nearSince, _leaveSince, _overSince, _outSince;
        const int DwellOpenMs = 2000; // столько держать курсор на острове, чтобы открылся большой виджет

        readonly string _snapshotDir;
        IslandState _state = IslandState.Collapsed;
        bool _hover, _hiddenFs, _hiddenUser, _seeking, _firstMedia = true, _iconPlaying;
        MediaSnapshot _snap = new MediaSnapshot();
        string _lastKey;
        byte[] _bgThumb;      // трогается только из потока медиа-сервиса
        Brush _placeholder;
        IntPtr _hwnd;

        public bool UserHidden => _hiddenUser;

        public MainWindow(string snapshotDir = null)
        {
            _snapshotDir = snapshotDir;
            InitializeComponent();

            _placeholder = (Brush)FindResource("ArtPlaceholder");
            Island.Clip = _islandClip;

            var glow = new RadialGradientBrush
            {
                Center = new Point(0.15, 0.12),
                GradientOrigin = new Point(0.15, 0.12),
                RadiusX = 0.9,
                RadiusY = 0.8
            };
            glow.GradientStops.Add(_glowStop);
            glow.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 1));
            Glow.Background = glow;

            CompactEq.BarBrush = BannerEq.BarBrush = ExpandedEq.BarBrush = _accent;
            SourceDot.Fill = _accent;

            foreach (var l in new FrameworkElement[] { CompactLayer, HudLayer, BannerLayer, ExpandedLayer })
            {
                l.RenderTransform = new ScaleTransform(1, 1);
                l.RenderTransformOrigin = new Point(0.5, l == ExpandedLayer || l == BannerLayer ? 0 : 0.5);
                l.Opacity = 0;
                l.Visibility = Visibility.Collapsed;
                l.IsHitTestVisible = false;
            }

            Bind(WifiBtn, _wifi);
            Bind(BtBtn, _bt);
            Bind(AirBtn, _air);
            Bind(MuteBtn, _mute);
            Bind(SnipBtn, _snip);
            ShadowFx.Opacity = Theme.ShadowOpacity;
            Theme.Changed += OnThemeChanged;

            _collapseTimer.Tick += (_, _) => OnCollapseTick();
            _tempTimer.Tick += (_, _) => OnTempTick();
            _progressTimer.Tick += (_, _) => UpdateProgress();
            _fsTimer.Tick += (_, _) => CheckFullscreen();
            _mouseTimer.Tick += (_, _) => OnMouseTick();

            VolSlider.ValueChanged += OnVolSlider;
            VolSlider.UnavailableClick += () => { OpenUri("ms-settings:sound"); Collapse(); };
            BrightSlider.ValueChanged += v => _bright.Set((int)Math.Round(v * 100));
            BrightSlider.UnavailableClick += () => { OpenUri("ms-settings:display"); Collapse(); };
            BrightSlider.Available = false;
            BrightSlider.Opacity = 0.45;

            SetArt(null);
            ApplyMediaTexts();
        }

        // ===================================================================
        //  Окно
        // ===================================================================

        void OnSourceInitialized(object sender, EventArgs e)
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            int ex = Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE);
            // не в Alt+Tab, не в панели задач, не забирает фокус у твоих окон
            ex = (ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE) & ~Native.WS_EX_APPWINDOW;
            Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE, ex);
            HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        }

        IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_MOUSEACTIVATE = 0x0021, MA_NOACTIVATE = 3, WM_DISPLAYCHANGE = 0x007E, WM_SETTINGCHANGE = 0x001A;
            if (msg == WM_MOUSEACTIVATE) { handled = true; return (IntPtr)MA_NOACTIVATE; }
            if (msg == WM_DISPLAYCHANGE || msg == WM_SETTINGCHANGE)
                Dispatcher.InvokeAsync(PositionWindow, DispatcherPriority.Background);
            return IntPtr.Zero;
        }

        void PositionWindow()
        {
            if (_snapshotDir != null) { Left = -4000; Top = 0; return; }
            Left = Math.Round((SystemParameters.PrimaryScreenWidth - Width) / 2);
            Top = 0;
        }

        async void OnLoaded(object sender, RoutedEventArgs e)
        {
            PositionWindow();

            // появление: маленькая точка «распускается» в остров
            Island.Width = 40;
            Island.Height = 18;
            Stage.Opacity = 0;
            Anim.To(Stage, OpacityProperty, 1, 320, Anim.OutCubic);
            await Task.Delay(80);
            ApplySize();

            if (_snapshotDir != null) { await RunSnapshots(); return; }

            // звук
            _audio.Changed += (v, m, self) => Dispatcher.InvokeAsync(() => OnVolume(v, m, self));
            _audio.DefaultDeviceChanged += () => Dispatcher.InvokeAsync(() => { _audio.Rebind(); SyncAudio(); });
            _audio.Init();
            SyncAudio();

            // медиа
            _media.Updated += OnMediaUpdated;
            _ = _media.StartAsync();

            _fsTimer.Start();
            _leaveSince = Environment.TickCount64 + 1500; // после запуска остров видно ~2 с, потом прячется
            _mouseTimer.Start();

            // радио и яркость — не блокируют запуск
            _radio.Changed += () => Dispatcher.InvokeAsync(UpdateRadioUI);
            await _radio.InitAsync();
            UpdateRadioUI();

            ApplyBrightness(await _bright.InitAsync());

            TrimLater();
        }

        public void Cleanup()
        {
            Theme.Changed -= OnThemeChanged;
            _fsTimer.Stop();
            _mouseTimer.Stop();
            _progressTimer.Stop();
            _media.Dispose();
            _audio.Dispose();
        }

        async void TrimLater()
        {
            await Task.Delay(15000);
            try
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Native.EmptyWorkingSet(Process.GetCurrentProcess().Handle);
            }
            catch { }
        }

        // ===================================================================
        //  Тема
        // ===================================================================

        static void Bind(Button b, Tog t)
        {
            b.Background = t.Bg;
            b.Foreground = t.Fg;
        }

        static void SetTog(Tog t, Color? on, double ms = 240)
        {
            t.On = on;
            Anim.Tint(t.Bg, on ?? Theme.ToggleOff, ms);
            Anim.Tint(t.Fg, on.HasValue ? Colors.White : Theme.ToggleGlyph, ms);
        }

        static Color? If(bool on, Color c) => on ? c : null;

        void OnThemeChanged()
        {
            foreach (var t in new[] { _wifi, _bt, _air, _mute, _snip }) SetTog(t, t.On, 380);
            Anim.To(ShadowFx, System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, Theme.ShadowOpacity, 380, Anim.OutCubic);
        }

        /// <summary>Показать остров на пару секунд (например, чтобы было видно новую тему).</summary>
        public void Peek()
        {
            if (_hiddenFs || _hiddenUser || _snapshotDir != null) return;
            Reveal();
            _leaveSince = Environment.TickCount64 + 1300;
        }

        // ===================================================================
        //  Состояния и анимации
        // ===================================================================

        // есть что играть (даже на паузе) — показываем пилюлю с мини-кнопками
        IslandState BaseState() => _snap.HasSession ? IslandState.Compact : IslandState.Collapsed;

        FrameworkElement LayerOf(IslandState s) => s switch
        {
            IslandState.Compact => CompactLayer,
            IslandState.Hud => HudLayer,
            IslandState.Banner => BannerLayer,
            IslandState.Expanded => ExpandedLayer,
            _ => null
        };

        void GoTo(IslandState s)
        {
            var old = _state;
            _state = s;

            if (old != s)
            {
                var oldL = LayerOf(old);
                var newL = LayerOf(s);
                if (oldL != null && oldL != newL) HideLayer(oldL);
                if (newL != null && newL != oldL) ShowLayer(newL, s == IslandState.Expanded ? 110 : 140);
            }

            if (s == IslandState.Expanded && old != IslandState.Expanded) OnExpanded();
            if (s == IslandState.Expanded || s == IslandState.Hud || s == IslandState.Banner) Reveal();

            ApplySize();
            UpdateEqualizers();
            UpdateProgressTimer();
        }

        void Collapse()
        {
            _collapseTimer.Stop();
            GoTo(BaseState());
        }

        Size TargetSize()
        {
            double w, h;
            switch (_state)
            {
                case IslandState.Compact: w = CompactW; h = CompactH; break;
                case IslandState.Hud: w = HudW; h = PillH; break;
                case IslandState.Banner: w = BannerW; h = BannerH; break;
                case IslandState.Expanded:
                    w = ExpandedW;
                    ExpandedLayer.Measure(new Size(ExpandedW, double.PositiveInfinity));
                    h = Math.Ceiling(ExpandedLayer.DesiredSize.Height);
                    break;
                default: w = CollapsedW; h = PillH; break;
            }
            if (_hover && (_state == IslandState.Collapsed || _state == IslandState.Compact))
            {
                w += 12;
                h += 3;
            }
            return new Size(w, h);
        }

        void ApplySize()
        {
            var t = TargetSize();
            bool grow = t.Width * t.Height >= Island.ActualWidth * Island.ActualHeight - 1;
            var ease = grow ? Anim.Bouncy : Anim.Soft;
            double ms = grow ? 640 : 500;
            Anim.To(Island, WidthProperty, t.Width, ms, ease);
            Anim.To(Island, HeightProperty, t.Height, ms, ease);

            bool big = _state == IslandState.Expanded || _state == IslandState.Banner;
            Anim.To(IslandShadow, OpacityProperty, big ? 1 : 0, big ? 380 : 160, Anim.OutCubic);
            Anim.To(Glow, OpacityProperty, big ? 1 : 0, 420, Anim.OutCubic);
        }

        void ShowLayer(FrameworkElement l, double delay)
        {
            l.Visibility = Visibility.Visible;
            l.IsHitTestVisible = true;
            var st = (ScaleTransform)l.RenderTransform;
            if (l.Opacity < 0.05)
            {
                st.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                st.ScaleX = st.ScaleY = 0.9;
            }
            Anim.To(l, OpacityProperty, 1, 260, Anim.OutCubic, delay);
            Anim.To(st, ScaleTransform.ScaleXProperty, 1, 560, Anim.Bouncy, delay);
            Anim.To(st, ScaleTransform.ScaleYProperty, 1, 560, Anim.Bouncy, delay);
        }

        void HideLayer(FrameworkElement l)
        {
            l.IsHitTestVisible = false;
            var st = (ScaleTransform)l.RenderTransform;
            Anim.To(st, ScaleTransform.ScaleXProperty, 0.94, 160, Anim.OutCubic);
            Anim.To(st, ScaleTransform.ScaleYProperty, 0.94, 160, Anim.OutCubic);
            Anim.To(l, OpacityProperty, 0, 130, Anim.OutCubic, 0, () =>
            {
                if (!l.IsHitTestVisible) l.Visibility = Visibility.Collapsed;
            });
        }

        void Island_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            double w = Island.ActualWidth, h = Island.ActualHeight;
            double r = Math.Min(h / 2, 42);
            Island.CornerRadius = new CornerRadius(r);
            _islandClip.Rect = new Rect(0, 0, w, h);
            _islandClip.RadiusX = r;
            _islandClip.RadiusY = r;
        }

        void ShowTemp(IslandState s, int ms)
        {
            if (_state == IslandState.Expanded || _hiddenFs || _hiddenUser) return;
            _tempTimer.Stop();
            _tempTimer.Interval = TimeSpan.FromMilliseconds(ms);
            _tempTimer.Start();
            GoTo(s);
        }

        void OnTempTick()
        {
            _tempTimer.Stop();
            if (_state != IslandState.Hud && _state != IslandState.Banner) return;
            if (_hover) { _tempTimer.Start(); return; } // пока мышь на острове — не прячем
            GoTo(BaseState());
        }

        void OnCollapseTick()
        {
            _collapseTimer.Stop();
            _collapseTimer.Interval = TimeSpan.FromMilliseconds(650);
            if (_state != IslandState.Expanded) return;
            if (Island.IsMouseOver || Mouse.Captured != null) return;
            GoTo(BaseState());
        }

        void UpdateEqualizers()
        {
            bool on = _snap.HasSession && _snap.IsPlaying && !_hiddenFs && !_hiddenUser && _revealed;
            CompactEq.IsActive = on && _state == IslandState.Compact;
            BannerEq.IsActive = on && _state == IslandState.Banner;
            ExpandedEq.IsActive = on && _state == IslandState.Expanded;
        }

        void UpdateProgressTimer()
        {
            bool run = _state == IslandState.Expanded && _snap.HasSession && _snap.IsPlaying &&
                       _snap.Duration.TotalSeconds >= 1 && !_hiddenFs && !_hiddenUser;
            if (run) { if (!_progressTimer.IsEnabled) _progressTimer.Start(); }
            else _progressTimer.Stop();
        }

        void OnExpanded()
        {
            SyncAudio();
            UpdateRadioUI();
            UpdateProgress();
            _ = RefreshBrightnessAsync();
        }

        // ===================================================================
        //  Мышь
        // ===================================================================

        void Island_MouseEnter(object sender, MouseEventArgs e)
        {
            _hover = true;
            _collapseTimer.Stop();
            if (_state == IslandState.Collapsed || _state == IslandState.Compact) ApplySize();
        }

        void Island_MouseLeave(object sender, MouseEventArgs e)
        {
            _hover = false;
            if (_state == IslandState.Expanded)
            {
                _collapseTimer.Stop();
                _collapseTimer.Interval = TimeSpan.FromMilliseconds(650);
                _collapseTimer.Start();
            }
            else if (_state == IslandState.Collapsed || _state == IslandState.Compact) ApplySize();
        }

        void Island_Click(object sender, MouseButtonEventArgs e)
        {
            if (_state != IslandState.Expanded)
            {
                _tempTimer.Stop();
                GoTo(IslandState.Expanded);
            }
        }

        void Island_RightClick(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            (Application.Current as App)?.ShowMenu();
        }

        void Island_Wheel(object sender, MouseWheelEventArgs e)
        {
            e.Handled = true;
            if (!_audio.Available) return;
            float v = Math.Clamp(_audio.Volume + 0.02f * e.Delta / 120f, 0f, 1f);
            _audio.SetVolume(v);
            if (_audio.Muted && e.Delta > 0) _audio.SetMute(false);
            VolSlider.SetLevel(v, true);
            UpdateMuteUI(_audio.Muted);
            if (_state != IslandState.Expanded) ShowHud(v, _audio.Muted);
        }

        public void TrayActivate()
        {
            if (_hiddenUser) { SetUserHidden(false); return; }
            if (_hiddenFs) return;
            GoTo(IslandState.Expanded);
            if (!_hover)
            {
                _collapseTimer.Stop();
                _collapseTimer.Interval = TimeSpan.FromMilliseconds(3000);
                _collapseTimer.Start();
            }
        }

        // ===================================================================
        //  Спрятан, пока не наведёшь мышь
        // ===================================================================

        void OnMouseTick()
        {
            if (_hiddenFs || _hiddenUser || _hwnd == IntPtr.Zero) return;
            if (!Native.GetCursorPos(out var pt) || !Native.GetWindowBounds(_hwnd, out var wr)) return;

            var dpi = VisualTreeHelper.GetDpi(this);
            double sx = dpi.DpiScaleX, sy = dpi.DpiScaleY;
            double cx = (wr.Left + wr.Right) / 2.0;
            double halfW = Island.ActualWidth * sx / 2;
            double bottom = wr.Top + (6 + Island.ActualHeight) * sy;
            double pad = (_revealed ? 8 : 0) * sx;   // запас, чтобы не мигало на границе
            bool over = pt.X >= cx - halfW - pad && pt.X <= cx + halfW + pad &&
                        pt.Y >= wr.Top && pt.Y <= bottom + pad;

            // курсор далеко — проверяем реже, рядом — чаще (быстрая реакция, почти ноль нагрузки)
            var iv = TimeSpan.FromMilliseconds(pt.Y > bottom + 150 * sy ? 150 : 40);
            if (_mouseTimer.Interval != iv) _mouseTimer.Interval = iv;

            long now = Environment.TickCount64;

            // нажал кнопку на острове — не открываем виджет сам, пока курсор не уйдёт
            if (Mouse.Captured != null) _dwellBlocked = true;
            else if (!over) _dwellBlocked = false;

            // задержал курсор на острове ~2 с — открываем большой виджет
            if (_revealed && over && !_dwellBlocked && _state != IslandState.Expanded)
            {
                if (_overSince == 0) _overSince = now;
                else if (now - _overSince >= DwellOpenMs)
                {
                    _overSince = 0;
                    _tempTimer.Stop();
                    GoTo(IslandState.Expanded);
                }
            }
            else _overSince = 0;

            // страховка: курсор ушёл с раскрытого виджета, а окно этого не заметило — сворачиваем
            if (_state == IslandState.Expanded && !over && Mouse.Captured == null && !_collapseTimer.IsEnabled)
            {
                if (_outSince == 0) _outSince = now;
                else if (now - _outSince >= 1500) { _outSince = 0; GoTo(BaseState()); }
            }
            else _outSince = 0;

            bool keep = over || Mouse.Captured != null ||
                        _state == IslandState.Expanded || _state == IslandState.Hud || _state == IslandState.Banner;

            if (keep)
            {
                _leaveSince = 0;
                if (!_revealed)
                {
                    // маленькая задержка: быстрый клик по вкладке под островом всё равно проходит
                    if (_nearSince == 0) _nearSince = now;
                    else if (now - _nearSince >= 140) Reveal();
                }
            }
            else
            {
                _nearSince = 0;
                if (_revealed)
                {
                    if (_leaveSince == 0) _leaveSince = now;
                    else if (now >= _leaveSince + 350) Conceal();
                }
            }
        }

        void Reveal()
        {
            _nearSince = 0;
            _leaveSince = 0;
            if (_revealed) return;
            _revealed = true;
            Native.SetClickThrough(_hwnd, false);
            Anim.To(FloatShift, TranslateTransform.YProperty, 0, 560, Anim.Bouncy);
            Anim.To(Floater, OpacityProperty, 1, 200, Anim.OutCubic);
            Anim.To(GhostHandle, OpacityProperty, 0, 120, Anim.OutCubic);
            UpdateEqualizers();
        }

        void Conceal()
        {
            _leaveSince = 0;
            if (!_revealed) return;
            _revealed = false;
            _hover = false;
            Native.SetClickThrough(_hwnd, true);
            Anim.To(FloatShift, TranslateTransform.YProperty, -(Island.ActualHeight + 16), 260, Anim.OutCubic);
            Anim.To(Floater, OpacityProperty, 0, 220, Anim.OutCubic);
            Anim.To(GhostHandle, OpacityProperty, 1, 260, Anim.OutCubic, 160);
            UpdateEqualizers();
            if (_state == IslandState.Collapsed || _state == IslandState.Compact) ApplySize();
        }

        // ===================================================================
        //  Медиа
        // ===================================================================

        void OnMediaUpdated(MediaSnapshot s) // фон
        {
            bool artChanged = !ReferenceEquals(s.Thumbnail, _bgThumb);
            BitmapSource art = null;
            Color? accent = null;
            if (artChanged)
            {
                _bgThumb = s.Thumbnail;
                if (s.Thumbnail != null)
                {
                    try { art = ArtTools.Decode(s.Thumbnail, 256); accent = ArtTools.Vivid(art); }
                    catch { art = null; accent = null; }
                }
            }
            Dispatcher.InvokeAsync(() => ApplyMedia(s, artChanged, art, accent));
        }

        void ApplyMedia(MediaSnapshot s, bool artChanged, BitmapSource art, Color? accent)
        {
            _snap = s;

            if (artChanged)
            {
                SetArt(art);
                AnimateAccent(accent ?? DefaultAccent);
            }
            ApplyMediaTexts();
            if (!_seeking) SetPlayIcon(s.IsPlaying);
            UpdateProgress();

            string key = s.HasSession ? s.AppId + "|" + s.Title + "|" + s.Artist : null;
            bool trackChanged = key != _lastKey;
            _lastKey = key;

            bool first = _firstMedia;
            _firstMedia = false;

            if (_state == IslandState.Expanded)
                ApplySize(); // высота могла поменяться (появилась/пропала полоса перемотки)
            else if (!first && trackChanged && s.HasSession && s.IsPlaying && !string.IsNullOrWhiteSpace(s.Title))
                ShowTemp(IslandState.Banner, 3400); // новый трек — показываем баннер как Live Activity
            else if (_state == IslandState.Collapsed || _state == IslandState.Compact)
                GoTo(BaseState());

            UpdateEqualizers();
            UpdateProgressTimer();
        }

        void ApplyMediaTexts()
        {
            var s = _snap;
            string title = !s.HasSession ? "Ничего не играет"
                         : string.IsNullOrWhiteSpace(s.Title) ? s.AppName : s.Title;
            string artist = !s.HasSession ? "Включи музыку в YouTube или Telegram"
                          : string.IsNullOrWhiteSpace(s.Artist) ? s.AppName : s.Artist;

            ExpTitle.Text = BannerTitle.Text = title;
            ExpArtist.Text = BannerArtist.Text = artist;
            SourceText.Text = s.HasSession ? s.AppName : "Медиа";
            PrevBtn.IsEnabled = NextBtn.IsEnabled = PlayBtn.IsEnabled =
                MiniPrevBtn.IsEnabled = MiniNextBtn.IsEnabled = MiniPlayBtn.IsEnabled = s.HasSession;
            ProgressRow.Visibility = s.HasSession && s.Duration.TotalSeconds >= 1 ? Visibility.Visible : Visibility.Collapsed;
        }

        void SetArt(BitmapSource art)
        {
            Brush b = null;
            if (art != null)
            {
                var ib = new ImageBrush(art) { Stretch = Stretch.UniformToFill };
                ib.Freeze();
                b = ib;
            }
            Apply(CompactArt, CompactArtGlyph);
            Apply(BannerArt, BannerArtGlyph);
            Apply(ExpandedArt, ExpandedArtGlyph);

            void Apply(Border border, TextBlock glyph)
            {
                border.Background = b ?? _placeholder;
                glyph.Visibility = b == null ? Visibility.Visible : Visibility.Collapsed;
                Anim.From(border, OpacityProperty, 0.35, 1, 380, Anim.OutCubic);
            }
        }

        void AnimateAccent(Color c)
        {
            var d = TimeSpan.FromMilliseconds(600);
            _accent.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(c, d) { EasingFunction = Anim.OutCubic });
            _glowStop.BeginAnimation(GradientStop.ColorProperty,
                new ColorAnimation(Color.FromArgb(0x55, c.R, c.G, c.B), d) { EasingFunction = Anim.OutCubic });
        }

        void SetPlayIcon(bool playing)
        {
            if (_iconPlaying == playing) return;
            _iconPlaying = playing;
            PlayIcon.Visibility = playing ? Visibility.Collapsed : Visibility.Visible;
            PauseIcon.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
            MiniPlayIcon.Visibility = PlayIcon.Visibility;
            MiniPauseIcon.Visibility = PauseIcon.Visibility;
            Anim.From(PlayGlyphScale, ScaleTransform.ScaleXProperty, 0.55, 1, 480, Anim.Bouncy);
            Anim.From(PlayGlyphScale, ScaleTransform.ScaleYProperty, 0.55, 1, 480, Anim.Bouncy);
            Anim.From(MiniGlyphScale, ScaleTransform.ScaleXProperty, 0.5, 1, 480, Anim.Bouncy);
            Anim.From(MiniGlyphScale, ScaleTransform.ScaleYProperty, 0.5, 1, 480, Anim.Bouncy);
        }

        void Prev_Click(object sender, RoutedEventArgs e) => _media.Prev();
        void Next_Click(object sender, RoutedEventArgs e) => _media.Next();

        void Play_Click(object sender, RoutedEventArgs e)
        {
            SetPlayIcon(!_iconPlaying); // мгновенный отклик, не ждём приложение
            _media.PlayPause();
        }

        // ---------- полоса перемотки ----------

        void UpdateProgress()
        {
            if (_seeking) return;
            var s = _snap;
            if (!s.HasSession || s.Duration.TotalSeconds < 1) return;
            var pos = s.Position;
            if (s.IsPlaying && s.UpdatedAt.Year > 2000)
            {
                var el = DateTimeOffset.Now - s.UpdatedAt;
                if (el > TimeSpan.Zero) pos += el;
            }
            if (pos > s.Duration) pos = s.Duration;
            if (pos < TimeSpan.Zero) pos = TimeSpan.Zero;
            ShowSeek(pos.TotalSeconds / s.Duration.TotalSeconds);
        }

        void ShowSeek(double f)
        {
            f = Math.Clamp(f, 0, 1);
            var d = _snap.Duration;
            var pos = TimeSpan.FromSeconds(f * d.TotalSeconds);
            SeekFill.Width = f * SeekArea.ActualWidth;
            PosText.Text = Fmt(pos);
            DurText.Text = "-" + Fmt(d - pos);
        }

        static string Fmt(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
        }

        double SeekFraction(MouseEventArgs e)
        {
            double w = SeekArea.ActualWidth;
            return w <= 0 ? 0 : Math.Clamp(e.GetPosition(SeekArea).X / w, 0, 1);
        }

        void Seek_Down(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (!_snap.HasSession || _snap.Duration.TotalSeconds < 1) return;
            _seeking = true;
            SeekArea.CaptureMouse();
            ShowSeek(SeekFraction(e));
        }

        void Seek_Move(object sender, MouseEventArgs e)
        {
            if (_seeking) ShowSeek(SeekFraction(e));
        }

        void Seek_Up(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (!_seeking) return;
            double f = SeekFraction(e);
            _seeking = false;
            SeekArea.ReleaseMouseCapture();
            var target = TimeSpan.FromSeconds(f * _snap.Duration.TotalSeconds);
            _snap.Position = target;
            _snap.UpdatedAt = DateTimeOffset.Now;
            _media.Seek(target);
            UpdateProgress();
        }

        void Seek_Lost(object sender, MouseEventArgs e)
        {
            if (!_seeking) return;
            _seeking = false;
            UpdateProgress();
        }

        void Seek_Enter(object sender, MouseEventArgs e)
        {
            Anim.To(SeekTrack, HeightProperty, 8, 260, Anim.Spring);
            Anim.To(SeekFill, HeightProperty, 8, 260, Anim.Spring);
        }

        void Seek_Leave(object sender, MouseEventArgs e)
        {
            Anim.To(SeekTrack, HeightProperty, 5, 260, Anim.Spring);
            Anim.To(SeekFill, HeightProperty, 5, 260, Anim.Spring);
        }

        // ===================================================================
        //  Громкость / яркость
        // ===================================================================

        void SyncAudio()
        {
            bool ok = _audio.Available;
            VolSlider.Available = ok;
            VolSlider.Opacity = ok ? 1 : 0.45;
            if (!ok) return;
            if (!VolSlider.IsDragging) VolSlider.SetLevel(_audio.Volume, false);
            UpdateMuteUI(_audio.Muted);
        }

        void OnVolSlider(double v)
        {
            if (!_audio.Available) return;
            _audio.SetVolume((float)v);
            if (v > 0.005 && _audio.Muted) _audio.SetMute(false);
            UpdateMuteUI(_audio.Muted);
        }

        void OnVolume(float v, bool muted, bool fromSelf)
        {
            if (!VolSlider.IsDragging) VolSlider.SetLevel(v, true);
            UpdateMuteUI(muted);
            if (!fromSelf && _state != IslandState.Expanded) ShowHud(v, muted);
        }

        void UpdateMuteUI(bool muted)
        {
            string g = muted ? "" : "";
            if (!Equals(MuteBtn.Content, g)) MuteBtn.Content = g;
            if (VolSlider.Glyph != g) VolSlider.Glyph = g;
            SetTog(_mute, If(muted, Red));
        }

        void ShowHud(float v, bool muted)
        {
            HudIcon.Text = muted ? "" : "";
            HudText.Text = muted ? "Выкл" : $"{Math.Round(v * 100)}%";
            double w = muted ? 0 : v * HudBarW;
            if (_state == IslandState.Hud) Anim.To(HudFill, WidthProperty, w, 180, Anim.OutCubic);
            else { HudFill.BeginAnimation(WidthProperty, null); HudFill.Width = w; }
            ShowTemp(IslandState.Hud, 1600);
        }

        void ApplyBrightness(int b)
        {
            bool ok = b >= 0 && _bright.Available;
            BrightSlider.Available = ok;
            BrightSlider.Opacity = ok ? 1 : 0.45;
            if (ok && !BrightSlider.IsDragging) BrightSlider.SetLevel(b / 100.0, true);
        }

        async Task RefreshBrightnessAsync()
        {
            if (!_bright.Available) return;
            int b = await _bright.ReadAsync();
            if (b >= 0) ApplyBrightness(b);
        }

        // ===================================================================
        //  Переключатели
        // ===================================================================

        void UpdateRadioUI()
        {
            SetTog(_wifi, If(_radio.WifiOn == true, Blue));
            SetTog(_bt, If(_radio.BtOn == true, Blue));
        }

        async void Wifi_Click(object sender, RoutedEventArgs e)
        {
            if (_radio.WifiOn == null) { OpenUri("ms-settings:network-wifi"); Collapse(); return; }
            SetTog(_wifi, If(_radio.WifiOn != true, Blue));
            if (!await _radio.ToggleWifiAsync()) { OpenUri("ms-settings:network-wifi"); Collapse(); }
            await Task.Delay(900);
            UpdateRadioUI();
        }

        async void Bt_Click(object sender, RoutedEventArgs e)
        {
            if (_radio.BtOn == null) { OpenUri("ms-settings:bluetooth"); Collapse(); return; }
            SetTog(_bt, If(_radio.BtOn != true, Blue));
            if (!await _radio.ToggleBtAsync()) { OpenUri("ms-settings:bluetooth"); Collapse(); }
            await Task.Delay(900);
            UpdateRadioUI();
        }

        void Air_Click(object sender, RoutedEventArgs e)
        {
            OpenUri("ms-settings:network-airplanemode");
            Collapse();
        }

        void Mute_Click(object sender, RoutedEventArgs e)
        {
            if (!_audio.Available) return;
            bool m = !_audio.Muted;
            _audio.SetMute(m);
            UpdateMuteUI(m);
        }

        async void Snip_Click(object sender, RoutedEventArgs e)
        {
            Collapse();
            await Task.Delay(450); // чтобы остров не попал в скриншот
            OpenUri("ms-screenclip:");
        }

        static void OpenUri(string uri)
        {
            try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
            catch (Exception ex) { App.Log(ex); }
        }

        // ===================================================================
        //  Полноэкранные приложения / скрытие
        // ===================================================================

        void CheckFullscreen()
        {
            bool fs = Native.IsForegroundFullscreen(_hwnd);
            if (fs == _hiddenFs) return;
            _hiddenFs = fs;
            ApplyHidden();
        }

        public void SetUserHidden(bool hidden)
        {
            _hiddenUser = hidden;
            ApplyHidden();
        }

        void ApplyHidden()
        {
            bool hide = _hiddenFs || _hiddenUser;
            if (hide)
            {
                _tempTimer.Stop();
                if (_state != IslandState.Collapsed && _state != IslandState.Compact) GoTo(BaseState());
                Stage.IsHitTestVisible = false;
                // полностью убираем окно, чтобы не мешать играм и видео
                Anim.To(Stage, OpacityProperty, 0, 220, Anim.OutCubic, 0, () => { if (_hiddenFs || _hiddenUser) Hide(); });
            }
            else
            {
                if (!IsVisible) Show();
                Stage.IsHitTestVisible = true;
                Anim.To(Stage, OpacityProperty, 1, 280, Anim.OutCubic);
            }
            UpdateEqualizers();
            UpdateProgressTimer();
        }

        // ===================================================================
        //  --snapshot: отрисовка всех состояний в PNG (для проверки дизайна)
        // ===================================================================

        async Task RunSnapshots()
        {
            Directory.CreateDirectory(_snapshotDir);
            Theme.Set(false, save: false);
            var art = ArtTools.Demo();
            var s = new MediaSnapshot
            {
                HasSession = true,
                AppId = "chrome.exe",
                AppName = "Google Chrome",
                Title = "Lo-fi beats to relax / study to",
                Artist = "Lofi Girl",
                IsPlaying = true,
                CanSeek = true,
                CanNext = true,
                CanPrev = true,
                Duration = TimeSpan.FromSeconds(214),
                Position = TimeSpan.FromSeconds(83),
                UpdatedAt = DateTimeOffset.Now
            };
            ApplyMedia(s, true, art, ArtTools.Vivid(art));
            VolSlider.SetLevel(0.62, false);
            BrightSlider.Available = true;
            BrightSlider.Opacity = 1;
            BrightSlider.SetLevel(0.8, false);
            SetTog(_wifi, Blue, 1);

            await Shot("1-compact");
            SetPlayIcon(false);
            await Shot("1b-compact-paused");
            SetPlayIcon(true);
            GoTo(IslandState.Collapsed);
            await Shot("0-idle");
            ShowHud(0.62f, false);
            await Shot("2-hud");
            ShowTemp(IslandState.Banner, 8000);
            await Shot("3-banner");
            GoTo(IslandState.Expanded);
            await Shot("4-expanded");

            Theme.Set(true, save: false);
            await Shot("4-expanded-light");
            GoTo(IslandState.Compact);
            await Shot("1-compact-light");
            ShowTemp(IslandState.Banner, 8000);
            await Shot("3-banner-light");
            ShowHud(0.62f, false);
            await Shot("2-hud-light");

            Application.Current.Shutdown();
        }

        async Task Shot(string name)
        {
            await Task.Delay(1100);
            int w = (int)ActualWidth, h = (int)ActualHeight;
            var rtb = new RenderTargetBitmap(w * 2, h * 2, 192, 192, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
                dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0xC9, 0xD6, 0xFF), Color.FromRgb(0xE2, 0xE2, 0xE2), 90), null, new Rect(0, 0, w, h));
            rtb.Render(dv);
            rtb.Render(Stage);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = File.Create(Path.Combine(_snapshotDir, name + ".png"));
            enc.Save(fs);
        }
    }
}
