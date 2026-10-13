using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using TopDock.Models;
using TopDock.Services;

namespace TopDock
{
    public partial class MainWindow : Window
    {
        // ViewMode(도크 모양)는 TopDock.Logic으로 옮겼다 — 전환 결정이 UI 밖 순수 로직이 됐다.

        /// <summary>배터리는 퍼센트가 아니라 3분류로만 판단한다.</summary>
        private enum BatteryLevel { Normal, Charging, Low }

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WM_NCHITTEST = 0x0084;
        private const int HTTRANSPARENT = -1;

        // AI 비서 전역 핫키: Ctrl+Shift+Space
        private const int HOTKEY_ID_ASSISTANT = 0xB00B;
        private const int HOTKEY_ID_CLIPBOARD = 0xB00C;   // Ctrl+Shift+V
        private const int HOTKEY_ID_INBOX = 0xB00D;       // Ctrl+Shift+N
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint MOD_NOREPEAT = 0x4000;
        private const int WM_HOTKEY = 0x0312;

        /// <summary>
        /// 시작할 때 각 핫키가 걸렸는가(OS가 준 답). 설정을 적용할 때마다 이 값과 견주어,
        /// 적용이 핫키 하나를 조용히 죽여 놓았는지 확인한다 — 견줄 기준은 언제나 '시작 상태'다.
        /// </summary>
        private Dictionary<HotkeySlot, bool>? _hotkeyAtStart;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hwnd, int index);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

        // 창을 노치가 앉을 모니터의 사각형에 그대로 덮는다 — WPF의 Left/Top(DIP) 대신 장치 좌표를 쓴다.
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(
            IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

        private const int WM_CLIPBOARDUPDATE = 0x031D;
        private const int WM_POWERBROADCAST = 0x0218;
        private const int WM_DISPLAYCHANGE = 0x007E;   // 모니터가 붙거나 빠지거나 해상도·배율이 바뀌었다
        private const int PBT_APMPOWERSTATUSCHANGE = 0x000A;
        private const int PBT_POWERSETTINGCHANGE = 0x8013;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AddClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

        // 클립보드 항목 클릭 → 직전 창에 붙여넣기 주입용
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const ushort VK_CONTROL = 0x11;
        private const ushort VK_V = 0x56;

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public InputUnion u;
        }

        // SendInput은 실제 구조체 크기와 cbSize가 다르면 아무것도 보내지 않는다 —
        // 쓰지 않는 멤버까지 정의해 x86/x64 양쪽에서 크기를 맞춘다.
        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx, dy;
            public uint mouseData, dwFlags, time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk, wScan;
            public uint dwFlags, time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL, wParamH;
        }

        private readonly AudioService _audioService;
        private readonly MediaService _mediaService;
        private readonly LyricsService _lyricsService;
        private readonly BatteryService _batteryService;
        private readonly NotificationService _notificationService;
        private readonly AssistantService _assistant;
        private readonly AssistantTools _assistantTools;
        private SettingsWindow? _settingsWindow;

        // ── AI 비서 상태 ──
        private CancellationTokenSource? _assistantCts;
        private bool _assistantBusy;
        private bool _assistantStreaming; // 첫 델타 이후 응답 스트리밍 중인가
        private bool _assistantToolExecuting; // 도구 실행 중이면 탭을 다시 열어도 실행 표정을 유지한다.
        // 이번 턴의 답이 화면에 남아 있는가 — 남아 있는 동안 얼굴은 멈춘다(Still).
        // 새 지시가 들어오면 다시 살아난다.
        private bool _assistantResponseOnScreen;

        // ── 배터리 표시 ──
        private BatteryLevel _batteryLevel = BatteryLevel.Normal;
        private float _batteryPercent = -1f;

        /// <summary>
        /// 종료가 시작됐는가 — 배경 이벤트가 UI로 넘어오는 것을 여기서 멈춘다.
        /// (닫히는 중에 도착한 배경 이벤트는 예외를 던져 전역 핸들러를 거쳐 오류창이 된다)
        /// </summary>
        private volatile bool _shuttingDown;

        private readonly DispatcherTimer _progressTimer;
        private readonly DispatcherTimer _clockTimer;
        private DispatcherTimer? _volumeHudTimer;
        private DispatcherTimer? _notificationTimer;
        private DispatcherTimer? _emptyMediaDebounceTimer;
        private readonly DispatcherTimer _audioBarsTimer;

        // 지금 나는 소리를 되받아 세 칸으로 바꿔 둔다 — SMTC는 음량을 알려주지 않는다.
        // 캡처는 자기 스레드에서 돌고, 창은 30fps로 그 결과만 읽는다.
        private readonly AudioCaptureService _audioCapture = new();
        private readonly float[] _levelBars = new float[Tokens.RingBarCount];
        private bool _mediaPlaying;

        // 링 안의 바늘은 테두리 빛과 같은 색을 쓴다 — 브러시 하나를 공유하고 색만 바꾼다
        private readonly SolidColorBrush _audioBarsBrush = new(AudioBarsIdleColor);
        private Color _audioBarsAccent = AudioBarsIdleColor;
        private static readonly Color AudioBarsIdleColor = Color.FromRgb(0xF0, 0xF3, 0xF8);

        // 트랙 전환 사이 SMTC가 잠깐 보고하는 빈 미디어를 필터링하기 위한 유예 시간
        private static readonly TimeSpan EmptyMediaDebounceDelay = TimeSpan.FromMilliseconds(1500);

        // 도크 모양과 "열려 있기를 바라는 의도"는 상태 머신 하나가 들고 있다. 아래 두 이름은
        // 그 값을 읽고 쓰는 창구라, 창 코드가 필드를 다루던 자리를 그대로 둘 수 있다.
        private readonly DockState _dock = new();
        private ViewMode _currentViewMode { get => _dock.Mode; set => _dock.Show(value); }
        private ViewMode _viewModeBeforeNotification = ViewMode.IdleCompact;
        private string _lastMediaKey = string.Empty;
        private Color? _albumColor;   // 현재 트랙 앨범 지배색 — 노치 테두리 빛에 물린다
        private bool _orbCentral;     // 작업 중 노치 테두리 강조가 활성화되어 있는가
        private string _lastBeamState = "";
        private bool _isExpanded { get => _dock.Expanded; set => _dock.SetExpanded(value); }

        // 가사 싱크 — 뮤직비디오 앞 여백(인트로)만큼 가사를 늦춘다.
        private TimeSpan _lyricOffset = TimeSpan.Zero;

        private bool _volumeRingOverlayVisible = false;
        private bool HasMedia => !string.IsNullOrEmpty(_lastMediaKey);
        private List<LyricLine> _syncedLyrics = new();
        private System.Windows.Forms.NotifyIcon? _trayIcon;

        public MainWindow()
        {
            InitializeComponent();
            // 링의 바늘은 브러시 하나를 공유 — 곡이 바뀌면 색만 갈아 끼우면 된다
            VolumeRing.BarsBrush = _audioBarsBrush;

            ConfigService.Load();

            _audioService = new AudioService();
            _mediaService = new MediaService();
            _lyricsService = new LyricsService();
            _batteryService = new BatteryService();
            _notificationService = new NotificationService();
            _assistant = new AssistantService();

            // 도구 실행기: 실제 기기 조작은 전부 여기서, 시각 연출은 이벤트로 이 창이 붙는다.
            _assistantTools = new AssistantTools(_mediaService, _audioService);
            _assistantTools.ToolStarted += name =>
            {
                var kind = OrbForTool(name);
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _assistantToolExecuting = true;
                    ShowAssistantFace(kind);
                    // 비서 화면이 닫혀 있으면 무엇이 실행 중인지 노치 알림으로도 알린다
                    ShowToolNotice(name);
                }));
            };
            _assistantTools.ToolFinished += _ =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _assistantToolExecuting = false;
                    // 도구가 끝나면 모델 추론이 이어진다.
                    ShowAssistantFace(_assistantStreaming ? Controls.OrbKind.Composing : Controls.OrbKind.Working);
                    ShowThinkingNotice();
                }));
            };
            _assistant.Tools = _assistantTools;

            _progressTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(50)
            };
            _progressTimer.Tick += ProgressTimer_Tick;

            // 소리 바늘 — 30fps. 값이 없으면(캡처를 못 열었다) 바늘만 감추고 호는 그대로 둔다.
            _audioBarsTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(Tokens.MeterRenderMs)
            };
            _audioBarsTimer.Tick += AudioBars_Tick;

            _clockTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _clockTimer.Tick += ClockTimer_Tick;
            _clockTimer.Start();
            ClockTimer_Tick(null, EventArgs.Empty);

            _audioService.VolumeChanged += AudioService_VolumeChanged;
            _mediaService.MediaChanged += MediaService_MediaChanged;
            _mediaService.PlaybackStatusChanged += MediaService_PlaybackStatusChanged;
            _mediaService.TimelineChanged += MediaService_TimelineChanged;

            _notificationService.NotificationReceived += NotificationService_NotificationReceived;
            _batteryService.BatteryStatusChanged += BatteryService_BatteryStatusChanged;

            // 참고: DeltaReceived는 SendAssistantMessageAsync의 DeltaProxy가 유일 구독자다.
            // 여기서도 구독하면 델타가 2번씩 붙는 버그가 생긴다.

            Closed += MainWindow_Closed;

            InitializeTrayIcon();

            _ = _mediaService.InitializeAsync();
            _ = _notificationService.InitializeAsync();
            _batteryService.Start();            SwitchViewMode(ViewMode.IdleCompact);
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);

            // 비서 화면/작업 중 아일랜드에서는 포커스 위치와 무관하게 Esc로 취소·닫기
            if (e.Key == Key.Escape && _currentViewMode is ViewMode.Assistant or ViewMode.AssistantCompact)
            {
                if (_assistantBusy) _assistantCts?.Cancel();
                CloseAssistant();
                e.Handled = true;
                return;
            }

            // 클립보드 기록 패널 키보드 조작 — ↑↓ 이동 / Enter 붙여넣기 / Delete 삭제 / Esc 닫기
            if (_clipboardHistoryOpen)
            {
                switch (e.Key)
                {
                    case Key.Escape:
                        HideClipboardHistoryPanel();
                        e.Handled = true;
                        break;
                    case Key.Up:
                        MoveClipboardSelection(-1);
                        e.Handled = true;
                        break;
                    case Key.Down:
                        MoveClipboardSelection(1);
                        e.Handled = true;
                        break;
                    case Key.Delete:
                        RemoveSelectedFromHistory();
                        e.Handled = true;
                        break;
                    case Key.Enter:
                        ActivateClipboardSelection();
                        e.Handled = true;
                        break;
                }
            }

            // 알림 인박스 키보드 조작 — ↑↓ 이동 / Delete 지우기 / Esc 닫기
            if (_inboxOpen)
            {
                switch (e.Key)
                {
                    case Key.Escape:
                        CloseNotificationInbox();
                        e.Handled = true;
                        break;
                    case Key.Up:
                        MoveInboxSelection(-1);
                        e.Handled = true;
                        break;
                    case Key.Down:
                        MoveInboxSelection(1);
                        e.Handled = true;
                        break;
                    case Key.Delete:
                        RemoveSelectedFromInbox();
                        e.Handled = true;
                        break;
                }
            }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            int extendedStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, extendedStyle | WS_EX_TOOLWINDOW);

            var source = HwndSource.FromHwnd(hwnd);
            source?.AddHook(WndProc);

            AddClipboardFormatListener(hwnd);

            _hotkeyAtStart = RegisterGlobalHotkeys(hwnd);

            UpdatePosition();

            // 회귀 하네스가 부른 실행이면 여기서 잠깐 서서 '설정 적용'을 대신 눌러 준다
            if (IsHotkeySelfTestRun()) StartHotkeySelfTest();
        }

        /// <summary>
        /// 전역 핫키를 지금 설정에 맞게 (다시) 건다 — 시작할 때와 설정을 적용할 때가 같은 길을 쓴다.
        ///
        /// 한 아이디는 한 번만 등록되므로, 다시 걸 때는 같은 아이디를 먼저 내려야 한다. 그리고
        /// 함께 내린 것은 함께 올려야 한다: 예전에는 설정 적용이 클립보드 핫키(Ctrl+Shift+V)를 내리고
        /// 비서 핫키만 다시 걸어서, 설정을 한 번 적용하면 복사 기록 단축기가 영영 듣지 않았다.
        ///
        /// 돌려주는 것은 "이번에 각 자리가 실제로 걸렸는가"(OS가 준 답)다. 설정 적용은 이 값을 시작
        /// 시점의 것과 견주어, 조용히 죽은 자리가 있으면 로그에 남긴다(<see cref="LogHotkeyAudit"/>).
        /// </summary>
        private Dictionary<HotkeySlot, bool> RegisterGlobalHotkeys(IntPtr hwnd)
        {
            UnregisterHotKey(hwnd, HOTKEY_ID_ASSISTANT);
            UnregisterHotKey(hwnd, HOTKEY_ID_CLIPBOARD);
            UnregisterHotKey(hwnd, HOTKEY_ID_INBOX);

            var live = new Dictionary<HotkeySlot, bool>();

            // 복사 기록을 어디서든 꺼내는 전역 단축키 (Ctrl+Shift+V)
            live[HotkeySlot.Clipboard] = RegisterHotKey(hwnd, HOTKEY_ID_CLIPBOARD, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, 0x56);
            if (!live[HotkeySlot.Clipboard])
                Log.Warn("Clipboard hotkey Ctrl+Shift+V is already taken by another app");

            // AI 비서 전역 핫키 (Ctrl+Shift+Space) — 설정에서 끔 수 있다.
            // 꺼 두었으면 '걸지 않은 것'이 이 자리의 정상 상태다(그래서 거짓으로 적는다 —
            // 여기에 참을 적어 두면 설정을 끈 사람의 로그가 매번 회귀처럼 보인다).
            bool assistantOn = ConfigService.Current.AssistantEnabled;
            bool assistantLive = assistantOn
                && RegisterHotKey(hwnd, HOTKEY_ID_ASSISTANT, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, 0x20);
            live[HotkeySlot.Assistant] = assistantLive;
            if (assistantOn && !assistantLive)
                Log.Warn("Assistant hotkey Ctrl+Shift+Space is already taken by another app");

            // 놓친 알림 기록 (Ctrl+Shift+N)
            live[HotkeySlot.Inbox] = RegisterHotKey(hwnd, HOTKEY_ID_INBOX, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, 0x4E);
            if (!live[HotkeySlot.Inbox])
                Log.Warn("Inbox hotkey Ctrl+Shift+N is already taken by another app");

            return live;
        }

        /// <summary>
        /// 설정을 적용한 뒤 핫키가 설정대로 살아 있는지 견주어 로그에 남긴다.
        ///
        /// 이것이 없으면 회귀는 '사용자가 어느 날 우연히 알아차리는 것'으로만 드러난다(13번이 그랬다).
        /// 판단은 UI 밖 순수 규칙(<see cref="HotkeyPlan.Audit"/>)이 한다 — 같은 규칙을 회귀 하네스도
        /// 쓰므로, 로그와 테스트가 같은 말로 같은 것을 본다.
        /// </summary>
        private static void LogHotkeyAudit(
            IReadOnlyDictionary<HotkeySlot, bool>? before,
            IReadOnlyDictionary<HotkeySlot, bool> after,
            bool assistantEnabled)
        {
            if (before == null) return;

            HotkeyAudit audit = HotkeyPlan.Audit(before, after, assistantEnabled);
            if (audit.Lost.Count > 0)
                Log.Error($"Global hotkey lost after settings apply — {audit.Describe()}");
            else if (audit.Stale.Count > 0)
                Log.Warn($"Global hotkey state does not match settings — {audit.Describe()}");
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID_ASSISTANT)
            {
                Dispatcher.Invoke(OpenAssistant);
                return (IntPtr)1;
            }
            if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID_CLIPBOARD)
            {
                Dispatcher.Invoke(ToggleClipboardHistory);
                return (IntPtr)1;
            }
            if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID_INBOX)
            {
                Dispatcher.Invoke(ToggleNotificationInbox);
                return (IntPtr)1;
            }
            if (msg == WM_CLIPBOARDUPDATE)
            {
                HandleClipboardUpdate();
            }
            if (msg == WM_POWERBROADCAST &&
                (wParam.ToInt32() == PBT_APMPOWERSTATUSCHANGE || wParam.ToInt32() == PBT_POWERSETTINGCHANGE))
            {
                _batteryService.RefreshNow();
            }
            if (msg == WM_DISPLAYCHANGE)
            {
                // 모니터 구성이 바뀌었다 — 노치의 자리도 다시 정한다. 고른 모니터가 빠졌다면
                // MonitorLayout이 주 모니터로 물러서고, 주 모니터가 바뀌었으면 따라간다.
                RunOnUi(() =>
                {
                    UpdatePosition();
                    if (_clipboardHistoryOpen) RepositionClipboardHistoryPanel();
                });
            }
            if (msg == WM_NCHITTEST)
            {
                try
                {
                    int x = unchecked((short)(long)lParam);
                    int y = unchecked((short)((long)lParam >> 16));

                    if (!NotchBorder.IsLoaded)
                    {
                        handled = true;
                        return (IntPtr)HTTRANSPARENT;
                    }

                    var screenPoint = new Point(x, y);

                    // 기록패널은 노치 밖에 떠있지만 클릭을 받아야 한다
                    if (IsInside(ClipboardHistoryPanel, screenPoint))
                    {
                        return IntPtr.Zero;
                    }

                    Point pt = NotchBorder.PointFromScreen(screenPoint);

                    if (pt.X < 0 || pt.Y < 0 || pt.X > NotchBorder.ActualWidth || pt.Y > NotchBorder.ActualHeight)
                    {
                        handled = true;
                        return (IntPtr)HTTRANSPARENT;
                    }
                }
                catch
                {
                    handled = true;
                    return (IntPtr)HTTRANSPARENT;
                }
            }
            return IntPtr.Zero;
        }

        /// <summary>
        /// 배경 스레드(COM 콜백·SMTC·WinForms 트레이)의 이벤트를 UI 스레드로 넘긴다.
        ///
        /// Dispatcher.Invoke를 그대로 쓰면 두 가지가 걸린다: UI 스레드가 바쁠 때 배경 스레드가
        /// 붙잡히고, 창이 닫힌 뒤 도착한 이벤트는 예외를 던져 전역 핸들러를 거쳐 모달 오류창이 된다
        /// (파형·미디어 이벤트는 초당 수십 번 온다). 그래서 종료 중에는 버리고, 그 밖에는 비동기로 넘긴다.
        /// </summary>
        private void RunOnUi(Action action)
        {
            if (_shuttingDown) return;

            Dispatcher dispatcher = Dispatcher;
            if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;

            if (dispatcher.CheckAccess()) action();
            else dispatcher.BeginInvoke(action);
        }

        /// <summary>화면 좌표가 이 요소의 실제 렌더 영역 안인가 (보이지 않으면 false).</summary>
        private static bool IsInside(FrameworkElement element, Point screenPoint)
        {
            if (element.Visibility != Visibility.Visible) return false;
            if (element.ActualWidth < 1 || element.ActualHeight < 1) return false;

            try
            {
                Point pt = element.PointFromScreen(screenPoint);
                return pt.X >= 0 && pt.Y >= 0 && pt.X <= element.ActualWidth && pt.Y <= element.ActualHeight;
            }
            catch
            {
                return false;
            }
        }

        private void MainWindow_Closed(object? sender, EventArgs e)
        {
            // 배경 콜백이 이 뒤로 UI를 건드리지 않게 먼저 잠근다
            _shuttingDown = true;

            _progressTimer.Stop();
            _clockTimer.Stop();
            _volumeHudTimer?.Stop();
            _emptyMediaDebounceTimer?.Stop();
            _notificationTimer?.Stop();
            _clipboardToastTimer?.Stop();
            _assistantDoneTimer?.Stop();
            _chargingBeamTimer?.Stop();
            StopNotchRadiusAnimation();
            _audioBarsTimer.Stop();
            _audioCapture.Dispose();

            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                RemoveClipboardFormatListener(hwnd);
                UnregisterHotKey(hwnd, HOTKEY_ID_ASSISTANT);
                UnregisterHotKey(hwnd, HOTKEY_ID_CLIPBOARD);
                UnregisterHotKey(hwnd, HOTKEY_ID_INBOX);   // 예전엔 인박스 핫키만 남겨 두었다
            }

            // 돌고 있는 요청은 취소만 한다 — CTS는 그 요청의 finally가 자기 자리에서 정리한다
            // (여기서 Dispose하면 아직 풀리는 중인 토큰 등록이 깨진다).
            _assistantCts?.Cancel();
            _assistantCts = null;
            _assistant.ResetConversation();

            // 트레이는 프로세스가 살아 있는 동안 계속 보인다 — 창이 닫히는 경로(트레이 종료가 아닌 경우)에서도 걷는다
            _trayIcon?.Dispose();
            _trayIcon = null;
            _trayIconImage?.Dispose();   // NotifyIcon은 자기 Icon을 정리하지 않는다
            _trayIconImage = null;

            _audioService.Dispose();
            _mediaService.Dispose();
            _lyricsService.Dispose();
            _batteryService.Dispose();
            _notificationService.Dispose();
            _assistantTools.Dispose();   // 도구가 들고 있는 HttpClient들(날씨·유튜브)
            _assistant.Dispose();        // 모델 클라이언트의 HttpClient
        }
    }
}
