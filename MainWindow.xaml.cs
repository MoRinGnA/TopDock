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
        private enum ViewMode { IdleCompact, IdleExpanded, MediaCompact, MediaExpanded, VolumeHud, NotificationCompact, NotificationExpanded, Assistant }

        /// <summary>배터리는 퍼센트가 아니라 3분류로만 판단한다.</summary>
        private enum BatteryLevel { Normal, Charging, Low }

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WM_NCHITTEST = 0x0084;
        private const int HTTRANSPARENT = -1;

        // AI 비서 전역 핫키: Ctrl+Shift+Space
        private const int HOTKEY_ID_ASSISTANT = 0xB00B;
        private const int HOTKEY_ID_CLIPBOARD = 0xB00C;   // Ctrl+Shift+V
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint MOD_NOREPEAT = 0x4000;
        private const int WM_HOTKEY = 0x0312;

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

        private const int WM_CLIPBOARDUPDATE = 0x031D;

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

        // ── 배터리 표시 ──
        private BatteryLevel _batteryLevel = BatteryLevel.Normal;
        private float _batteryPercent = -1f;

        private readonly DispatcherTimer _progressTimer;
        private readonly DispatcherTimer _clockTimer;
        private DispatcherTimer? _volumeHudTimer;
        private DispatcherTimer? _notificationTimer;
        private DispatcherTimer? _emptyMediaDebounceTimer;
        private Storyboard? _eqStoryboard;

        // 이퀄라이저 바는 테두리 빛과 같은 색을 쓴다 — 브러시 하나를 세 바가 공유하고 색만 바꾼다
        private readonly SolidColorBrush _equalizerBrush = new(EqualizerIdleColor);
        private Color _equalizerAccent = EqualizerIdleColor;
        private static readonly Color EqualizerIdleColor = Color.FromRgb(0xF0, 0xF3, 0xF8);

        // 트랙 전환 사이 SMTC가 잠깐 보고하는 빈 미디어를 필터링하기 위한 유예 시간
        private static readonly TimeSpan EmptyMediaDebounceDelay = TimeSpan.FromMilliseconds(1500);

        private ViewMode _currentViewMode = ViewMode.IdleCompact;
        private ViewMode _viewModeBeforeNotification = ViewMode.IdleCompact;
        private string _lastMediaKey = string.Empty;
        private Color? _albumColor;   // 현재 트랙 앨범 지배색 — 노치 테두리 빛에 물린다
        private bool _orbCentral;     // 중앙(작업 중) 오브가 떠 있는가
        private string _lastBeamState = "";
        private bool _isExpanded = false;
        private bool _isVolumeAdjusting = false;
        private bool _volumeBarVisible = false;
        private bool HasMedia => !string.IsNullOrEmpty(_lastMediaKey);
        private List<LyricLine> _syncedLyrics = new();
        private System.Windows.Forms.NotifyIcon? _trayIcon;

        public MainWindow()
        {
            InitializeComponent();
            StartStatusSheen(); // 상태 텍스트 광택 스윕 (원작 thinking-orbs 디테일)

            // 세 바가 브러시 하나를 공유 — UpdateEqualizerAccent가 색만 갈아 끼우면 전부 바뀐다
            EqBar1.Background = _equalizerBrush;
            EqBar2.Background = _equalizerBrush;
            EqBar3.Background = _equalizerBrush;

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
                // 도구가 실제로 하는 일을 오브 디자인으로 드러낸다(검색=Globe, 연결=Web, 소리=Wave).
                var (kind, status) = OrbForTool(name);
                PostOrb(kind, status);
            };
            _assistantTools.ToolFinished += _ =>
            {
                // 도구가 끝나면 모델 추론이 이어지므로 '사고 중'으로 — 단, 이미 응답이
                // 흐르기 시작했다면(델타 수신 뒤) 헤더의 응답 오브로 돌아간다.
                PostOrb(_assistantStreaming ? Controls.OrbKind.Composing : Controls.OrbKind.Working);
            };
            _assistant.Tools = _assistantTools;

            _progressTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(50)
            };
            _progressTimer.Tick += ProgressTimer_Tick;

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
            _batteryService.Start();
            
            SwitchViewMode(ViewMode.IdleCompact);
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);

            // 비서 화면에서는 포커스 위치와 무관하게 Esc로 닫기 (입력창이 먼저 받아도 무방)
            if (e.Key == Key.Escape && _currentViewMode == ViewMode.Assistant)
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

            // 복사 기록을 어디서든 꺼내는 전역 단축키 (Ctrl+Shift+V)
            if (!RegisterHotKey(hwnd, HOTKEY_ID_CLIPBOARD, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, 0x56))
                Log.Warn("Clipboard hotkey Ctrl+Shift+V is already taken by another app");

            // AI 비서 전역 핫키 등록 (Ctrl+Shift+Space)
            if (ConfigService.Current.AssistantEnabled)
            {
                RegisterHotKey(hwnd, HOTKEY_ID_ASSISTANT, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, 0x20);
            }

            UpdatePosition();
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
            if (msg == WM_CLIPBOARDUPDATE)
            {
                HandleClipboardUpdate();
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
            _progressTimer.Stop();
            _clockTimer.Stop();
            _volumeHudTimer?.Stop();
            _emptyMediaDebounceTimer?.Stop();
            StopNotchRadiusAnimation();
            StopEqualizerAnimation();

            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                RemoveClipboardFormatListener(hwnd);
                UnregisterHotKey(hwnd, HOTKEY_ID_ASSISTANT);
                UnregisterHotKey(hwnd, HOTKEY_ID_CLIPBOARD);
            }

            _assistantCts?.Cancel();
            _assistant.ResetConversation();

            _audioService.Dispose();
            _mediaService.Dispose();
            _lyricsService.Dispose();
            _batteryService.Dispose();
            _notificationService.Dispose();
        }
    }
}
