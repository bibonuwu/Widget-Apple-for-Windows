using System;
using System.Collections.Generic;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace WinIsland.Services
{
    /// <summary>
    /// Яркость: ноутбук — через WMI, внешний монитор — через DDC/CI.
    /// Всё в фоне, частые изменения слайдера склеиваются, чтобы не дёргать монитор.
    /// </summary>
    public sealed class BrightnessService
    {
        enum Mode { None, Wmi, Ddc }

        Mode _mode;
        readonly object _io = new object();
        readonly List<(IntPtr h, uint min, uint max)> _ddc = new List<(IntPtr, uint, uint)>();
        ManagementObject _wmiMethods;
        int _target = -1;
        int _busy;

        public bool Available => _mode != Mode.None;

        public Task<int> InitAsync() => Task.Run(() =>
        {
            lock (_io)
            {
                try { int v = WmiGet(); if (v >= 0) { _mode = Mode.Wmi; return v; } } catch { }
                try { int v = DdcInit(); if (v >= 0) { _mode = Mode.Ddc; return v; } } catch { }
                _mode = Mode.None;
                return -1;
            }
        });

        public Task<int> ReadAsync() => Task.Run(() =>
        {
            lock (_io)
            {
                try
                {
                    return _mode switch
                    {
                        Mode.Wmi => WmiGet(),
                        Mode.Ddc => DdcGet(),
                        _ => -1
                    };
                }
                catch { return -1; }
            }
        });

        public void Set(int pct)
        {
            if (_mode == Mode.None) return;
            Interlocked.Exchange(ref _target, Math.Clamp(pct, 0, 100));
            if (Interlocked.CompareExchange(ref _busy, 1, 0) == 0) Task.Run(Pump);
        }

        void Pump()
        {
            while (true)
            {
                int t = Interlocked.Exchange(ref _target, -1);
                if (t < 0)
                {
                    Volatile.Write(ref _busy, 0);
                    if (Volatile.Read(ref _target) >= 0 && Interlocked.CompareExchange(ref _busy, 1, 0) == 0) continue;
                    return;
                }
                try { lock (_io) Apply(t); } catch { }
            }
        }

        void Apply(int pct)
        {
            if (_mode == Mode.Wmi)
            {
                if (_wmiMethods == null)
                {
                    using var s = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
                    foreach (ManagementObject o in s.Get()) { _wmiMethods = o; break; }
                }
                _wmiMethods?.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)pct });
            }
            else if (_mode == Mode.Ddc)
            {
                foreach (var (h, min, max) in _ddc)
                    SetMonitorBrightness(h, (uint)(min + (max - min) * pct / 100.0));
            }
        }

        static int WmiGet()
        {
            using var s = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            using var col = s.Get();
            foreach (ManagementBaseObject o in col)
                using (o) return Convert.ToInt32(o["CurrentBrightness"]);
            return -1;
        }

        int DdcInit()
        {
            var monitors = new List<IntPtr>();
            MonitorEnumProc cb = (IntPtr hMon, IntPtr hdc, ref RECT r, IntPtr data) => { monitors.Add(hMon); return true; };
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero);
            GC.KeepAlive(cb);

            int first = -1;
            foreach (var hm in monitors)
            {
                if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hm, out uint n) || n == 0) continue;
                var arr = new PHYSICAL_MONITOR[n];
                if (!GetPhysicalMonitorsFromHMONITOR(hm, n, arr)) continue;
                foreach (var pm in arr)
                {
                    if (GetMonitorBrightness(pm.hPhysicalMonitor, out uint min, out uint cur, out uint max) && max > min)
                    {
                        _ddc.Add((pm.hPhysicalMonitor, min, max));
                        if (first < 0) first = (int)Math.Round((cur - min) * 100.0 / (max - min));
                    }
                    else DestroyPhysicalMonitor(pm.hPhysicalMonitor);
                }
            }
            return first;
        }

        int DdcGet()
        {
            if (_ddc.Count == 0) return -1;
            var (h, _, _) = _ddc[0];
            if (!GetMonitorBrightness(h, out uint min, out uint cur, out uint max) || max <= min) return -1;
            return (int)Math.Round((cur - min) * 100.0 / (max - min));
        }

        // ---------- interop ----------
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct PHYSICAL_MONITOR
        {
            public IntPtr hPhysicalMonitor;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szPhysicalMonitorDescription;
        }

        delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

        [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
        [DllImport("dxva2.dll", SetLastError = true)] static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint count);
        [DllImport("dxva2.dll", SetLastError = true)] static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint size, [In, Out] PHYSICAL_MONITOR[] arr);
        [DllImport("dxva2.dll", SetLastError = true)] static extern bool GetMonitorBrightness(IntPtr h, out uint min, out uint cur, out uint max);
        [DllImport("dxva2.dll", SetLastError = true)] static extern bool SetMonitorBrightness(IntPtr h, uint value);
        [DllImport("dxva2.dll", SetLastError = true)] static extern bool DestroyPhysicalMonitor(IntPtr h);
    }
}
