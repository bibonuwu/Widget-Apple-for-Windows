using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using WinIsland.Controls;

namespace WinIsland
{
    /// <summary>
    /// Тёмная / светлая тема. Все цвета лежат в ресурсах приложения (DynamicResource),
    /// при смене темы кисти не пересоздаются, а плавно перетекают в новый цвет.
    /// Выбор хранится в HKCU\Software\WinIsland\Theme.
    /// </summary>
    internal static class Theme
    {
        const string SettingsKey = @"Software\WinIsland";
        const double FadeMs = 380;

        public static bool IsLight { get; private set; }
        public static event Action Changed;

        // ключ ресурса → цвет в тёмной и светлой теме (ARGB)
        static readonly (string Key, uint Dark, uint Light)[] Palette =
        {
            ("IslandBg",          0xFF000000, 0xFFFFFFFF),
            ("IslandStroke",      0x00FFFFFF, 0x1F000000),
            ("Fg",                0xFFFFFFFF, 0xFF1C1C1E),
            ("Secondary",         0xFF98989F, 0xFF6E6E73),
            ("HudTrack",          0x40FFFFFF, 0x24000000),
            ("SeekTrack",         0x38FFFFFF, 0x1F000000),
            ("SeekFill",          0xF2FFFFFF, 0xE61C1C1E),
            ("SliderTrack",       0x3DFFFFFF, 0xFFEDEDF0),
            ("SliderFill",        0xFFF5F5F7, 0xFF2C2C2E),
            ("SliderIconOnFill",  0xFF48484C, 0xFFF5F5F7),
            ("SliderIconOnTrack", 0xFFF5F5F7, 0xFF48484C),
            ("HandleBg",          0xC8000000, 0xEBFFFFFF),
            ("HandleStroke",      0x73FFFFFF, 0x47000000),
            ("MenuBg",            0xF51C1C1E, 0xF7FBFBFD),
            ("MenuStroke",        0x26FFFFFF, 0x1A000000),
            ("MenuFg",            0xFFF2F2F7, 0xFF1C1C1E),
            ("MenuHover",         0x26FFFFFF, 0x12000000),
            ("MenuSep",           0x22FFFFFF, 0x17000000),
            ("TipBg",             0xF52C2C2E, 0xF7FFFFFF),
        };

        // заглушка обложки — градиент из двух цветов
        static readonly (uint Dark, uint Light) PlaceholderA = (0xFF3A3A3E, 0xFFE5E5EA);
        static readonly (uint Dark, uint Light) PlaceholderB = (0xFF1F1F23, 0xFFD1D1D6);

        /// <summary>Фон выключенного круглого переключателя (Wi-Fi, Bluetooth…).</summary>
        public static Color ToggleOff => C(IsLight ? 0xFFEDEDF0 : 0xFF333336);
        /// <summary>Иконка на выключенном переключателе.</summary>
        public static Color ToggleGlyph => C(IsLight ? 0xFF1C1C1E : 0xFFFFFFFF);
        public static double ShadowOpacity => IsLight ? 0.22 : 0.55;

        /// <summary>Загрузить сохранённую тему и положить кисти в ресурсы. Вызывать до создания окон.</summary>
        public static void Init()
        {
            IsLight = Load();
            Apply(false);
        }

        /// <param name="save">false — только показать (для --snapshot), выбор пользователя не трогать.</param>
        public static void Set(bool light, bool save = true)
        {
            if (light == IsLight) return;
            IsLight = light;
            if (save) Save(light);
            Apply(true);
        }

        static void Apply(bool animate)
        {
            var res = Application.Current.Resources;
            foreach (var p in Palette)
            {
                var c = C(IsLight ? p.Light : p.Dark);
                if (animate && res[p.Key] is SolidColorBrush b && !b.IsFrozen) Anim.Tint(b, c, FadeMs);
                else res[p.Key] = new SolidColorBrush(c);
            }

            Color a = C(IsLight ? PlaceholderA.Light : PlaceholderA.Dark);
            Color z = C(IsLight ? PlaceholderB.Light : PlaceholderB.Dark);
            if (animate && res["ArtPlaceholder"] is LinearGradientBrush g && !g.IsFrozen && g.GradientStops.Count == 2)
            {
                Fade(g.GradientStops[0], a);
                Fade(g.GradientStops[1], z);
            }
            else
            {
                g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
                g.GradientStops.Add(new GradientStop(a, 0));
                g.GradientStops.Add(new GradientStop(z, 1));
                res["ArtPlaceholder"] = g;
            }

            Changed?.Invoke();
        }

        static void Fade(GradientStop s, Color to) =>
            s.BeginAnimation(GradientStop.ColorProperty,
                new ColorAnimation(to, TimeSpan.FromMilliseconds(FadeMs)) { EasingFunction = Anim.OutCubic });

        static Color C(uint argb) =>
            Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

        static bool Load()
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(SettingsKey);
                return k?.GetValue("Theme") as string == "light";
            }
            catch { return false; }
        }

        static void Save(bool light)
        {
            try
            {
                using var k = Registry.CurrentUser.CreateSubKey(SettingsKey);
                k.SetValue("Theme", light ? "light" : "dark");
            }
            catch (Exception ex) { App.Log(ex); }
        }
    }
}
