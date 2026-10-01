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
    }
}
