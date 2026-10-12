using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using TopDock.Models;
using TopDock.Services;

namespace TopDock
{
    public partial class MainWindow : Window
    {
        /// <summary>미디어 컴팩트 뷰 상태 아이콘 묶음에 예약하는 배터리 점 슬롯(점 12 + 간격 8).</summary>
        private const double BatteryDotSlotWidth = 20;

        private bool _batteryStatusInitialized;
        private DispatcherTimer? _chargingBeamTimer;
        private bool _chargingBeamActive;

        /// <summary>지금 숨쉬고 있는 배터리 점. 뷰가 바뀌면 이 점만 멈추면 된다.</summary>
        private FrameworkElement? _pulsingDot;

        /// <summary>배터리는 퍼센트가 아니라 3분류로 다룬다(일반/충전/부족).
        /// 일반 상태는 숨기고, 접힌 화면에는 작은 상태 점을, 펼친 화면에는 퍼센트를 보여준다.</summary>
        private void BatteryService_BatteryStatusChanged(object? sender, BatteryStatusArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                _batteryPercent = e.BatteryPercent;

                bool wasInitialized = _batteryStatusInitialized;
                bool wasCharging = _batteryLevel == BatteryLevel.Charging;
                var level = e.IsCharging
                    ? BatteryLevel.Charging
                    : e.BatteryPercent <= 0.20f ? BatteryLevel.Low : BatteryLevel.Normal;

                // 앱 시작 시 이미 연결된 충전기를 새 연결로 오인하지 않고, 이후 꽂는 순간만 알린다.
                _batteryStatusInitialized = true;
                bool flash = wasInitialized && level != _batteryLevel && level != BatteryLevel.Normal;
                _batteryLevel = level;

                if (wasInitialized && !wasCharging && e.IsCharging)
                {
                    ShowChargingFeedback();
                }

                ApplyBatteryIndicator(flash);
            });
        }

        /// <summary>충전 연결 순간에만 알림과 초록 빔을 잠시 보여주고 평소 상태로 되돌린다.</summary>
        private void ShowChargingFeedback()
        {
            ShowNotchNotice(IconBolt, NoticeCharging, string.Empty, "충전 중", null,
                TimeSpan.FromSeconds(2.4), NoticeSource.Charging, flashEdge: false);

            // 현재 빔의 모양·속도·밝기는 그대로 두고 액센트 색만 잠시 초록색으로 바꾼다.
            _chargingBeamActive = true;
            UpdateBeamState();

            if (_chargingBeamTimer == null)
            {
                _chargingBeamTimer = new DispatcherTimer();
                _chargingBeamTimer.Tick += ChargingBeamTimer_Tick;
            }
            _chargingBeamTimer.Stop();
            _chargingBeamTimer.Interval = TimeSpan.FromSeconds(2.4);
            _chargingBeamTimer.Start();
        }

        private void ChargingBeamTimer_Tick(object? sender, EventArgs e)
        {
            _chargingBeamTimer?.Stop();
            _chargingBeamActive = false;
            UpdateBeamState();
        }

        /// <summary>현재 뷰와 잔량에 맞춰 작은 에너지 코어를 갱신한다.</summary>
        private void ApplyBatteryIndicator(bool flash = false)
        {
            // 일반 상태에서는 아무것도 그리지 않는다 — 조용한 게 기본값.
            if (_batteryLevel == BatteryLevel.Normal)
            {
                HideBatteryDot(BatteryDot);
                StopBatteryPulse();
                return;
            }

            Color accent = _batteryLevel == BatteryLevel.Charging
                ? DesignTokens.AccentChargingColor   // 충전 중 — 애플 그린
                : DesignTokens.AccentLowColor;       // 배터리 부족 — 애플 레드

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
            // 접힌 아이들·미디어 노치와 기본 확장에서 같은 점 하나가 보인다 — 뷰마다 다른 자리에
            // 다른 점을 두면 화면을 옮겨 다닐 때마다 눈이 상태 표시를 다시 찾아야 한다.
            bool showBattery = _currentViewMode is ViewMode.IdleCompact or ViewMode.IdleExpanded or ViewMode.MediaCompact or ViewMode.MediaExpanded;
            BatteryDot.Visibility = showBattery ? Visibility.Visible : Visibility.Collapsed;

            if (!showBattery)
            {
                StopBatteryPulse();
                return;
            }

            if (flash)
            {
                // 꽂는 순간: 밝게 나타났다가 잠깐 어두워지고, 끝나면 pulse로 넘어간다.
                BatteryDot.BeginAnimation(UIElement.OpacityProperty, null);
                BatteryDot.Opacity = 1;
                var blink = new DoubleAnimation(1.0, 0.35, new Duration(TimeSpan.FromMilliseconds(260)))
                {
                    AutoReverse = true,
                };
                blink.Completed += (_, _) =>
                {
                    // 깜박이는 사이에 뷰가 바뀌었으면 그 점은 이제 남의 것이다
                    if (BatteryDot.Visibility == Visibility.Visible) StartBatteryPulse(BatteryDot);
                };
                BatteryDot.BeginAnimation(UIElement.OpacityProperty, blink);
            }
            else
            {
                StartBatteryPulse(BatteryDot);
            }
        }

        private static void HideBatteryDot(FrameworkElement dot)
        {
            dot.Visibility = Visibility.Collapsed;
            dot.BeginAnimation(UIElement.OpacityProperty, null);
            dot.Opacity = 1;
        }

        /// <summary>숨쉬던 점을 멈추고 원래 불투명도로 돌려놓는다.</summary>
        private void StopBatteryPulse()
        {
            if (_pulsingDot == null) return;
            _pulsingDot.BeginAnimation(UIElement.OpacityProperty, null);
            _pulsingDot.Opacity = 1;
            _pulsingDot = null;
        }

        /// <summary>충전 중에만 은은하게 숨쉬게 한다. 부족은 고정 — 경고가 흔들리면 거슬린다.</summary>
        private void StartBatteryPulse(FrameworkElement dot)
        {
            if (_batteryLevel != BatteryLevel.Charging)
            {
                StopBatteryPulse();
                return;
            }
            if (ReferenceEquals(_pulsingDot, dot)) return; // 이미 이 점이 숨쉬는 중

            StopBatteryPulse();
            _pulsingDot = dot;
            dot.Opacity = 1;
            dot.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation
            {
                From = 0.45,
                To = 1.0,
                Duration = new Duration(TimeSpan.FromSeconds(1.8)),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            });
        }

        // ── 노치 알림 ──
        // Windows 알림 · 도구 실행 · AI 상태가 모두 같은 통로(알림 뷰)로 노치에 뜬다.
        // 복사 피드백(작은 배지)은 조용한 마이크로 피드백이라 이 통로를 쓰지 않는다.

        // 색은 한 곳(DesignTokens)에서 온다 — 같은 파랑을 화면마다 새로 적지 않는다.
        private static readonly Color NoticeInfo = DesignTokens.AccentInfoColor;         // Windows 알림
        private static readonly Color NoticeTool = DesignTokens.AccentToolColor;         // 도구 실행
        private static readonly Color NoticeThink = DesignTokens.AccentThinkColor;       // AI 사고·응답 중
        private static readonly Color NoticeCharging = DesignTokens.AccentChargingColor; // 충전 연결

        /// <summary>알림 파랑 — 인박스 카드도 같은 색을 쓴다(둘은 같은 알림의 두 순간이다).</summary>
        private static readonly Brush NoticeInfoBrush = DesignTokens.AccentInfo;

        /// <summary>지금 떠 있는 알림의 출처 — AI·도구 알림이 서로를 잘못 걷지 않게 구분한다.</summary>
        private enum NoticeSource { None, Windows, Tool, Assistant, Charging }
        private NoticeSource _activeNotice = NoticeSource.None;

        /// <summary>알림이 하나라도 떠 있는가 — 타이머 없이 계속 떠 있는(sticky) 알림도 포함한다.</summary>
        private bool NoticeActive => _activeNotice != NoticeSource.None;

        // 출처별 아이콘 — 라벨 글자 앞에 붙어 "무엇에 대한 알림인지"를 한눈에 보여준다.
        private static readonly Geometry IconBell = Freeze(Geometry.Parse("M12 22c1.1 0 2-.9 2-2h-4c0 1.1.9 2 2 2zm6-6v-5c0-3.07-1.63-5.64-4.5-6.32V4c0-.83-.67-1.5-1.5-1.5S10.5 3.17 10.5 4v.68C7.64 5.36 6 7.92 6 11v5l-2 2v1h16v-1l-2-2z"));
        private static readonly Geometry IconGear = Freeze(Geometry.Parse("M22.7 19l-9.1-9.1c.9-2.3.4-5-1.5-6.9-2-2-5-2.4-7.4-1.3L9 6 6 9 1.6 4.7C.4 7.1.9 10.1 2.9 12.1c1.9 1.9 4.6 2.4 6.9 1.5l9.1 9.1c.4.4 1 .4 1.4 0l2.3-2.3c.5-.4.5-1.1.1-1.4z"));
        private static readonly Geometry IconSparkle = Freeze(Geometry.Parse("M12 2l1.9 6.1L20 10l-6.1 1.9L12 18l-1.9-6.1L4 10l6.1-1.9L12 2z"));
        private static readonly Geometry IconBolt = Freeze(Geometry.Parse("M7 2v11h3v9l7-12h-4l3-8z"));

        private static Geometry Freeze(Geometry g) { g.Freeze(); return g; }

        /// <summary>
        /// 노치 알림을 띄운다 — 아이콘·라벨·제목, 펼치면 본문까지.
        /// 알림이 뜨는 동안 노치는 잠깐 넓은 알림 바로 바뀌었다가 끝나면 원래 화면으로 돌아간다.
        /// </summary>
        private void ShowNotchNotice(Geometry icon, Color accent, string label, string title,
            string? body, TimeSpan? duration, NoticeSource source, bool flashEdge = true)
        {
            _activeNotice = source;
            Log.Info($"Notch notice: {source} · {label} · {title}"
                + (duration is { } shown ? $" ({shown.TotalSeconds:0.#}s)" : " (sticky)"));

            var brush = new SolidColorBrush(accent);
            brush.Freeze();

            // 앱에서 온 알림은 앱 타일(이름 첫 글자)로 정체성을 만들고, AI·도구·충전은 앱이 아니라
            // "지금 벌어지는 일"이라 글리프가 맞다 — 같은 자리를 나눠 쓴다.
            bool appNotice = source == NoticeSource.Windows;
            string monogram = appNotice ? NoticeIdentity.Monogram(label) : string.Empty;
            Visibility tile = appNotice ? Visibility.Visible : Visibility.Collapsed;
            Visibility glyph = appNotice ? Visibility.Collapsed : Visibility.Visible;

            NotifCompactChip.Visibility = tile;
            NotifCompactChip.Background = brush;
            NotifCompactChipText.Text = monogram;
            NotifCompactIcon.Visibility = glyph;
            NotifCompactIcon.Data = icon;
            NotifCompactIcon.Fill = brush;
            NotifCompactAppText.Text = label;
            NotifCompactAppText.Foreground = brush;
            NotifCompactTitleText.Text = title;

            NotifExpandedChip.Visibility = tile;
            NotifExpandedChip.Background = brush;
            NotifExpandedChipText.Text = monogram;
            NotifExpandedIcon.Visibility = glyph;
            NotifExpandedIcon.Data = icon;
            NotifExpandedIcon.Fill = brush;
            NotifExpandedAppText.Text = label;
            NotifExpandedAppText.Foreground = brush;
            NotifExpandedTitleText.Text = title;
            NotifExpandedBodyText.Text = body ?? string.Empty;

            // 알림이 끝나면 돌아갈 화면 — 이미 알림 화면이면 직전 값을 지킨다
            if (_currentViewMode is not (ViewMode.NotificationCompact or ViewMode.NotificationExpanded))
                _viewModeBeforeNotification = _currentViewMode;

            // 알림이 떠 있는 동안의 모양은 알림 모양이다 (펼쳐져 있으면 본문까지 보여준다)
            ApplyMode(_dock.Resolve(Signals));
            PlayNoticeEntrance();

            // 살아있는 가장자리 — 무슨 일이 일어났다는 빛의 신호
            if (flashEdge) FlashNotchEdge();

            _notificationTimer ??= NewNotificationTimer();
            _notificationTimer.Stop();
            if (duration is { } span)
            {
                _notificationTimer.Interval = span;
                _notificationTimer.Start();
            }
            // duration이 null이면 다음 알림/해제까지 유지된다 — 사고·도구 실행처럼
            // 끝나는 시점을 모르는 상태는 눈에서 사라졌다 나타나지 않고 계속 떠 있어야 한다
        }

        // ── 알림 내용의 등장과 퇴장 ──
        // 도크(상자)는 모프로 자리를 만들고, 내용은 아주 짧게 미끄러져 그 자리에 앉는다.
        // 둘이 같은 프레임에 나타나면 상자가 툭 튀어나온 것처럼 보인다 — 조금 기다렸다(90ms) 출발한다.

        /// <summary>알림 내용이 자리를 잡고 들어온다 — 빈 카드에 글자가 툭 박히지 않게.</summary>
        private void PlayNoticeEntrance()
        {
            switch (_currentViewMode)
            {
                case ViewMode.NotificationCompact:
                    PlayContentSettle(NotificationCompactView, NotifCompactScale, NotifCompactSlide);
                    break;
                case ViewMode.NotificationExpanded:
                    PlayContentSettle(NotificationExpandedView, NotifExpandedScale, NotifExpandedSlide);
                    break;
            }
        }

        /// <summary>걷히기 전에 내용부터 살짝 물러난다 — 빈 상자만 줄어드는 것보다 낫다.</summary>
        private void PlayNoticeExit()
        {
            switch (_currentViewMode)
            {
                case ViewMode.NotificationCompact:
                    PlayContentExit(NotificationCompactView, NotifCompactScale, NotifCompactSlide);
                    break;
                case ViewMode.NotificationExpanded:
                    PlayContentExit(NotificationExpandedView, NotifExpandedScale, NotifExpandedSlide);
                    break;
            }
        }

        /// <summary>
        /// 내용 한 덩어리를 상자 안에서 짧게 움직여 앉힌다 — 알림과 인박스가 같이 쓰는 손짓이라
        /// 한 곳에 둔다. 화면마다 다른 리듬으로 움직이면 앱이 아니라 기능 모음처럼 보인다.
        /// </summary>
        /// <summary>
        /// 정착 손짓 — 상자가 자리를 만든 뒤 내용이 미끄러져 앉는다. 알림 · 인박스 · 클립보드 · 비서가
        /// 모두 이 하나를 쓴다: 화면마다 리듬이 다르면 앱이 아니라 기능 모음처럼 보인다.
        /// </summary>
        private static void PlayContentSettle(UIElement view, ScaleTransform? scale, TranslateTransform? slide)
            => PlayContentEntrance(view, scale, slide,
                Tokens.EntranceScale, Tokens.EntranceSlideY, Tokens.EntranceMs, Tokens.EntranceDelayMs);

        /// <summary>방향대로 넘기는 손짓 — 기다리지 않고 곧바로, 크기 변화 없이 미끄러지기만 한다.</summary>
        private static void PlayContentSlide(UIElement view, TranslateTransform slide, int direction)
            => PlayContentEntrance(view, null, slide, 1, direction >= 0 ? 9 : -9, Tokens.QuickSlideMs, 0);

        private static void PlayContentEntrance(UIElement view, ScaleTransform? scale, TranslateTransform? slide,
            double fromScale, double fromY, int durationMs, int delayMs)
        {
            var ease = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = Tokens.EaseOutExponent };
            var duration = new Duration(TimeSpan.FromMilliseconds(durationMs));
            TimeSpan? begin = delayMs > 0 ? TimeSpan.FromMilliseconds(delayMs) : null;

            // 지금 보이는 불투명도에서 출발한다 — 떠 있던 알림의 내용을 새 알림이 갈아끼울 때
            // 0에서 다시 시작하면 글자가 잠깐 사라져 깜빡인다.
            double opacity = view.Opacity;
            view.BeginAnimation(UIElement.OpacityProperty, null);
            view.Opacity = opacity;
            Animate(view, UIElement.OpacityProperty, new DoubleAnimation(opacity, 1, duration)
            {
                BeginTime = begin,
                EasingFunction = ease,
            });

            // 기다리는 동안에는 '출발 자리'에 서 있어야 한다 — 기준값을 그 자리로 옮겨 두고 애니메이션을 건다
            // (기준값을 그대로 두면 기다리는 90ms 동안 제자리에 있다가 나타나면서 튄다).
            if (scale != null)
            {
                scale.ScaleX = fromScale;
                scale.ScaleY = fromScale;
                Animate(scale, ScaleTransform.ScaleXProperty, new DoubleAnimation(fromScale, 1, duration) { BeginTime = begin, EasingFunction = ease });
                Animate(scale, ScaleTransform.ScaleYProperty, new DoubleAnimation(fromScale, 1, duration) { BeginTime = begin, EasingFunction = ease });
            }

            if (slide != null)
            {
                slide.Y = fromY;
                Animate(slide, TranslateTransform.YProperty, new DoubleAnimation(fromY, 0, duration) { BeginTime = begin, EasingFunction = ease });
            }
        }

        /// <summary>내용이 물러난다 — 상자가 곧 줄어들 것이므로 아주 조금만 움직인다.</summary>
        private static void PlayContentExit(UIElement view, ScaleTransform scale, TranslateTransform slide)
        {
            var ease = new ExponentialEase { EasingMode = EasingMode.EaseIn, Exponent = Tokens.EaseInExponent };
            var duration = new Duration(TimeSpan.FromMilliseconds(Tokens.ExitMs));

            // 지금 값을 읽어 출발점으로 삼는다 — 기준값(출발 자리)에 끌려가면 나타날 때 자리로 튄다
            Animate(view, UIElement.OpacityProperty, new DoubleAnimation(0, duration) { EasingFunction = ease });
            Animate(scale, ScaleTransform.ScaleXProperty, new DoubleAnimation(scale.ScaleX, Tokens.ExitScale, duration) { EasingFunction = ease });
            Animate(scale, ScaleTransform.ScaleYProperty, new DoubleAnimation(scale.ScaleY, Tokens.ExitScale, duration) { EasingFunction = ease });
            Animate(slide, TranslateTransform.YProperty, new DoubleAnimation(slide.Y, Tokens.ExitSlideY, duration) { EasingFunction = ease });
        }

        /// <summary>애니메이션은 60fps로 — 기본값은 화면 주사율을 따라가지 못해 모프 중에 끊겨 보인다.</summary>
        private static void Animate(IAnimatable target, DependencyProperty property, DoubleAnimation animation)
        {
            Timeline.SetDesiredFrameRate(animation, Tokens.FrameRate);
            target.BeginAnimation(property, animation);
        }

        /// <summary>도구가 실제로 하는 일을 한 줄로 — 인자까지는 알 수 없으니 동작만 말한다.</summary>
        private static string ToolNoticeText(string toolName) => toolName switch
        {
            "open_app" => "앱 실행 중…",
            "open_url" => "링크 여는 중…",
            "play_youtube" => "유튜브 검색 중…",
            "set_volume" => "볼륨 조절 중…",
            "media_play_pause" or "media_next" or "media_previous" or "media_seek" => "음악 제어 중…",
            _ => "작업 실행 중…",
        };

        /// <summary>AI 사고 알림 — 비서 화면이나 작업 중 아일랜드가 이미 상태를 보여주므로 노치를 빼앗지 않는다.</summary>
        private void ShowThinkingNotice()
        {
            if (_currentViewMode is ViewMode.Assistant or ViewMode.AssistantCompact) return;
            ShowNotchNotice(IconSparkle, NoticeThink, "AI", "생각 중…", null,
                null, NoticeSource.Assistant);
        }

        private void ShowToolNotice(string toolName)
        {
            // 비서 화면이나 아일랜드가 이미 진행 중임을 보여준다 — 노치를 빼앗지 않는다
            if (_currentViewMode is ViewMode.Assistant or ViewMode.AssistantCompact) return;
            ShowNotchNotice(IconGear, NoticeTool, "실행", ToolNoticeText(toolName), null,
                null, NoticeSource.Tool);
        }

        // AI 완료는 별도 화면을 띄우지 않는다 — 배터리 점과 같은 자리의 상태 점이 초록으로
        // 잠깐 고정되는 것으로 알린다(MainWindow.Assistant.ShowAssistantStateDone). 글씨는 쓰지 않는다.

        /// <summary>AI 상태 알림이 아직 떠 있을 때만 걷는다 — 도구 알림 등 남의 것을 건드리지 않는다.</summary>
        private void ClearAssistantNotice()
        {
            if (_activeNotice == NoticeSource.Assistant) ClearNotchNotice();
        }

        /// <summary>노치 알림을 걷고 원래 화면으로 돌아간다. 알림이 없으면 아무 일도 하지 않는다.</summary>
        private void ClearNotchNotice()
        {
            bool wasActive = _activeNotice != NoticeSource.None || (_notificationTimer?.IsEnabled ?? false);
            _activeNotice = NoticeSource.None;
            _notificationTimer?.Stop();

            if (!wasActive) return;
            // 사용자가 그새 다른 화면으로 갔으면 알림 화면이 아니다 — 건드리지 않는다
            if (_currentViewMode is not (ViewMode.NotificationCompact or ViewMode.NotificationExpanded)) return;

            PlayNoticeExit();

            ViewMode back = _viewModeBeforeNotification;
            _viewModeBeforeNotification = ViewMode.IdleCompact;

            if (back == ViewMode.Assistant)
            {
                // 알림이 비서 대화를 잠시 가렸던 경우라면 비서 화면으로 되돌아간다
                SwitchViewMode(ViewMode.Assistant);
                return;
            }
            if (back == ViewMode.AssistantCompact)
            {
                // 작업 중 아일랜드를 가렸던 경우 — 아직 돌고 있으면 아일랜드로, 끝났으면 평소 화면으로
                if (_assistantBusy)
                {
                    SwitchViewMode(ViewMode.AssistantCompact);
                    return;
                }
                SyncViewMode();
                return;
            }
            // 알림이 걷혔으니 평소 모양으로 (볼륨 HUD가 떠 있으면 그쪽이 끝날 때 반영한다)
            SyncViewMode();
        }

        private DispatcherTimer NewNotificationTimer()
        {
            var timer = new DispatcherTimer();
            timer.Tick += (s, args) => ClearNotchNotice();
            return timer;
        }

        private void NotificationService_NotificationReceived(object? sender, NotificationEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                string title = string.IsNullOrEmpty(e.Title) ? e.Body : e.Title;

                // 기록은 노치 표시 여부와 상관없이 남긴다 — 노치 알림을 꺼 둔 사용자에게는
                // 인박스가 알림을 보는 유일한 통로이고, 켜 둔 사용자에게도 5초 뒤 걷힌 것을 되찾는 길이다.
                RecordNotification(e.AppName, title, e.Body);

                if (!ConfigService.Current.ShowNotifications) return;

                ShowNotchNotice(
                    icon: IconBell,
                    accent: NoticeInfo,
                    label: e.AppName,
                    title: title,
                    body: e.Body,
                    duration: TimeSpan.FromSeconds(5),
                    source: NoticeSource.Windows);
            });
        }
    }
}
