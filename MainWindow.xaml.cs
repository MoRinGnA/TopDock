using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
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

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WM_NCHITTEST = 0x0084;
        private const int HTTRANSPARENT = -1;

        private const double VolumeBarExtraHeight = 54;

        // AI 비서 전역 핫키: Ctrl+Shift+Space
        private const int HOTKEY_ID_ASSISTANT = 0xB00B;
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
        // 클립보드 히스토리 (최근 5개, 최신이 앞; 텍스트/이미지 혼합)
        private sealed record ClipboardItem(string Text, BitmapSource? Image)
        {
            public bool IsImage => Image != null;
        }

        private readonly List<ClipboardItem> _clipboardHistory = new();
        private readonly Image?[] _rowImages = new Image?[5];
        private DispatcherTimer? _clipboardToastTimer;

        // 캡처 도구가 클립보드를 연속 기록하며 생기는 중복 이미지 이벤트 필터용
        private DateTime _lastImageCaptureAt = DateTime.MinValue;
        // 행 클릭 등 앱 스스로 클립보드에 쓸 때 자기 복사를 새 항목으로 오판하지 않게 하는 플래그
        private bool _suppressNextClipboardEvent;

        private readonly DispatcherTimer _progressTimer;
        private readonly DispatcherTimer _clockTimer;
        private DispatcherTimer? _volumeHudTimer;
        private DispatcherTimer? _notificationTimer;
        private DispatcherTimer? _emptyMediaDebounceTimer;
        private Storyboard? _eqStoryboard;

        // 트랙 전환 사이 SMTC가 잠깐 보고하는 빈 미디어를 필터링하기 위한 유예 시간
        private static readonly TimeSpan EmptyMediaDebounceDelay = TimeSpan.FromMilliseconds(1500);

        private ViewMode _currentViewMode = ViewMode.IdleCompact;
        private ViewMode _viewModeBeforeNotification = ViewMode.IdleCompact;
        private string _lastMediaKey = string.Empty;
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
            if (msg == WM_CLIPBOARDUPDATE)
            {
                try
                {
                    // 행 클릭 재복사 등 앱이 스스로 쓴 클립보드는 무시
                    if (_suppressNextClipboardEvent)
                    {
                        _suppressNextClipboardEvent = false;
                        return IntPtr.Zero;
                    }

                    // Win+Shift+S 캡처 등 이미지 우선, 없으면 텍스트
                    if (Clipboard.ContainsImage())
                    {
                        var image = Clipboard.GetImage();
                        if (image != null)
                        {
                            image.Freeze();
                            Dispatcher.InvokeAsync(() => ShowClipboardToast(new ClipboardItem(string.Empty, image)));
                        }
                    }
                    else if (Clipboard.ContainsText())
                    {
                        string text = Clipboard.GetText();
                        // 빈 문자열/공백만 있는 복사는 토스트와 히스토리 모두에서 제외
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            Dispatcher.InvokeAsync(() => ShowClipboardToast(new ClipboardItem(text, null)));
                        }
                    }
                }
                catch { }
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

                    Point pt = NotchBorder.PointFromScreen(new Point(x, y));

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

        private void ShowClipboardToast(ClipboardItem item)
        {
            // 히스토리는 토스트 설정과 무관하게 항상 수집 (공백 텍스트는 제외)
            if (item.IsImage)
            {
                // Win+Shift+S 등 캡처 도구는 형식별로 클립보드를 연속 기록해서
                // 업데이트 이벤트가 짧은 간격으로 여러 번 온다 → 0.8초 내 연속 이미지는 1장으로 합침
                var now = DateTime.UtcNow;
                if (now - _lastImageCaptureAt < TimeSpan.FromMilliseconds(800)) return;
                _lastImageCaptureAt = now;

                try
                {
                    AddToHistory(item);
                }
                catch (Exception ex)
                {
                    // 해시 계산 실패 등 예외가 토스트까지 끊지 않도록 방어
                    System.Diagnostics.Debug.WriteLine($"Clipboard image history error: {ex.Message}");
                }
            }
            else if (!string.IsNullOrWhiteSpace(item.Text))
            {
                AddToHistory(item);
            }
            else
            {
                return;
            }

            if (!ConfigService.Current.ShowClipboardToast) return;

            // 캡처/복사 알림은 노치 본체 텍스트를 건드리지 않고 —
            // 항상 오른쪽으로 붙어 늘어나는 캡슐 하나로만 표시한다 (모든 뷰 공통, 통일성).
            // 캡처 즉시 캡슐이 뜨고, 캡슐을 클릭하면 기록 패널(오른쪽 아래 펼침)로 이어진다.
            string sideText = item.IsImage
                ? $"캡처 {item.Image!.PixelWidth}×{item.Image.PixelHeight}"
                : (item.Text.Length > 22 ? item.Text[..22] + "…" : item.Text);
            ShowClipboardSideCapsule(sideText);

            // Task.Delay 대신 재시작 가능한 일회성 타이머로 연속 복사 시 경쟁 상태 제거
            if (_clipboardToastTimer == null)
            {
                _clipboardToastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                _clipboardToastTimer.Tick += ClipboardToastTimer_Tick;
            }
            _clipboardToastTimer.Stop();
            _clipboardToastTimer.Start();
        }

        private void AddToHistory(ClipboardItem item)
        {
            // 텍스트 중복 제거: 같은 텍스트는 맨 위로 올림
            if (!item.IsImage)
            {
                _clipboardHistory.RemoveAll(i => !i.IsImage && i.Text == item.Text);
            }
            else
            {
                // 이미지도 같은 그림이면 맨 위로 (픽셀 해시 비교 — 캡처를 여러 번 떠도 1장만 유지)
                byte[] newHash = ComputeImageHash(item.Image!);
                _clipboardHistory.RemoveAll(i =>
                {
                    if (!i.IsImage || i.Image == null) return false;
                    byte[] oldHash = ComputeImageHash(i.Image);
                    return HashEquals(oldHash, newHash);
                });
            }

            _clipboardHistory.Insert(0, item);
            if (_clipboardHistory.Count > 5) _clipboardHistory.RemoveAt(5);
        }

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
            HideClipboardSideCapsule();
        }

        // ── 클립보드 기록 패널: 캡슐 클릭 → 오른쪽 아래로 펼쳐지는 최근 5개 ──

        private bool _clipboardHistoryOpen;

        private void ClipboardSideCapsule_Click(object sender, MouseButtonEventArgs e)
        {
            if (_clipboardHistoryOpen)
            {
                HideClipboardHistoryPanel();
            }
            else if (_clipboardHistory.Count > 0)
            {
                ShowClipboardHistoryPanel();
            }
            e.Handled = true;
        }

        private void ShowClipboardHistoryPanel()
        {
            BuildClipboardHistoryRows();
            _clipboardHistoryOpen = true;
            RepositionClipboardHistoryPanel();

            // 캡슐 타이머 정지 — 기록을 보는 동안 캡슐이 사라지지 않게
            _clipboardToastTimer?.Stop();

            ClipboardHistoryPanel.Visibility = Visibility.Visible;
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
            int count = Math.Min(_clipboardHistory.Count, 5);
            for (int i = 0; i < count; i++)
            {
                ClipboardItem item = _clipboardHistory[i];
                string label = item.IsImage
                    ? $"🖼 캡처 {item.Image!.PixelWidth}×{item.Image.PixelHeight}"
                    : (item.Text.Length > 28 ? item.Text[..28].ReplaceLineEndings(" ") + "…" : item.Text.ReplaceLineEndings(" "));

                var rowBorder = new Border
                {
                    Background = Brushes.Transparent,
                    CornerRadius = new CornerRadius(9),
                    Padding = new Thickness(10, 6, 10, 6),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Tag = item,
                };
                rowBorder.MouseLeftButtonUp += ClipboardHistoryRow_Click;
                rowBorder.MouseEnter += (s, e) => rowBorder.Background = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
                rowBorder.MouseLeave += (s, e) => rowBorder.Background = Brushes.Transparent;

                var text = new TextBlock
                {
                    Text = label,
                    Foreground = new SolidColorBrush(Color.FromArgb(0xE5, 0xFF, 0xFF, 0xFF)),
                    FontSize = 12,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                rowBorder.Child = text;
                ClipboardHistoryRows.Children.Add(rowBorder);
            }
        }

        private void ClipboardHistoryRow_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border border && border.Tag is ClipboardItem item)
            {
                try
                {
                    _suppressNextClipboardEvent = true;
                    if (item.IsImage && item.Image != null)
                        Clipboard.SetImage(item.Image);
                    else if (!string.IsNullOrWhiteSpace(item.Text))
                        Clipboard.SetText(item.Text);
                }
                catch
                {
                    _suppressNextClipboardEvent = false;
                }
                HideClipboardHistoryPanel();
            }
        }

        // ── 클립보드 사이드 캡슐 (본체 오른쪽에서 분리 확장) ──

        private void ShowClipboardSideCapsule(string text)
        {
            ClipboardSideText.Text = text;
            // 내용에 맞는 자연 폭을 먼저 확정한다 — Width 애니메이션은 이 값을 목표로 쓴다.
            // 여유 +14px를 줘야 폰트 메트릭 오차로 글자 끝이 잘리지 않는다 (실측 버그)
            ClipboardSideCapsule.Width = double.NaN; // Auto로 풀어 측정
            ClipboardSideCapsule.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double naturalWidth = Math.Max(80, ClipboardSideCapsule.DesiredSize.Width + 14);

            // 7번 피드백: 물리 크기 정규화 — 175%에서 본 실제 크기를 어느 배율에서든 유지
            double dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            double norm = dpiScale > 0 ? Math.Min(1.75 / dpiScale, 2.2) : 1.0;
            ClipboardSideDpiScale.ScaleX = norm;
            ClipboardSideDpiScale.ScaleY = norm;

            // 퇴장 잔여값 완전 초기화 (낙하·투명도·폭·변형 통짜 리셋)
            ResetCapsuleTransforms();
            // 시작 폭을 고정하고 Visible — Width가 NaN이면 애니메이션 시작값이 무의미해진다
            ClipboardSideCapsule.Width = naturalWidth * 0.45;
            ClipboardSideCapsule.Visibility = Visibility.Visible;

            RepositionClipboardSideCapsule();

            // 등장: 노치에서 오른쪽으로 붙어 폭이 자라나는 확장 (ScaleTransform 폐기 — 텍스트 왜곡 없음)
            var ease = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 5 };
            var dur = new Duration(TimeSpan.FromMilliseconds(420));

            var opacity = new DoubleAnimation(0, 1, dur) { EasingFunction = ease };
            var grow = new DoubleAnimation(naturalWidth * 0.35, naturalWidth, dur) { EasingFunction = ease };
            grow.Completed += (s, e) =>
            {
                // 애니메이션 후 폭을 Auto로 복원 — 어떤 텍스트 길이든 끝까지 보인다
                ClipboardSideCapsule.BeginAnimation(FrameworkElement.WidthProperty, null);
                ClipboardSideCapsule.Width = double.NaN;
            };
            Timeline.SetDesiredFrameRate(opacity, 60);
            Timeline.SetDesiredFrameRate(grow, 60);

            ClipboardSideCapsule.BeginAnimation(UIElement.OpacityProperty, opacity);
            ClipboardSideCapsule.BeginAnimation(FrameworkElement.WidthProperty, grow);
        }

        // 캡슐 추적 루프: 본체가 애니메이션 중인 동안 매 프레임 위치 재계산
        private bool _capsuleTrackingActive;

        private void StartCapsuleTracking()
        {
            if (_capsuleTrackingActive) return;
            _capsuleTrackingActive = true;
            CompositionTarget.Rendering += CapsuleTracking_Rendering;
        }

        private void StopCapsuleTracking()
        {
            if (!_capsuleTrackingActive) return;
            _capsuleTrackingActive = false;
            CompositionTarget.Rendering -= CapsuleTracking_Rendering;
        }

        private void CapsuleTracking_Rendering(object? sender, EventArgs e)
        {
            RepositionClipboardSideCapsule();
        }
        private void RepositionClipboardSideCapsule()
        {
            if (ClipboardSideCapsule.Visibility != Visibility.Visible) return;

            // 노치에서 오른쪽으로 '붙어서' 넓어진 느낌 — 살짝 겹쳐 테두리가 이어지게 한다
            double gap = -2;

            // 본체가 아직 레이아웃 전(ActualWidth 0)이면 실측 좌표가 화면 좌상단을 가리킨다 —
            // 이 경우가 시작 직후 캡슐이 좌상단에 뜨던 원인. 선언 폭 기준으로 중앙 오른쪽에 배치한다.
            if (NotchBorder.ActualWidth < 1 || NotchBorder.ActualHeight < 1)
            {
                Canvas.SetLeft(ClipboardSideCapsule, ActualWidth / 2 + NotchBorder.Width / 2 + gap);
                Canvas.SetTop(ClipboardSideCapsule, 8);
                return;
            }

            try
            {
                // 레이아웃 후에는 본체 우측 중앙의 실제 렌더 좌표를 화면 경유로 변환 — DPI·여백 무관 정확.
                // 스케일 정규화가 RenderTransformOrigin(0,0.5)라 세로 중앙 기준으로 확대되므로
                // 앵커도 세로 중앙 (ActualHeight/2)으로 잡아야 100% 화면에서 캡슐이 아래로 처지지 않는다 (실측 버그)
                Point notchRightCenter = NotchBorder.PointToScreen(new Point(NotchBorder.ActualWidth, NotchBorder.ActualHeight / 2));
                Point canvasOrigin = RootGrid.PointToScreen(new Point(0, 0));
                double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
                double scaleY = VisualTreeHelper.GetDpi(this).DpiScaleY;
                double dpiNormY = scaleY > 0 ? Math.Min(1.75 / scaleY, 2.2) : 1.0;

                double capsuleHalf = ClipboardSideCapsule.Height / 2;
                Canvas.SetLeft(ClipboardSideCapsule, (notchRightCenter.X - canvasOrigin.X) / dpi + gap);
                Canvas.SetTop(ClipboardSideCapsule, (notchRightCenter.Y - canvasOrigin.Y) / scaleY - capsuleHalf);
            }
            catch
            {
                Canvas.SetLeft(ClipboardSideCapsule, ActualWidth / 2 + NotchBorder.Width / 2 + gap);
                Canvas.SetTop(ClipboardSideCapsule, 8);
            }
        }

        private void HideClipboardSideCapsule()
        {
            HideClipboardHistoryPanel(); // 열려 있던 기록 패널도 함께 닫는다

            StopCapsuleTracking();

            // 오른쪽으로 밀리며 아래로 낙하·소멸하는 버블 퇴장 (11번 피드백)
            var ease = new ExponentialEase { EasingMode = EasingMode.EaseIn, Exponent = 5 };
            var dur = new Duration(TimeSpan.FromMilliseconds(460));

            var opacity = new DoubleAnimation(0, dur) { EasingFunction = ease };
            var slide = new DoubleAnimation(30, new Duration(TimeSpan.FromMilliseconds(220))) { EasingFunction = ease };
            var drop = new DoubleAnimation
            {
                From = 0,
                To = 56,
                EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseIn, Exponent = 2.2 }, // 낙하 가속
                Duration = new Duration(TimeSpan.FromMilliseconds(460)),
            };
            Timeline.SetDesiredFrameRate(opacity, 60);
            Timeline.SetDesiredFrameRate(slide, 60);
            Timeline.SetDesiredFrameRate(drop, 60);

            opacity.Completed += (s, e) =>
            {
                if (_clipboardToastTimer == null || !_clipboardToastTimer.IsEnabled)
                {
                    ClipboardSideCapsule.Visibility = Visibility.Collapsed;
                    ResetCapsuleTransforms();
                }
            };

            ClipboardSideCapsule.BeginAnimation(UIElement.OpacityProperty, opacity);
            ClipboardSideTransform.BeginAnimation(TranslateTransform.XProperty, slide);
            ClipboardSideDropTransform.BeginAnimation(TranslateTransform.YProperty, drop);
        }

        /// <summary>캡슐 변형·폭·투명도의 모든 잔여값을 완전 초기화한다. 등장 직전에도 호출.</summary>
        private void ResetCapsuleTransforms()
        {
            // 애니메이션 HoldEnd 잔여값을 전부 제거해 다음 등장이 항상 같은 상태에서 시작하게 한다 (실측 버그)
            ClipboardSideDropTransform.BeginAnimation(TranslateTransform.YProperty, null);
            ClipboardSideTransform.BeginAnimation(TranslateTransform.XProperty, null);
            ClipboardSideCapsule.BeginAnimation(UIElement.OpacityProperty, null);
            ClipboardSideDropTransform.Y = 0;
            ClipboardSideTransform.X = 0;
            ClipboardSideCapsule.Opacity = 0;
            ClipboardSideCapsule.Width = double.NaN;
        }

        // 캡슐 위치 계산에 쓰는 좌표를 기록 패널도 공유 — 캡슐 아래에 붙인다
        private void RepositionClipboardHistoryPanel()
        {
            if (ClipboardHistoryPanel.Visibility != Visibility.Visible) return;
            try
            {
                double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
                double scaleY = VisualTreeHelper.GetDpi(this).DpiScaleY;

                // 패널은 캡슐의 오른쪽 아래에 붙인다 — 아래로 펼쳐지는 느낌은 유지하되
                // 캡슐과 가로로 정렬돼 통일감을 준다 (DPI 정규화 스케일도 동일 적용)
                double dpiNorm = VisualTreeHelper.GetDpi(this).DpiScaleY;
                double panelScale = dpiNorm > 0 ? Math.Min(1.75 / dpiNorm, 2.2) : 1.0;
                ClipboardHistoryPanel.LayoutTransform = new ScaleTransform(panelScale, panelScale);

                Point capsuleBottomRight = ClipboardSideCapsule.PointToScreen(
                    new Point(ClipboardSideCapsule.ActualWidth, ClipboardSideCapsule.ActualHeight));
                Point canvasOrigin = RootGrid.PointToScreen(new Point(0, 0));
                Canvas.SetLeft(ClipboardHistoryPanel, (capsuleBottomRight.X - canvasOrigin.X) / dpi - ClipboardHistoryPanel.ActualWidth);
                Canvas.SetTop(ClipboardHistoryPanel, (capsuleBottomRight.Y - canvasOrigin.Y) / scaleY + 8);
            }
            catch
            {
                Canvas.SetLeft(ClipboardHistoryPanel, Canvas.GetLeft(ClipboardSideCapsule));
                Canvas.SetTop(ClipboardHistoryPanel, Canvas.GetTop(ClipboardSideCapsule) + 50);
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

            // 클립보드 캡슐은 호버와 무관하게 유지한다 — 본체 안 스트립은 철폐됐으므로
            // 캡슐이 유일한 클립보드 UI다 (확장 얼굴/AI 탭에서도 계속 보여야 함)

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

            // 노치를 떠나면 클립보드 스트립도 즉시 정리 (높이는 SwitchViewMode가 처리)
            _clipboardStripVisible = false;
            ClipboardStripPanel.Visibility = Visibility.Collapsed;
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

        /// <summary>배터리 상태를 3분류로만 표현한다(퍼센트 게이지 없음).
        /// 일반 상태에서는 노치 테두리를 원래대로 두고, 충전 중 / 배터리 부족일 때만
        /// 테두리에 상태 색 라이트를 입힌다 — 순수 WPF(Border.BorderBrush)로만 처리하며
        /// 별도 프로세스나 셰이더를 쓰지 않는다.</summary>
        private void BatteryService_BatteryStatusChanged(object? sender, BatteryStatusArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                // 배터리 라이트는 접힌 기본 아일랜드에서만 보여준다.
                // SwitchViewMode도 다른 뷰로 갈 때 테두리를 비우므로 여기서도 같은 기준을 지킨다
                // (지키지 않으면 확장 뷰에서 5초마다 배터리 색이 되살아난다).
                if (_currentViewMode != ViewMode.IdleCompact) return;

                bool isNormal = !e.IsCharging && e.BatteryPercent > 0.20f;

                if (isNormal)
                {
                    // 일반 상태 — DarkNotchStyle의 원래 테두리로 되돌린다.
                    NotchBorder.ClearValue(Border.BorderBrushProperty);
                    return;
                }

                Color accent = e.IsCharging
                    ? Color.FromRgb(57, 255, 20)   // 충전 중 — 녹색 라이트
                    : Color.FromRgb(255, 59, 48);  // 배터리 부족 — 적색 라이트

                // 원본 DarkNotchStyle과 같은 세로 결로 살짝 흐려지게 해서
                // 1px 림 위에 상태 색이 자연스럽게 얹히게 한다.
                var brush = new LinearGradientBrush
                {
                    StartPoint = new Point(0.5, 0),
                    EndPoint = new Point(0.5, 1),
                };
                brush.GradientStops.Add(new GradientStop(accent, 0.0));
                brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x70, accent.R, accent.G, accent.B), 1.0));
                brush.Freeze();

                NotchBorder.BorderBrush = brush;
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

            SetViewActive(activeView, duration, ease);
            NotchBorder.CornerRadius = new CornerRadius(targetRadius);

            if (mode != ViewMode.IdleCompact)
            {
                NotchBorder.ClearValue(Border.BorderBrushProperty);
            }
            else
            {
                _batteryService.ForceUpdate();
            }

            bool expanding = targetWidth > NotchBorder.Width;
            duration = new Duration(TimeSpan.FromMilliseconds(expanding ? 520 : 380));

            DoubleAnimation widthAnim = new DoubleAnimation { To = targetWidth, Duration = duration, EasingFunction = ease };
            DoubleAnimation heightAnim = new DoubleAnimation { To = targetHeight, Duration = duration, EasingFunction = ease };

            Timeline.SetDesiredFrameRate(widthAnim, 60);
            Timeline.SetDesiredFrameRate(heightAnim, 60);

            NotchBorder.BeginAnimation(Border.WidthProperty, widthAnim);
            NotchBorder.BeginAnimation(Border.HeightProperty, heightAnim);

            // 클립보드 캡슐이 떠 있는 동안 본체 크기 변화를 실시간 추적
            if (ClipboardSideCapsule.Visibility == Visibility.Visible)
            {
                StartCapsuleTracking();
            }

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

        private void ShowVolumeBarExpanded()
        {
            // 볼륨 바와 클립보드 스트립이 같은 하단 슬롯을 쓰므로 상호 배타
            HideClipboardStripExpanded(animateHeight: false);

            VolumeBarPanel.Visibility = Visibility.Visible;

            double baseHeight = HasMedia ? 190 : 120;
            double targetHeight = baseHeight + VolumeBarExtraHeight;

            NotchBorder.BeginAnimation(Border.HeightProperty,
                new DoubleAnimation { To = targetHeight, Duration = NotchAnimDuration, EasingFunction = NotchAnimEase });

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

            double baseHeight = HasMedia ? 190 : 120;

            NotchBorder.BeginAnimation(Border.HeightProperty,
                new DoubleAnimation { To = baseHeight, Duration = NotchAnimDuration, EasingFunction = NotchAnimEase });

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

                        // 볼륨 조정이 끝나면 시계 모드에서 클립보드 스트립 복원
                        if (_currentViewMode == ViewMode.IdleExpanded)
                        {
                            ShowClipboardStripExpanded();
                        }
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
                }
                else
                {
                    ExpandedAlbumArtImage.Source = null;
                    ExpandedDefaultIcon.Visibility = Visibility.Visible;

                    if (isNewTrack)
                    {
                        _ = RetryThumbnailAsync(key);
                    }
                }

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

        private bool _clipboardStripVisible;

        private void ClipboardRow_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border border && border.Tag is ClipboardItem item)
            {
                try
                {
                    // 자기 복사가 리스너에 새 항목으로 잡히는 것을 방지
                    _suppressNextClipboardEvent = true;

                    if (item.IsImage && item.Image != null)
                        Clipboard.SetImage(item.Image);
                    else if (!string.IsNullOrWhiteSpace(item.Text))
                        Clipboard.SetText(item.Text);
                }
                catch
                {
                    _suppressNextClipboardEvent = false;
                }
            }
        }

        private void ClipboardToggleButton_Click(object sender, RoutedEventArgs e)
        {
            // 확장 노치에서도 동일한 오른쪽 패널로 접근 — 하단 스트립은 철폐됐다
            if (_clipboardHistoryOpen)
            {
                HideClipboardHistoryPanel();
                HideClipboardSideCapsule();
            }
            else if (_clipboardHistory.Count > 0)
            {
                // 캡슐이 안 떠 있으면 조용히 띄워 위치 기준점을 만든 뒤 패널을 연다
                ShowClipboardSideCapsule("클립보드");
                ShowClipboardHistoryPanel();
            }
        }

        private void RenderClipboardHistory()
        {
            TextBlock?[] texts = { ClipboardRowText1, ClipboardRowText2, ClipboardRowText3, ClipboardRowText4, ClipboardRowText5 };
            Border?[] rows = { ClipboardRow1, ClipboardRow2, ClipboardRow3, ClipboardRow4, ClipboardRow5 };

            int visibleCount = Math.Min(_clipboardHistory.Count, 5);
            for (int i = 0; i < 5; i++)
            {
                var row = rows[i];
                var text = texts[i];
                if (row == null || text == null) continue;

                if (i < visibleCount)
                {
                    var item = _clipboardHistory[i];

                    if (item.IsImage)
                    {
                        // 행 내용을 썸네일 이미지로 교체 (행 높이는 스트립 레이아웃 유지)
                        var img = _rowImages[i] ??= new Image
                        {
                            Height = 18,
                            Stretch = Stretch.Uniform,
                            HorizontalAlignment = HorizontalAlignment.Left
                        };
                        img.Source = item.Image;
                        row.Child = img;
                    }
                    else
                    {
                        text.Text = item.Text.ReplaceLineEndings(" ").Trim();
                        row.Child = text;
                    }

                    row.Tag = item;
                    row.Visibility = Visibility.Visible;
                }
                else
                {
                    text.Text = string.Empty;
                    row.Tag = null;
                    row.Visibility = Visibility.Collapsed;
                }
            }
        }

        private double CalcClipboardStripHeight()
        {
            int rows = Math.Min(_clipboardHistory.Count, 5);
            if (rows == 0) return 0;
            return 38 + rows * 26; // 헤더 + 행 높이 + 패널 패딩
        }

        private void ShowClipboardStripExpanded()
        {
            if (_clipboardHistory.Count == 0) return;

            _clipboardStripVisible = true;
            RenderClipboardHistory();
            ClipboardStripPanel.Visibility = Visibility.Visible;

            double baseHeight = HasMedia ? 190 : 120;
            double targetHeight = baseHeight + CalcClipboardStripHeight();

            NotchBorder.BeginAnimation(Border.HeightProperty,
                new DoubleAnimation { To = targetHeight, Duration = NotchAnimDuration, EasingFunction = NotchAnimEase });
            ClipboardStripPanel.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation { To = 1, Duration = NotchAnimDuration, EasingFunction = NotchAnimEase });
            ClipboardStripTransform.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation { To = 0, Duration = NotchAnimDuration, EasingFunction = NotchAnimEase });
        }

        private void HideClipboardStripExpanded(bool animateHeight)
        {
            if (!_clipboardStripVisible) return;
            _clipboardStripVisible = false;

            if (animateHeight)
            {
                double baseHeight = HasMedia ? 190 : 120;
                NotchBorder.BeginAnimation(Border.HeightProperty,
                    new DoubleAnimation { To = baseHeight, Duration = NotchAnimDuration, EasingFunction = NotchAnimEase });
            }

            var opacityAnim = new DoubleAnimation { To = 0, Duration = NotchAnimDuration, EasingFunction = NotchAnimEase };
            opacityAnim.Completed += (s, e) =>
            {
                if (!_clipboardStripVisible) ClipboardStripPanel.Visibility = Visibility.Collapsed;
            };
            ClipboardStripPanel.BeginAnimation(UIElement.OpacityProperty, opacityAnim);
            ClipboardStripTransform.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation { To = -10, Duration = NotchAnimDuration, EasingFunction = NotchAnimEase });
        }

        private double CalculateIdleExpandedHeight()
        {
            double baseHeight = 120;
            if (_clipboardStripVisible && _clipboardHistory.Count > 0)
            {
                baseHeight += CalcClipboardStripHeight();
            }
            return baseHeight;
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
        }

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
            // AI 탭에서는 하단 클립보드 스트립이 대화와 겹치므로 항상 닫는다
            _clipboardStripVisible = false;
            ClipboardStripPanel.Visibility = Visibility.Collapsed;
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

        private void ShowConversationOrb(Controls.OrbKind kind)
        {
            ConversationOrb.Kind = kind;
            bool thinking = kind == Controls.OrbKind.Working;
            // 사고 중엔 중앙 대형 오브만 — 헤더 오브와 겹쳐 '구체 2개'로 보이지 않게 한다
            ConversationOrb.Visibility = thinking ? Visibility.Collapsed : Visibility.Visible;
            CenterOrb.Kind = kind;
            CenterOrb.Visibility = thinking ? Visibility.Visible : Visibility.Collapsed;
            AssistantLatestResponseText.Visibility = thinking ? Visibility.Collapsed : Visibility.Visible;

            // border-beam: 입력창은 응답 스트리밍 중에만 광선이 흐른다.
            // 노치 beam은 엠비언트(Assistant 글로우)가 관리하므로 여기서 건드리지 않는다.
            InputBeam.Active = kind == Controls.OrbKind.Composing;

            AssistantStatusText.Text = kind switch
            {
                Controls.OrbKind.Working => "생각 중...",
                Controls.OrbKind.Composing => "응답 중...",
                Controls.OrbKind.Searching => "검색 중...",
                Controls.OrbKind.Solving => "풀이 중...",
                Controls.OrbKind.Listening => "대화 중...",
                Controls.OrbKind.Connecting => "연결 중...",
                Controls.OrbKind.Weaving => "정리 중...",
                Controls.OrbKind.Shaping => "준비 중...",
                _ => "대기 중...",
            };
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
            }

            StopCapsuleTracking();

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
