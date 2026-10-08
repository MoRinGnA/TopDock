using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace TopDock.Controls
{
    /// <summary>
    /// AI 비서 얼굴. 민무늬 흰 눈 두 개가 전부다 — 얼굴에는 색을 쓰지 않는다(후광도 없다).
    /// Idle     : 보통 크기, 깜빡임 + 시선 이동 + 은은한 호흡
    /// Thinking : 조금 가늘게 뜨고 시선을 위로, 얼굴이 미세하게 흔들림
    /// Alert    : 눈을 크게 뜬 집중(도구 실행) 표정
    /// Talking  : 응답 조각이 올 때마다 눈이 오므라졌다 풀리는 발화 표정
    /// Sad      : 눈이 작아지고 시선이 아래로
    /// 눈동자·눈썹·입은 쓰지 않는다 — 넣으면 표정이 무섭게 읽힌다(실측 피드백).
    /// 반복 연출은 이 컨트롤이 자기 상태와 보이는 여부만 보고 스스로 켜고 끈다.
    /// </summary>
    public partial class AiFace : UserControl
    {
        public enum FaceState { Idle, Alert, Thinking, Talking, Sad }

        // 발화 리듬: 한 번 오므렸다 풀리는 시간과 연속 발화의 최소 간격
        private const double SpeechPulseMs = 190;
        private const double SpeechMinGapMs = 120;
        private const double SpeechSqueeze = 0.76;

        private FaceState _state = FaceState.Idle;
        private readonly DispatcherTimer _blinkTimer;
        private readonly DispatcherTimer _gazeTimer;
        private readonly DispatcherTimer _speechTimer;
        private readonly Random _rand = new();
        private Storyboard? _wobbleStoryboard;
        private Storyboard? _breathStoryboard;
        private DateTime _lastPulse = DateTime.MinValue;

        public AiFace()
        {
            InitializeComponent();
            IsVisibleChanged += (_, _) => SyncAnimations();
            Loaded += (_, _) => SyncAnimations();
            Unloaded += (_, _) => StopAnimations();

            _blinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(3400) };
            _blinkTimer.Tick += (_, _) => _ = BlinkAsync();

            _gazeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2200) };
            _gazeTimer.Tick += (_, _) => MoveGaze();

            // 응답 조각이 띄엄띄엄 와도 눈이 멈춰 보이지 않도록 최소한의 리듬을 만든다.
            _speechTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(210) };
            _speechTimer.Tick += (_, _) => PulseSpeech();
        }

        public void SetState(FaceState state)
        {
            if (_state == state)
            {
                SyncAnimations();
                return;
            }
            _state = state;
            ApplyState();
        }

        /// <summary>
        /// 응답 텍스트가 한 조각 도착할 때마다 호출한다.
        /// 글자가 흐르는 박자에 맞춰 눈이 오므라졌다 풀리는 것이 이 얼굴의 발화 연동이다.
        /// </summary>
        public void PulseSpeech()
        {
            if (_state != FaceState.Talking) return;

            var now = DateTime.UtcNow;
            if ((now - _lastPulse).TotalMilliseconds < SpeechMinGapMs) return;
            _lastPulse = now;

            var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
            var squeeze = new DoubleAnimationUsingKeyFrames
            {
                Duration = TimeSpan.FromMilliseconds(SpeechPulseMs),
            };
            squeeze.KeyFrames.Add(new EasingDoubleKeyFrame(SpeechSqueeze, KeyTime.FromPercent(0.35), ease));
            squeeze.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(1.0), ease));

            LeftBlink.BeginAnimation(ScaleTransform.ScaleYProperty, squeeze);
            RightBlink.BeginAnimation(ScaleTransform.ScaleYProperty, squeeze.Clone());

            // 발화할 때 눈 전체도 아주 살짝 따라 움직인다
            EyesBounce.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(-1.4, TimeSpan.FromMilliseconds(110))
                {
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut },
                    AutoReverse = true,
                });
        }

        /// <summary>상태별 눈 높이와 시선. 눈 폭은 항상 같다.</summary>
        private static (double Height, double GazeY) EyeShape(FaceState state) => state switch
        {
            // 실행: 눈을 크게 뜬다
            FaceState.Alert => (20, 0),
            // 생각: 눈은 뜬 채로 시선만 위로 (눈을 감으면 무슨 상태인지 읽히지 않는다)
            FaceState.Thinking => (13, -3),
            FaceState.Sad => (8, 3),
            _ => (16, 0),
        };

        private void ApplyState()
        {
            // 이전 상태의 반복 애니메이션을 먼저 걷어낸 뒤 새 표정을 걸어야 방금 건 애니메이션이 취소되지 않는다.
            StopBreathing();
            StopThinkingWobble();

            var (height, gazeY) = EyeShape(_state);
            var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
            var sizeTime = TimeSpan.FromMilliseconds(220);

            foreach (var eye in new[] { LeftEye, RightEye })
            {
                eye.BeginAnimation(HeightProperty, new DoubleAnimation(height, sizeTime) { EasingFunction = ease });
            }

            // 발화 펄스도 눈 바운스를 쓰므로, 바운스 정리는 먼저 끝낸다
            EyesBounce.BeginAnimation(TranslateTransform.YProperty, null);
            EyesBounce.Y = 0;

            LeftGaze.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(gazeY, sizeTime) { EasingFunction = ease });
            RightGaze.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(gazeY, sizeTime) { EasingFunction = ease });

            // 말하기 시작하는 순간부터 눈이 박자를 탄다
            if (_state == FaceState.Talking)
            {
                _lastPulse = DateTime.MinValue;
                PulseSpeech();
            }

            SyncAnimations();
        }

        /// <summary>지금 상태와 보이는 여부에 맞춰 반복 애니메이션을 켜고 끈다.</summary>
        private void SyncAnimations()
        {
            bool visible = IsVisible;
            bool lively = visible && _state is FaceState.Idle or FaceState.Alert or FaceState.Thinking or FaceState.Talking;

            if (lively)
            {
                if (!_blinkTimer.IsEnabled) _blinkTimer.Start();
                if (!_gazeTimer.IsEnabled) _gazeTimer.Start();
            }
            else
            {
                _blinkTimer.Stop();
                _gazeTimer.Stop();
            }

            if (visible && _state == FaceState.Idle)
            {
                StartBreathing();
            }
            else if (_breathStoryboard != null)
            {
                StopBreathing();
            }

            if (visible && _state == FaceState.Thinking)
            {
                StartThinkingWobble();
            }
            else if (_wobbleStoryboard != null)
            {
                StopThinkingWobble();
            }

            if (visible && _state == FaceState.Talking)
            {
                if (!_speechTimer.IsEnabled) _speechTimer.Start();
            }
            else
            {
                _speechTimer.Stop();
            }
        }

        private async System.Threading.Tasks.Task BlinkAsync()
        {
            if (_state is not (FaceState.Idle or FaceState.Alert or FaceState.Thinking or FaceState.Talking)) return;
            var dur = TimeSpan.FromMilliseconds(90);
            var ease = new SineEase { EasingMode = EasingMode.EaseInOut };

            BlinkOnce(dur, ease);

            // 가끔 두 번 연속 깜빡임
            if (_rand.NextDouble() < 0.22)
            {
                await System.Threading.Tasks.Task.Delay(240);
                BlinkOnce(dur, ease);
            }
        }

        private void BlinkOnce(TimeSpan dur, IEasingFunction ease)
        {
            foreach (var blink in new[] { LeftBlink, RightBlink })
            {
                blink.BeginAnimation(ScaleTransform.ScaleYProperty,
                    new DoubleAnimation(0.08, dur) { EasingFunction = ease, AutoReverse = true });
            }
        }

        private void MoveGaze()
        {
            if (_state == FaceState.Sad) return;

            // 사고 중에도 시선은 고정하지 않는다. 표현은 유지하되 idle보다 이동 폭을 줄인다.
            (int minX, int maxX, int minY, int maxY, double baseY) = _state switch
            {
                FaceState.Thinking => (-2, 3, -1, 2, -3),
                FaceState.Alert => (-2, 3, -1, 2, 0),
                FaceState.Talking => (-2, 3, -1, 2, 0),
                _ => (-4, 5, -2, 4, 0),
            };
            double x = _rand.Next(minX, maxX);
            double y = baseY + _rand.Next(minY, maxY);
            var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
            var dur = TimeSpan.FromMilliseconds(360);
            LeftGaze.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, dur) { EasingFunction = ease });
            RightGaze.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, dur) { EasingFunction = ease });
            LeftGaze.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(y, dur) { EasingFunction = ease });
            RightGaze.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(y, dur) { EasingFunction = ease });
        }

        private void StartBreathing()
        {
            if (_state != FaceState.Idle || !IsVisible || _breathStoryboard != null) return;

            // 작은 크기에서도 살아 있는 느낌만 나도록 눈 너비를 천천히 호흡시킨다.
            var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
            var leftEye = new DoubleAnimation(1.0, 1.035, TimeSpan.FromSeconds(1.35))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = ease,
            };
            var rightEye = leftEye.Clone();

            _breathStoryboard = new Storyboard();
            Storyboard.SetTarget(leftEye, LeftBlink);
            Storyboard.SetTargetProperty(leftEye, new PropertyPath(ScaleTransform.ScaleXProperty));
            Storyboard.SetTarget(rightEye, RightBlink);
            Storyboard.SetTargetProperty(rightEye, new PropertyPath(ScaleTransform.ScaleXProperty));
            _breathStoryboard.Children.Add(leftEye);
            _breathStoryboard.Children.Add(rightEye);
            _breathStoryboard.Begin();
        }

        /// <summary>호흡만 걷어낸다 — 방금 건 표정 애니메이션은 취소하지 않는다.</summary>
        private void StopBreathing()
        {
            _breathStoryboard?.Stop();
            _breathStoryboard = null;
        }

        private void StopAnimations()
        {
            _blinkTimer.Stop();
            _gazeTimer.Stop();
            _speechTimer.Stop();
            StopBreathing();
            StopThinkingWobble();
        }

        /// <summary>Thinking 상태에서의 미세 흔들림 — 얼굴 전체를 아주 조금 옆으로 흔든다.</summary>
        private void StartThinkingWobble()
        {
            if (_wobbleStoryboard != null) return;

            var anim = new DoubleAnimation(-1.2, 1.2, TimeSpan.FromMilliseconds(300))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            _wobbleStoryboard = new Storyboard();
            Storyboard.SetTarget(anim, EyesBounce);
            Storyboard.SetTargetProperty(anim, new PropertyPath(TranslateTransform.XProperty));
            _wobbleStoryboard.Children.Add(anim);
            _wobbleStoryboard.Begin();
        }

        private void StopThinkingWobble()
        {
            _wobbleStoryboard?.Stop();
            _wobbleStoryboard = null;
            EyesBounce.BeginAnimation(TranslateTransform.XProperty, null);
            EyesBounce.X = 0;
        }
    }
}
