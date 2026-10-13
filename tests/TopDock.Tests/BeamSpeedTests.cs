using TopDock.Logic;

namespace TopDock.Tests
{
    /// <summary>
    /// 빛이 도는 속도는 '느낌'이 아니라 값이다 — 표면 크기에 따라 달라지면 그 순간
    /// 알약과 확장 카드가 같은 앱의 빛으로 보이지 않는다. 여기서 그 불변식을 잠근다.
    ///
    /// (실제로 어긋났던 것: 큰 카드에서 환산이 0.30에서 막혀 대기 빛이 알약의 1.3~2.2배로
    ///  빨랐다. 큰 표면일수록 천천히 흐른다는 주석과 정반대였다.)
    /// </summary>
    public class BeamSpeedTests
    {
        /// <summary>노치가 실제로 가질 수 있는 크기 — 둘레 = 직선 4구간 + 모서리 호 4개.</summary>
        private static double Perimeter(double w, double h, double r)
            => 2 * (w - 2 * r) + 2 * (h - 2 * r) + 2 * Math.PI * r;

        private static readonly (string Name, double W, double H, double R)[] Surfaces =
        {
            ("알약 100×38", 100, 38, 19),
            ("카드 260×120", 260, 120, 26),
            ("카드 430×144", 430, 144, 30),
            ("가사 카드 748×144", 748, 144, 30),
        };

        /// <summary>상태별 Speed — MainWindow.Beam.cs의 값과 같다(대기·일시정지·재생·스트리밍·작업).</summary>
        private static readonly (string Name, double Speed)[] States =
        {
            ("대기", 0.09),
            ("일시정지", 0.07),
            ("재생", 0.13),
            ("스트리밍", 0.16),
            ("작업", 0.30),
        };

        /// <summary>
        /// 핵심 불변식 — 어느 크기에서든 픽셀 속도는 240 × Speed. 크기가 달라지는 것은
        /// 한 바퀴에 걸리는 시간뿐이고, 눈에 보이는 흐름의 빠르기는 같다.
        /// </summary>
        [Fact]
        public void EverysurfaceFlowsAtTheSamePixelSpeed()
        {
            foreach (var (stateName, speed) in States)
            {
                double expected = BeamSpeed.RefPerimeter * speed;
                foreach (var (surfaceName, w, h, r) in Surfaces)
                {
                    double px = BeamSpeed.PixelSpeed(speed, Perimeter(w, h, r));
                    Assert.True(Math.Abs(px - expected) < 0.5,
                        $"{stateName} · {surfaceName}: {px:F1}px/s (기대 {expected:F1}px/s)");
                }
            }
        }

        /// <summary>한 바퀴 시간은 둘레에 비례해 길어진다 — 큰 표면이 '느리게' 보이는 이유는 이것뿐이다.</summary>
        [Fact]
        public void BiggerSurfacesTakeLongerPerLap()
        {
            double lap(double w, double h, double r)
            {
                double per = Perimeter(w, h, r);
                return per / BeamSpeed.PixelSpeed(0.09, per);
            }

            Assert.True(lap(748, 144, 30) > lap(430, 144, 30));
            Assert.True(lap(430, 144, 30) > lap(100, 38, 19));
        }

        /// <summary>환산은 둘레가 짧을수록 커지되, 아주 작은 표면에서 폭주하지 않게 막힌다.</summary>
        [Fact]
        public void TheScaleIsCappedOnTinySurfaces()
        {
            Assert.Equal(4.0, BeamSpeed.ScaleFor(1), 3);
            Assert.Equal(BeamSpeed.MaxScale, BeamSpeed.ScaleFor(40), 3);
            Assert.Equal(1.0, BeamSpeed.ScaleFor(BeamSpeed.RefPerimeter), 3);
            Assert.Equal(240.0 / 1096.0, BeamSpeed.ScaleFor(1096), 4);
        }

        /// <summary>음수 Speed(반시계)도 빠르기는 같다 — 방향은 부호가, 빠르기는 절댓값이 정한다.</summary>
        [Fact]
        public void DirectionDoesNotChangeSpeed()
            => Assert.Equal(BeamSpeed.PixelSpeed(0.3, 1096), BeamSpeed.PixelSpeed(-0.3, 1096), 6);

        /// <summary>
        /// 프레임 수는 '상태 이름'이 아니라 프레임당 이동량이 정한다 — 상태 표를 그대로 박아 두면
        /// 나중에 Speed를 다듬을 때 조용히 어크나게 된다.
        ///
        /// 실측 근거: 정지한 빛을 30fps로 그리는 값은 코어 1개의 13.5%(135ms/s),
        /// 15fps면 3.3%(33ms/s)다. 대기·일시정지는 프레임당 1px도 못 가므로 15fps로 내려간다.
        /// </summary>
        [Fact]
        public void FramesFollowPixelsPerFrameNotTheState()
        {
            double per = Perimeter(100, 38, 19);
            Assert.Equal(15, BeamSpeed.FramesPerSecond(0.09, per, largeOrFlashing: false));   // 대기 21.6px/s
            Assert.Equal(15, BeamSpeed.FramesPerSecond(0.07, per, largeOrFlashing: false));   // 일시정지 16.8px/s
            Assert.Equal(30, BeamSpeed.FramesPerSecond(0.13, per, largeOrFlashing: false));   // 재생 31.2px/s
            Assert.Equal(30, BeamSpeed.FramesPerSecond(0.16, per, largeOrFlashing: false));   // 스트리밍 38.4px/s
            Assert.Equal(60, BeamSpeed.FramesPerSecond(0.30, per, largeOrFlashing: false));   // 작업 72px/s
        }

        /// <summary>굵은 빛과 번쩍임은 프레임 사이가 실제로 보인다 — 느려도 60fps를 지킨다.</summary>
        [Fact]
        public void LargeOrFlashingAlwaysKeepsSixty()
        {
            double per = Perimeter(748, 144, 30);
            foreach (var (_, speed) in States)
            {
                Assert.Equal(60, BeamSpeed.FramesPerSecond(speed, per, largeOrFlashing: true));
            }
        }

        /// <summary>
        /// 문턱은 실제로 앉는 값들 사이여야 한다 — 아니라면 어떤 상태는 프레임 수가 계속 오가고,
        /// 스프링의 오버슈트(≈4.6%)가 계단을 넘나들 수도 있다.
        /// </summary>
        [Fact]
        public void TheStillThresholdSitsBetweenRestingStates()
        {
            double idle = BeamSpeed.PixelSpeed(0.09, BeamSpeed.RefPerimeter);      // 21.6px/s
            double paused = BeamSpeed.PixelSpeed(0.07, BeamSpeed.RefPerimeter);    // 16.8px/s
            double playing = BeamSpeed.PixelSpeed(0.13, BeamSpeed.RefPerimeter);   // 31.2px/s

            Assert.True(idle < BeamSpeed.StillMotionPxPerSec, $"대기 {idle:F1}px/s가 문턱 위에 있다");
            Assert.True(paused < BeamSpeed.StillMotionPxPerSec, $"일시정지 {paused:F1}px/s가 문턱 위에 있다");
            Assert.True(playing > BeamSpeed.StillMotionPxPerSec, $"재생 {playing:F1}px/s가 문턱 아래다");
            // 오버슈트를 더해도 문턱을 넘지 않아야 계단이 흔들리지 않는다
            Assert.True(idle * 1.046 < BeamSpeed.StillMotionPxPerSec, "대기 빛이 오버슈트로 문턱을 넘는다");
        }

        /// <summary>픽셀 속도가 크기와 무관하므로 프레임 수의 문턱도 크기와 무관하다.</summary>
        [Fact]
        public void TheFrameCountIsTheSameOnEverySurface()
        {
            foreach (var (stateName, speed) in States)
            {
                int expected = BeamSpeed.FramesPerSecond(speed, BeamSpeed.RefPerimeter, false);
                foreach (var (surfaceName, w, h, r) in Surfaces)
                {
                    Assert.True(expected == BeamSpeed.FramesPerSecond(speed, Perimeter(w, h, r), false),
                        $"{stateName} · {surfaceName}: 크기에 따라 프레임 수가 다르다");
                }
            }
        }

        /// <summary>기준 둘레는 '알약 정도' — 그래야 작은 표면의 환산이 1에 가깝고 큰 표면만 늦춘다.</summary>
        [Fact]
        public void TheReferencePerimeterIsPillSized()
            => Assert.InRange(BeamSpeed.RefPerimeter, Perimeter(100, 38, 19) * 0.9, Perimeter(100, 38, 19) * 1.1);
    }
}
