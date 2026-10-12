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

        /// <summary>
        /// 도크 모양을 정하는 데 필요한 지금의 신호 한 벌. 창이 아는 것을 그대로 모아 주면
        /// "그래서 어떤 모양이어야 하는가"는 DockState(순수 로직)가 답한다.
        /// </summary>
        private DockSignals Signals => new(
            HasMedia: HasMedia,
            VolumeHudActive: _volumeHudTimer?.IsEnabled == true,
            NoticeActive: NoticeVisible,
            AssistantView: _currentViewMode is ViewMode.Assistant or ViewMode.AssistantCompact,
            MouseOver: NotchBorder.IsMouseOver,
            InboxOpen: _inboxOpen);

        /// <summary>알림이 하나라도 떠 있는가 — 타이머가 도는 중이거나, 스스로 사라지지 않는 알림.</summary>
        private bool NoticeVisible => NoticeActive || _notificationTimer?.IsEnabled == true;

        /// <summary>
        /// 상태 머신이 정한 모양으로 옮긴다. 이미 그 모양이거나 옮길 게 없으면(null) 아무 일도 하지 않는다 —
        /// 같은 모양으로 다시 전환하면 모프 애니메이션만 처음부터 돌아 화면이 튄다.
        ///
        /// redraw: 같은 모양이어도 내용에 따라 크기가 달라졌을 수 있을 때(새 곡의 가사 자리 등).
        /// 그때는 모양 전환이 아니라 다시 그리기다.
        /// </summary>
        private void ApplyMode(ViewMode? next, bool redraw = false)
        {
            if (next is not { } mode || (mode == _currentViewMode && !redraw)) return;

            SwitchViewMode(mode);

            // 접힌 아이들 얼굴은 다시 접힐 때마다 기본 표정으로 돌려놓는다
            if (mode is ViewMode.IdleCompact or ViewMode.MediaCompact)
            {
                CompactFace?.SetState(Controls.AiFace.FaceState.Idle);
            }
        }

        /// <summary>
        /// 지금 신호로 모양을 다시 정한다. 볼륨 HUD가 떠 있는 동안엔 크기 변경을 미룬다 —
        /// 그 사이 마우스가 들어오거나 나간 일은 HUD가 끝날 때 VolumeHudExpired가 한 번만 반영한다.
        /// </summary>
        private void SyncViewMode()
        {
            if (Signals.VolumeHudActive) return;
            ApplyMode(_dock.Resolve(Signals));
        }

        private void Notch_MouseEnter(object sender, MouseEventArgs e)
        {
            // 비서 화면(작업 중 아일랜드)·볼륨 HUD·알림까지 아는 한 곳에서 다음 모양을 정한다
            ViewMode? next = _dock.Hover(Signals);
            ApplyMode(next);
            if (next is ViewMode.IdleExpanded) RefreshIdleFace();
        }

        private void Notch_MouseLeave(object sender, MouseEventArgs e)
        {
            // 비서 화면은 마우스가 벗어나도 닫지 않는다 — 닫기는 ✕ 버튼과 Esc가 맡는다(DockState가 안다)
            SyncAudioRing(animate: true);
            ApplyMode(_dock.Leave(Signals));
        }

        private void Notch_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            // 인박스를 보는 중엔 휠이 볼륨이 아니라 기록을 넘긴다 — 위로 굴리면 최신 쪽으로
            if (_inboxOpen)
            {
                MoveInboxSelection(e.Delta > 0 ? -1 : 1);
                e.Handled = true;
                return;
            }

            float step = 0.02f;
            int newVol = _audioService.StepVolume(e.Delta > 0 ? step : -step, out bool isMuted);
            ShowVolumeRing(newVol, isMuted);
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

            Timeline.SetDesiredFrameRate(wAnim, Tokens.FrameRate);
            Timeline.SetDesiredFrameRate(hAnim, Tokens.FrameRate);

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

        /// <summary>컴팩트 노치 오른쪽 끝에서 링이 쓰는 폭 — 링 24 + 오른쪽 여백 6.</summary>
        private const double RingSlotWidth = 30;

        private double CalculateCompactWidth()
        {
            string title = CompactTitleText.Text ?? "";

            double titleWidth = MeasureTextWidth(title, 13.5, FontWeights.SemiBold);

            // 가사는 실제 길이가 아니라 고정 예산만 쓴다. 예전에는 쥴 길이를 그대로 더해서
            // 가사가 한 줄 바뀔 때마다 노치 폭이 200ms 애니메이션으로 흔들렸다.
            double lyricWidth = LyricsExpected ? CompactLyricBudget : 0;

            // 35 = 좌우 여백 + 제목과 가사 사이, 30 = 오른쪽 끝 자리(링 24 + 여백 6).
            // 이 자리는 재생 내내 링이 쓰고 그 위에 배터리 점이 잠깐 앉는다 — 둘 중 넓은 쪽을
            // 늘 비워 둔다. 상태가 바뀔 때마다 폭을 다시 계산하러 들어갈 필요가 없다.
            double baseWidth = 45;
            double rightSlot = Math.Max(RingSlotWidth, BatteryDotSlotWidth);
            double calculated = baseWidth + titleWidth + lyricWidth + 35 + rightSlot;
            return Math.Clamp(calculated, 320, 800);
        }

        /// <summary>
        /// 화면과 같은 글꼴. App.xaml의 AppFont 하나만 본다 — 폭을 재는 쪽과 글자를 그리는
        /// 쪽이 서로 다른 글꼴을 쓰면 컴팩트 노치 폭이 실제 글자 폭과 달라진다.
        /// </summary>
        private static FontFamily? _uiFont;
        private static FontFamily UiFont =>
            _uiFont ??= Application.Current?.Resources["AppFont"] as FontFamily ?? new FontFamily("Segoe UI");

        private double MeasureTextWidth(string text, double fontSize, FontWeight fontWeight)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            var formattedText = new FormattedText(
                text,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface(UiFont, FontStyles.Normal, fontWeight, FontStretches.Normal),
                fontSize,
                Brushes.White,
                VisualTreeHelper.GetDpi(this).PixelsPerDip
            );
            return formattedText.Width;
        }

        private void SwitchViewMode(ViewMode mode)
        {
            ViewMode previous = _currentViewMode;
            _currentViewMode = mode;
            // 얼굴 애니메이션은 AiFace가 자기 상태와 보이는 여부로 관리한다 —
            // 여기서 상태를 추측해 켜고 끄면 실제 표정과 어긋난다.
            AssistantFace.Visibility = mode == ViewMode.Assistant ? Visibility.Visible : Visibility.Collapsed;

            // 확장은 여유 있게, 축소는 빠르게 — 모프 방향에 따라 리듬을 다르게 (targetWidth 결정 후 계산)
            Duration duration = new Duration(TimeSpan.FromMilliseconds(Tokens.ViewFadeInMs));
            ExponentialEase ease = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = Tokens.MorphEaseExponent };

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
                    // 2단 — 아트·제목·컨트롤이 한 줄, 그 아래 진행바·시간.
                    // 카드 = 아트 줄 390 + 좌우 20. 가사를 보여줄 때만 가사 자리(약 283)를 더한다.
                    // 높이 144는 내용(약 90) 위아래로 약 27씩 남는 값 — 120은 숨이 답답했다.
                    targetWidth = LyricsExpected ? 748 : 430;
                    targetHeight = 144;
                    targetRadius = 30;
                    activeView = MediaExpandedView;
                    break;
                case ViewMode.NotificationCompact:
                    // 앱 타일(18) + 타일-이름(8) + 이름-제목(9) + 좌우 여백(14×2) = 63에 글자 폭을 더한다.
                    // 상한을 좁게 잡은 이유: 예전엔 500까지 늘어나 긴 제목 하나가 노치를 가로질렀다.
                    // 음악이 흐르는 동안엔 오른쪽 끝 자리를 링이 쓰므로 그만큼 더 넓한다 —
                    // 한 줄 카드에선 제목이 오른쪽 끝까지 차서, 안 넓하면 링이 글자 위에 얹힌다.
                    targetWidth = Math.Clamp(
                        MeasureTextWidth(NotifCompactAppText.Text, 12, FontWeights.SemiBold)
                        + MeasureTextWidth(NotifCompactTitleText.Text, 13.5, FontWeights.SemiBold)
                        + 67 + (WantAudioRing ? RingSlotWidth : 0), 240, 420);
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
                case ViewMode.Inbox:
                    // 기록 카드 — 한 건씩 앉히니 내용에 맞춰 자리를 넉넉히 준다.
                    // 너무 크면 노치가 아니라 창처럼 보인다(알림 확장 360×160보다 한 뼘만 크게).
                    targetWidth = 420;
                    targetHeight = 200;
                    targetRadius = 32;
                    activeView = InboxView;
                    break;
                case ViewMode.AssistantCompact:
                    // 작업 중 아일랜드 — 눈만 둔다. 문구가 없으니 아이들 아일랜드와 같은 폭(100)으로 고정,
                    // 무엇을 하는 중이든 노치가 늘었다 줄지 않는다.
                    targetWidth = 100;
                    targetHeight = 38;
                    targetRadius = 19;
                    activeView = AssistantCompactView;
                    break;
                case ViewMode.Assistant:
                    // 얼굴 무대 · 응답 · 입력 3단
                    targetWidth = 460;
                    targetHeight = 336;
                    targetRadius = 34;
                    activeView = AssistantView;
                    // 헤더 오버레이(버튼)는 본체 뷰와 독립적으로 함께 표시
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
                    new DoubleAnimation { To = 0, Duration = new Duration(TimeSpan.FromMilliseconds(Tokens.ViewFadeOutMs)) });
            }

            // 링은 모양이 바뀔 때마다 지금의 의도에 맞춰 즉시 앉힌다 — 페이드가 모프와 겹치면
            // 어긋나 보인다(예전엔 접힐 때만 정리했지만, 이제 링은 재생 내내 떠 있다)
            SettleAudioRing();

            // 가사 자리는 폭과 함께 결정된다 — 좌우 정렬과 열 너비를 먼저 맞춰야
            // 페이드인 순간에 내용이 다른 자리에 앉았다가 건너뛰지 않는다
            ApplyLyricsLayout(LyricsExpected);

            SetViewActive(activeView, duration, ease);

            // 배터리 점은 접힌/기본 확장 노치에서 같은 자리에 유지한다.
            ApplyBatteryIndicator();
            // AI 상태 점도 같은 자리 — 비서 화면을 벗어나면 여기서 함께 걷힌다.
            ApplyAssistantIndicator();

            bool expanding = targetWidth > NotchBorder.Width;
            // 알림은 빠르게 — 오래 늘어나는 카드가 아니라 "방금 무슨 일이 있었다"는 신호라
            // 빨리 자리를 잡고(300ms) 빨리 걷히는 편이 알림답다. 걷히는 방향(평소 화면으로)은
            // 아래 기본값을 그대로 써서 부드럽게 돌아간다.
            bool notice = mode is ViewMode.NotificationCompact or ViewMode.NotificationExpanded;
            duration = new Duration(TimeSpan.FromMilliseconds(
                notice ? Tokens.NoticeMorphMs : expanding ? Tokens.MorphExpandMs : Tokens.MorphCollapseMs));

            DoubleAnimation widthAnim = new DoubleAnimation { To = targetWidth, Duration = duration, EasingFunction = ease };
            DoubleAnimation heightAnim = new DoubleAnimation { To = targetHeight, Duration = duration, EasingFunction = ease };

            Timeline.SetDesiredFrameRate(widthAnim, Tokens.FrameRate);
            Timeline.SetDesiredFrameRate(heightAnim, Tokens.FrameRate);

            NotchBorder.BeginAnimation(Border.WidthProperty, widthAnim);
            NotchBorder.BeginAnimation(Border.HeightProperty, heightAnim);

            // 반경은 크기와 같은 길이·곡선으로 움직여야 빛이 본체에 붙어 있는다
            AnimateNotchRadius(targetRadius, duration, ease);
            UpdateGlowDimensions(targetWidth, targetHeight, duration, ease);

            DoubleAnimation mainContainerHeightAnim = new DoubleAnimation { To = targetHeight, Duration = duration, EasingFunction = ease };
            Timeline.SetDesiredFrameRate(mainContainerHeightAnim, Tokens.FrameRate);
            MainContainer.BeginAnimation(FrameworkElement.HeightProperty, mainContainerHeightAnim);

            UpdateBeamInteriorLight();

            // 비서 화면의 내용도 알림 카드와 같은 순서로 바뀐다 — 상자가 먼저 자리를 만들고
            // 내용이 뒤따라 앉는다. 같은 프레임에 다 나타나면 카드가 툭 튀어나온 것처럼 보인다.
            PlayAssistantContentTransition(previous, mode);
        }

        /// <summary>비서 카드의 내용이 앉고 물러나는 손짓 — 알림 내용과 같은 리듬(220ms · 90ms 지연).</summary>
        private void PlayAssistantContentTransition(ViewMode previous, ViewMode next)
        {
            if (next == ViewMode.Assistant && previous != ViewMode.Assistant)
            {
                PlayContentSettle(AssistantView, AssistantContentScale, AssistantContentSlide);
            }
            else if (previous == ViewMode.Assistant && next != ViewMode.Assistant)
            {
                PlayContentExit(AssistantView, AssistantContentScale, AssistantContentSlide);
            }
        }

        /// <summary>
        /// 노치 테두리 빛의 '안쪽 물들임·틴트'를 상황에 맞게 낮춘다.
        ///
        /// 이 빛은 표면 크기에 비례해 세진다 — 작은 알약(100×38)에선 사실상 0이고,
        /// 확장 카드(260×120, 748×144)에선 최대로 번진다. 빛은 링 위에 얹혀 그려지므로
        /// 그대로 두면 링이 앨범색에 물든다.
        ///
        /// 다만 0으로 꺼서는 안 된다 — 확장 카드에서 안쪽 빛은 '분위기' 그 자체라,
        /// 0으로 끄면 볼륨을 만질 때마다 카드의 빛이 통째로 사라져 고장처럼 보인다.
        /// 게다가 이 빛은 표면이 작은 접힌 노치에선 눈에 띄지 않으므로, 껴 놓으면
        /// 확장 카드에서만 꺼지는 셈이 된다. 그래서 남겨 두고, 즉시가 아니라 부드럽게.
        ///
        /// 기준이 '링이 떠 있는가'가 아니라 '볼륨을 만지는 중인가'인 이유: 링은 이제 재생 내내
        /// 떠 있어서, 링을 기준으로 두면 카드의 분위기가 재생 내내 죽는다. 물들어도 되는 순간은
        /// 링이 손끝의 관심사일 때뿐이라 그 짧은 동안만 낮춘다.
        /// </summary>
        private void UpdateBeamInteriorLight()
        {
            bool volumeHud = _volumeHudTimer?.IsEnabled == true;
            double target = volumeHud ? Tokens.InteriorLightWhileVolume : 1.0;
            var ease = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = Tokens.EaseOutExponent };
            var dur = new Duration(TimeSpan.FromMilliseconds(Tokens.InteriorFadeMs));

            // 지금 보이는 값에서 출발한다 — HUD를 연달아 만져도 매번 튀지 않고 이어진다
            Animate(NotchBeam, Controls.BorderBeam.InteriorLightProperty,
                new DoubleAnimation(NotchBeam.InteriorLight, target, dur) { EasingFunction = ease });
        }

        private void SetViewActive(UIElement activeView, Duration duration, IEasingFunction ease)
        {
            UIElement[] views = { IdleCompactView, IdleExpandedView, MediaCompactView, MediaExpandedView, NotificationCompactView, NotificationExpandedView, InboxView, AssistantView, AssistantCompactView };
            Duration fadeOutDuration = new Duration(TimeSpan.FromMilliseconds(Tokens.ViewFadeOutMs));

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
                ShowVolumeRing(volume, isMuted);
            });
        }

        private void ShowVolumeRing(int volumeVal, bool isMuted)
        {
            // AI 비서 대화/작업 중에는 볼륨 UI가 얼굴을 가리지 않게 차단
            if (_currentViewMode is ViewMode.Assistant or ViewMode.AssistantCompact)
            {
                StartHudTimer();
                return;
            }

            // 어느 화면이든 볼륨은 우측 상태 점 자리의 링 하나로 말한다 —
            // 접힌 노치도 노치 전체가 계기로 바뀌지 않고 모양을 그대로 유지한다(볼륨바 대신 링).
            VolumeRing.Show(volumeVal, isMuted);

            // 순서가 곧 뜻이다: 먼저 '볼륨을 만지는 중'이 되어야 링이 뜬다 — 링의 수명은
            // 이 신호로 정해지므로, SyncAudioRing을 먼저 부르면 켜야 할 이유를 아직 모른다.
            StartHudTimer();
            SyncAudioRing(animate: true);
            // 어느 경로(휠·키보드)로 만져도 테두리 빛이 링을 물들이지 않게 한다.
            UpdateBeamInteriorLight();
        }

        /// <summary>
        /// 이 자리의 주인 — 소리가 흐르는 동안 링이 상주하고, 볼륨을 만지는 동안은 늘 뜬다.
        /// 비서 화면에서는 얼굴을 가리지 않게 언제나 비켜선다.
        /// </summary>
        private bool WantAudioRing =>
            AudioSlot.ShowRing(_mediaPlaying, _volumeHudTimer?.IsEnabled == true)
            && _currentViewMode is not (ViewMode.Assistant or ViewMode.AssistantCompact);

        /// <summary>상태 점이 이 자리를 쓰고 있는가 — 배터리·AI·복사 피드백이 모두 같은 자리다.</summary>
        private bool StatusDotVisible =>
            BatteryDot.Visibility == Visibility.Visible
            || AssistantStatusChip.Visibility == Visibility.Visible
            || ClipboardFeedbackChip.Visibility == Visibility.Visible;

        /// <summary>
        /// 소리 바늘 — 캡처가 자기 스레드에서 다듬어 둔 세 칸을 그대로 옮긴다.
        /// 여기서 정하는 것은 보일지 여부뿐이다: 값이 없으면(캡처를 못 열었다) 바늘만 감추고
        /// 호는 그대로 둔다. 계기가 없다고 볼륨 UI가 사라지면 안 된다.
        /// </summary>
        private void AudioBars_Tick(object? sender, EventArgs e)
        {
            bool hasLevels = _audioCapture.TryReadBars(_levelBars);
            VolumeRing.BarsVisible = AudioSlot.ShowBars(_mediaPlaying, hasLevels, StatusDotVisible);

            if (hasLevels)
            {
                VolumeRing.SetBars(_levelBars[0], _levelBars[1], _levelBars[2]);
            }
        }

        /// <summary>링을 지금의 의도에 맞춘다 — 등장·퇴장은 손짓과 함께, 모양 전환 중엔 즉시.</summary>
        private void SyncAudioRing(bool animate)
        {
            if (!animate)
            {
                SettleAudioRing();   // 모양이 바뀌는 중엔 페이드가 모프와 섮여 보인다
            }
            else if (WantAudioRing)
            {
                ShowAudioRing();
            }
            else
            {
                HideAudioRing();
            }
        }

        /// <summary>
        /// 우측 상태 점 자리에 링을 띄운다 — 이 자리는 이제 링의 것이다.
        /// 진행바 자리를 빌리지 않으므로 재생 위치가 계속 보이고, 카드 높이·내용도 그대로다.
        /// </summary>
        private void ShowAudioRing()
        {
            if (!_volumeRingOverlayVisible)
            {
                _volumeRingOverlayVisible = true;

                // 재생 때문에 처음 뜨는 것이면 지금의 볼륨에서 시작한다 — 0에서 차오르면 값이 튄다
                VolumeRing.Show(_audioService.GetCurrentVolume(out bool muted), muted);
            }

            VolumeRing.Visibility = Visibility.Visible;

            // 링도 '툭' 켜지지 않고 앉는다 — 알림 내용과 같은 손짓(살짝 작은 자리에서 제 크기로).
            // 링은 뜨는 즉시 값이 차오르므로 크기 변화는 아주 작게 둔다: 등장이 아니라 정착이다.
            var ease = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = Tokens.EaseOutExponent };
            var dur = new Duration(TimeSpan.FromMilliseconds(Tokens.RingInMs));
            Animate(VolumeRing, UIElement.OpacityProperty,
                new DoubleAnimation { To = 1, Duration = dur, EasingFunction = ease });
            AnimateRingScale(Tokens.RingInScale, 1, dur, ease);
            UpdateBeamInteriorLight();
        }

        private void HideAudioRing()
        {
            if (!_volumeRingOverlayVisible) return;
            _volumeRingOverlayVisible = false;

            var ease = new ExponentialEase { EasingMode = EasingMode.EaseIn, Exponent = Tokens.EaseInExponent };
            var dur = new Duration(TimeSpan.FromMilliseconds(Tokens.RingOutMs));
            var fade = new DoubleAnimation { To = 0, Duration = dur, EasingFunction = ease };
            fade.Completed += (s, e) =>
            {
                // 사라지는 도중에 재생이 돌아올 수 있다 — 그때는 걷지 않고 그대로 둔다
                if (WantAudioRing) return;
                VolumeRing.Visibility = Visibility.Collapsed;
                UpdateBeamInteriorLight();
            };
            Animate(VolumeRing, UIElement.OpacityProperty, fade);
            // 내용이 먼저 빠지는 알림 퇴장과 같은 방향 — 사라지기 전에 아주 살짝 잦아든다
            AnimateRingScale(VolumeRingScale.ScaleX, Tokens.RingOutScale, dur, ease);
            UpdateBeamInteriorLight();
        }

        /// <summary>링의 크기를 지금 보이는 값에서 목표로 미끄러뜨린다(애니메이션 중이면 그 값에서 이어 간다).</summary>
        private void AnimateRingScale(double from, double to, Duration duration, IEasingFunction ease)
        {
            Animate(VolumeRingScale, ScaleTransform.ScaleXProperty, new DoubleAnimation(from, to, duration) { EasingFunction = ease });
            Animate(VolumeRingScale, ScaleTransform.ScaleYProperty, new DoubleAnimation(from, to, duration) { EasingFunction = ease });
        }

        /// <summary>
        /// 링을 지금의 의도에 즉시 앉힌다(애니메이션 없이). 도크 모양이 바뀌는 순간에 쓴다 —
        /// 페이드가 모프와 겹치면 어긋나 보인다. 재생 중이면 링은 그대로 남는다.
        /// </summary>
        private void SettleAudioRing()
        {
            bool visible = WantAudioRing;
            _volumeRingOverlayVisible = visible;

            VolumeRing.BeginAnimation(UIElement.OpacityProperty, null);
            VolumeRingScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            VolumeRingScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            VolumeRingScale.ScaleX = 1;
            VolumeRingScale.ScaleY = 1;
            VolumeRing.Opacity = visible ? 1 : 0;
            VolumeRing.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

            UpdateBeamInteriorLight();
        }

        private void StartHudTimer()
        {
            if (_volumeHudTimer == null)
            {
                _volumeHudTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.3) };
                _volumeHudTimer.Tick += (s, args) =>
                {
                    _volumeHudTimer.Stop();

                    // 재생 중이면 링은 남고, 아니면 여기서 걷힌다
                    SyncAudioRing(animate: true);

                    // 볼륨을 만지는 동안엔 도크 크기를 건드리지 않는다(링만 뜬다) — 그래서 그 사이에
                    // 마우스가 들어오거나 나간 일은 여기서 한 번만 반영한다. 이 되돌림이 없으면
                    // HUD를 만지다 벗어난 마우스를 아무도 기억하지 못해 확장된 도크가 그대로 남는다.
                    ApplyMode(_dock.VolumeHudExpired(Signals));
                };
            }
            _volumeHudTimer.Stop();
            _volumeHudTimer.Start();
        }
    }
}
