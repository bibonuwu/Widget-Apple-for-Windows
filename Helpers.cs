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
        const string Name = "WinIsland";

        public static bool IsEnabled
        {
            get
            {
                try
                {
                    using var k = Registry.CurrentUser.OpenSubKey(RunKey);
                    return k?.GetValue(Name) is string;
                }
                catch { return false; }
            }
        }

        public static void Set(bool on)
        {
            try
            {
                using var k = Registry.CurrentUser.CreateSubKey(RunKey);
                if (on) k.SetValue(Name, $"\"{Environment.ProcessPath}\"");
                else k.DeleteValue(Name, false);
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
