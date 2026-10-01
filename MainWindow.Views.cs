using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using TopDock.Models;
using TopDock.Services;

namespace TopDock
{
    public partial class MainWindow : Window
    {
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
        private void UpdateGlowDimensions(double targetWidth, double targetHeight, Duration duration, IEasingFunction ease)
        {
            var wAnim = new DoubleAnimation { To = targetWidth, Duration = duration, EasingFunction = ease };
            var hAnim = new DoubleAnimation { To = targetHeight, Duration = duration, EasingFunction = ease };

            Timeline.SetDesiredFrameRate(wAnim, 60);
            Timeline.SetDesiredFrameRate(hAnim, 60);

            NotchBeam.BeginAnimation(WidthProperty, wAnim);
            NotchBeam.BeginAnimation(HeightProperty, hAnim);
        }

        // ── 모서리 반경 ──
        // WPF에는 CornerRadius용 애니메이션이 없다 — Border.CornerRadius는 CornerRadius 타입인데
        // Thickness처럼 대응하는 *Animation 클래스가 제공되지 않는다(실제로 없음을 확인).
        // 그래서 프레임을 직접 돌린다. 빛(BorderBeam.CornerRadius는 double)과 본체에 같은 값을
        // 밀어주므로 도는 동안에도 둘이 벌어지지 않는다 — 예전에는 본체만 즉시 목표 반경으로
        // 튀어서, 펼친 아일랜드가 접힐 때 빛이 본체 모서리에서 떨어져 나간 것처럼 보였다.

        private DispatcherTimer? _radiusTimer;
        private long _radiusStart;
        private double _radiusFrom;
        private double _radiusTo;
        private double _radiusTotalMs;
        private IEasingFunction? _radiusEase;

        /// <summary>본체와 빛의 모서리 반경을 크기 애니메이션과 같은 길이·곡선으로 함께 바꾼다.</summary>
        private void AnimateNotchRadius(double target, Duration duration, IEasingFunction ease)
        {
            StopNotchRadiusAnimation();

            double from = NotchBorder.CornerRadius.TopLeft;
            _radiusTotalMs = duration.TimeSpan.TotalMilliseconds;

            // 반경이 거의 안 바뀌는 전환(기본↔미디어 접힘 등)은 프레임을 돌릴 이유가 없다
            if (_radiusTotalMs <= 1 || Math.Abs(target - from) < 0.5)
            {
                SetNotchRadius(target);
                return;
            }

            _radiusFrom = from;
            _radiusTo = target;
            _radiusEase = ease;
            _radiusStart = System.Diagnostics.Stopwatch.GetTimestamp();

            _radiusTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(8),
            };
            _radiusTimer.Tick += RadiusTimer_Tick;
            _radiusTimer.Start();
            RadiusTimer_Tick(null, EventArgs.Empty); // 첫 프레임을 기다리지 않고 바로 출발
        }

        private void RadiusTimer_Tick(object? sender, EventArgs e)
        {
            double elapsedMs = (System.Diagnostics.Stopwatch.GetTimestamp() - _radiusStart) * 1000.0
                               / System.Diagnostics.Stopwatch.Frequency;
            double t = Math.Clamp(elapsedMs / _radiusTotalMs, 0, 1);
            SetNotchRadius(_radiusFrom + (_radiusTo - _radiusFrom) * (_radiusEase?.Ease(t) ?? t));
            if (t >= 1) StopNotchRadiusAnimation();
        }

        /// <summary>본체와 빛에 같은 반경을 먹인다.</summary>
        private void SetNotchRadius(double radius)
        {
            NotchBorder.CornerRadius = new CornerRadius(radius);
            NotchBeam.CornerRadius = radius;
        }

        private void StopNotchRadiusAnimation()
        {
            if (_radiusTimer == null) return;
            _radiusTimer.Stop();
            _radiusTimer.Tick -= RadiusTimer_Tick;
            _radiusTimer = null;
        }

        /// <summary>컴팩트 노치에서 가사 한 줄에 내주는 최대 폭.</summary>
        private const double CompactLyricBudget = 210;

        private double CalculateCompactWidth()
        {
            string title = CompactTitleText.Text ?? "";

            double titleWidth = MeasureTextWidth(title, 13.5, FontWeights.SemiBold);

            // 가사는 실제 길이가 아니라 고정 예산만 쓴다. 예전에는 쥴 길이를 그대로 더해서
            // 가사가 한 줄 바뀔 때마다 노치 폭이 200ms 애니메이션으로 흔들렸다.
            double lyricWidth = LyricsExpected ? CompactLyricBudget : 0;

            // 35 = 좌우 여백 + 이퀄라이저 폭, 20 = 그 오른쪽 배터리 점 자리.
            // 점은 대부분 숨겨져 있지만 자리는 항상 비워 둔다 — 재생 중에 충전을 시작해도
            // 폭을 다시 계산하러 들어갈 필요가 없다.
            double baseWidth = 45;
            double calculated = baseWidth + titleWidth + lyricWidth + 35 + BatteryDotSlotWidth;
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
                    // 가사가 있으면 가사 자리까지, 없으면 앨범아트+컨트롤 폭만
                    targetWidth = LyricsExpected ? 620 : 276;
                    targetHeight = 190;
                    targetRadius = 36;
                    activeView = MediaExpandedView;
                    break;
                case ViewMode.VolumeHud:
                    targetWidth = _isExpanded ? (LyricsExpected ? 620 : 276) : 240;
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

            // 가사 자리는 폭과 함께 결정된다 — 좌우 정렬과 열 너비를 먼저 맞춰야
            // 페이드인 순간에 내용이 다른 자리에 앉았다가 건너뛰지 않는다
            ApplyLyricsLayout(LyricsExpected);

            SetViewActive(activeView, duration, ease);

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

            // 반경은 크기와 같은 길이·곡선으로 움직여야 빛이 본체에 붙어 있는다
            AnimateNotchRadius(targetRadius, duration, ease);
            UpdateGlowDimensions(targetWidth, targetHeight, duration, ease);

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
    }
}
