using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using TopDock.Models;
using TopDock.Services;
using MenuItem = System.Windows.Forms.MenuItem;

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
        private readonly SponsorSkipService _sponsorSkip;
        private readonly AssistantService _assistant;
        private readonly YouTubeSearchService _youTubeSearch = new();
        private SettingsWindow? _settingsWindow;
        private string _lastMarkerSignature = string.Empty;

        // ── AI 비서 상태 ──
        private CancellationTokenSource? _assistantCts;
        private bool _assistantBusy;
        private bool _assistantStreaming; // 첫 델타 이후 응답 스트리밍 중인가

        // ── 배터리 표시 ──
        private BatteryLevel _batteryLevel = BatteryLevel.Normal;
        private float _batteryPercent = -1f;
        private bool _batteryPulseRunning;
        // 클립보드 히스토리 한 항목 — 텍스트 / 이미지 / 파일 중 하나 (최근 5개, 최신이 앞)
        private sealed class ClipboardItem
        {
            public ClipboardItem(string text, BitmapSource? image, string[]? files = null)
            {
                Text = text;
                Image = image;
                Files = files;
            }

            public string Text { get; }
            public BitmapSource? Image { get; }
            public string[]? Files { get; }
            public BitmapSource? Thumbnail { get; set; }   // 목록 미리보기용 축소본 (지연 생성)

            public bool IsImage => Image != null;
            public bool IsFiles => Files is { Length: > 0 };
        }

        private const int HistoryLimit = 5;

        private readonly List<ClipboardItem> _clipboardHistory = new();
        private DispatcherTimer? _clipboardToastTimer;

        // 붙여넣기 대상: 복사가 일어난 순간의 전경 창
        private IntPtr _pasteTargetHwnd;
        private bool _pasteInFlight;

        // 앱이 스스로 클립보드에 쓴 내용의 지문 — 이벤트가 안 오더라도 다음 정상 복사를 삼키지 않게 한다
        private string? _selfCopySignature;
        private DateTime _selfCopyAt = DateTime.MinValue;

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
        private bool _hasLyrics = false;
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
            _sponsorSkip = new SponsorSkipService { IsEnabled = ConfigService.Current.SponsorSkipEnabled, Mode = ConfigService.Current.SponsorSkipMode };
            _assistant = new AssistantService();
            _assistant.ToolExecutor = ExecuteAssistantToolAsync;

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

        /// <summary>
        /// 클립보드가 바뀔 때마다 불린다. 읽기는 한 번만 하고(다른 앱과의 잠금 경쟁 최소화)
        /// 우리 앱이 쓴 내용이면 무시한 뒤, 표시는 UI 쪽으로 넘긴다.
        /// </summary>
        private void HandleClipboardUpdate()
        {
            ClipboardItem? item = ReadClipboard();
            if (item == null) return;

            // 방금 앱이 스스로 쓴 내용(항목 클릭 → 재복사)은 새 항목으로 잡지 않는다
            if (IsSelfCopy(item)) return;

            // 붙여넣기 대상은 '복사가 일어난 순간'의 전경 창 — 노치가 아니라 사용자가 쓰던 앱이어야 한다
            IntPtr foreground = GetForegroundWindow();
            if (foreground != IntPtr.Zero && foreground != new WindowInteropHelper(this).Handle)
                _pasteTargetHwnd = foreground;

            Dispatcher.InvokeAsync(() => ShowClipboardToast(item));
        }

        /// <summary>
        /// 클립보드를 한 번만 열이 이미지 / 파일 / 텍스트를 읽는다.
        /// (기존에는 Contains→Get을 형식마다 반복해 OpenClipboard를 4번 시도했다)
        /// </summary>
        private static ClipboardItem? ReadClipboard()
        {
            // 다른 앱이 클립보드를 쥐고 있으면 열기가 실패한다 — 짧게 재시도 (최대 ~75ms)
            for (int attempt = 0; attempt < 4; attempt++)
            {
                try
                {
                    System.Windows.IDataObject? data = Clipboard.GetDataObject();
                    if (data == null) return null;

                    // Win+Shift+S 캡처 등 이미지가 본체인 경우 → 이미지 우선 (기존 동작 유지)
                    if (data.GetDataPresent(DataFormats.Bitmap) && data.GetData(DataFormats.Bitmap) is BitmapSource image)
                    {
                        image.Freeze();   // 해시·썸네일 계산 전에 동결해야 안전하다
                        return new ClipboardItem(string.Empty, image);
                    }

                    // 탐색기 파일 복사(CF_HDROP) — 그동안 조용히 무시되던 형식
                    if (data.GetDataPresent(DataFormats.FileDrop)
                        && data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                    {
                        return new ClipboardItem(string.Empty, null, files);
                    }

                    // 빈 문자열/공백만 있는 복사는 히스토리에서도 제외
                    if (data.GetDataPresent(DataFormats.UnicodeText)
                        && data.GetData(DataFormats.UnicodeText) is string text && !string.IsNullOrWhiteSpace(text))
                    {
                        return new ClipboardItem(text, null);
                    }

                    return null;
                }
                catch (COMException)
                {
                    Thread.Sleep(25);   // 클립보드 잠금 경쟁
                }
                catch
                {
                    return null;
                }
            }
            return null;
        }

        /// <summary>내용 지문 — 우리가 쓴 클립보드를 되읽지 않기 위한 비교용. 계산 불가면 null.</summary>
        private static string? Signature(ClipboardItem item)
        {
            if (item.IsImage && item.Image != null)
            {
                byte[] hash = ComputeImageHash(item.Image);
                return hash.Length == 0 ? null : "i:" + Convert.ToBase64String(hash);
            }
            if (item.IsFiles) return "f:" + string.Join('\n', item.Files!);
            return "t:" + item.Text;
        }

        private bool IsSelfCopy(ClipboardItem item)
        {
            if (_selfCopySignature == null) return false;
            if (DateTime.UtcNow - _selfCopyAt > TimeSpan.FromSeconds(2)) return false;
            try { return Signature(item) == _selfCopySignature; }
            catch { return false; }
        }

        private void ShowClipboardToast(ClipboardItem item)
        {
            // 히스토리는 토스트 설정과 무관하게 항상 수집한다
            AddToHistory(item);

            if (!ConfigService.Current.ShowClipboardToast) return;
            // 복사는 노치 안에서만 조용히 알린다
            // 기록패널이 열려있으면 목록만 갱신한다
            if (_clipboardHistoryOpen)
            {
                BuildClipboardHistoryRows();
                return;
            }

            ShowClipboardFeedback();
        }

        private void AddToHistory(ClipboardItem item)
        {
            // 중복은 새로 쌓지 않고 맨 위로 올린다 (이미지는 픽셀 해시 비교 — 캡처를 여러 번 떠도 1장만 유지)
            if (item.IsImage && item.Image != null)
            {
                byte[] newHash = ComputeImageHash(item.Image);
                _clipboardHistory.RemoveAll(i =>
                {
                    if (!i.IsImage || i.Image == null) return false;
                    return HashEquals(ComputeImageHash(i.Image), newHash);
                });
            }
            else if (item.IsFiles)
            {
                string key = string.Join('\n', item.Files!);
                _clipboardHistory.RemoveAll(i => i.IsFiles && string.Join('\n', i.Files!) == key);
            }
            else
            {
                _clipboardHistory.RemoveAll(i => !i.IsImage && !i.IsFiles && i.Text == item.Text);
            }

            _clipboardHistory.Insert(0, item);
            if (_clipboardHistory.Count > HistoryLimit) _clipboardHistory.RemoveAt(HistoryLimit);
            _clipboardSelectedIndex = -1;
        }

        private static string ClipboardRowLabel(ClipboardItem item)
        {
            if (item.IsImage) return $"캡처 {item.Image!.PixelWidth}×{item.Image.PixelHeight}";
            if (item.IsFiles)
            {
                string? name = SafeFileName(item.Files![0]);
                return item.Files.Length == 1 ? name : $"{name} 외 {item.Files.Length - 1}개 파일";
            }
            return Collapse(item.Text, 120);
        }

        private static string SafeFileName(string? path)
        {
            if (string.IsNullOrEmpty(path)) return "파일";
            string name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
            return string.IsNullOrEmpty(name) ? path : name;
        }

        /// <summary>공백(개행·탭 포함)을 단일 공백으로 접고 max자에서 자른다.</summary>
        private static string Collapse(string text, int max)
        {
            const int ScanLimit = 512;   // 아주 큰 텍스트는 앞부분만 본다
            int consumed = Math.Min(text.Length, ScanLimit);
            var sb = new StringBuilder(consumed);
            bool pendingSpace = false;
            for (int i = 0; i < consumed; i++)
            {
                char c = text[i];
                if (char.IsWhiteSpace(c)) { pendingSpace = sb.Length > 0; continue; }
                if (pendingSpace) { sb.Append(' '); pendingSpace = false; }
                sb.Append(c);
            }

            bool truncated = text.Length > consumed;
            string collapsed = sb.ToString();
            if (collapsed.Length > max)
            {
                collapsed = collapsed[..max];
                truncated = true;
            }
            return truncated ? collapsed + "…" : collapsed;
        }

        /// <summary>목록 미리보기용 축소본. 원본은 재복사 정확도를 위해 그대로 보관한다.</summary>
        private static BitmapSource? ThumbnailFor(ClipboardItem item)
        {
            if (item.Thumbnail != null || item.Image == null) return item.Thumbnail;

            const int MaxSide = 96;
            try
            {
                var image = item.Image;
                double scale = Math.Min(1.0, (double)MaxSide / Math.Max(image.PixelWidth, image.PixelHeight));
                BitmapSource thumb = scale >= 1.0
                    ? image
                    : new TransformedBitmap(image, new ScaleTransform(scale, scale));
                thumb.Freeze();
                item.Thumbnail = thumb;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Clipboard thumbnail failed: {ex.Message}");
            }
            return item.Thumbnail;
        }

        /// <summary>항목을 클립보드에 올린다. 다른 앱이 쥐고 있으면 짧게 재시도.</summary>
        private bool CopyToClipboard(ClipboardItem item)
        {
            for (int attempt = 0; attempt < 4; attempt++)
            {
                try
                {
                    if (item.IsImage && item.Image != null)
                    {
                        Clipboard.SetImage(item.Image);
                    }
                    else if (item.IsFiles)
                    {
                        var list = new StringCollection();
                        list.AddRange(item.Files!);
                        Clipboard.SetFileDropList(list);
                    }
                    else if (!string.IsNullOrWhiteSpace(item.Text))
                    {
                        Clipboard.SetText(item.Text);
                    }
                    else
                    {
                        return false;
                    }

                    _selfCopySignature = Signature(item);
                    _selfCopyAt = DateTime.UtcNow;
                    return true;
                }
                catch (COMException)
                {
                    Thread.Sleep(25);
                }
                catch
                {
                    return false;
                }
            }
            Log.Warn("Clipboard copy failed: clipboard locked by another app");
            return false;
        }

        /// <summary>항목 사용: 클립보드에 올리고 → 최근 순서 맨 앞으로 → 직전 창에 붙여넣기.</summary>
        private void UseClipboardItem(ClipboardItem item)
        {
            if (!CopyToClipboard(item)) return;

            // 재사용한 항목도 최근 순서 맨 앞으로 (다시 쓰기 쉬운 위치로)
            _clipboardHistory.Remove(item);
            _clipboardHistory.Insert(0, item);
            _clipboardSelectedIndex = -1;

            HideClipboardHistoryPanel();
            PasteIntoPasteTarget();
        }

        /// <summary>직전 창으로 포커스를 돌린 뒤 Ctrl+V를 주입한다. 실패하면 복사만 남긴다.</summary>
        private async void PasteIntoPasteTarget()
        {
            IntPtr target = _pasteTargetHwnd;
            if (_pasteInFlight || target == IntPtr.Zero || !IsWindow(target))
            {
                // 붙여넣을 창을 모를 때(복사 순간에 전경 창이 없었을 때) — 복사만 해 둔다
                Log.Info("Clipboard paste: no target window, kept clipboard only");
                return;
            }

            _pasteInFlight = true;
            try
            {
                // 전경 창 전환은 비동기라 실제로 넘어갈 때까지 짧게 기다린다 (최대 ~300ms)
                for (int i = 0; i < 12 && GetForegroundWindow() != target; i++)
                {
                    SetForegroundWindow(target);
                    await Task.Delay(25);
                }

                if (GetForegroundWindow() != target)
                {
                    // 정책상 포커스를 되찾지 못한 경우 — 복사는 이미 됐으므로 사용자가 직접 Ctrl+V 하면 된다
                    Log.Info("Clipboard paste: target refused focus, kept clipboard only");
                    return;
                }

                await Task.Delay(60);   // 활성화 직후 앱이 키 입력을 받을 준비를 할 여유
                SendCtrlV();
                Log.Info("Clipboard paste: Ctrl+V sent");
            }
            catch (Exception ex)
            {
                Log.Warn($"Clipboard paste failed: {ex.Message}");
            }
            finally
            {
                _pasteInFlight = false;
            }
        }

        private static void SendCtrlV()
        {
            var inputs = new[]
            {
                KeyInput(VK_CONTROL, down: true),
                KeyInput(VK_V, down: true),
                KeyInput(VK_V, down: false),
                KeyInput(VK_CONTROL, down: false),
            };
            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        }

        private static INPUT KeyInput(ushort vk, bool down) => new()
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = down ? 0u : KEYEVENTF_KEYUP } },
        };

        // 이미지 저해상도 평균 해시 (aHash): 크기/포맷이 달라도 같은 그림이면 같은 해시
        private static byte[] ComputeImageHash(BitmapSource image)
        {
            const int Size = 8;

            // 비정상 이미지 방어
            if (image.PixelWidth <= 0 || image.PixelHeight <= 0)
                return Array.Empty<byte>();

            var scaled = new TransformedBitmap(image, new ScaleTransform(
                (double)Size / image.PixelWidth, (double)Size / image.PixelHeight));
            var converted = new FormatConvertedBitmap(scaled, PixelFormats.Gray8, null, 0);

            // Gray8 = 픽셀당 1바이트 → stride = 폭 (바이트 단위)
            int width = converted.PixelWidth;   // 8
            int height = converted.PixelHeight; // 8
            int stride = width;                 // Gray8: 1 byte per pixel
            byte[] pixels = new byte[height * stride];
            converted.CopyPixels(pixels, stride, 0);

            // 평균 밝기보다 크면 1, 작으면 0 → 64비트 해시
            long sum = 0;
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    sum += pixels[y * stride + x];
            double avg = (double)sum / (width * height);

            byte[] hash = new byte[8];
            int bit = 0;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (pixels[y * stride + x] > avg)
                        hash[bit / 8] |= (byte)(1 << (bit % 8));
                    bit++;
                }
            }
            return hash;
        }

        private static bool HashEquals(byte[] a, byte[] b)
        {
            if (a.Length == 0 || b.Length == 0) return false;
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++)
            {
                int x = a[i] ^ b[i];
                while (x != 0) { diff += x & 1; x >>= 1; } // Hamming distance
            }
            return diff <= 2; // 약간의 압축/리샘플 차이는 허용
        }

        private void ClipboardToastTimer_Tick(object? sender, EventArgs e)
        {
            _clipboardToastTimer!.Stop();
            HideClipboardFeedback();
        }

        // ── 복사 피드백: 아일랜드 안쪽 배지 + 가장자리 빛 펄스 ──

        /// <summary>복사 직후의 조용한 알림 — 떠 있는 창을 뛰우지 않고 노치 안에서만 알린다.</summary>
        private void ShowClipboardFeedback()
        {
            ClipboardFeedbackChip.Visibility = Visibility.Visible;
            ClipboardFeedbackChip.BeginAnimation(UIElement.OpacityProperty, null);
            ClipboardFeedbackScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ClipboardFeedbackScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);

            var ease = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 4 };
            var fadeIn = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(130))) { EasingFunction = ease };
            var popIn = new DoubleAnimation(0.65, 1, new Duration(TimeSpan.FromMilliseconds(240))) { EasingFunction = ease };
            Timeline.SetDesiredFrameRate(fadeIn, 60);
            Timeline.SetDesiredFrameRate(popIn, 60);
            ClipboardFeedbackChip.BeginAnimation(UIElement.OpacityProperty, fadeIn);
            ClipboardFeedbackScale.BeginAnimation(ScaleTransform.ScaleXProperty, popIn);
            ClipboardFeedbackScale.BeginAnimation(ScaleTransform.ScaleYProperty, popIn);

            // 가장자리 빛 펄스 — 복사된 것을 "빛으로" 알린다
            var flash = new DoubleAnimationUsingKeyFrames();
            flash.KeyFrames.Add(new EasingDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            flash.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(150)))
            { EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 3 } });
            flash.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(380))));
            flash.KeyFrames.Add(new EasingDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1100)))
            { EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 2 } });
            Timeline.SetDesiredFrameRate(flash, 60);
            NotchBeam.BeginAnimation(Controls.BorderBeam.FlashProperty, flash);

            // 재시작 가능한 일회성 타이머 — 연속 복사 시 경쟁 상태 없이 사라지는 시점만 미룬다
            if (_clipboardToastTimer == null)
            {
                _clipboardToastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1300) };
                _clipboardToastTimer.Tick += ClipboardToastTimer_Tick;
            }
            _clipboardToastTimer.Stop();
            _clipboardToastTimer.Start();
        }

        private void HideClipboardFeedback()
        {
            var ease = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 3 };
            var fade = new DoubleAnimation(0, new Duration(TimeSpan.FromMilliseconds(220))) { EasingFunction = ease };
            Timeline.SetDesiredFrameRate(fade, 60);
            fade.Completed += (s, e) =>
            {
                // 그사이 또 복사됐으면 그대로 둔다
                if (_clipboardToastTimer == null || !_clipboardToastTimer.IsEnabled)
                    ClipboardFeedbackChip.Visibility = Visibility.Collapsed;
            };
            ClipboardFeedbackChip.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        // 복사 알림 · 기록패널 (Ctrl+Shift+V)

        private bool _clipboardHistoryOpen;

        // 키보드(↑↓/Enter/Delete)로 고른 항목 — -1이면 선택 없음
        private int _clipboardSelectedIndex = -1;

        /// <summary>기록 패널 열기/닫기 — 단축키와 노치 클릭이 공유하는 입구.</summary>
        private void ToggleClipboardHistory()
        {
            if (_clipboardHistoryOpen)
            {
                HideClipboardHistoryPanel();
            }
            else if (_clipboardHistory.Count > 0)
            {
                ShowClipboardHistoryPanel();
            }
        }

        /// <summary>기록 패널을 연다 — 노치 아래 중앙에 내려온다.</summary>
        /// <summary>노치 본체 클릭 → 기록 열기/닫기. 확장 뷰의 버튼들은 자기 클릭을 먼저 처리한다.</summary>
        private void Notch_Click(object sender, MouseButtonEventArgs e)
        {
            if (_currentViewMode == ViewMode.Assistant) return;   // 대화 중엔 입력을 가로채지 않는다
            if (_clipboardHistory.Count == 0) return;

            ToggleClipboardHistory();
            e.Handled = true;
        }

        private void ShowClipboardHistoryPanel()
        {
            BuildClipboardHistoryRows();
            _clipboardHistoryOpen = true;

            _clipboardToastTimer?.Stop();

            ClipboardHistoryPanel.Visibility = Visibility.Visible;
            // 먼저 배치해야 패널 실제 크기가 나온다 — 그 전에 계산하면 좌상단(0,0)에 붙는다
            ClipboardHistoryPanel.UpdateLayout();
            RepositionClipboardHistoryPanel();
            var ease = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 5 };
            var dur = new Duration(TimeSpan.FromMilliseconds(280));
            var opacity = new DoubleAnimation(0, 1, dur) { EasingFunction = ease };
            var slide = new DoubleAnimation(-8, 0, dur) { EasingFunction = ease };
            Timeline.SetDesiredFrameRate(opacity, 60);
            Timeline.SetDesiredFrameRate(slide, 60);
            ClipboardHistoryPanel.BeginAnimation(UIElement.OpacityProperty, opacity);
            ClipboardHistoryTransform.BeginAnimation(TranslateTransform.YProperty, slide);
        }

        private void HideClipboardHistoryPanel()
        {
            if (!_clipboardHistoryOpen) return;
            _clipboardHistoryOpen = false;

            var dur = new Duration(TimeSpan.FromMilliseconds(200));
            var opacity = new DoubleAnimation(0, dur);
            opacity.Completed += (s, e) =>
            {
                if (!_clipboardHistoryOpen) ClipboardHistoryPanel.Visibility = Visibility.Collapsed;
            };
            ClipboardHistoryPanel.BeginAnimation(UIElement.OpacityProperty, opacity);
            ClipboardHistoryTransform.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(-8, dur));
        }

        private void BuildClipboardHistoryRows()
        {
            ClipboardHistoryRows.Children.Clear();
            int count = Math.Min(_clipboardHistory.Count, HistoryLimit);
            for (int i = 0; i < count; i++)
            {
                ClipboardHistoryRows.Children.Add(BuildClipboardHistoryRow(_clipboardHistory[i], i));
            }
        }

        private Border BuildClipboardHistoryRow(ClipboardItem item, int index)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // 이미지 항목은 썸네일로 — 캡처가 여러 장이어도 구분된다
            BitmapSource? thumb = ThumbnailFor(item);
            if (thumb != null)
            {
                var preview = new Image
                {
                    Source = thumb,
                    Height = 30,
                    MaxWidth = 48,
                    Stretch = Stretch.Uniform,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 9, 0),
                    IsHitTestVisible = false,
                };
                RenderOptions.SetBitmapScalingMode(preview, BitmapScalingMode.HighQuality);
                Grid.SetColumn(preview, 0);
                grid.Children.Add(preview);
            }

            var text = new TextBlock
            {
                Text = ClipboardRowLabel(item),
                Foreground = new SolidColorBrush(Color.FromArgb(0xE5, 0xFF, 0xFF, 0xFF)),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);

            // 개별 삭제 — 호버할 때만 보이는 최소 표시
            var remove = new TextBlock
            {
                Text = "✕",
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 2, 0),
                Cursor = Cursors.Hand,
                Opacity = 0,
                ToolTip = "이 항목 삭제",
            };
            remove.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;   // 행 클릭(붙여넣기)으로 번지지 않게
                RemoveFromHistory(item);
            };
            Grid.SetColumn(remove, 2);
            grid.Children.Add(remove);

            var row = new Border
            {
                Background = index == _clipboardSelectedIndex ? SelectedRowBrush : Brushes.Transparent,
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(9, 5, 9, 5),
                Margin = new Thickness(0, 1, 0, 1),
                Cursor = Cursors.Hand,
                Tag = item,
                Child = grid,
            };
            row.MouseLeftButtonUp += ClipboardHistoryRow_Click;
            row.MouseEnter += (s, e) =>
            {
                remove.Opacity = 1;
                row.Background = HoverRowBrush;
            };
            row.MouseLeave += (s, e) =>
            {
                remove.Opacity = 0;
                row.Background = index == _clipboardSelectedIndex ? SelectedRowBrush : Brushes.Transparent;
            };
            return row;
        }

        private static readonly Brush HoverRowBrush = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
        private static readonly Brush SelectedRowBrush = new SolidColorBrush(Color.FromArgb(0x33, 0x0A, 0x84, 0xFF));

        private void ClipboardHistoryRow_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border border && border.Tag is ClipboardItem item)
            {
                UseClipboardItem(item);
            }
        }

        private void RemoveFromHistory(ClipboardItem item)
        {
            _clipboardHistory.Remove(item);
            _clipboardSelectedIndex = -1;

            if (_clipboardHistory.Count == 0)
            {
                HideClipboardHistoryPanel();
                return;
            }
            BuildClipboardHistoryRows();
        }

        private void RemoveSelectedFromHistory()
        {
            if (_clipboardSelectedIndex < 0 || _clipboardSelectedIndex >= _clipboardHistory.Count) return;
            RemoveFromHistory(_clipboardHistory[_clipboardSelectedIndex]);
        }

        private void ClipboardClearButton_Click(object sender, RoutedEventArgs e)
        {
            _clipboardHistory.Clear();
            _clipboardSelectedIndex = -1;
            HideClipboardHistoryPanel();
        }

        private void MoveClipboardSelection(int delta)
        {
            if (_clipboardHistory.Count == 0) return;
            int current = _clipboardSelectedIndex < 0 ? (delta > 0 ? -1 : _clipboardHistory.Count) : _clipboardSelectedIndex;
            _clipboardSelectedIndex = Math.Clamp(current + delta, 0, _clipboardHistory.Count - 1);
            BuildClipboardHistoryRows();
        }

        private void ActivateClipboardSelection()
        {
            if (_clipboardSelectedIndex < 0 || _clipboardSelectedIndex >= _clipboardHistory.Count) return;
            UseClipboardItem(_clipboardHistory[_clipboardSelectedIndex]);
        }


        /// <summary>기록 패널을 노치 아래 중앙에 내린다.</summary>
        private void RepositionClipboardHistoryPanel()
        {
            if (ClipboardHistoryPanel.Visibility != Visibility.Visible) return;
            try
            {
                double scaleX = VisualTreeHelper.GetDpi(this).DpiScaleX;
                double scaleY = VisualTreeHelper.GetDpi(this).DpiScaleY;

                // 물리 크기 정규화 — 어느 배율에서도 같은 크기로 보이게
                double panelScale = scaleY > 0 ? Math.Min(1.75 / scaleY, 2.2) : 1.0;
                ClipboardHistoryPanel.LayoutTransform = new ScaleTransform(panelScale, panelScale);
                double panelWidth = ClipboardHistoryPanel.ActualWidth * panelScale;

                Point notchBottom = NotchBorder.PointToScreen(new Point(NotchBorder.ActualWidth / 2, NotchBorder.ActualHeight));
                Point origin = RootGrid.PointToScreen(new Point(0, 0));
                double left = (notchBottom.X - origin.X) / scaleX - panelWidth / 2;

                // 화면 밖으로 밀려나지 않게 좌우를 눌러 둔다
                double screenWidth = SystemParameters.PrimaryScreenWidth;
                left = Math.Clamp(left, 8, Math.Max(8, screenWidth - panelWidth - 8));

                Canvas.SetLeft(ClipboardHistoryPanel, left);
                Canvas.SetTop(ClipboardHistoryPanel, (notchBottom.Y - origin.Y) / scaleY + 10);

                Log.Info("Clipboard panel layout: "
                    + $"scale={scaleX:F2} panelScale={panelScale:F2} "
                    + $"panel={ClipboardHistoryPanel.ActualWidth:F0}x{ClipboardHistoryPanel.ActualHeight:F0} "
                    + $"notch={notchBottom.X:F0},{notchBottom.Y:F0} origin={origin.X:F0},{origin.Y:F0} "
                    + $"left={Canvas.GetLeft(ClipboardHistoryPanel):F0} top={Canvas.GetTop(ClipboardHistoryPanel):F0}");
            }
            catch
            {
                Canvas.SetLeft(ClipboardHistoryPanel, ActualWidth / 2 - ClipboardHistoryPanel.ActualWidth / 2);
                Canvas.SetTop(ClipboardHistoryPanel, 60);
            }
        }

        private void UpdatePosition()
        {
            this.Left = 0;
            this.Top = ConfigService.Current.TopMargin;
            this.Width = SystemParameters.PrimaryScreenWidth;
            this.Height = SystemParameters.PrimaryScreenHeight;
        }

        private void ClockTimer_Tick(object? sender, EventArgs e)
        {
            // 시계 UI는 없앴지만 시각은 AI 비서 컨텍스트용으로 계속 갱신
            _currentClockText = DateTime.Now.ToString("yyyy-MM-dd dddd HH:mm");
        }

        private string _currentClockText = string.Empty;

        // ── AI 얼굴 표정 관리 ──

        private Controls.AiFace? ActiveFace =>
            _currentViewMode == ViewMode.IdleExpanded ? ExpandedFace :
            _currentViewMode == ViewMode.IdleCompact ? CompactFace : null;

        /// <summary>Idle 상태에서 표정만 갱신한다. 인사·상태 텍스트는 전면 제거 (미니멀 원칙).</summary>
        private void RefreshIdleFace()
        {
            var face = ActiveFace;
            if (face == null) return;
            face.SetState(Controls.AiFace.FaceState.Idle);
        }

        private void Notch_MouseEnter(object sender, MouseEventArgs e)
        {
            _isExpanded = true;


            if (_currentViewMode == ViewMode.Assistant) return;
            if (_volumeHudTimer != null && _volumeHudTimer.IsEnabled) return;
            if (_notificationTimer != null && _notificationTimer.IsEnabled)
            {
                SwitchViewMode(ViewMode.NotificationExpanded);
                return;
            }
            SwitchViewMode(HasMedia ? ViewMode.MediaExpanded : ViewMode.IdleExpanded);
            RefreshIdleFace();
        }

        private void Notch_MouseLeave(object sender, MouseEventArgs e)
        {
            // AI 비서 대화 중에는 마우스가 벗어나도 닫지 않는다 (닫기는 ✕ 버튼 또는 Esc)
            if (_currentViewMode == ViewMode.Assistant)
            {
                _isVolumeAdjusting = false;
                HideVolumeBarExpanded();
                return;
            }
            _isExpanded = false;
            _isVolumeAdjusting = false;
            HideVolumeBarExpanded();

            if (_volumeHudTimer != null && _volumeHudTimer.IsEnabled) return;
            if (_notificationTimer != null && _notificationTimer.IsEnabled)
            {
                SwitchViewMode(ViewMode.NotificationCompact);
                return;
            }
            SwitchViewMode(HasMedia ? ViewMode.MediaCompact : ViewMode.IdleCompact);
            CompactFace?.SetState(Controls.AiFace.FaceState.Idle);
        }

        private void Notch_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            float step = 0.02f;
            int newVol = _audioService.StepVolume(e.Delta > 0 ? step : -step, out bool isMuted);
            ShowVolumeHud(newVol, isMuted);
            e.Handled = true;
        }

        // ── 노치 테두리 빛: 원본 border-beam을 순정 그대로 사용(색·강도 커스텀 없음).
        //    beam은 콘텐츠 위에서 항상 도는 오버레이라 XAML에서 Active=True 상태로 상주한다.
        //    앨범색·시간대 팔레트 등 기존 엠비언트 라이트는 전부 제거됐다.

        /// <summary>노치 크기 변화에 맞춰 beam을 함께 모핑한다(본체와 같은 크기 유지).</summary>
        private void UpdateGlowDimensions(double targetWidth, double targetHeight, double targetRadius, Duration duration, IEasingFunction ease)
        {
            var wAnim = new DoubleAnimation { To = targetWidth, Duration = duration, EasingFunction = ease };
            var hAnim = new DoubleAnimation { To = targetHeight, Duration = duration, EasingFunction = ease };
            var rAnim = new DoubleAnimation { To = targetRadius, Duration = duration, EasingFunction = ease };

            Timeline.SetDesiredFrameRate(wAnim, 60);
            Timeline.SetDesiredFrameRate(hAnim, 60);
            Timeline.SetDesiredFrameRate(rAnim, 60);

            NotchBeam.BeginAnimation(WidthProperty, wAnim);
            NotchBeam.BeginAnimation(HeightProperty, hAnim);
            NotchBeam.BeginAnimation(Controls.BorderBeam.CornerRadiusProperty, rAnim);
        }

        private double CalculateCompactWidth()
        {
            string title = CompactTitleText.Text ?? "";
            string lyric = CompactLyricText.Text ?? "";

            double titleWidth = MeasureTextWidth(title, 13.5, FontWeights.SemiBold);
            double lyricWidth = MeasureTextWidth(lyric, 13, FontWeights.Medium);

            double baseWidth = 45;
            double calculated = baseWidth + titleWidth + lyricWidth + 35;
            return Math.Clamp(calculated, 320, 800);
        }

        private double MeasureTextWidth(string text, double fontSize, FontWeight fontWeight)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            var formattedText = new FormattedText(
                text,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI, -apple-system"), FontStyles.Normal, fontWeight, FontStretches.Normal),
                fontSize,
                Brushes.White,
                VisualTreeHelper.GetDpi(this).PixelsPerDip
            );
            return formattedText.Width;
        }

        /// <summary>배터리는 퍼센트가 아니라 3분류로 다룬다(일반/충전/부족).
        /// 노치 테두리를 통째로 칠하면 노치에서 가장 밝은 요소가 되어 beam과 색이 섞이므로,
        /// 안쪽 우측의 작은 점으로만 조용히 알리고 펼치면 퍼센트를 텍스트로 보여준다.</summary>
        private void BatteryService_BatteryStatusChanged(object? sender, BatteryStatusArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                _batteryPercent = e.BatteryPercent;

                var level = e.IsCharging
                    ? BatteryLevel.Charging
                    : e.BatteryPercent <= 0.20f ? BatteryLevel.Low : BatteryLevel.Normal;

                // 상태가 실제로 바뀐 순간에만 한 번 밝게 깜빡인다(꽂는 순간의 체감).
                bool flash = level != _batteryLevel && level != BatteryLevel.Normal;
                _batteryLevel = level;

                ApplyBatteryIndicator(flash);
            });
        }

        /// <summary>현재 뷰와 배터리 상태에 맞춰 점·퍼센트를 다시 그린다. 뷰 전환에서도 호출된다.</summary>
        private void ApplyBatteryIndicator(bool flash = false)
        {
            // 일반 상태에서는 아무것도 그리지 않는다 — 조용한 게 기본값.
            if (_batteryLevel == BatteryLevel.Normal)
            {
                BatteryDot.Visibility = Visibility.Collapsed;
                BatteryDot.BeginAnimation(UIElement.OpacityProperty, null);
                BatteryDot.Opacity = 1;
                _batteryPulseRunning = false;
                ExpandedBatteryPanel.Visibility = Visibility.Collapsed;
                return;
            }

            Color accent = _batteryLevel == BatteryLevel.Charging
                ? Color.FromRgb(0x34, 0xC7, 0x59)   // 충전 중 — 애플 그린
                : Color.FromRgb(0xFF, 0x45, 0x3A);  // 배터리 부족 — 애플 레드

            var core = new SolidColorBrush(accent);
            core.Freeze();
            var glow = new RadialGradientBrush
            {
                Center = new Point(0.5, 0.5),
                GradientOrigin = new Point(0.5, 0.5),
                RadiusX = 0.5,
                RadiusY = 0.5,
            };
            glow.GradientStops.Add(new GradientStop(accent, 0.0));
            glow.GradientStops.Add(new GradientStop(Color.FromArgb(0, accent.R, accent.G, accent.B), 1.0));
            glow.Freeze();

            BatteryDotCore.Fill = core;
            BatteryDotGlow.Fill = glow;
            ExpandedBatteryDot.Fill = core;

            // 점은 접힌 기본 아일랜드에서만, 퍼센트 텍스트는 펼친 화면에서만.
            BatteryDot.Visibility = _currentViewMode == ViewMode.IdleCompact
                ? Visibility.Visible : Visibility.Collapsed;
            ExpandedBatteryPanel.Visibility = _currentViewMode == ViewMode.IdleExpanded
                ? Visibility.Visible : Visibility.Collapsed;
            ExpandedBatteryText.Text =
                $"{(_batteryLevel == BatteryLevel.Charging ? "충전" : "부족")} {Math.Round(_batteryPercent * 100)}%";

            if (flash)
            {
                // 꽂는 순간: 밝게 나타났다가 잠깐 어두워지고, 끝나면 pulse로 넘어간다.
                BatteryDot.BeginAnimation(UIElement.OpacityProperty, null);
                BatteryDot.Opacity = 1;
                var blink = new DoubleAnimation(1.0, 0.35, new Duration(TimeSpan.FromMilliseconds(260)))
                {
                    AutoReverse = true,
                };
                blink.Completed += (_, _) => StartBatteryPulse();
                BatteryDot.BeginAnimation(UIElement.OpacityProperty, blink);
            }
            else
            {
                StartBatteryPulse();
            }
        }

        /// <summary>충전 중에만 은은하게 숨쉬게 한다. 부족은 고정 — 경고가 흔들리면 거슬린다.</summary>
        private void StartBatteryPulse()
        {
            bool shouldPulse = _batteryLevel == BatteryLevel.Charging;
            if (shouldPulse == _batteryPulseRunning) return;

            _batteryPulseRunning = shouldPulse;
            BatteryDot.BeginAnimation(UIElement.OpacityProperty, null);
            BatteryDot.Opacity = 1;
            if (!shouldPulse) return;

            BatteryDot.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation
            {
                From = 0.45,
                To = 1.0,
                Duration = new Duration(TimeSpan.FromSeconds(1.8)),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            });
        }

        private void NotificationService_NotificationReceived(object? sender, NotificationEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                if (!ConfigService.Current.ShowNotifications) return;

                NotifCompactAppText.Text = e.AppName;
                NotifCompactTitleText.Text = string.IsNullOrEmpty(e.Title) ? e.Body : e.Title;
                
                NotifExpandedAppText.Text = e.AppName;
                NotifExpandedTitleText.Text = e.Title;
                NotifExpandedBodyText.Text = e.Body;

                if (_notificationTimer == null || !_notificationTimer.IsEnabled)
                {
                    _viewModeBeforeNotification = _currentViewMode;
                }

                SwitchViewMode(_isExpanded ? ViewMode.NotificationExpanded : ViewMode.NotificationCompact);

                if (_notificationTimer == null)
                {
                    _notificationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                    _notificationTimer.Tick += (s, args) =>
                    {
                        _notificationTimer.Stop();
                        // 알림이 비서 대화를 잠시 가린 경우라면 비서 화면으로 되돌아간다
                        if (_viewModeBeforeNotification == ViewMode.Assistant)
                        {
                            SwitchViewMode(ViewMode.Assistant);
                            return;
                        }
                        if (_isExpanded)
                        {
                            SwitchViewMode(HasMedia ? ViewMode.MediaExpanded : ViewMode.IdleExpanded);
                        }
                        else
                        {
                            SwitchViewMode(HasMedia ? ViewMode.MediaCompact : ViewMode.IdleCompact);
                        }
                    };
                }
                _notificationTimer.Stop();
                _notificationTimer.Start();
            });
        }

        private void SwitchViewMode(ViewMode mode)
        {
            _currentViewMode = mode;

            // 확장은 여유 있게, 축소는 빠르게 — 모프 방향에 따라 리듬을 다르게 (targetWidth 결정 후 계산)
            Duration duration = new Duration(TimeSpan.FromMilliseconds(450));
            ExponentialEase ease = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 };

            double targetWidth = 100;
            double targetHeight = 38;
            double targetRadius = 19;
            UIElement activeView = IdleCompactView;

            switch (mode)
            {
                case ViewMode.IdleCompact:
                    // 얼굴만 있으니 노치 기본 실루엣(100×38) 그대로 유지
                    targetWidth = 100;
                    targetHeight = 38;
                    targetRadius = 19;
                    activeView = IdleCompactView;
                    break;
                case ViewMode.IdleExpanded:
                    targetWidth = 260;
                    // 클립보드는 오른쪽 분리 패널로만 표시 — 본체 확장은 얼굴+상태+버튼만
                    targetHeight = 120;
                    targetRadius = 26;
                    activeView = IdleExpandedView;
                    break;
                case ViewMode.MediaCompact:
                    targetWidth = CalculateCompactWidth();
                    targetHeight = 38;
                    targetRadius = 19;
                    activeView = MediaCompactView;
                    break;
                case ViewMode.MediaExpanded:
                    targetWidth = _hasLyrics ? 620 : 276;
                    targetHeight = 190;
                    targetRadius = 36;
                    activeView = MediaExpandedView;
                    break;
                case ViewMode.VolumeHud:
                    targetWidth = _isExpanded ? (_hasLyrics ? 620 : 276) : 240;
                    targetHeight = _isExpanded ? 190 : 38;
                    targetRadius = _isExpanded ? 36 : 19;
                    activeView = _isExpanded ? MediaExpandedView : VolumeHudView;
                    break;
                case ViewMode.NotificationCompact:
                    targetWidth = Math.Clamp(MeasureTextWidth(NotifCompactAppText.Text, 13.5, FontWeights.Bold) + MeasureTextWidth(NotifCompactTitleText.Text, 13, FontWeights.SemiBold) + 80, 260, 500);
                    targetHeight = 38;
                    targetRadius = 19;
                    activeView = NotificationCompactView;
                    break;
                case ViewMode.NotificationExpanded:
                    targetWidth = 360;
                    targetHeight = 160;
                    targetRadius = 32;
                    activeView = NotificationExpandedView;
                    break;
                case ViewMode.Assistant:
                    // 지시 콘솔은 컴팩트하게 — 큰 캔버스 대신 오브+결과+입력 3단 구성
                    targetWidth = 420;
                    targetHeight = 224;
                    targetRadius = 30;
                    activeView = AssistantView;
                    // 헤더 오버레이(오브+상태+버튼)는 본체 뷰와 독립적으로 함께 표시
                    AssistantHeaderOverlay.IsHitTestVisible = true;
                    AssistantHeaderOverlay.BeginAnimation(UIElement.OpacityProperty,
                        new DoubleAnimation { To = 1, Duration = duration, EasingFunction = ease });
                    break;
            }

            // AI 탭이 아니면 헤더 오버레이를 닫는다 (독립 페이드아웃)
            if (mode != ViewMode.Assistant)
            {
                AssistantHeaderOverlay.IsHitTestVisible = false;
                AssistantHeaderOverlay.BeginAnimation(UIElement.OpacityProperty,
                    new DoubleAnimation { To = 0, Duration = new Duration(TimeSpan.FromMilliseconds(150)) });
            }

            // 접힌 상태로 돌아가면 볼륨바 오버레이도 함께 정리한다 (확장 상태에서만 쓰는 UI)
            if (!_isExpanded && _volumeBarVisible)
            {
                _volumeBarVisible = false;
                VolumeBarPanel.BeginAnimation(UIElement.OpacityProperty, null);
                VolumeBarPanel.Visibility = Visibility.Collapsed;
                VolumeBarTransform.BeginAnimation(TranslateTransform.YProperty, null);
                VolumeBarTransform.Y = -10;
            }

            SetViewActive(activeView, duration, ease);
            NotchBorder.CornerRadius = new CornerRadius(targetRadius);

            // 배터리 표시는 뷰에 따라 자리만 바뀐다 — 접힌 아일랜드는 점, 펼치면 퍼센트.
            ApplyBatteryIndicator();

            bool expanding = targetWidth > NotchBorder.Width;
            duration = new Duration(TimeSpan.FromMilliseconds(expanding ? 520 : 380));

            DoubleAnimation widthAnim = new DoubleAnimation { To = targetWidth, Duration = duration, EasingFunction = ease };
            DoubleAnimation heightAnim = new DoubleAnimation { To = targetHeight, Duration = duration, EasingFunction = ease };

            Timeline.SetDesiredFrameRate(widthAnim, 60);
            Timeline.SetDesiredFrameRate(heightAnim, 60);

            NotchBorder.BeginAnimation(Border.WidthProperty, widthAnim);
            NotchBorder.BeginAnimation(Border.HeightProperty, heightAnim);

            UpdateGlowDimensions(targetWidth, targetHeight, targetRadius, duration, ease);

            DoubleAnimation mainContainerHeightAnim = new DoubleAnimation { To = targetHeight, Duration = duration, EasingFunction = ease };
            Timeline.SetDesiredFrameRate(mainContainerHeightAnim, 60);
            MainContainer.BeginAnimation(FrameworkElement.HeightProperty, mainContainerHeightAnim);
        }

        private void SetViewActive(UIElement activeView, Duration duration, IEasingFunction ease)
        {
            UIElement[] views = { VolumeHudView, IdleCompactView, IdleExpandedView, MediaCompactView, MediaExpandedView, NotificationCompactView, NotificationExpandedView, AssistantView };
            Duration fadeOutDuration = new Duration(TimeSpan.FromMilliseconds(150));

            foreach (var view in views)
            {
                if (view == activeView)
                {
                    view.IsHitTestVisible = true;
                    DoubleAnimation fadeIn = new DoubleAnimation { To = 1, Duration = duration, EasingFunction = ease };
                    view.BeginAnimation(UIElement.OpacityProperty, fadeIn);
                }
                else
                {
                    view.IsHitTestVisible = false;
                    DoubleAnimation fadeOut = new DoubleAnimation { To = 0, Duration = fadeOutDuration };
                    view.BeginAnimation(UIElement.OpacityProperty, fadeOut);
                }
            }
        }

        private void AudioService_VolumeChanged(int volume, bool isMuted)
        {
            Dispatcher.Invoke(() =>
            {
                ShowVolumeHud(volume, isMuted);
            });
        }

        private void ShowVolumeHud(int volumeVal, bool isMuted)
        {
            // AI 비서 대화 중에는 볼륨 UI가 대화창에 끼어들지 않게 차단
            if (_currentViewMode == ViewMode.Assistant)
            {
                StartHudTimer();
                return;
            }

            _isVolumeAdjusting = true;
            string volStr = isMuted ? "Mute" : $"{volumeVal}%";

            VolumeProgressBar.Value = volumeVal;
            VolumeText.Text = volStr;

            VolumeBarProgressBar.Value = volumeVal;
            VolumeBarText.Text = volStr;

            if (_isExpanded)
            {
                ShowVolumeBarExpanded();
            }
            else
            {
                SwitchViewMode(ViewMode.VolumeHud);
            }
            StartHudTimer();
        }
        private static readonly Duration NotchAnimDuration = new Duration(TimeSpan.FromMilliseconds(450));
        private static readonly ExponentialEase NotchAnimEase = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 };

        /// <summary>
        /// 볼륨바는 볼륨을 만지는 동안만 아래에서 올라오는 오버레이다.
        /// 본체 높이는 건드리지 않는다 — 크기가 바뀌면 테두리 빛이 본체와 어긋나 어색해진다.
        /// 그래서 본체·빛의 크기는 SwitchViewMode 한 곳에서만 결정된다.
        /// </summary>
        private void ShowVolumeBarExpanded()
        {
            VolumeBarPanel.Visibility = Visibility.Visible;

            VolumeBarPanel.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation { To = 1, Duration = NotchAnimDuration, EasingFunction = NotchAnimEase });

            VolumeBarTransform.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation { To = 0, Duration = NotchAnimDuration, EasingFunction = NotchAnimEase });

            _volumeBarVisible = true;
        }

        private void HideVolumeBarExpanded()
        {
            if (!_volumeBarVisible) return;
            _volumeBarVisible = false;

            var opacityAnim = new DoubleAnimation { To = 0, Duration = NotchAnimDuration, EasingFunction = NotchAnimEase };
            opacityAnim.Completed += (s, e) =>
            {
                if (!_volumeBarVisible)
                {
                    VolumeBarPanel.Visibility = Visibility.Collapsed;
                }
            };
            VolumeBarPanel.BeginAnimation(UIElement.OpacityProperty, opacityAnim);

            VolumeBarTransform.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation { To = -10, Duration = NotchAnimDuration, EasingFunction = NotchAnimEase });
        }

        private void StartHudTimer()
        {
            if (_volumeHudTimer == null)
            {
                _volumeHudTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.3) };
                _volumeHudTimer.Tick += (s, args) =>
                {
                    _volumeHudTimer.Stop();
                    _isVolumeAdjusting = false;

                    if (_isExpanded)
                    {
                        HideVolumeBarExpanded();
                    }
                    else
                    {
                        SwitchViewMode(HasMedia ? ViewMode.MediaCompact : ViewMode.IdleCompact);
                    }
                };
            }
            _volumeHudTimer.Stop();
            _volumeHudTimer.Start();
        }

        private void MediaService_MediaChanged(MediaMetadata? media)
        {
            Dispatcher.Invoke(async () =>
            {
                if (media == null || string.IsNullOrWhiteSpace(media.Title))
                {
                    // 트랙 전환 시 SMTC가 순간적으로 빈 미디어를 보고하는 것을 유예 시간으로 필터링.
                    // 그 안에 새 곡이 도착하면 리셋 없이 기존 라이트에서 바로 크로스페이드됨.
                    StartEmptyMediaDebounce();
                    return;
                }

                _emptyMediaDebounceTimer?.Stop();

                string key = $"{media.Artist}:::{media.Title}";
                bool isNewTrack = _lastMediaKey != key;

                if (isNewTrack)
                {
                    _lastMediaKey = key;

                    var parsed = LyricsService.ParseTitleAndArtist(media.Title, media.Artist);
                    string displayTitle = string.IsNullOrEmpty(parsed.Title) ? media.Title : parsed.Title;
                    string displayArtist = string.IsNullOrEmpty(parsed.Artist) ? (string.IsNullOrEmpty(media.Artist) ? "YouTube" : media.Artist) : parsed.Artist;

                    CompactTitleText.Text = displayTitle;
                    if (!_isVolumeAdjusting)
                    {
                        ExpandedTitleText.Text = displayTitle;
                        ExpandedArtistText.Text = displayArtist;
                    }

                    string searchArtist = displayArtist == "YouTube" ? string.Empty : displayArtist;
                    await LoadLyricsAsync(displayTitle, searchArtist);

                    if (_volumeHudTimer == null || !_volumeHudTimer.IsEnabled)
                    {
                        SwitchViewMode(_isExpanded ? ViewMode.MediaExpanded : ViewMode.MediaCompact);
                    }

                    // 새 트랙의 스폰서 구간을 백그라운드로 조회 (videoId → SponsorBlock)
                    _ = _sponsorSkip.LoadSegmentsForTrackAsync(key, media.Title, media.Artist);
                }

                bool isValidThumbnail = false;
                BitmapSource? validBmp = null;
                if (media.HasThumbnail && media.Thumbnail is BitmapSource bmp)
                {
                    if (bmp.PixelWidth >= 48 && bmp.PixelHeight >= 48 && !IsLikelyFavicon(bmp))
                    {
                        isValidThumbnail = true;
                        validBmp = bmp;
                    }
                }

                if (isValidThumbnail && validBmp != null)
                {
                    ExpandedAlbumArtImage.Source = media.Thumbnail;
                    ExpandedDefaultIcon.Visibility = Visibility.Collapsed;
                    // 앨범 지배색 — 미디어 재생 중 테두리 빛을 이 색으로 물들인다.
                    _albumColor = ExtractDominantColor(validBmp);
                }
                else
                {
                    ExpandedAlbumArtImage.Source = null;
                    ExpandedDefaultIcon.Visibility = Visibility.Visible;
                    _albumColor = null; // 이전 트랙 색이 남지 않게

                    if (isNewTrack)
                    {
                        _ = RetryThumbnailAsync(key);
                    }
                }

                if (isNewTrack)
                    Log.Info($"Media track: {key} thumb={isValidThumbnail} playing={media.IsPlaying}");

                UpdatePlaybackState(media.IsPlaying);
                UpdateTimelineDisplay(media.CurrentEstimatedPosition, media.Duration);
            });
        }

        private async Task RetryThumbnailAsync(string expectedKey)
        {
            for (int i = 0; i < 6; i++)
            {
                await Task.Delay(500);

                if (_lastMediaKey != expectedKey) return;

                await _mediaService.UpdateMediaPropertiesAsync();

                var media = _mediaService.CurrentMedia;
                if (media?.HasThumbnail == true && media.Thumbnail is BitmapSource bmp)
                {
                    if (bmp.PixelWidth >= 48 && bmp.PixelHeight >= 48 && !IsLikelyFavicon(bmp))
                    {
                        Dispatcher.Invoke(() =>
                        {
                            if (_lastMediaKey == expectedKey)
                            {
                                ExpandedAlbumArtImage.Source = media.Thumbnail;
                                ExpandedDefaultIcon.Visibility = Visibility.Collapsed;
                                _albumColor = ExtractDominantColor(bmp);
                                UpdateBeamState();
                            }
                        });
                        return;
                    }
                }
            }
        }

        private static bool IsLikelyFavicon(BitmapSource bmp)
        {
            try
            {
                if (bmp.PixelWidth < 48 || bmp.PixelHeight < 48) return true;

                var converted = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
                int stride = converted.PixelWidth * 4;
                byte[] pixels = new byte[converted.PixelHeight * stride];
                converted.CopyPixels(pixels, stride, 0);

                int w = converted.PixelWidth;
                int h = converted.PixelHeight;
                (int x, int y)[] corners = { (0, 0), (w - 1, 0), (0, h - 1), (w - 1, h - 1) };

                int transparentCorners = 0;
                foreach (var (x, y) in corners)
                {
                    int idx = (y * stride) + (x * 4);
                    if (idx + 3 < pixels.Length && pixels[idx + 3] < 250)
                    {
                        transparentCorners++;
                    }
                }

                return transparentCorners >= 4;
            }
            catch
            {
                return false;
            }
        }

        private void MediaService_PlaybackStatusChanged(bool isPlaying)
        {
            Dispatcher.Invoke(() =>
            {
                UpdatePlaybackState(isPlaying);
            });
        }

        private void MediaService_TimelineChanged(TimeSpan position, TimeSpan duration)
        {
            Dispatcher.Invoke(() =>
            {
                UpdateTimelineDisplay(position, duration);
            });
        }

        private void UpdatePlaybackState(bool isPlaying)
        {
            if (isPlaying)
            {
                _progressTimer.Start();
                StartEqualizerAnimation();
                PlayPauseIcon.Data = Geometry.Parse("M6 19h4V5H6v14zm8-14v14h4V5h-4z");
            }
            else
            {
                _progressTimer.Stop();
                StopEqualizerAnimation();
                PlayPauseIcon.Data = Geometry.Parse("M8 5v14l11-7z");
            }

            UpdateBeamState();
        }

        private static readonly TimeSpan LyricLookahead = TimeSpan.FromMilliseconds(500);

        private void ProgressTimer_Tick(object? sender, EventArgs e)
        {
            if (_mediaService.GetExactPosition(out var currentPos, out var duration))
            {
                // 스폰서/인트로 등 스킵 대상 구간 진입 시 자동 건너뛰기 (auto 모드 한정)
                var skipTarget = _sponsorSkip.GetSkipTarget(currentPos, duration);
                if (skipTarget.HasValue)
                {
                    _ = _mediaService.TrySeekAsync(skipTarget.Value);
                    SponsorSkipButton.Visibility = Visibility.Collapsed;
                }
                else if (_sponsorSkip.IsInSkipSegment(currentPos, duration))
                {
                    // manual 모드: 흐름은 끊지 않고 우상단에 건너뛰기 버튼만 노출
                    if (SponsorSkipButton.Visibility != Visibility.Visible)
                    {
                        SponsorSkipButton.Visibility = Visibility.Visible;
                    }
                }
                else
                {
                    SponsorSkipButton.Visibility = Visibility.Collapsed;
                }

                UpdateTimelineDisplay(currentPos, duration);
                // 가사 표시는 500ms 미리 룩업하여 실제 음악과 싱크 맞춤
                var lyricPos = _lyricsService.GetAdjustedPosition(currentPos + LyricLookahead, duration);
                UpdateLyricsDisplay(lyricPos);
            }
        }

        private async void SponsorSkipButton_Click(object sender, RoutedEventArgs e)
        {
            if (_mediaService.GetExactPosition(out var pos, out var dur))
            {
                if (_sponsorSkip.GetManualSkipTarget(pos, dur) is { } target)
                {
                    await _mediaService.TrySeekAsync(target).ConfigureAwait(true);
                }
            }
            SponsorSkipButton.Visibility = Visibility.Collapsed;
        }

        private void UpdateTimelineDisplay(TimeSpan currentPos, TimeSpan duration)
        {
            if (duration.TotalSeconds > 0)
            {
                double progress = (currentPos.TotalSeconds / duration.TotalSeconds) * 100;
                progress = Math.Clamp(progress, 0, 100);
                ExpandedProgressBar.Value = progress;

                CurrentTimeText.Text = currentPos.ToString(@"m\:ss");
                TotalTimeText.Text = duration.ToString(@"m\:ss");
            }
            else
            {
                ExpandedProgressBar.Value = 0;
                CurrentTimeText.Text = "0:00";
                TotalTimeText.Text = "0:00";
            }

            RenderProgressMarkers(duration);
        }

        private void RenderProgressMarkers(TimeSpan duration)
        {
            try
            {
                if (!(_sponsorSkip.HasSegments && duration.TotalSeconds > 0))
                {
                    if (ProgressMarkerCanvas.Children.Count > 0) ProgressMarkerCanvas.Children.Clear();
                    return;
                }

                double width = ExpandedProgressBar.ActualWidth;
                if (width <= 0) return;

                // 50ms 타이머마다 재생성하지 않도록, 구성이 바뀐 경우에만 다시 그린다
                string signature = $"{_sponsorSkip.Segments.Count}:{duration.TotalSeconds:F1}:{width:F0}";
                if (signature == _lastMarkerSignature) return;
                _lastMarkerSignature = signature;

                ProgressMarkerCanvas.Children.Clear();

                foreach (var seg in _sponsorSkip.Segments)
                {
                    if (seg.StartTime >= duration) continue;

                    double left = Math.Clamp(seg.StartTime.TotalSeconds / duration.TotalSeconds, 0, 1) * width;
                    double segWidth = Math.Clamp(seg.Duration.TotalSeconds / duration.TotalSeconds, 0, 1) * width;
                    segWidth = Math.Max(3, segWidth);

                    var rect = new System.Windows.Shapes.Rectangle
                    {
                        Width = segWidth,
                        Height = 4,
                        RadiusX = 1.5,
                        RadiusY = 1.5,
                        Fill = GetSegmentBrush(seg.Category),
                        Opacity = 0.9
                    };
                    System.Windows.Controls.Canvas.SetLeft(rect, Math.Min(left, Math.Max(0, width - 3)));
                    ProgressMarkerCanvas.Children.Add(rect);
                }
            }
            catch { }
        }

        private static Brush GetSegmentBrush(string category) => category switch
        {
            "music_offtopic" => new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x0A)),
            "intro" => new SolidColorBrush(Color.FromRgb(0x00, 0xC7, 0xB7)),
            "outro" => new SolidColorBrush(Color.FromRgb(0x5E, 0x5C, 0xE6)),
            "intermission" => new SolidColorBrush(Color.FromRgb(0xBF, 0x5A, 0xF2)),
            _ => new SolidColorBrush(Color.FromRgb(0x00, 0xD1, 0x66)) // sponsor
        };

        private async Task LoadLyricsAsync(string rawTitle, string rawArtist)
        {
            _syncedLyrics.Clear();
            _hasLyrics = false;
            SetLyricsVisibility(false);

            var lyrics = await _lyricsService.GetLyricsAsync(rawTitle, rawArtist);
            if (lyrics != null && lyrics.Count > 0)
            {
                _syncedLyrics = lyrics;
                _hasLyrics = true;
                SetLyricsVisibility(true);
            }
            else
            {
                _syncedLyrics.Clear();
                _hasLyrics = false;
                SetLyricsVisibility(false);
            }

            if (_isExpanded && (_volumeHudTimer == null || !_volumeHudTimer.IsEnabled))
            {
                SwitchViewMode(ViewMode.MediaExpanded);
            }
        }

        private void ClipboardToggleButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleClipboardHistory();
        }

        private void SetLyricsVisibility(bool visible)
        {
            if (visible)
            {
                LyricsDivider.Visibility = Visibility.Visible;
                LyricsContainer.Visibility = Visibility.Visible;

                MediaExpandedView.HorizontalAlignment = HorizontalAlignment.Stretch;
                LyricsDividerCol.Width = GridLength.Auto;
                LyricsCol.Width = new GridLength(1, GridUnitType.Star);
            }
            else
            {
                LyricsDivider.Visibility = Visibility.Collapsed;
                LyricsContainer.Visibility = Visibility.Collapsed;

                MediaExpandedView.HorizontalAlignment = HorizontalAlignment.Center;
                LyricsDividerCol.Width = new GridLength(0);
                LyricsCol.Width = new GridLength(0);
            }
        }
        private void UpdateLyricsDisplay(TimeSpan currentPos)
        {
            if (_syncedLyrics.Count == 0)
            {
                CompactLyricText.Text = string.Empty;
                return;
            }

            int activeIndex = -1;
            for (int i = 0; i < _syncedLyrics.Count; i++)
            {
                if (_syncedLyrics[i].Time <= currentPos)
                {
                    activeIndex = i;
                }
                else
                {
                    break;
                }
            }

            if (activeIndex >= 0)
            {
                string activeText = _syncedLyrics[activeIndex].Text;
                bool lyricChanged = CurrentLyricText.Text != activeText;
                CompactLyricText.Text = activeText;
                PrevLyricText.Text = activeIndex > 0 ? _syncedLyrics[activeIndex - 1].Text : string.Empty;
                CurrentLyricText.Text = activeText;
                NextLyricText.Text = activeIndex < _syncedLyrics.Count - 1 ? _syncedLyrics[activeIndex + 1].Text : string.Empty;

                // 가사가 바뀔 때 살짝 떠오르는 트랜지션
                if (lyricChanged)
                {
                    AnimateLyricLine(CurrentLyricText);
                }

                if (!_isExpanded && _currentViewMode == ViewMode.MediaCompact)
                {
                    double newWidth = CalculateCompactWidth();
                    if (Math.Abs(NotchBorder.Width - newWidth) > 5)
                    {
                        var animDuration = TimeSpan.FromMilliseconds(200);
                        var animEase = new QuadraticEase();
                        DoubleAnimation widthAnim = new DoubleAnimation { To = newWidth, Duration = animDuration, EasingFunction = animEase };
                        NotchBorder.BeginAnimation(Border.WidthProperty, widthAnim);
                        UpdateGlowDimensions(newWidth, 38, 19, animDuration, animEase);
                    }
                }
            }
            else
            {
                CompactLyricText.Text = string.Empty;
                PrevLyricText.Text = string.Empty;
                CurrentLyricText.Text = "...";
                NextLyricText.Text = _syncedLyrics.Count > 0 ? _syncedLyrics[0].Text : string.Empty;
            }
        }

        private void AnimateLyricLine(TextBlock text)
        {
            var transform = text.RenderTransform as TranslateTransform;
            if (transform == null)
            {
                transform = new TranslateTransform();
                text.RenderTransform = transform;
            }

            var fade = new DoubleAnimation
            {
                From = 0.35,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(260),
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut }
            };
            var slide = new DoubleAnimation
            {
                From = 7,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(300),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Timeline.SetDesiredFrameRate(fade, 60);
            Timeline.SetDesiredFrameRate(slide, 60);

            text.BeginAnimation(UIElement.OpacityProperty, fade);
            transform.BeginAnimation(TranslateTransform.YProperty, slide);
        }

        private void StartEmptyMediaDebounce()
        {
            if (_emptyMediaDebounceTimer == null)
            {
                _emptyMediaDebounceTimer = new DispatcherTimer { Interval = EmptyMediaDebounceDelay };
                _emptyMediaDebounceTimer.Tick += (s, e) =>
                {
                    _emptyMediaDebounceTimer.Stop();

                    // 유예 시간이 끝난 시점에도 여전히 미디어가 없으면 진짜 종료로 판단
                    var current = _mediaService.CurrentMedia;
                    if (current == null || string.IsNullOrWhiteSpace(current.Title))
                    {
                        ResetToEmptyMedia();
                    }
                };
            }
            _emptyMediaDebounceTimer.Stop();
            _emptyMediaDebounceTimer.Start();
        }

        private void ResetToEmptyMedia()
        {
            _lastMediaKey = string.Empty;
            _albumColor = null;
            UpdateBeamState();
            CompactTitleText.Text = "재생 중인 미디어 없음";
            CompactLyricText.Text = string.Empty;
            ExpandedTitleText.Text = "재생 중인 미디어 없음";
            ExpandedArtistText.Text = string.Empty;
            ExpandedAlbumArtImage.Source = null;
            ExpandedDefaultIcon.Visibility = Visibility.Visible;
            StopEqualizerAnimation();

            _syncedLyrics.Clear();
            _hasLyrics = false;
            SetLyricsVisibility(false);

            ExpandedProgressBar.Value = 0;
            CurrentTimeText.Text = "0:00";
            TotalTimeText.Text = "0:00";
            _progressTimer.Stop();

            if (_volumeHudTimer == null || !_volumeHudTimer.IsEnabled)
            {
                SwitchViewMode(_isExpanded ? ViewMode.IdleExpanded : ViewMode.IdleCompact);
            }
        }

        /// <summary>이퀄라이저 바 색을 현재 곡의 색으로 맞춘다 (테두리 빛과 동일한 색).</summary>
        private void UpdateEqualizerAccent(Color color)
        {
            if (_equalizerAccent == color) return;
            _equalizerAccent = color;
            _equalizerBrush.Color = color;   // 이미 붙어 있는 브러시 색만 바꾼다 (재할당 없음)
        }

        private void StartEqualizerAnimation()
        {
            StopEqualizerAnimation();

            _eqStoryboard = new Storyboard();

            var anim1 = new DoubleAnimation(4, 13, TimeSpan.FromMilliseconds(380))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };
            Storyboard.SetTarget(anim1, EqBar1);
            Storyboard.SetTargetProperty(anim1, new PropertyPath(Border.HeightProperty));

            var anim2 = new DoubleAnimation(14, 5, TimeSpan.FromMilliseconds(460))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };
            Storyboard.SetTarget(anim2, EqBar2);
            Storyboard.SetTargetProperty(anim2, new PropertyPath(Border.HeightProperty));

            var anim3 = new DoubleAnimation(6, 12, TimeSpan.FromMilliseconds(330))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };
            Storyboard.SetTarget(anim3, EqBar3);
            Storyboard.SetTargetProperty(anim3, new PropertyPath(Border.HeightProperty));

            _eqStoryboard.Children.Add(anim1);
            _eqStoryboard.Children.Add(anim2);
            _eqStoryboard.Children.Add(anim3);
            _eqStoryboard.Begin();
        }

        private void StopEqualizerAnimation()
        {
            if (_eqStoryboard != null)
            {
                _eqStoryboard.Stop();
                _eqStoryboard = null;
            }
            EqBar1.Height = 3;
            EqBar2.Height = 3;
            EqBar3.Height = 3;
        }

        private async void PrevButton_Click(object sender, RoutedEventArgs e)
        {
            await _mediaService.TrySkipPreviousAsync();
        }

        private async void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            if (_mediaService.CurrentMedia != null)
            {
                bool toggled = !_mediaService.CurrentMedia.IsPlaying;
                _mediaService.CurrentMedia.IsPlaying = toggled;
                UpdatePlaybackState(toggled);
            }
            await _mediaService.TryTogglePlayPauseAsync();
        }

        private async void NextButton_Click(object sender, RoutedEventArgs e)
        {
            await _mediaService.TrySkipNextAsync();
        }

        private void InitializeTrayIcon()
        {
            _trayIcon = new System.Windows.Forms.NotifyIcon
            {
                Icon = System.Drawing.SystemIcons.Application,
                Visible = true,
                Text = "TopDock"
            };

            var contextMenu = new System.Windows.Forms.ContextMenuStrip();
            contextMenu.Items.Add("열기/포커스", null, (s, e) =>
            {
                this.Activate();
            });

            contextMenu.Items.Add("설정", null, (s, e) => OpenSettings());

            contextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            contextMenu.Items.Add("종료", null, (s, e) => TrayExit_Click());

            _trayIcon.ContextMenuStrip = contextMenu;
            _trayIcon.DoubleClick += (s, e) => this.Activate();
        }

        private void OpenSettings()
        {
            if (_settingsWindow != null)
            {
                _settingsWindow.Activate();
                return;
            }

            _settingsWindow = new SettingsWindow();
            _settingsWindow.SettingsSaved += (s, e) => ApplySettings();
            _settingsWindow.Closed += (s, e) => _settingsWindow = null;
            _settingsWindow.Show();
        }

        private void AssistantSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            OpenSettings();
        }

        private void AssistantAiButton_Click(object sender, RoutedEventArgs e)
        {
            // 새 지시: 기록을 비우고 오브를 대기 상태로 — 텍스트 없이 초기화
            if (_assistantBusy) return;
            _assistant.ResetConversation();
            ClearAssistantConversation();
            ShowConversationOrb(Controls.OrbKind.Breathing);
            ActivateSelfAndFocusInput();
        }

        private void ApplySettings()
        {
            var cfg = ConfigService.Current;

            _sponsorSkip.IsEnabled = cfg.SponsorSkipEnabled;
            _sponsorSkip.Mode = cfg.SponsorSkipMode;

            // AI 비서 핫키 등록 상태를 설정과 동기화
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                UnregisterHotKey(hwnd, HOTKEY_ID_ASSISTANT);
                UnregisterHotKey(hwnd, HOTKEY_ID_CLIPBOARD);
                if (cfg.AssistantEnabled)
                {
                    RegisterHotKey(hwnd, HOTKEY_ID_ASSISTANT, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, 0x20);
                }
            }

            UpdatePosition();

            // 배터리 표현은 설정 변경과 무관하게 최신 상태로 갱신
            if (!HasMedia)
            {
                _batteryService.ForceUpdate();
            }

            // 스폰서 카테고리 설정이 바뀌었으면 현재 트랙 구간을 다시 조회
            if (HasMedia && _mediaService.CurrentMedia != null)
            {
                _ = _sponsorSkip.LoadSegmentsForTrackAsync(
                    _lastMediaKey, _mediaService.CurrentMedia.Title, _mediaService.CurrentMedia.Artist);
            }
        }

        private void TrayExit_Click()
        {
            _trayIcon!.Visible = false;
            _trayIcon.Dispose();
            System.Windows.Application.Current.Shutdown();
        }

        // ────────────────────────── AI 비서 ──────────────────────────

        /// <summary>AI 도구 호출 → 실제 기기 조작. AssistantService.ToolExecutor에 등록된다.</summary>
        private async Task<string> ExecuteAssistantToolAsync(AiToolCall call)
        {
            Log.Info($"Assistant tool call: {call.Name} {call.ArgumentsJson}");

            // 도구가 실제로 하는 일을 오브 디자인으로 드러낸다(검색=Globe, 연결=Web, 소리=Wave).
            // ToolExecutor는 비UI 스레드에서 불릴 수 있어 Dispatcher를 거친다.
            var (orbKind, orbStatus) = OrbForTool(call.Name);
            PostOrb(orbKind, orbStatus);

            try
            {
                switch (call.Name)
                {
                    case "media_play_pause":
                        {
                            bool ok = await _mediaService.TryTogglePlayPauseAsync().ConfigureAwait(true);
                            return ok ? "재생/일시정지 토글 성공" : "제어할 미디어 세션이 없다";
                        }
                    case "media_next":
                        return await _mediaService.TrySkipNextAsync().ConfigureAwait(true)
                            ? "다음 곡으로 넘어갔다" : "제어할 미디어 세션이 없다";
                    case "media_previous":
                        return await _mediaService.TrySkipPreviousAsync().ConfigureAwait(true)
                            ? "이전 곡으로 돌아갔다" : "제어할 미디어 세션이 없다";
                    case "set_volume":
                        {
                            using var doc = System.Text.Json.JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
                            if (!doc.RootElement.TryGetProperty("volume", out var v) || !v.TryGetInt32(out int vol))
                            {
                                return "volume 인자가 올바르지 않다";
                            }
                            int clamped = Math.Clamp(vol, 0, 100);
                            bool muted = false;
                            float delta = (clamped / 100f) - _audioService.GetCurrentVolume(out muted) / 100f;
                            _audioService.StepVolume(delta, out muted);
                            return $"볼륨을 {clamped}%로 설정했다";
                        }
                    case "open_app":
                        {
                            string app = GetStringArg(call.ArgumentsJson, "app");
                            if (string.IsNullOrWhiteSpace(app)) return "app 인자가 비었다";
                            bool ok = LaunchApp(app);
                            return ok ? $"{app} 실행을 시작했다" : $"{app}을(를) 찾지 못했다";
                        }
                    case "open_url":
                        {
                            string url = GetStringArg(call.ArgumentsJson, "url");
                            string browser = GetStringArg(call.ArgumentsJson, "browser");
                            if (string.IsNullOrWhiteSpace(url)) return "url 인자가 비었다";
                            OpenUrl(url, browser);
                            return string.IsNullOrWhiteSpace(browser)
                                ? $"{url}을(를) 기본 브라우저로 열었다"
                                : $"{browser}에서 {url}을(를) 열었다";
                        }
                    case "play_youtube":
                        {
                            string query = GetStringArg(call.ArgumentsJson, "query");
                            string browser = GetStringArg(call.ArgumentsJson, "browser");
                            if (string.IsNullOrWhiteSpace(query)) return "query 인자가 비었다";
                            string target = await ResolveYouTubeUrlAsync(query).ConfigureAwait(true);
                            OpenUrl(target, browser);
                            return $"유튜브에서 '{query}'을(를) 열었다: {target}";
                        }
                    default:
                        return $"알 수 없는 도구: {call.Name}";
                }
            }
            catch (Exception ex)
            {
                Log.Error("Assistant tool execution failed", ex);
                return $"도구 실행 중 오류: {ex.Message}";
            }
            finally
            {
                // 도구가 끝나면 모델 추론이 이어지므로 '사고 중'으로 — 단, 이미 응답이
                // 흐르기 시작했다면(델타 수신 뒤) 헤더의 응답 오브로 돌아간다.
                _ = Dispatcher.BeginInvoke(new Action(() =>
                    ShowConversationOrb(_assistantStreaming ? Controls.OrbKind.Composing : Controls.OrbKind.Working)));
            }
        }

        /// <summary>도구 이름 → 어울리는 오브 디자인과 상태 문구.</summary>
        private static (Controls.OrbKind Kind, string Status) OrbForTool(string toolName) => toolName switch
        {
            "play_youtube" => (Controls.OrbKind.Searching, "검색 중..."),     // 점 구름을 훑는 스캔
            "open_url" => (Controls.OrbKind.Connecting, "연결 중..."),        // 노드 네트워크
            "open_app" => (Controls.OrbKind.Shaping, "준비 중..."),           // 도형이 바뀌는 중
            "set_volume" => (Controls.OrbKind.Listening, "볼륨 조절 중..."),  // 구면을 타는 파동
            "media_play_pause" or "media_next" or "media_previous"
                => (Controls.OrbKind.Listening, "음악 제어 중..."),
            _ => (Controls.OrbKind.Solving, "처리 중..."),                    // 레이어가 풀리는 큐브
        };

        /// <summary>비UI 스레드에서도 안전하게 오브 상태를 바꾼다.</summary>
        private void PostOrb(Controls.OrbKind kind, string? status = null)
            => Dispatcher.BeginInvoke(new Action(() => ShowConversationOrb(kind, status)));

        private static string GetStringArg(string json, string key)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
                if (doc.RootElement.TryGetProperty(key, out var el) && el.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    return el.GetString() ?? string.Empty;
                }
            }
            catch { }
            return string.Empty;
        }

        /// <summary>앱 이름 → 실행 파일로 실행. 잘 알려진 앱 별칭을 우선하고, 실패 시 shell에서 시도한다.</summary>
        private static bool LaunchApp(string name)
        {
            string key = name.Trim().ToLowerInvariant().Replace(" ", "");
            string? exe = key switch
            {
                "brave" or "브레이브" => "brave",
                "chrome" or "크롬" or "구글크롬" => "chrome",
                "edge" or "엣지" => "msedge",
                "firefox" or "파이어폭스" => "firefox",
                "notepad" or "메모장" => "notepad",
                "calc" or "calculator" or "계산기" => "calc",
                "spotify" or "스포티파이" => "spotify",
                "discord" or "디스코드" => "discord",
                "explorer" or "파일탐색기" => "explorer",
                "steam" or "스팀" => "steam",
                _ => null,
            };
            try
            {
                if (exe != null)
                {
                    using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = exe,
                        UseShellExecute = true,
                    });
                    return true;
                }
                // 별칭에 없으면 이름 그대로 실행 시도 (PATH/앱 실행 별칭 활용)
                using var p2 = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = name.Trim(),
                    UseShellExecute = true,
                });
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>URL 열기. 브라우저 지정 시 그 앱으로, 아니면 기본 브라우저로 연다.</summary>
        private static void OpenUrl(string url, string? browser)
        {
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                url = "https://" + url;
            }

            try
            {
                if (!string.IsNullOrWhiteSpace(browser))
                {
                    string exe = browser.Trim().ToLowerInvariant() switch
                    {
                        "brave" or "브레이브" => "brave",
                        "chrome" or "크롬" => "chrome",
                        "edge" or "엣지" => "msedge",
                        "firefox" or "파이어폭스" => "firefox",
                        _ => string.Empty,
                    };
                    if (exe.Length > 0)
                    {
                        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = exe,
                            Arguments = $"\"{url}\"",
                            UseShellExecute = true,
                        });
                        return;
                    }
                }
            }
            catch { }

            // 폴백: 시스템 기본 브라우저
            using var p2 = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }

        /// <summary>YouTube 검색(키 있으면 API, 없으면 HTML)으로 1위 영상 URL을 얻는다. 실패 시 검색 결과 페이지.</summary>
        private async Task<string> ResolveYouTubeUrlAsync(string query)
        {
            try
            {
                string? videoId = await _youTubeSearch.SearchVideoIdAsync(query).ConfigureAwait(true);
                if (!string.IsNullOrEmpty(videoId))
                {
                    return $"https://www.youtube.com/watch?v={videoId}";
                }
            }
            catch { }
            return "https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(query);
        }

        /// <summary>핫키/버튼으로 비서를 연다. 이미 열려 있으면 닫는다(토글).</summary>
        private void OpenAssistant()
        {
            if (!ConfigService.Current.AssistantEnabled)
            {
                Log.Info("Assistant hotkey ignored: disabled in settings");
                return;
            }
            if (_assistantBusy)
            {
                // 답변 생성 중이면 무시 (의도치 않은 취소 방지)
                return;
            }
            if (_currentViewMode == ViewMode.Assistant)
            {
                CloseAssistant();
                return;
            }

            _isExpanded = true;
            SwitchViewMode(ViewMode.Assistant);

            // 지시 콘솔: 대기 오브(호흡)가 비서 그 자체 — 인사 말풍선 없음
            ShowConversationOrb(Controls.OrbKind.Breathing);

            // 스위치 애니메이션 이후 포커스 (노치가 Topmost 투명 오버레이라 스스로 활성화 필요)
            ActivateSelfAndFocusInput();
        }

        private void CloseAssistant()
        {
            AssistantInputBox.Clear();
            SwitchViewMode(HasMedia ? ViewMode.MediaCompact : ViewMode.IdleCompact);
        }

        private void ActivateSelfAndFocusInput()
        {
            try { Activate(); } catch { }
            Dispatcher.BeginInvoke(new Action(() =>
            {
                AssistantInputBox.Focus();
                AssistantInputBox.CaretIndex = AssistantInputBox.Text.Length;
            }), System.Windows.Threading.DispatcherPriority.Input);
        }

        /// <summary>확장 크기에서 벗어나면 자동으로 닫히도록 하는 마우스 이탈 처리 포함.</summary>
        private void AssistantInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                CloseAssistant();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Enter && !_assistantBusy)
            {
                string text = AssistantInputBox.Text.Trim();
                if (text.Length > 0)
                {
                    _ = SendAssistantMessageAsync(text);
                }
                e.Handled = true;
            }
        }

        private async System.Threading.Tasks.Task SendAssistantMessageAsync(string userMessage)
        {
            if (_assistantBusy) return;
            _assistantBusy = true;
            _assistantCts = new CancellationTokenSource();
            _assistantStreaming = false;

            AssistantInputBox.Clear();
            AssistantLatestResponseText.Text = string.Empty; // 최신 지시 결과만 보여준다
            ShowConversationOrb(Controls.OrbKind.Working);   // 사고 중

            UpdateAssistantContext();
            Log.Info($"Assistant query: {userMessage}");

            bool firstDeltaSeen = false;
            void OnFirstDelta()
            {
                if (firstDeltaSeen) return;
                firstDeltaSeen = true;
                _assistantStreaming = true;
                ShowConversationOrb(Controls.OrbKind.Composing); // 응답 스트리밍 중
            }

            try
            {
                void DeltaProxy(string d)
                {
                    Dispatcher.BeginInvoke(new Action(OnFirstDelta));
                    Assistant_DeltaReceived(d);
                }

                string answer;
                _assistant.DeltaReceived += DeltaProxy;
                try
                {
                    answer = await _assistant.SendAsync(userMessage, _assistantCts.Token).ConfigureAwait(true);
                }
                finally
                {
                    _assistant.DeltaReceived -= DeltaProxy;
                }
                Log.Info($"Assistant answer length: {answer.Length}");
                // 빈 응답 판정은 반드시 완성된 문자열로 — UI 텍스트로 판정하면 델타가
                // BeginInvoke로 늦게 붙는 경쟁에서 "(빈 응답)"이 실제 답변 앞에 끼어든다 (실측 버그)
                if (string.IsNullOrWhiteSpace(answer))
                {
                    AssistantLatestResponseText.Text = "(빈 응답)";
                }
            }
            catch (OperationCanceledException)
            {
                Log.Info("Assistant request cancelled");
            }
            catch (AiException ex)
            {
                Log.Error("Assistant request failed", ex);
                AssistantLatestResponseText.Text = "⚠ " + ex.Message;
            }
            catch (Exception ex)
            {
                Log.Error("Assistant unexpected error", ex);
                AssistantLatestResponseText.Text = "⚠ 알 수 없는 오류가 발생했습니다.";
            }
            finally
            {
                _assistantBusy = false;
                _assistantStreaming = false;
                ShowConversationOrb(Controls.OrbKind.Breathing); // 대기 복귀
            }
        }

        private void Assistant_DeltaReceived(string delta)
        {
            // 네트워크 스레드 → UI 스레드: 최신 응답 흐름에 조각을 붙인다
            Dispatcher.BeginInvoke(new Action(() =>
            {
                AssistantLatestResponseText.Inlines.Add(delta);
                AssistantScroll.ScrollToEnd();
            }));
        }

        private void UpdateAssistantContext()
        {
            string mediaText = string.Empty;
            string lyricText = string.Empty;
            var media = _mediaService.CurrentMedia;
            if (media != null && !string.IsNullOrWhiteSpace(media.Title) && media.Title != "재생 중인 미디어 없음")
            {
                mediaText = $"{media.Title}" + (string.IsNullOrWhiteSpace(media.Artist) ? "" : $" - {media.Artist}");

                // 현재 재생 위치의 가사 줄
                if (_syncedLyrics.Count > 0)
                {
                    var mediaPos = media.CurrentEstimatedPosition;
                    LyricLine? current = null;
                    foreach (LyricLine line in _syncedLyrics)
                    {
                        if (line.Time <= mediaPos) current = line; else break;
                    }
                    if (current != null && !string.IsNullOrWhiteSpace(current.Text))
                    {
                        lyricText = current.Text;
                    }
                }
            }

            string clipboardText = string.Empty;
            if (_clipboardHistory.Count > 0)
            {
                ClipboardItem last = _clipboardHistory[0];
                if (!last.IsImage) clipboardText = last.Text;
            }

            _assistant.UpdateContext(new AssistantContext(
                NowText: DateTime.Now.ToString("yyyy-MM-dd dddd HH:mm"),
                MediaText: mediaText,
                LyricText: lyricText,
                ClipboardText: clipboardText));
        }

        private void Assistant_NotchClick(object sender, MouseButtonEventArgs e)
        {
            // 1·2번 피드백: 노치 클릭 → AI 호출은 제거됐다. AI는 핫키(Ctrl+Shift+Space)로만 연다.
        }

        private void AssistantCloseButton_Click(object sender, RoutedEventArgs e)
        {
            if (_assistantBusy)
            {
                // 생성 중이면 요청을 취소하고 창도 닫는다
                _assistantCts?.Cancel();
            }
            CloseAssistant();
        }

        private void ClearAssistantConversation()
        {
            AssistantLatestResponseText.Text = string.Empty;
            AssistantScroll.ScrollToHome();
        }

        // 상태 텍스트 광택 스윕: 밝은 빛이 글자 뒤를 왼→오른쪽으로 스치는 원작 디테일.
        // XAML EventTrigger는 TextBlock.Triggers에 못 쓰므로 코드에서 구동한다.
        private void StartStatusSheen()
        {
            if (StatusSheenBright == null) return;
            var anim = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = new Duration(TimeSpan.FromSeconds(2.4)),
                RepeatBehavior = RepeatBehavior.Forever,
            };
            StatusSheenBright.BeginAnimation(GradientStop.OffsetProperty, anim);
        }

        // ── Thinking Orbs 상태 전환 ──
        // 대기=Breathing(상단 소형) / 사고=Working(중앙 대형, 입력 불가 시각화) / 응답=Composing(상단 소형).
        // CenterOrb는 지시 직후 응답이 오기 전까지 중앙에서 크게 도는 전용 오브다.

        private void ShowConversationOrb(Controls.OrbKind kind, string? statusOverride = null)
        {
            ConversationOrb.Kind = kind;

            // 대기(Breathing)와 응답 스트리밍(Composing)은 헤더의 작은 오브,
            // 그 밖의 '작업 중' 상태(사고·검색·연결·파동 등)는 중앙 대형 오브로 보여준다.
            // 둘을 같이 띄우면 '구체 2개'로 보이므로 항상 한쪽만 보인다.
            bool central = kind is not (Controls.OrbKind.Breathing or Controls.OrbKind.Composing);
            ConversationOrb.Visibility = central ? Visibility.Collapsed : Visibility.Visible;
            CenterOrb.Kind = kind;
            CenterOrb.Visibility = central ? Visibility.Visible : Visibility.Collapsed;
            AssistantLatestResponseText.Visibility = central ? Visibility.Collapsed : Visibility.Visible;

            // border-beam: 입력창은 응답 스트리밍 중에만 광선이 흐른다.
            InputBeam.Active = kind == Controls.OrbKind.Composing;

            // 노치 테두리 = 살아있는 가장자리 — 상태에 맞춰 형태·색·속도를 다시 계산
            _orbCentral = central;
            UpdateBeamState();

            AssistantStatusText.Text = statusOverride ?? (kind switch
            {
                Controls.OrbKind.Working => "생각 중...",
                Controls.OrbKind.Composing => "응답 중...",
                Controls.OrbKind.Searching => "검색 중...",
                Controls.OrbKind.Solving => "처리 중...",
                Controls.OrbKind.Listening => "대화 중...",
                Controls.OrbKind.Connecting => "연결 중...",
                Controls.OrbKind.Weaving => "정리 중...",
                Controls.OrbKind.Shaping => "준비 중...",
                _ => "대기 중...",
            });
        }

        /// <summary>
        /// 노치 테두리(살아있는 가장자리)의 형태·색·속도를 현재 상태로 결정한다.
        /// 우선순위: 비서 작업 중(크고 단색) &gt; 응답 스트리밍(얇은 단색) &gt; 미디어 재생(앨범색) &gt; 대기(모노 선).
        /// </summary>
        private void UpdateBeamState()
        {
            // 트랙이 로드되면 일시정지 상태여도 그 곡의 색을 유지한다(재생 여부는 형태·속도로만).
            bool track = HasMedia;
            bool playing = _mediaService.CurrentMedia?.IsPlaying == true;
            string log = "";

            // 이퀄라이저 바도 같은 곡의 색 — 한 곡 안에서는 테두리와 바가 같은 색을 쓴다
            UpdateEqualizerAccent(track ? (_albumColor ?? ColorFromKey(_lastMediaKey)) : EqualizerIdleColor);

            if (_orbCentral)
            {
                log = "assistant-working";
                // 사고·검색·연결 등 — 크게 도는 모노 빛
                NotchBeam.Form = Controls.BeamForm.Large;
                NotchBeam.Tint = Controls.BeamTint.Mono;
                NotchBeam.AccentColor = null;
                NotchBeam.Speed = 0.30;
            }
            else if (_assistantBusy)
            {
                log = "assistant-streaming";
                // 응답 스트리밍 — 얇은 모노 선
                NotchBeam.Form = Controls.BeamForm.Line;
                NotchBeam.Tint = Controls.BeamTint.Mono;
                NotchBeam.AccentColor = null;
                NotchBeam.Speed = 0.16;
            }
            else if (track)
            {
                // 앨범아트가 없으면(브라우저 탭 오디오 등) 트랙 이름에서 안정적인 색을 파생한다.
                // 그래야 어떤 곡이든 '그 곡의 색'을 갖는다.
                Color beamColor = _albumColor ?? ColorFromKey(_lastMediaKey);
                log = $"media({(_albumColor.HasValue ? "album" : "derived")} #{beamColor.R:X2}{beamColor.G:X2}{beamColor.B:X2} {(playing ? "playing" : "paused")})";
                NotchBeam.Tint = Controls.BeamTint.Album;
                NotchBeam.AccentColor = beamColor;
                // 재생 중엔 크게 빠르게, 일시정지엔 얇게 느리게 — 곡의 색을 두고 운동만 달라진다.
                NotchBeam.Form = playing ? Controls.BeamForm.Large : Controls.BeamForm.Line;
                NotchBeam.Speed = playing ? 0.13 : 0.07;
            }
            else
            {
                log = "idle";
                // 대기 — 밝은 모노 선이 천천히 흐른다
                NotchBeam.Form = Controls.BeamForm.Line;
                NotchBeam.Tint = Controls.BeamTint.Mono;
                NotchBeam.AccentColor = null;
                NotchBeam.Speed = 0.09;
            }

            if (log != _lastBeamState)
            {
                _lastBeamState = log;
                Log.Info($"Beam state: {log}");
            }
        }

        /// <summary>
        /// 트랙 키에서 안정적인 색을 파생한다 — 앨범아트가 없는 곡도 자기 색을 갖도록.
        /// FNV-1a 해시 → 색상환. 노랑/형광이 촌스럽지 않게 채도·명도를 눌러 쓴다.
        /// </summary>
        private static Color ColorFromKey(string key)
        {
            unchecked
            {
                uint h = 2166136261;
                foreach (char ch in key) { h = (h ^ ch) * 16777619; }
                return HsvToRgb(h % 360, 0.66, 0.96);
            }
        }

        private static Color HsvToRgb(double hueDeg, double s, double v)
        {
            double h = ((hueDeg % 360) + 360) % 360 / 60.0;
            int i = (int)Math.Floor(h);
            double f = h - i;
            double p = v * (1 - s), q = v * (1 - f * s), t = v * (1 - (1 - f) * s);
            double r, g, b;
            switch (i % 6)
            {
                case 0: r = v; g = t; b = p; break;
                case 1: r = q; g = v; b = p; break;
                case 2: r = p; g = v; b = t; break;
                case 3: r = p; g = q; b = v; break;
                case 4: r = t; g = p; b = v; break;
                default: r = v; g = p; b = q; break;
            }
            return Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
        }

        /// <summary>
        /// 앨범아트에서 '지배색'을 뽑는다 — 밝고 선명한 픽셀에 가중치를 준 색 히스토그램의 최빈 구간.
        /// 24×24로 줄여 576픽셀만 훑으므로 트랙이 바뀔 때 한 번이면 충분하다.
        /// 여러 색을 평균내면 회색빛으로 죽으므로, 구간별로 모아 가장 뜨거운 색 하나를 고른다.
        /// </summary>
        private static Color? ExtractDominantColor(BitmapSource bmp)
        {
            try
            {
                if (bmp.PixelWidth < 4 || bmp.PixelHeight < 4) return null;
                var converted = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
                int sw = converted.PixelWidth, sh = converted.PixelHeight;
                int stride = sw * 4;
                byte[] px = new byte[sh * stride];
                converted.CopyPixels(px, stride, 0);

                // 24×24 격자로 솎아 표본만 본다(TransformedBitmap은 freeze 요구가 까다로워 직접 훑는다)
                const int G = 24;
                const int BINS = 4096; // 4bit/채널
                var wsum = new double[BINS];
                var ar = new double[BINS]; var ag = new double[BINS]; var ab = new double[BINS];

                for (int gy = 0; gy < G; gy++)
                {
                    int y = (gy * sh) / G;
                    for (int gx = 0; gx < G; gx++)
                    {
                        int x = (gx * sw) / G;
                        int o = y * stride + x * 4;
                        double b = px[o] / 255.0, g = px[o + 1] / 255.0, r = px[o + 2] / 255.0;
                        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                        double sat = max <= 0 ? 0 : (max - min) / max;
                        double weight = max * (0.35 + sat); // 밝고 선명할수록 세게 — 검은 여백은 자연히 제외
                        int bin = ((px[o + 2] >> 4) << 8) | ((px[o + 1] >> 4) << 4) | (px[o] >> 4);
                        wsum[bin] += weight; ar[bin] += r * weight; ag[bin] += g * weight; ab[bin] += b * weight;
                    }
                }

                int best = -1; double bestW = 0;
                for (int i = 0; i < BINS; i++)
                    if (wsum[i] > bestW) { bestW = wsum[i]; best = i; }
                if (best < 0 || bestW < 1e-4) return null;

                double r0 = ar[best] / bestW * 255, g0 = ag[best] / bestW * 255, b0 = ab[best] / bestW * 255;
                var raw = Color.FromRgb(
                    (byte)Math.Clamp(r0, 0, 255),
                    (byte)Math.Clamp(g0, 0, 255),
                    (byte)Math.Clamp(b0, 0, 255));
                if (Math.Max(raw.R, Math.Max(raw.G, raw.B)) < 40) { Log.Info($"Album color: too dark from {sw}x{sh}"); return null; }

                // 무채색에 가까운 앨범(사진·회색 배경)도 테두리에서 '색'으로 읽히도록
                // 색상은 그대로 두고 채도·명도만 끌어올린다.
                RgbToHsv(raw, out double hue, out double rawSat, out double rawVal);
                var c = HsvToRgb(hue, Math.Max(rawSat, 0.55), Math.Max(rawVal, 0.92));
                Log.Info($"Album color: #{c.R:X2}{c.G:X2}{c.B:X2} (raw #{raw.R:X2}{raw.G:X2}{raw.B:X2} s={rawSat:F2}) from {sw}x{sh}");
                return c;
            }
            catch (Exception ex)
            {
                Log.Error("Album color extraction failed", ex);
                return null;
            }
        }

        /// <summary>RGB → HSV(색상은 degree). 추출 색을 정규화할 때 쓴다.</summary>
        private static void RgbToHsv(Color c, out double hueDeg, out double s, out double v)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            double d = max - min;
            v = max;
            s = max <= 1e-6 ? 0 : d / max;
            double h = 0;
            if (d > 1e-6)
            {
                if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
                else if (max == g) h = (b - r) / d + 2;
                else h = (r - g) / d + 4;
                h /= 6;
            }
            hueDeg = h * 360;
        }

        private void MainWindow_Closed(object? sender, EventArgs e)
        {
            _progressTimer.Stop();
            _clockTimer.Stop();
            _volumeHudTimer?.Stop();
            _emptyMediaDebounceTimer?.Stop();
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

            _sponsorSkip.Dispose();
            _audioService.Dispose();
            _mediaService.Dispose();
            _lyricsService.Dispose();
            _batteryService.Dispose();
            _notificationService.Dispose();
        }
    }
}
