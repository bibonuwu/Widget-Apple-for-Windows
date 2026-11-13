using System;
using System.Runtime.InteropServices;
using System.Text;

namespace WinIsland
{
    internal static class Native
    {
        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_APPWINDOW = 0x00040000;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const int WS_EX_TRANSPARENT = 0x00000020;
        const int WS_EX_TOPMOST = 0x00000008;
        const uint GW_HWNDPREV = 3;
        const int DWMWA_CLOAKED = 14;
        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010, SWP_NOOWNERZORDER = 0x0200;

        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out int value, int size);

        /// <summary>
        /// Поднять окно наверх слоя «поверх всех», не забирая фокус.
        /// Работает и когда флаг WS_EX_TOPMOST у окна остался, а само оно уже провалилось под обычные окна.
        /// </summary>
        public static void BringToTopmost(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return;
            SetWindowPos(hWnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        }

        /// <summary>
        /// true — окно оказалось под обычным (не «поверх всех») видимым окном.
        /// Смотрим реальный Z-порядок, а не флаг: Windows иногда оставляет WS_EX_TOPMOST,
        /// хотя окно уже стоит среди обычных окон.
        /// </summary>
        public static bool IsBuried(IntPtr self)
        {
            if (self == IntPtr.Zero) return false;
            int guard = 0;
            for (IntPtr h = GetWindow(self, GW_HWNDPREV); h != IntPtr.Zero && guard++ < 1024; h = GetWindow(h, GW_HWNDPREV))
            {
                if ((GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0 || !IsWindowVisible(h)) continue;
                if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) continue;
                switch (ClassName(h))
                {
                    case "Windows.UI.Core.CoreWindow":       // меню «Пуск», поиск — живут в слое выше, их не перекрыть
                    case "XamlExplorerHostIslandWindow":     // Alt+Tab, представление задач
                        continue;
                }
                return true;
            }
            return false;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll", EntryPoint = "GetWindowRect")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetWindowBounds(IntPtr hWnd, out RECT rect);

        /// <summary>Окно пропускает все клики насквозь (к вкладкам браузера под ним).</summary>
        public static void SetClickThrough(IntPtr hWnd, bool on)
        {
            if (hWnd == IntPtr.Zero) return;
            int ex = GetWindowLong(hWnd, GWL_EXSTYLE);
            int nx = on ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT;
            if (nx != ex) SetWindowLong(hWnd, GWL_EXSTYLE, nx);
        }
        const uint MONITOR_DEFAULTTONEAREST = 2;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] public static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] public static extern int SetWindowLong(IntPtr hWnd, int index, int value);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool IsZoomed(IntPtr hWnd);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr hWnd, StringBuilder sb, int max);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO mi);
        [DllImport("psapi.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool EmptyWorkingSet(IntPtr hProcess);

        static readonly StringBuilder ClassBuf = new StringBuilder(128);

        static string ClassName(IntPtr hWnd)
        {
            ClassBuf.Clear();
            GetClassNameW(hWnd, ClassBuf, ClassBuf.Capacity);
            return ClassBuf.ToString();
        }

        /// <summary>Активное окно развёрнуто на весь экран (игра, видео F11) на том же мониторе, что и остров.</summary>
        public static bool IsForegroundFullscreen(IntPtr self)
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == self) return false;

            switch (ClassName(fg))
            {
                case "WorkerW":
                case "Progman":
                case "Shell_TrayWnd":
                case "Shell_SecondaryTrayWnd":
                case "Windows.UI.Core.CoreWindow":
                case "XamlExplorerHostIslandWindow":
                    return false;
            }

            if (IsZoomed(fg)) return false; // обычное развёрнутое окно — не полноэкранный режим
            if (!GetWindowRect(fg, out RECT r)) return false;

            IntPtr mon = MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST);
            if (mon != MonitorFromWindow(self, MONITOR_DEFAULTTONEAREST)) return false;

            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(mon, ref mi)) return false;

            return r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top &&
                   r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
        }
    }
}
