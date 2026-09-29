using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace TopDock.Controls
{
    /// <summary>
    /// AI 비서 얼굴. 상태에 따라 표정이 바뀐다:
    /// Idle(깜빡임+시선 이동) / Alert(눈 큼) / Thinking(가늘고 위) / Talking(통통) / Sad(아래).
    /// </summary>
    public partial class AiFace : UserControl
    {
        public enum FaceState { Idle, Alert, Thinking, Talking, Sad }

        private FaceState _state = FaceState.Idle;
        private readonly DispatcherTimer _blinkTimer;
        private readonly DispatcherTimer _gazeTimer;
        private readonly DispatcherTimer _talkTimer;
        private readonly Random _rand = new();
        private Storyboard? _talkStoryboard;

        public AiFace()
        {
            InitializeComponent();
            IsVisibleChanged += (_, _) => SyncTimers();

            _blinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(3400) };
            _blinkTimer.Tick += (_, _) => _ = BlinkAsync();

            _gazeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2200) };
            _gazeTimer.Tick += (_, _) => MoveGaze();

            _talkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
            _talkTimer.Tick += (_, _) => BounceOnce();
        }

        public void SetState(FaceState state)
        {
            if (_state == state) return;
            _state = state;
            ApplyState();
        }

        private void ApplyState()
        {
            double targetEyeH = _state switch
            {
                FaceState.Alert => 20,
                FaceState.Thinking => 5,
                FaceState.Sad => 8,
                _ => 16
            };

            var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
            foreach (var eye in new[] { LeftEye, RightEye })
            {
                var h = new DoubleAnimation(targetEyeH, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease };
                eye.BeginAnimation(Border.HeightProperty, h);
            }

            double haloTarget = _state == FaceState.Thinking ? 0.85 : 0.5;
            Halo.BeginAnimation(OpacityProperty, new DoubleAnimation(haloTarget, TimeSpan.FromMilliseconds(400)));

            // 토크 바운스는 상태 전환마다 재구성
            if (_talkStoryboard != null)
            {
                _talkStoryboard.Stop();
                _talkStoryboard = null;
            }
            EyesBounce.BeginAnimation(TranslateTransform.YProperty, null);
            EyesBounce.Y = 0;

            if (_state == FaceState.Thinking)
            {
                // 눈을 위로 살짝
                LeftGaze.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-3, TimeSpan.FromMilliseconds(220)));
                RightGaze.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-3, TimeSpan.FromMilliseconds(220)));
            }
            else if (_state == FaceState.Sad)
            {
                LeftGaze.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(3, TimeSpan.FromMilliseconds(220)));
                RightGaze.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(3, TimeSpan.FromMilliseconds(220)));
            }
            else
            {
                LeftGaze.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(180)));
                RightGaze.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(180)));
            }

            SyncTimers();
        }

        private void SyncTimers()
        {
            bool run = IsVisible && _state is FaceState.Idle or FaceState.Talking;
            if (run)
            {
                if (!_blinkTimer.IsEnabled) _blinkTimer.Start();
                if (!_gazeTimer.IsEnabled && _state == FaceState.Idle) _gazeTimer.Start();
            }
            else
            {
                _blinkTimer.Stop();
                _gazeTimer.Stop();
            }

            if (_state == FaceState.Talking)
            {
                if (!_talkTimer.IsEnabled) _talkTimer.Start();
            }
            else
            {
                _talkTimer.Stop();
            }
        }

        private async System.Threading.Tasks.Task BlinkAsync()
        {
            if (_state is not (FaceState.Idle or FaceState.Talking)) return;
            var dur = TimeSpan.FromMilliseconds(90);
            var ease = new SineEase { EasingMode = EasingMode.EaseInOut };

            foreach (var blink in new[] { LeftBlink, RightBlink })
            {
                blink.BeginAnimation(ScaleTransform.ScaleYProperty,
                    new DoubleAnimation(0.08, dur) { EasingFunction = ease, AutoReverse = true });
            }

            // 가끔 두 번 연속 깜빡임
            if (_rand.NextDouble() < 0.22)
            {
                await System.Threading.Tasks.Task.Delay(240);
                foreach (var blink in new[] { LeftBlink, RightBlink })
                {
                    blink.BeginAnimation(ScaleTransform.ScaleYProperty,
                        new DoubleAnimation(0.08, dur) { EasingFunction = ease, AutoReverse = true });
                }
            }
        }

        private void MoveGaze()
        {
            if (_state != FaceState.Idle) return;
            double x = _rand.Next(-4, 5);
            double y = _rand.Next(-2, 4);
            var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
            var dur = TimeSpan.FromMilliseconds(360);
            LeftGaze.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, dur) { EasingFunction = ease });
            RightGaze.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, dur) { EasingFunction = ease });
            LeftGaze.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(y, dur) { EasingFunction = ease });
            RightGaze.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(y, dur) { EasingFunction = ease });
        }

        private void BounceOnce()
        {
            var dur = TimeSpan.FromMilliseconds(130);
            var ease = new SineEase { EasingMode = EasingMode.EaseOut };
            EyesBounce.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(-3.5, dur) { EasingFunction = ease, AutoReverse = true });
        }

        /// <summary>Thinking 상태에서의 미세 흔들림 시작/중지.</summary>
        public void StartThinkingWobble()
        {
            if (_talkStoryboard != null) return;
            var anim = new DoubleAnimation(-1.5, 1.5, TimeSpan.FromMilliseconds(280))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            _talkStoryboard = new Storyboard();
            Storyboard.SetTarget(anim, EyesBounce);
            Storyboard.SetTargetProperty(anim, new PropertyPath(TranslateTransform.XProperty));
            _talkStoryboard.Children.Add(anim);
            _talkStoryboard.Begin();
        }

        public void StopThinkingWobble()
        {
            _talkStoryboard?.Stop();
            _talkStoryboard = null;
            EyesBounce.BeginAnimation(TranslateTransform.XProperty, null);
            EyesBounce.X = 0;
        }
    }
}
