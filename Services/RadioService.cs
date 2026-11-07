using System;
using System.Threading.Tasks;
using Windows.Devices.Radios;

namespace WinIsland.Services
{
    /// <summary>Настоящие переключатели Wi-Fi и Bluetooth (как в Пункте управления).</summary>
    public sealed class RadioService
    {
        Radio _wifi, _bt;

        /// <summary>Состояние радио поменялось — в фоновом потоке.</summary>
        public event Action Changed;

        public async Task InitAsync()
        {
            try
            {
                var access = Radio.RequestAccessAsync().AsTask();
                if (await Task.WhenAny(access, Task.Delay(4000)) != access) return;
                if (access.Result != RadioAccessStatus.Allowed) return;

                foreach (var r in await Radio.GetRadiosAsync())
                {
                    if (r.Kind == RadioKind.WiFi && _wifi == null) _wifi = r;
                    else if (r.Kind == RadioKind.Bluetooth && _bt == null) _bt = r;
                }
                if (_wifi != null) _wifi.StateChanged += OnChanged;
                if (_bt != null) _bt.StateChanged += OnChanged;
            }
            catch (Exception ex) { App.Log(ex); }
        }

        void OnChanged(Radio sender, object args) => Changed?.Invoke();

        public bool? WifiOn => State(_wifi);
        public bool? BtOn => State(_bt);

        static bool? State(Radio r)
        {
            if (r == null) return null;
            try { return r.State == RadioState.On; } catch { return null; }
        }

        public Task<bool> ToggleWifiAsync() => Toggle(_wifi);
        public Task<bool> ToggleBtAsync() => Toggle(_bt);

        static async Task<bool> Toggle(Radio r)
        {
            if (r == null) return false;
            try
            {
                var target = r.State == RadioState.On ? RadioState.Off : RadioState.On;
                return await r.SetStateAsync(target) == RadioAccessStatus.Allowed;
            }
            catch { return false; }
        }
    }
}
