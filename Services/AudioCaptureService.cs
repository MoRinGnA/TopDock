using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using TopDock.Logic;

namespace TopDock.Services
{
    // ── WASAPI 루프백 캡처에 필요한 COM 조각 ──
    // 프로젝트의 다른 오디오 코드(AudioService)와 같은 방식으로 손으로 쓴 interop이다.
    // NuGet(NAudio)을 쓰지 않는 이유도 같다: 볼륨 하나 때문에 이미 Core Audio를 직접 다루고 있고,
    // 여기서 새로 쓰는 인터페이스는 둘뿐이라 라이브러리를 통째로 들이는 것보다 가볍다.

    /// <summary>
    /// 재생 스트림을 되받아 읽는 클라이언트. vtable 순서가 곧 계약이라 선언 순서를 바꾸면 안 된다.
    /// </summary>
    [ComImport]
    [Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioClient
    {
        /// <summary>
        /// 포맷은 구조체가 아니라 포인터로 넘긴다 — GetMixFormat이 준 메모리를 그대로 넘겨야
        /// 엔진이 WAVEFORMATEXTENSIBLE의 SubFormat(부동소수점인지)까지 읽는다.
        /// 구조체로 마샬링하면 18바이트(WAVEFORMATEX)에서 잘려 엔진이 없는 바이트를 읽는다.
        /// </summary>
        int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        int GetBufferSize(out uint bufferFrames);
        int GetStreamLatency(out long latency);
        int GetCurrentPadding(out uint paddingFrames);
        int IsFormatSupported(int shareMode, ref WAVEFORMATEX format, out IntPtr closestMatch);
        int GetMixFormat(out IntPtr deviceFormat);
        int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        int Start();
        int Stop();
        int Reset();
        int SetEventHandle(IntPtr eventHandle);
        int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    /// <summary>캡처 버퍼를 한 덩어리씩 받아 가는 쪽.</summary>
    [ComImport]
    [Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioCaptureClient
    {
        int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        int ReleaseBuffer(uint frames);
        int GetNextPacketSize(out uint frames);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct WAVEFORMATEX
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSec;
        public uint AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort ExtraSize;
    }

    /// <summary>WAVEFORMATEX + 확장부(유효 비트·채널 마스크·서브포맷). 40바이트가 정확히 맞아야 한다.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct WAVEFORMATEXTENSIBLE
    {
        public WAVEFORMATEX Format;
        public ushort ValidBitsPerSample;
        public uint ChannelMask;
        public Guid SubFormat;
    }

    /// <summary>
    /// 지금 나오고 있는 소리를 되받아 세 칸의 높이로 바꿔 두는 서비스.
    ///
    /// 왜 루프백인가: SMTC는 음량을 알려주지 않는다 — 재생 중인지, 어디까지 왔는지만 안다.
    /// "소리가 실제로 얼마나 큰가"는 출력 장치가 흘려보낸 파형을 되받는 것 말고는 알 길이 없다.
    ///
    /// 읽는 것은 시스템 전체 소리다(재생 중인 앱만 골라 받으려면 세션별 활성화가 따로 필요하다).
    /// 링은 재생 중일 때만 뜨므로 이 화면에서는 대개 지금 듣는 소리 그대로다.
    ///
    /// 실패해도 앱은 아무 일 없이 돌아간다: 캡처를 못 열면 <see cref="TryReadBars"/>가 false를
    /// 돌려주고, 링은 바늘 없이(호만) 그려진다. 계기가 없다고 볼륨 UI가 사라지면 안 된다.
    ///
    /// UI 스레드에서 Start/Stop을 부르고, 파형은 자기 스레드에서 읽어 UI가 폴링하도록 스냅샷만 남긴다.
    /// </summary>
    public sealed class AudioCaptureService : IDisposable, IMMNotificationClient
    {
        private const int DataFlowRender = 0;
        private const int RoleConsole = 0;
        private const int ClsCtxAll = 23;
        private const int SharedMode = 0;
        private const int StreamFlagsLoopback = 0x00020000;
        private const uint BufferFlagsSilent = 0x2;
        private const long BufferDurationHns = 1_000_000;   // 100ms — 폴링으로 충분히 따라온다
        private const int PollIntervalMs = 10;
        /// <summary>이만큼 소리가 아예 오지 않으면 '무음'으로 본다 — 바늘이 멈춰 서지 않게 바닥으로 흘린다.</summary>
        private const int SilentGapMs = 60;
        private const int MaxRebuilds = 3;                  // 여기까지 다시 붙어 보고, 안 되면 손을 뗀다

        private static readonly Guid SubTypeIeeeFloat = new("00000003-0000-0010-8000-00AA00389B71");

        private readonly object _gate = new();
        private readonly float[] _bars = new float[AudioAnalyzer.BandCount];
        private bool _hasData;
        private volatile bool _running;
        private volatile bool _deviceChanged;
        private Thread? _thread;
        private ManualResetEventSlim? _stop;

        /// <summary>캡처를 열지 못했을 때의 사유(로그·진단용). 열려 있으면 null.</summary>
        public string? Failure { get; private set; }

        /// <summary>지금 파형을 읽고 있는가.</summary>
        public bool IsRunning => _running;

        /// <summary>
        /// 지금의 세 칸(0~1, 이미 부드럽게 다듬은 값)을 복사한다.
        /// 아직 소리를 한 번도 못 받았으면 false — 그때는 그리는 쪽이 바늘을 비워 둔다.
        /// </summary>
        public bool TryReadBars(Span<float> bars)
        {
            if (bars.Length < AudioAnalyzer.BandCount) throw new ArgumentException("밴드 수만큼 자리가 필요하다", nameof(bars));
            if (!_running) return false;

            lock (_gate)
            {
                if (!_hasData) return false;
                for (int i = 0; i < AudioAnalyzer.BandCount; i++) bars[i] = _bars[i];
            }
            return true;
        }

        /// <summary>재생이 시작될 때 부른다. 이미 돌고 있으면 아무 일도 하지 않는다.</summary>
        public void Start()
        {
            if (_running) return;

            _running = true;
            _hasData = false;
            Failure = null;
            _deviceChanged = false;

            _stop = new ManualResetEventSlim(false);
            _thread = new Thread(CaptureThread)
            {
                IsBackground = true,
                Name = "TopDock.AudioCapture",
            };
            _thread.Start();
        }

        /// <summary>재생이 끝나면 부른다 — 멈춘 화면을 위해 계속 파형을 훑고 있을 이유가 없다.</summary>
        public void Stop()
        {
            if (!_running) return;
            _running = false;

            _stop?.Set();
            // 캡처 스레드가 COM을 정리할 시간을 준다. 안 기다리면 다음 Start와 겹쳐 장치를 두 번 연다.
            // (이벤트 객체는 일부러 버리지 않는다 — 정리를 못 기다린 스레드가 아직 쓰고 있을 수 있다.)
            _thread?.Join(500);
            _thread = null;
            _stop = null;

            lock (_gate)
            {
                _hasData = false;
                Array.Clear(_bars);
            }
        }

        public void Dispose() => Stop();

        /// <summary>기본 장치가 바뀌면(블루투스 연결 등) 잡고 있던 장치는 죽은 장치다 — 루프를 다시 세운다.</summary>
        public int OnDefaultDeviceChanged(int dataFlow, int role, string defaultDeviceId)
        {
            if (dataFlow == DataFlowRender) _deviceChanged = true;
            return 0;
        }

        public int OnDeviceStateChanged(string deviceId, int newState) => 0;
        public int OnDeviceAdded(string deviceId) => 0;
        public int OnDeviceRemoved(string deviceId) => 0;
        public int OnPropertyValueChanged(string deviceId, PROPERTYKEY propertyKey) => 0;

        private void CaptureThread()
        {
            CoInitializeEx(IntPtr.Zero, 0);   // 이 스레드는 MTA — UI 스레드(STA)에서 열면 콜백과 얽힌다

            try
            {
                int rebuilds = 0;
                var stop = _stop!;

                while (_running)
                {
                    try
                    {
                        CaptureSession(stop);
                    }
                    catch (Exception ex)
                    {
                        Failure = ex.Message;
                        Log.Info($"Audio capture stopped: {ex.Message}");

                        if (++rebuilds > MaxRebuilds) break;
                        if (stop.Wait(1000)) break;   // 장치가 뽑혔다 꽂히는 것을 기다린다
                        continue;
                    }

                    if (!_deviceChanged) break;       // Stop이 걸렸거나 스트림이 스스로 끝났다

                    _deviceChanged = false;
                    rebuilds = 0;
                    Failure = null;
                }
            }
            finally
            {
                CoUninitialize();
            }
        }

        /// <summary>
        /// 장치를 잡고 파형을 읽는다. 기본 장치가 바뀌거나 멈추면 돌아오고, 그 밖의 실패는 예외로 올린다.
        /// </summary>
        private void CaptureSession(ManualResetEventSlim stop)
        {
            IMMDeviceEnumerator? enumerator = null;
            IMMDevice? device = null;
            IAudioClient? client = null;
            IAudioCaptureClient? capture = null;
            IntPtr mixFormat = IntPtr.Zero;
            bool started = false;

            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                enumerator.RegisterEndpointNotificationCallback(this);

                int hr = enumerator.GetDefaultAudioEndpoint(DataFlowRender, RoleConsole, out device);
                if (hr != 0 || device == null)
                {
                    throw new InvalidOperationException($"기본 출력 장치가 없다 (0x{hr:X8})");
                }

                Guid clientIid = typeof(IAudioClient).GUID;
                hr = device.Activate(ref clientIid, ClsCtxAll, IntPtr.Zero, out object activated);
                if (hr != 0 || activated is not IAudioClient audioClient)
                {
                    throw new InvalidOperationException($"오디오 클라이언트를 열지 못했다 (0x{hr:X8})");
                }
                client = audioClient;

                hr = client.GetMixFormat(out mixFormat);
                if (hr != 0 || mixFormat == IntPtr.Zero)
                {
                    throw new InvalidOperationException($"장치의 형식을 읽지 못했다 (0x{hr:X8})");
                }

                var format = ReadFormat(mixFormat);

                hr = client.Initialize(SharedMode, StreamFlagsLoopback, BufferDurationHns, 0, mixFormat, IntPtr.Zero);
                if (hr != 0)
                {
                    throw new InvalidOperationException($"루프백을 열지 못했다 (0x{hr:X8})");
                }

                Guid captureIid = typeof(IAudioCaptureClient).GUID;
                hr = client.GetService(ref captureIid, out object service);
                if (hr != 0 || service is not IAudioCaptureClient audioCapture)
                {
                    throw new InvalidOperationException($"캡처 인터페이스를 얻지 못했다 (0x{hr:X8})");
                }
                capture = audioCapture;

                hr = client.Start();
                if (hr != 0)
                {
                    throw new InvalidOperationException($"캡처를 시작하지 못했다 (0x{hr:X8})");
                }
                started = true;

                Log.Info($"Audio capture open: {format.SampleRate}Hz {format.Channels}ch {format.Bits}bit float={format.IsFloat}");

                if (!format.Supported)
                {
                    // 열리긴 했지만 우리가 접을 줄 아는 형식이 아니다 — 바늘 없이 조용히 돌려준다
                    Failure = $"지원하지 않는 형식 ({format.Bits}bit tag={format.FormatTag})";
                    Log.Info($"Audio capture unusable: {Failure}");
                    WaitForStop(stop);
                    return;
                }

                ReadLoop(capture, format, stop);
            }
            finally
            {
                if (started)
                {
                    try { client?.Stop(); } catch { }
                }

                try { enumerator?.UnregisterEndpointNotificationCallback(this); } catch { }
                Release(capture);
                Release(client);
                Release(device);
                Release(enumerator);

                if (mixFormat != IntPtr.Zero) Marshal.FreeCoTaskMem(mixFormat);
            }
        }

        /// <summary>더 볼 것이 없을 때(형식 미지원) 정지 신호만 기다린다.</summary>
        private void WaitForStop(ManualResetEventSlim stop)
        {
            while (_running && !_deviceChanged && !stop.Wait(100)) { }
        }

        /// <summary>
        /// 폴링 루프 — 이벤트 콜백 대신 10ms마다 "읽을 게 있는가"를 물어본다.
        /// 루프백의 이벤트 방식은 윈도 버전에 따라 지원이 갈리는데, 폴링은 어디서나 같다.
        /// </summary>
        private void ReadLoop(IAudioCaptureClient capture, in DeviceFormat format, ManualResetEventSlim stop)
        {
            var meter = new Meter(this, format);
            long lastPacketTick = Environment.TickCount64;

            while (_running && !_deviceChanged)
            {
                int hr = capture.GetNextPacketSize(out uint packetFrames);
                if (hr != 0)
                {
                    throw new InvalidOperationException($"캡처 스트림이 끊겼다 (0x{hr:X8})");
                }

                bool drained = false;

                while (packetFrames > 0)
                {
                    hr = capture.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out _);
                    if (hr != 0)
                    {
                        throw new InvalidOperationException($"버퍼를 읽지 못했다 (0x{hr:X8})");
                    }

                    try
                    {
                        meter.Feed(data, frames, format, (flags & BufferFlagsSilent) != 0);
                    }
                    finally
                    {
                        capture.ReleaseBuffer(frames);
                    }

                    drained = true;
                    lastPacketTick = Environment.TickCount64;

                    hr = capture.GetNextPacketSize(out packetFrames);
                    if (hr != 0) break;
                }

                if (!drained)
                {
                    // 재생기가 스트림을 놓으면(곡 사이·정지) 패킷이 아예 오지 않는다. 그때 바늘이
                    // 마지막 값에 멈춰 서면 소리가 끝났는데도 계기가 살아 있는 것처럼 보인다.
                    if (Environment.TickCount64 - lastPacketTick > SilentGapMs) meter.Idle();

                    // 읽을 게 없으면 잠깐 쉰다 — 바쁘게 돌아도 소리는 빨리 오지 않는다
                    if (stop.Wait(PollIntervalMs)) break;
                }
            }
        }

        private void Publish(float[] smoothed)
        {
            lock (_gate)
            {
                Array.Copy(smoothed, _bars, _bars.Length);
                _hasData = true;
            }
        }

        /// <summary>
        /// 읽어 들인 파형을 세 칸으로 바꿔 두는 계산기. 캡처 스레드 하나만 만지므로 잠금이 없다.
        ///
        /// 창은 '가장 최근의 한 조각'만 본다 — 소리의 지금을 보는 계기라 오래된 파형은 쓸모가 없다.
        /// 채널은 평균으로 접는다: 그대로 두면 좌우가 다른 소리에서 두 배로 세어져 저음이 혼자 튄다.
        /// </summary>
        private sealed class Meter
        {
            private readonly AudioCaptureService _owner;
            private readonly AudioAnalyzer _analyzer;
            private readonly MeterSmoother _smoother;
            private readonly float[] _raw = new float[AudioAnalyzer.BandCount];
            private readonly float[] _smoothed = new float[AudioAnalyzer.BandCount];
            private readonly float[] _mono = new float[AudioAnalyzer.FftSize * 2];
            private readonly Stopwatch _clock = Stopwatch.StartNew();
            private double _lastAnalysisMs;
            private int _count;

            private float[] _floatScratch = Array.Empty<float>();
            private short[] _shortScratch = Array.Empty<short>();

            public Meter(AudioCaptureService owner, DeviceFormat format)
            {
                _owner = owner;
                _analyzer = new AudioAnalyzer((int)format.SampleRate);
                _smoother = new MeterSmoother(AudioAnalyzer.BandCount);
            }

            public void Feed(IntPtr data, uint frames, in DeviceFormat format, bool silent)
            {
                int needed = (int)(frames * format.Channels);
                if (needed <= 0) return;

                // 자리가 없으면 새 소리를 버리지 않고 먼저 비운다
                if (_count + needed > _mono.Length) Flush();

                _count = Append(_mono, _count, data, frames, format, silent);

                if (_count >= AudioAnalyzer.FftSize) Flush();
            }

            private void Flush()
            {
                if (_count <= 0) return;

                _analyzer.Analyze(_mono.AsSpan(0, _count), _raw);
                Settle();
                _count = 0;
            }

            /// <summary>소리가 오지 않는 구간 — 목표를 바닥으로 두고 같은 손으로 다듬는다.</summary>
            public void Idle()
            {
                Array.Clear(_raw);
                Settle();
            }

            private void Settle()
            {
                // 실제로 흐른 시간으로 다듬는다 — 폴링이 밀려도 바늘이 같은 속도로 움직이도록
                double now = _clock.Elapsed.TotalMilliseconds;
                _smoother.Step(_raw, now - _lastAnalysisMs, _smoothed);
                _lastAnalysisMs = now;

                _owner.Publish(_smoothed);
            }

            /// <summary>받은 덩어리를 모노로 접어 누적기에 붙인다. 자리는 호출한 쪽이 비워 둔다.</summary>
            private int Append(float[] mono, int count, IntPtr data, uint frames, in DeviceFormat format, bool silent)
            {
                int channels = format.Channels;
                int room = mono.Length - count;
                int take = (int)Math.Min(frames, (uint)(room / channels));
                if (take <= 0) return count;

                int samples = take * channels;

                if (!silent)
                {
                    // 덩어리가 누적기보다 크면 뒤쪽(가장 최근 소리)을 남긴다
                    int skip = ((int)frames - take) * channels * (format.IsFloat ? 4 : 2);
                    IntPtr source = skip > 0 ? IntPtr.Add(data, skip) : data;

                    if (format.IsFloat)
                    {
                        if (_floatScratch.Length < samples) _floatScratch = new float[samples];
                        Marshal.Copy(source, _floatScratch, 0, samples);
                    }
                    else
                    {
                        if (_shortScratch.Length < samples) _shortScratch = new short[samples];
                        Marshal.Copy(source, _shortScratch, 0, samples);
                    }
                }

                for (int frame = 0; frame < take; frame++)
                {
                    float sum = 0f;
                    if (!silent)
                    {
                        for (int c = 0; c < channels; c++)
                        {
                            sum += format.IsFloat
                                ? _floatScratch[frame * channels + c]
                                : _shortScratch[frame * channels + c] / 32768f;
                        }
                    }
                    mono[count + frame] = sum / channels;
                }

                return count + take;
            }
        }

        private static DeviceFormat ReadFormat(IntPtr mixFormat)
        {
            var core = Marshal.PtrToStructure<WAVEFORMATEX>(mixFormat);
            bool isFloat = core.FormatTag == 3;   // WAVE_FORMAT_IEEE_FLOAT

            if (core.ExtraSize >= 22)
            {
                var extended = Marshal.PtrToStructure<WAVEFORMATEXTENSIBLE>(mixFormat);
                isFloat = extended.SubFormat == SubTypeIeeeFloat;
            }

            bool supported = (isFloat && core.BitsPerSample == 32)
                             || (!isFloat && core.FormatTag == 1 && core.BitsPerSample == 16);

            return new DeviceFormat(
                core.SamplesPerSec,
                core.Channels,
                core.BitsPerSample,
                core.FormatTag,
                isFloat,
                supported);
        }

        private static void Release(object? comObject)
        {
            if (comObject == null) return;
            try { Marshal.ReleaseComObject(comObject); } catch { }
        }

        /// <summary>장치가 알려준 형식 — 채널·비트·부동소수점 여부.</summary>
        private readonly record struct DeviceFormat(
            uint SampleRate, ushort Channels, ushort Bits, ushort FormatTag, bool IsFloat, bool Supported);

        [DllImport("ole32.dll")]
        private static extern int CoInitializeEx(IntPtr reserved, uint apartment);

        [DllImport("ole32.dll")]
        private static extern void CoUninitialize();
    }
}
