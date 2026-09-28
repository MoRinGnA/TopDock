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
        private enum ViewMode { IdleCompact, IdleExpanded, MediaCompact, MediaExpanded, VolumeHud, NotificationCompact, NotificationExpanded }

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WM_NCHITTEST = 0x0084;
        private const int HTTRANSPARENT = -1;

        private const double VolumeBarExtraHeight = 54;

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
        private SettingsWindow? _settingsWindow;
        private string _lastMarkerSignature = string.Empty;

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
        private bool _ambientActive = false;
        private Color _currentAmbientColor = Colors.Transparent;
        private List<LyricLine> _syncedLyrics = new();
        private System.Windows.Forms.NotifyIcon? _trayIcon;
        private bool HasMedia => !string.IsNullOrEmpty(_lastMediaKey);

        public MainWindow()
        {
            InitializeComponent();

            ConfigService.Load();

            _audioService = new AudioService();
            _mediaService = new MediaService();
            _lyricsService = new LyricsService();
            _batteryService = new BatteryService();
            _notificationService = new NotificationService();
            _sponsorSkip = new SponsorSkipService { IsEnabled = ConfigService.Current.SponsorSkipEnabled };

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

            _batteryService.BatteryStatusChanged += BatteryService_BatteryStatusChanged;
            _notificationService.NotificationReceived += NotificationService_NotificationReceived;

            Closed += MainWindow_Closed;

            InitializeTrayIcon();

            _ = _mediaService.InitializeAsync();
            _ = _notificationService.InitializeAsync();
            _batteryService.Start();
            
            SwitchViewMode(ViewMode.IdleCompact);
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

            UpdatePosition();
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
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

            if (item.IsImage)
            {
                NotifCompactAppText.Text = "\U0001F4F7 캡처됨";
                NotifCompactTitleText.Text = $"{item.Image!.PixelWidth}×{item.Image.PixelHeight}";
            }
            else
            {
                NotifCompactAppText.Text = "\U0001f4cb 복사됨";
                NotifCompactTitleText.Text = item.Text.Length > 20 ? item.Text.Substring(0, 20) + "..." : item.Text;
            }

            RenderClipboardHistory();
            SwitchViewMode(ViewMode.NotificationCompact);

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
            SwitchViewMode(HasMedia ? ViewMode.MediaCompact : ViewMode.IdleCompact);
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
            DateTime now = DateTime.Now;
            IdleTimeText.Text = now.ToString("HH:mm");
            IdleExpandedTimeText.Text = now.ToString("HH:mm");
            IdleExpandedDateText.Text = now.ToString("M월 d일 dddd");
        }

        private void Notch_MouseEnter(object sender, MouseEventArgs e)
        {
            _isExpanded = true;
            if (_volumeHudTimer != null && _volumeHudTimer.IsEnabled) return;
            if (_notificationTimer != null && _notificationTimer.IsEnabled)
            {
                SwitchViewMode(ViewMode.NotificationExpanded);
                return;
            }
            SwitchViewMode(HasMedia ? ViewMode.MediaExpanded : ViewMode.IdleExpanded);
        }

        private void Notch_MouseLeave(object sender, MouseEventArgs e)
        {
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
        }

        private void Notch_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            float step = 0.02f;
            int newVol = _audioService.StepVolume(e.Delta > 0 ? step : -step, out bool isMuted);
            ShowVolumeHud(newVol, isMuted);
            e.Handled = true;
        }

        private static readonly Duration AmbientColorFade = new Duration(TimeSpan.FromMilliseconds(600));

        private void SetAmbientColor(Color color, Color? secondaryColor = null)
        {
            Color primary = EnhanceAmbientColor(color);
            Color secondary = secondaryColor ?? GenerateShiftedColor(primary, 14);

            _currentAmbientColor = primary;

            var ease = new SineEase { EasingMode = EasingMode.EaseInOut };

            void AnimateStop(GradientStop stop, Color to)
            {
                var anim = new ColorAnimation { To = to, Duration = AmbientColorFade, EasingFunction = ease };
                stop.BeginAnimation(GradientStop.ColorProperty, anim);
            }

            // 색은 진하게 유지하되 확산은 노치 바로 옆에 머물게 해서 '빛 안개'가 되지 않도록
            Color aura1 = Color.FromArgb(170, primary.R, primary.G, primary.B);
            Color aura2 = Color.FromArgb(60, secondary.R, secondary.G, secondary.B);
            Color rim1 = Color.FromArgb(215, primary.R, primary.G, primary.B);
            Color rim2 = Color.FromArgb(180, secondary.R, secondary.G, secondary.B);

            // 트랙 전환 시 색이 뿅 바뀌지 않고 자연스럽게 크로스페이드되도록
            if (_ambientActive)
            {
                AnimateStop(AuraColorStop1, aura1);
                AnimateStop(AuraColorStop2, aura2);
                AnimateStop(RimColorStop1, rim1);
                AnimateStop(RimColorStop2, rim2);
            }
            else
            {
                // 첫 표시는 즉시 적용 (브레스 애니메이션 페이드인이 자연스럽게 이어줌)
                AuraColorStop1.Color = aura1;
                AuraColorStop2.Color = aura2;
                RimColorStop1.Color = rim1;
                RimColorStop2.Color = rim2;
            }

            // Sync compact equalizer bars to vibrant ambient color
            var eqFade = new ColorAnimation { To = primary, Duration = AmbientColorFade, EasingFunction = ease };
            foreach (var bar in new[] { EqBar1, EqBar2, EqBar3 })
            {
                if (bar.Background is SolidColorBrush eqBrush && !eqBrush.IsFrozen)
                    eqBrush.BeginAnimation(SolidColorBrush.ColorProperty, eqFade);
                else
                    bar.Background = new SolidColorBrush(primary);
            }

            if (!_ambientActive)
            {
                _ambientActive = true;
                StartAmbientBreathAnimation();
            }
        }

        private void ClearAmbientLight()
        {
            _ambientActive = false;
            StopAmbientBreathAnimation();

            var eqRestore = new ColorAnimation { To = Color.FromRgb(255, 159, 10), Duration = TimeSpan.FromMilliseconds(400) };
            foreach (var bar in new[] { EqBar1, EqBar2, EqBar3 })
            {
                if (bar.Background is SolidColorBrush eqBrush && !eqBrush.IsFrozen)
                    eqBrush.BeginAnimation(SolidColorBrush.ColorProperty, eqRestore);
                else
                    bar.Background = new SolidColorBrush(Color.FromRgb(255, 159, 10));
            }

            var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(400));
            NotchAmbientContainer.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        }

        private void StartAmbientBreathAnimation()
        {
            StopAmbientBreathAnimation();

            var breathAnim = new DoubleAnimation(0.62, 0.96, TimeSpan.FromSeconds(2.4))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            Timeline.SetDesiredFrameRate(breathAnim, 30);
            NotchAmbientContainer.BeginAnimation(UIElement.OpacityProperty, breathAnim);
        }

        private void StopAmbientBreathAnimation()
        {
            NotchAmbientContainer.BeginAnimation(UIElement.OpacityProperty, null);
            NotchAmbientContainer.Opacity = 0;
        }

        private void UpdateGlowDimensions(double targetWidth, double targetHeight, double targetRadius, Duration duration, IEasingFunction ease)
        {
            // 오로라는 노치 에지에서 20~30px 내로 빠르게 소멸하는 타이트한 헤일로
            double auraW = targetWidth + 85;
            double auraH = targetHeight + 28;
            double auraRadius = targetRadius + 14;

            double rimW = targetWidth + 4;
            double rimH = targetHeight + 4;
            double rimRadius = targetRadius + 2;

            NotchAmbientAura.CornerRadius = new CornerRadius(auraRadius);
            NotchAmbientRim.CornerRadius = new CornerRadius(rimRadius);

            var auraWAnim = new DoubleAnimation { To = auraW, Duration = duration, EasingFunction = ease };
            var auraHAnim = new DoubleAnimation { To = auraH, Duration = duration, EasingFunction = ease };
            var rimWAnim = new DoubleAnimation { To = rimW, Duration = duration, EasingFunction = ease };
            var rimHAnim = new DoubleAnimation { To = rimH, Duration = duration, EasingFunction = ease };

            Timeline.SetDesiredFrameRate(auraWAnim, 60);
            Timeline.SetDesiredFrameRate(auraHAnim, 60);
            Timeline.SetDesiredFrameRate(rimWAnim, 60);
            Timeline.SetDesiredFrameRate(rimHAnim, 60);

            NotchAmbientAura.BeginAnimation(Border.WidthProperty, auraWAnim);
            NotchAmbientAura.BeginAnimation(Border.HeightProperty, auraHAnim);
            NotchAmbientRim.BeginAnimation(Border.WidthProperty, rimWAnim);
            NotchAmbientRim.BeginAnimation(Border.HeightProperty, rimHAnim);
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

        private void BatteryService_BatteryStatusChanged(object? sender, BatteryStatusArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                bool isNormal = !e.IsCharging && e.BatteryPercent > 0.20f;

                if (isNormal)
                {
                    // Clear battery border modifications and restore style defaults
                    NotchBorder.ClearValue(Border.BorderBrushProperty);
                    
                    // If no media is playing, clear glow. If media is playing, keep media glow.
                    if (!HasMedia)
                    {
                        ClearAmbientLight();
                    }
                    return;
                }

                Color borderColor = e.IsCharging ? Color.FromRgb(57, 255, 20) : Color.FromRgb(255, 59, 48);

                // Music art glow and border takes priority over battery!
                if (!HasMedia)
                {
                    if (e.IsCharging)
                        SetAmbientColor(borderColor, Color.FromRgb(10, 120, 5));
                    else
                        SetAmbientColor(borderColor, Color.FromRgb(150, 15, 10));

                    // Create a LinearGradientBrush to act as a progress bar along the actual Notch border
                    var gradient = new LinearGradientBrush
                    {
                        StartPoint = new Point(0, 0.5),
                        EndPoint = new Point(1, 0.5)
                    };
                    gradient.GradientStops.Add(new GradientStop(borderColor, e.BatteryPercent));
                    // Use the subtle notch border color (#33FFFFFF) for the unfilled portion so it blends perfectly
                    gradient.GradientStops.Add(new GradientStop(Color.FromArgb(51, 255, 255, 255), e.BatteryPercent));

                    NotchBorder.BorderBrush = gradient;
                }
                else
                {
                    // If media is playing, restore normal border
                    NotchBorder.ClearValue(Border.BorderBrushProperty);
                }
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
                    targetWidth = 100;
                    targetHeight = 38;
                    targetRadius = 19;
                    activeView = IdleCompactView;
                    break;
                case ViewMode.IdleExpanded:
                    targetWidth = 260;
                    // 시계 확장 진입 시 클립보드 히스토리가 있으면 하단 스트립을 자동 표시
                    if (_clipboardHistory.Count > 0)
                    {
                        ShowClipboardStripExpanded();
                    }
                    targetHeight = CalculateIdleExpandedHeight();
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

            UpdateGlowDimensions(targetWidth, targetHeight, targetRadius, duration, ease);

            DoubleAnimation mainContainerHeightAnim = new DoubleAnimation { To = targetHeight, Duration = duration, EasingFunction = ease };
            Timeline.SetDesiredFrameRate(mainContainerHeightAnim, 60);
            MainContainer.BeginAnimation(FrameworkElement.HeightProperty, mainContainerHeightAnim);
        }

        private void SetViewActive(UIElement activeView, Duration duration, IEasingFunction ease)
        {
            UIElement[] views = { VolumeHudView, IdleCompactView, IdleExpandedView, MediaCompactView, MediaExpandedView, NotificationCompactView, NotificationExpandedView };
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

                    var avgColor = GetDominantColor(validBmp);
                    SetAmbientColor(avgColor);
                }
                else
                {
                    ExpandedAlbumArtImage.Source = null;
                    ExpandedDefaultIcon.Visibility = Visibility.Visible;

                    // 썸네일이 아직 도착 전(또는 파비콘으로 필터링됨).
                    // 제목 해시 랜덤 색 대신: 이전 곡 글로우를 유지하다가 진짜 앨범 색이 오면 크로스페이드.
                    // 첫 곡이라 살아 있는 글로우가 없으면 절제된 중립 모노톤으로 시작.
                    if (!_ambientActive)
                    {
                        SetAmbientColor(Color.FromRgb(96, 96, 104));
                    }

                    if (isNewTrack)
                    {
                        _ = RetryThumbnailAsync(key);
                    }
                }

                UpdatePlaybackState(media.IsPlaying);
                UpdateTimelineDisplay(media.CurrentEstimatedPosition, media.Duration);
            });
        }

        private Color GenerateFallbackColor(string title)
        {
            int hash = Math.Abs(title.GetHashCode());
            double hue = (hash % 360);
            return HslToColor(hue, 0.82, 0.56);
        }

        private static void ColorToHsl(Color c, out double h, out double s, out double l)
        {
            double r = c.R / 255.0;
            double g = c.G / 255.0;
            double b = c.B / 255.0;

            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            double delta = max - min;

            l = (max + min) / 2.0;

            if (delta < 0.00001)
            {
                h = 0;
                s = 0;
            }
            else
            {
                s = l <= 0.5 ? delta / (max + min) : delta / (2.0 - max - min);

                if (Math.Abs(r - max) < 0.00001)
                    h = (g - b) / delta + (g < b ? 6.0 : 0.0);
                else if (Math.Abs(g - max) < 0.00001)
                    h = (b - r) / delta + 2.0;
                else
                    h = (r - g) / delta + 4.0;

                h *= 60.0;
            }
        }

        private static Color HslToColor(double h, double s, double l)
        {
            h = (h % 360 + 360) % 360;
            s = Math.Clamp(s, 0.0, 1.0);
            l = Math.Clamp(l, 0.0, 1.0);

            double c = (1 - Math.Abs(2 * l - 1)) * s;
            double x = c * (1 - Math.Abs((h / 60) % 2 - 1));
            double m = l - c / 2;
            double r = 0, g = 0, b = 0;

            if (h < 60) { r = c; g = x; }
            else if (h < 120) { r = x; g = c; }
            else if (h < 180) { g = c; b = x; }
            else if (h < 240) { g = x; b = c; }
            else if (h < 300) { r = x; b = c; }
            else { r = c; b = x; }

            return Color.FromRgb(
                (byte)Math.Clamp((r + m) * 255, 0, 255),
                (byte)Math.Clamp((g + m) * 255, 0, 255),
                (byte)Math.Clamp((b + m) * 255, 0, 255));
        }

        private static Color EnhanceAmbientColor(Color c)
        {
            ColorToHsl(c, out double h, out double s, out double l);

            // 무채색 입력은 채도를 부스트하지 않고 모노톤으로 유지 (흑백 커버, 중립 글로우)
            if (s < 0.06)
            {
                double monoL = Math.Clamp(l * 1.15, 0.38, 0.62);
                return HslToColor(h, 0, monoL);
            }

            // 설정의 글로우 강도 프리셋에 따라 부스트 배수와 클램프 범위가 달라진다
            bool subtle = ConfigService.Current.GlowIntensity == "subtle";
            bool vivid = ConfigService.Current.GlowIntensity == "vivid";

            double sMul = subtle ? 1.15 : vivid ? 1.70 : 1.45;
            double sMin = subtle ? 0.30 : vivid ? 0.55 : 0.45;
            double sMax = subtle ? 0.80 : vivid ? 1.00 : 0.98;
            double lMul = subtle ? 1.10 : vivid ? 1.20 : 1.15;

            // 원본 앨범의 색감을 최대한 유지하면서 앰비언트 느낌만 주도록 부스팅
            double boostedS = Math.Clamp(s * sMul, sMin, sMax);

            // 명도를 너무 심하게 좁은 구간으로 뭉개지 않고, 고유의 밝기를 살려줌
            double tunedL = Math.Clamp(l * lMul, 0.35, 0.75);

            return HslToColor(h, boostedS, tunedL);
        }

        private static Color GenerateShiftedColor(Color primary, double hueShiftDegrees)
        {
            ColorToHsl(primary, out double h, out double s, out double l);
            double shiftedH = (h + hueShiftDegrees) % 360;
            return HslToColor(shiftedH, Math.Max(0.78, s), Math.Clamp(l * 0.96, 0.46, 0.56));
        }

        private Color GetDominantColor(BitmapSource bitmap)
        {
            try
            {
                int targetW = Math.Min(bitmap.PixelWidth, 48);
                int targetH = Math.Min(bitmap.PixelHeight, 48);
                var scaled = new TransformedBitmap(bitmap, new ScaleTransform(
                    (double)targetW / bitmap.PixelWidth,
                    (double)targetH / bitmap.PixelHeight));

                var formatConverted = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
                int width = formatConverted.PixelWidth;
                int height = formatConverted.PixelHeight;
                int stride = width * 4;
                byte[] pixels = new byte[height * stride];
                formatConverted.CopyPixels(pixels, stride, 0);

                // 16개 Hue 버킷 (각 22.5도) 히스토그램 클러스터링
                const int numBuckets = 16;
                double[] bucketScores = new double[numBuckets];
                double[] bucketSumR = new double[numBuckets];
                double[] bucketSumG = new double[numBuckets];
                double[] bucketSumB = new double[numBuckets];
                double[] bucketWeights = new double[numBuckets];

                double lumSum = 0;
                int lumCount = 0;

                for (int i = 0; i < pixels.Length; i += 4)
                {
                    byte b = pixels[i];
                    byte g = pixels[i + 1];
                    byte r = pixels[i + 2];

                    ColorToHsl(Color.FromRgb(r, g, b), out double h, out double s, out double l);

                    lumSum += l;
                    lumCount++;

                    // 유효한 유색 픽셀 판별 (채도와 명도가 적절한 픽셀)
                    if (s >= 0.18 && l >= 0.12 && l <= 0.88)
                    {
                        // 채도가 높고 중간 밝기일수록 가중치 부여
                        double weight = s * s * (1.0 - Math.Abs(l - 0.5) * 1.3);
                        int bucket = (int)(h / (360.0 / numBuckets)) % numBuckets;
                        if (bucket < 0) bucket = 0;

                        bucketScores[bucket] += weight;
                        bucketSumR[bucket] += r * weight;
                        bucketSumG[bucket] += g * weight;
                        bucketSumB[bucket] += b * weight;
                        bucketWeights[bucket] += weight;
                    }
                }

                // 가장 점수가 높은 지배적 색상 버킷 선정
                int bestBucket = -1;
                double maxScore = 0;
                for (int b = 0; b < numBuckets; b++)
                {
                    if (bucketScores[b] > maxScore)
                    {
                        maxScore = bucketScores[b];
                        bestBucket = b;
                    }
                }

                if (bestBucket >= 0 && bucketWeights[bestBucket] > 0.05)
                {
                    // SetAmbientColor에서 EnhanceAmbientColor를 다시 적용하므로 여기서는 순수 지배색만 반환 (이중 부스팅 방지)
                    return Color.FromRgb(
                        (byte)Math.Clamp(bucketSumR[bestBucket] / bucketWeights[bestBucket], 0, 255),
                        (byte)Math.Clamp(bucketSumG[bestBucket] / bucketWeights[bestBucket], 0, 255),
                        (byte)Math.Clamp(bucketSumB[bestBucket] / bucketWeights[bestBucket], 0, 255));
                }

                // 흑백/무채색 커버: 해시 폴백 대신 커버의 실제 밝기에 맞춘 모노톤 글로우
                if (lumCount > 0)
                {
                    double avgL = Math.Clamp((lumSum / lumCount) * 1.15, 0.30, 0.66);
                    return HslToColor(0, 0, avgL);
                }

                return GenerateFallbackColor(_lastMediaKey);
            }
            catch
            {
                return GenerateFallbackColor(_lastMediaKey);
            }
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

                                var avgColor = GetDominantColor(bmp);
                                SetAmbientColor(avgColor);
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
                // 스폰서/인트로 등 스킵 대상 구간 진입 시 자동 건너뛰기
                var skipTarget = _sponsorSkip.GetSkipTarget(currentPos, duration);
                if (skipTarget.HasValue)
                {
                    _ = _mediaService.TrySeekAsync(skipTarget.Value);
                }

                UpdateTimelineDisplay(currentPos, duration);
                // 가사 표시는 500ms 미리 룩업하여 실제 음악과 싱크 맞춤
                var lyricPos = _lyricsService.GetAdjustedPosition(currentPos + LyricLookahead, duration);
                UpdateLyricsDisplay(lyricPos);
            }
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
            if (_clipboardStripVisible)
                HideClipboardStripExpanded(animateHeight: true);
            else
                ShowClipboardStripExpanded();
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

            ClearAmbientLight();

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

        private void ApplySettings()
        {
            var cfg = ConfigService.Current;

            _sponsorSkip.IsEnabled = cfg.SponsorSkipEnabled;

            UpdatePosition();

            // 글로우 강도 변경을 현재 색에 즉시 반영
            if (_mediaService.CurrentMedia?.Thumbnail is BitmapSource bmp && bmp.PixelWidth >= 48 && bmp.PixelHeight >= 48)
            {
                SetAmbientColor(GetDominantColor(bmp));
            }
            else if (!HasMedia)
            {
                ClearAmbientLight();
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

        private void MainWindow_Closed(object? sender, EventArgs e)
        {
            _progressTimer.Stop();
            _clockTimer.Stop();
            _volumeHudTimer?.Stop();
            _emptyMediaDebounceTimer?.Stop();
            StopEqualizerAnimation();
            StopAmbientBreathAnimation();

            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero) RemoveClipboardFormatListener(hwnd);

            _sponsorSkip.Dispose();
            _audioService.Dispose();
            _mediaService.Dispose();
            _lyricsService.Dispose();
            _batteryService.Dispose();
            _notificationService.Dispose();
        }
    }
}
