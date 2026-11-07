using System;
using System.Runtime.InteropServices;

namespace WinIsland.Services
{
    /// <summary>
    /// Громкость системы через Core Audio. Подписывается на изменения —
    /// поэтому остров показывает HUD, когда ты жмёшь клавиши громкости.
    /// Все COM-вызовы делаются из UI-потока; колбэки только пересылают данные.
    /// </summary>
    public sealed class AudioService : IDisposable
    {
        IMMDeviceEnumerator _enum;
        IAudioEndpointVolume _vol;
        readonly VolumeCallback _cb;
        readonly DeviceCallback _dc;
        Guid _ctx = Guid.NewGuid();

        /// <summary>(уровень 0..1, без звука, изменение от нас самих) — в COM-потоке.</summary>
        public event Action<float, bool, bool> Changed;
        /// <summary>Сменилось устройство вывода (наушники и т.п.) — в COM-потоке.</summary>
        public event Action DefaultDeviceChanged;

        public AudioService()
        {
            _cb = new VolumeCallback(this);
            _dc = new DeviceCallback(this);
        }

        public bool Available => _vol != null;

        public bool Init()
        {
            try
            {
                _enum = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                _enum.RegisterEndpointNotificationCallback(_dc);
            }
            catch (Exception ex) { App.Log(ex); _enum = null; return false; }
            return Rebind();
        }

        public bool Rebind()
        {
            Unbind();
            if (_enum == null) return false;
            try
            {
                if (_enum.GetDefaultAudioEndpoint(0 /*eRender*/, 1 /*eMultimedia*/, out IMMDevice dev) != 0 || dev == null)
                    return false;
                Guid iid = typeof(IAudioEndpointVolume).GUID;
                int hr = dev.Activate(ref iid, 0x17 /*CLSCTX_ALL*/, IntPtr.Zero, out object o);
                Marshal.ReleaseComObject(dev);
                if (hr != 0 || o == null) return false;
                _vol = (IAudioEndpointVolume)o;
                _vol.RegisterControlChangeNotify(_cb);
                return true;
            }
            catch (Exception ex) { App.Log(ex); _vol = null; return false; }
        }

        void Unbind()
        {
            if (_vol == null) return;
            try { _vol.UnregisterControlChangeNotify(_cb); } catch { }
            try { Marshal.ReleaseComObject(_vol); } catch { }
            _vol = null;
        }

        public float Volume
        {
            get
            {
                if (_vol == null) return 0;
                try { _vol.GetMasterVolumeLevelScalar(out float v); return v; } catch { return 0; }
            }
        }

        public bool Muted
        {
            get
            {
                if (_vol == null) return false;
                try { _vol.GetMute(out bool m); return m; } catch { return false; }
            }
        }

        public void SetVolume(float v)
        {
            if (_vol == null) return;
            try { _vol.SetMasterVolumeLevelScalar(Math.Clamp(v, 0f, 1f), ref _ctx); } catch { }
        }

        public void SetMute(bool m)
        {
            if (_vol == null) return;
            try { _vol.SetMute(m, ref _ctx); } catch { }
        }

        internal void OnVolumeNotify(IntPtr data)
        {
            if (data == IntPtr.Zero) return;
            // AUDIO_VOLUME_NOTIFICATION_DATA: GUID(16) BOOL(4) float(4) ...
            var ctx = Marshal.PtrToStructure<Guid>(data);
            bool muted = Marshal.ReadInt32(data, 16) != 0;
            float level = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(data, 20));
            Changed?.Invoke(level, muted, ctx == _ctx);
        }

        internal void OnDefaultDevice() => DefaultDeviceChanged?.Invoke();

        public void Dispose()
        {
            Unbind();
            try { _enum?.UnregisterEndpointNotificationCallback(_dc); } catch { }
        }
    }

    // ===================== COM interop =====================

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    public class MMDeviceEnumeratorCom { }

    [StructLayout(LayoutKind.Sequential)]
    public struct PropertyKey { public Guid fmtid; public int pid; }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMNotificationClient
    {
        [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int newState);
        [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string defaultDeviceId);
        [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PropertyKey key);
    }

    [ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioEndpointVolumeCallback
    {
        [PreserveSig] int OnNotify(IntPtr notifyData);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
        [PreserveSig] int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid ctx);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid ctx);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid ctx);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        [PreserveSig] int GetVolumeStepInfo(out uint step, out uint stepCount);
        [PreserveSig] int VolumeStepUp(ref Guid ctx);
        [PreserveSig] int VolumeStepDown(ref Guid ctx);
        [PreserveSig] int QueryHardwareSupport(out uint mask);
        [PreserveSig] int GetVolumeRange(out float minDb, out float maxDb, out float incDb);
    }

    [ComVisible(true)]
    public sealed class VolumeCallback : IAudioEndpointVolumeCallback
    {
        readonly AudioService _owner;
        public VolumeCallback(AudioService owner) => _owner = owner;
        public int OnNotify(IntPtr notifyData)
        {
            try { _owner.OnVolumeNotify(notifyData); } catch { }
            return 0;
        }
    }

    [ComVisible(true)]
    public sealed class DeviceCallback : IMMNotificationClient
    {
        readonly AudioService _owner;
        public DeviceCallback(AudioService owner) => _owner = owner;
        public int OnDeviceStateChanged(string deviceId, int newState) => 0;
        public int OnDeviceAdded(string deviceId) => 0;
        public int OnDeviceRemoved(string deviceId) => 0;
        public int OnDefaultDeviceChanged(int flow, int role, string defaultDeviceId)
        {
            if (flow == 0 && role == 1) { try { _owner.OnDefaultDevice(); } catch { } }
            return 0;
        }
        public int OnPropertyValueChanged(string deviceId, PropertyKey key) => 0;
    }
}
