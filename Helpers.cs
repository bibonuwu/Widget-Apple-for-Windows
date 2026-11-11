using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using Microsoft.Win32;

namespace WinIsland
{
    /// <summary>Автозапуск через HKCU\...\Run (без прав администратора).</summary>
    internal static class Autostart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        // сюда Windows пишет «отключено» из Диспетчера задач / Параметров: пока флаг стоит, запись в Run не запускается
        const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        const string Name = "WinIsland";

        static string Command => $"\"{Environment.ProcessPath}\"";

        /// <summary>Запись есть и Windows её не отключила — то есть программа реально запустится при входе.</summary>
        public static bool IsEnabled
        {
            get
            {
                try
                {
                    using var k = Registry.CurrentUser.OpenSubKey(RunKey);
                    if (!(k?.GetValue(Name) is string cmd) || cmd.Length == 0) return false;
                    using var a = Registry.CurrentUser.OpenSubKey(ApprovedKey);
                    // первый байт: чётный — включено, нечётный — отключено пользователем
                    return !(a?.GetValue(Name) is byte[] flag && flag.Length > 0 && (flag[0] & 1) != 0);
                }
                catch { return false; }
            }
        }

        public static void Set(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue(Name, Command, RegistryValueKind.String);
                    else k.DeleteValue(Name, false);
                }
                // снимаем «отключено», иначе запись в Run была бы, а запуска — нет
                using var a = Registry.CurrentUser.OpenSubKey(ApprovedKey, true);
                a?.DeleteValue(Name, false);
            }
            catch (Exception ex) { App.Log(ex); }
        }

        /// <summary>Автозапуск включён, но ведёт на файл, которого уже нет (программу перенесли) — переписываем на текущий путь.</summary>
        public static void Repair()
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(RunKey, true);
                if (!(k?.GetValue(Name) is string cmd)) return;
                cmd = cmd.Trim();
                int end = cmd.StartsWith("\"") ? cmd.IndexOf('"', 1) : -1;
                string path = end > 0 ? cmd.Substring(1, end - 1) : cmd;
                if (!System.IO.File.Exists(path)) k.SetValue(Name, Command, RegistryValueKind.String);
            }
            catch (Exception ex) { App.Log(ex); }
        }
    }

    /// <summary>Иконка в трее рисуется кодом — маленькая «пилюля» с камерой.</summary>
    internal static class TrayIcon
    {
        public static Icon Create()
        {
            using var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using var path = Pill(new RectangleF(1.5f, 9.5f, 29f, 13f));
                using (var fill = new SolidBrush(Color.FromArgb(255, 10, 10, 12))) g.FillPath(fill, path);
                using (var pen = new Pen(Color.FromArgb(235, 235, 235, 245), 1.6f)) g.DrawPath(pen, path);
                using (var lens = new SolidBrush(Color.FromArgb(255, 70, 105, 190))) g.FillEllipse(lens, 20.5f, 13f, 6f, 6f);
            }
            return Icon.FromHandle(bmp.GetHicon());
        }

        static GraphicsPath Pill(RectangleF r)
        {
            var p = new GraphicsPath();
            float d = r.Height;
            p.AddArc(r.X, r.Y, d, d, 90, 180);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 180);
            p.CloseFigure();
            return p;
        }
    }
}
