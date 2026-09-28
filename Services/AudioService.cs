using System;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace TopDock.Services
{
    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    internal class MMDeviceEnumerator { }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        // vtable 순서 준수 필수 (GetDevice는 사용하지 않지만 순서 유지를 위해 선언)
        int EnumAudioEndpoints(int dataFlow, int dwStateMask, out IntPtr ppDevices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string deviceId, out IMMDevice device);
        int RegisterEndpointNotificationCallback(IMMNotificationClient pClient);
        int UnregisterEndpointNotificationCallback(IMMNotificationClient pClient);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
    }

    /// <summary>기본 오디오 장치 변경 등을 통지하는 Core Audio 콜백 인터페이스</summary>
    [ComImport]
    [Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMNotificationClient
    {
        [PreserveSig]
        int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int newState);
        [PreserveSig]
        int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig]
        int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig]
        int OnDefaultDeviceChanged(int dataFlow, int role, [MarshalAs(UnmanagedType.LPWStr)] string defaultDeviceId);
        [PreserveSig]
        int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PROPERTYKEY propertyKey);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioEndpointVolume
    {
        int RegisterControlNotificationCallback(IAudioEndpointVolumeCallback pNotify);
        int UnregisterControlNotificationCallback(IAudioEndpointVolumeCallback pNotify);
        int GetChannelCount(out uint pnChannelCount);
        int SetMasterVolumeLevel(float fLevelDB, ref Guid pguidEventContext);
        int SetMasterVolumeLevelScalar(float fLevel, ref Guid pguidEventContext);
        int GetMasterVolumeLevel(out float pfLevelDB);
        int GetMasterVolumeLevelScalar(out float pfLevel);
        int SetChannelVolumeLevel(uint nChannel, float fLevelDB, ref Guid pguidEventContext);
        int SetChannelVolumeLevelScalar(uint nChannel, float fLevel, ref Guid pguidEventContext);
        int GetChannelVolumeLevel(uint nChannel, out float pfLevelDB);
        int GetChannelVolumeLevelScalar(uint nChannel, out float pfLevel);
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, ref Guid pguidEventContext);
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
        int GetVolumeStepInfo(out uint pnStep, out uint pnStepCount);
        int VolumeStepUp(ref Guid pguidEventContext);
        int VolumeStepDown(ref Guid pguidEventContext);
        int QueryHardwareSupport(out uint pdwHardwareSupportMask);
        int GetVolumeRange(out float pflVolumeMindB, out float pflVolumeMaxdB, out float pflVolumeIncrementdB);
    }

    [ComImport]
    [Guid("65782020-4ACF-4537-B57A-3650C93223C3")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioEndpointVolumeCallback
    {
        [PreserveSig]
        int OnNotify(IntPtr pNotifyData);
    }

    public class AudioService : IAudioEndpointVolumeCallback, IMMNotificationClient, IDisposable
    {
        private const int DataFlowRender = 0;   // eRender
        private const int RoleConsole = 0;      // eConsole
        private const int RoleMultimedia = 1;   // eMultimedia

        private IMMDeviceEnumerator? _enumerator;
        private IMMDevice? _device;
        private IAudioEndpointVolume? _audioVolume;
        private readonly DispatcherTimer _pollTimer;
        private int _lastVolume = -1;
        private bool _lastMuted = false;
        private bool _disposed;

        // 콜백이 놓친 장치 변경을 폴링 실패로 백업 감지하기 위한 카운터
        private int _consecutivePollFailures;
        // role별로 이벤트가 연달아 오는 것을 걸러내는 디바운스
        private DateTime _lastReinitAt = DateTime.MinValue;

        public event Action<int, bool>? VolumeChanged;

        public AudioService()
        {
            Initialize();
            RegisterDeviceNotifications();

            // 60ms volume monitor guarantees 100% detection of keyboard & taskbar volume adjustments
            _pollTimer = new DispatcherTimer(DispatcherPriority.Normal)
            {
                Interval = TimeSpan.FromMilliseconds(60)
            };
            _pollTimer.Tick += PollTimer_Tick;
            _pollTimer.Start();
        }

        private void Initialize()
        {
            try
            {
                _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                // 0: eRender, 0: eConsole, 1: eMultimedia
                int hr = _enumerator.GetDefaultAudioEndpoint(DataFlowRender, RoleConsole, out _device);
                if (hr != 0 || _device == null)
                {
                    _enumerator.GetDefaultAudioEndpoint(DataFlowRender, RoleMultimedia, out _device);
                }

                if (_device != null)
                {
                    Guid iid = typeof(IAudioEndpointVolume).GUID;
                    int hrAct = _device.Activate(ref iid, 23, IntPtr.Zero, out object obj);
                    if (hrAct == 0 && obj != null)
                    {
                        _audioVolume = (IAudioEndpointVolume)obj;
                        _audioVolume.RegisterControlNotificationCallback(this);

                        // Read initial volume and mute
                        _audioVolume.GetMasterVolumeLevelScalar(out float level);
                        _audioVolume.GetMute(out _lastMuted);
                        _lastVolume = (int)Math.Round(level * 100);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"AudioService init error: {ex.Message}");
            }
        }

        /// <summary>기본 출력 장치 변경 통지를 구독한다</summary>
        private void RegisterDeviceNotifications()
        {
            try
            {
                _enumerator?.RegisterEndpointNotificationCallback(this);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"AudioService register notifications error: {ex.Message}");
            }
        }

        /// <summary>
        /// 출력 장치가 바뀌면(예: 블루투스 연결) 기존 COM 인터페이스는 죽은 장치를 가리킨다.
        /// COM 객체를 정리하고 새 기본 장치로 다시 바인딩한다.
        /// </summary>
        private void Reinitialize(string reason)
        {
            if (_disposed) return;

            try
            {
                // role별(eConsole/eMultimedia)로 이벤트가 연속 도착하는 것을 걸러냄
                if (DateTime.UtcNow - _lastReinitAt < TimeSpan.FromMilliseconds(300)) return;
                _lastReinitAt = DateTime.UtcNow;

                System.Diagnostics.Debug.WriteLine($"AudioService reinit: {reason}");

                int oldVolume = _lastVolume;
                bool oldMuted = _lastMuted;

                ReleaseDevice();
                Initialize();
                RegisterDeviceNotifications();

                _consecutivePollFailures = 0;

                // 새 장치의 볼륨이 다를 때만 UI에 알린다 (같으면 HUD를 깜빡이지 않음)
                if (_lastVolume != oldVolume || _lastMuted != oldMuted)
                {
                    VolumeChanged?.Invoke(_lastVolume, _lastMuted);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"AudioService reinit error: {ex.Message}");
            }
        }

        /// <summary>IMMNotificationClient 콜백: 기본 장치가 바뀌면(블루투스 연결/해제, 장치 전환) 재초기화</summary>
        public int OnDefaultDeviceChanged(int dataFlow, int role, string defaultDeviceId)
        {
            // 출력 장치(eRender) 변경만 관심 있다
            if (dataFlow != DataFlowRender) return 0;

            // COM 콜백 스레드에서 오므로 UI 스레드로 마샬링
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                Reinitialize($"기본 장치 변경 (role={role})")));
            return 0;
        }

        public int OnDeviceStateChanged(string deviceId, int newState) => 0;
        public int OnDeviceAdded(string deviceId) => 0;
        public int OnDeviceRemoved(string deviceId) => 0;
        public int OnPropertyValueChanged(string deviceId, PROPERTYKEY propertyKey) => 0;

        private void PollTimer_Tick(object? sender, EventArgs e)
        {
            if (_audioVolume == null) return;
            try
            {
                _audioVolume.GetMasterVolumeLevelScalar(out float level);
                _audioVolume.GetMute(out bool isMuted);
                int vol = (int)Math.Round(level * 100);

                _consecutivePollFailures = 0;

                if (vol != _lastVolume || isMuted != _lastMuted)
                {
                    _lastVolume = vol;
                    _lastMuted = isMuted;
                    VolumeChanged?.Invoke(vol, isMuted);
                }
            }
            catch
            {
                // 죽은 장치를 폴링하면 여기로 온다. 콜백 놓침 대비 백업 감지:
                // 약 3초(50회 × 60ms) 연속 실패하면 강제 재초기화
                if (++_consecutivePollFailures >= 50)
                {
                    _consecutivePollFailures = 0;
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                        Reinitialize("폴링 연속 실패 (장치 소실 추정)")));
                }
            }
        }

        public int OnNotify(IntPtr pNotifyData)
        {
            if (_audioVolume == null) return 0;
            try
            {
                _audioVolume.GetMasterVolumeLevelScalar(out float level);
                _audioVolume.GetMute(out bool isMuted);
                int vol = (int)Math.Round(level * 100);

                _lastVolume = vol;
                _lastMuted = isMuted;
                VolumeChanged?.Invoke(vol, isMuted);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"AudioService notify error: {ex.Message}");
            }
            return 0; // S_OK
        }

        public int GetCurrentVolume(out bool isMuted)
        {
            isMuted = _lastMuted;
            if (_audioVolume != null)
            {
                try
                {
                    _audioVolume.GetMasterVolumeLevelScalar(out float level);
                    _audioVolume.GetMute(out isMuted);
                    _lastMuted = isMuted;
                    _lastVolume = (int)Math.Round(level * 100);
                    return _lastVolume;
                }
                catch { }
            }
            return _lastVolume >= 0 ? _lastVolume : 50;
        }

        public int StepVolume(float deltaScalar, out bool isMuted)
        {
            isMuted = _lastMuted;
            if (_audioVolume == null) return 50;

            try
            {
                _audioVolume.GetMasterVolumeLevelScalar(out float currentLevel);
                float newLevel = Math.Clamp(currentLevel + deltaScalar, 0.0f, 1.0f);
                Guid empty = Guid.Empty;
                _audioVolume.SetMasterVolumeLevelScalar(newLevel, ref empty);
                _audioVolume.GetMute(out isMuted);

                int vol = (int)Math.Round(newLevel * 100);
                _lastVolume = vol;
                _lastMuted = isMuted;

                VolumeChanged?.Invoke(vol, isMuted);
                return vol;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"AudioService StepVolume error: {ex.Message}");
                return _lastVolume >= 0 ? _lastVolume : 50;
            }
        }

        /// <summary>바인딩된 COM 객체와 통지 등록을 모두 해제한다</summary>
        private void ReleaseDevice()
        {
            try
            {
                if (_audioVolume != null)
                {
                    _audioVolume.UnregisterControlNotificationCallback(this);
                    Marshal.ReleaseComObject(_audioVolume);
                    _audioVolume = null;
                }

                if (_enumerator != null)
                {
                    _enumerator.UnregisterEndpointNotificationCallback(this);
                    Marshal.ReleaseComObject(_enumerator);
                    _enumerator = null;
                }

                if (_device != null)
                {
                    Marshal.ReleaseComObject(_device);
                    _device = null;
                }
            }
            catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _pollTimer.Stop();
            ReleaseDevice();
        }
    }
}
