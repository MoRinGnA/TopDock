using TopDock.Logic;

namespace TopDock.Tests
{
    /// <summary>
    /// 가장자리 빛이 목표값으로 이완하는 방식은 '느낌'이 아니라 수학이다.
    ///
    /// 여기서 잠그는 것:
    ///   · 살짝 넘겼다 앉는다 — 오버슈트가 있어야 '쫀득'하고, 너무 크면 튀어 보인다
    ///   · 프레임 간격(30/60fps)이 달라도 같은 궤적 — 빔의 시계는 속도에 따라 fps를 바꾼다
    ///   · 결국 목표에 앉는다 — 오버슈트가 영원한 진동이 되면 안 된다
    /// </summary>
    public class BeamSpringTests
    {
        private static (double X, double V) Sim(double target, double x0, double v0,
            double omega, double dt, double seconds)
        {
            double x = x0, v = v0;
            int n = (int)Math.Round(seconds / dt);
            for (int i = 0; i < n; i++) BeamSpring.Step(ref x, ref v, target, dt, omega);
            return (x, v);
        }

        /// <summary>목표가 1일 때 첫 봉우리 — 딱 멈추면 1.0, 쫀득하면 1을 조금 넘는다.</summary>
        [Fact]
        public void ItOvershootsGentlyThenSettles()
        {
            double omega = BeamSpring.OmegaFor(0.62);
            double x = 0, v = 0, peak = 0;
            for (int i = 0; i < 4000; i++)
            {
                BeamSpring.Step(ref x, ref v, 1.0, 1.0 / 480.0, omega);
                peak = Math.Max(peak, x);
            }
            // 4~6% 사이 — 보이는 만큼 넘되 튀지는 않는다
            Assert.InRange(peak, 1.01, 1.08);
            Assert.Equal(1.0, x, 3);   // 결국 앉는다
            Assert.Equal(0.0, v, 3);
        }

        /// <summary>감쇠비가 1보다 작아야 오버슈트가 있다 — 1이면 딱 멈춰 '이질감'은 없지만 쫀득하지도 않다.</summary>
        [Fact]
        public void TheDampingRatioLeavesRoomForSpringiness()
            => Assert.InRange(BeamSpring.DampingRatio, 0.5, 0.9);

        /// <summary>
        /// 프레임 간격이 달라도 궤적이 같아야 한다. 빔의 시계는 빛이 느리면 30fps, 빠르면 60fps로
        /// 돈다 — 근사 적분이었다면 같은 이완이 두 속도로 갈려, 상태에 따라 손짓이 달라졌을 것이다.
        /// </summary>
        [Fact]
        public void TheTrajectoryDoesNotDependOnTheFrameStep()
        {
            double omega = BeamSpring.OmegaFor(0.62);
            var fast = Sim(1.0, 0, 0, omega, 1.0 / 60.0, 0.5);
            var slow = Sim(1.0, 0, 0, omega, 1.0 / 30.0, 0.5);
            Assert.Equal(slow.X, fast.X, 9);
            Assert.Equal(slow.V, fast.V, 9);
        }

        /// <summary>느린 스프링(작은 ω)은 더 오래 걸린다 — 파라미터별로 무게를 달리 둘 수 있는 근거.</summary>
        [Fact]
        public void ASlowerSpringTakesLongerToSettle()
        {
            double quick = BeamSpring.OmegaFor(0.3);
            double lazy = BeamSpring.OmegaFor(0.9);
            Assert.True(quick > lazy);
            var a = Sim(1.0, 0, 0, quick, 1.0 / 60.0, 0.45);
            var b = Sim(1.0, 0, 0, lazy, 1.0 / 60.0, 0.45);
            Assert.True(Math.Abs(1 - a.X) < Math.Abs(1 - b.X));
        }

        /// <summary>부호가 있는 목표(반시계 빛 = 음수 속도)도 같은 방식으로 이완한다.</summary>
        [Fact]
        public void ItRelaxesTowardNegativeTargetsToo()
        {
            double omega = BeamSpring.OmegaFor(0.62);
            var r = Sim(-0.3, 0.16, 0, omega, 1.0 / 60.0, 1.5);
            Assert.Equal(-0.3, r.X, 3);
        }

        /// <summary>긴 프레임(창이 멈췄다 돌아옴)에도 값이 폭주하지 않는다 — e^{−a·dt}가 눌러 준다.</summary>
        [Fact]
        public void ALongFrameDoesNotBlowUp()
        {
            double omega = BeamSpring.OmegaFor(0.62);
            double x = 0, v = 0;
            BeamSpring.Step(ref x, ref v, 1.0, 1.0, omega);   // 1초 한 스텝
            Assert.InRange(x, 0.0, 1.2);
            Assert.True(double.IsFinite(v));
        }

        /// <summary>dt가 0 이하면 아무 일도 일어나지 않는다 — 시계 정지 시 안전.</summary>
        [Fact]
        public void AZeroStepChangesNothing()
        {
            double x = 0.4, v = 0.2;
            BeamSpring.Step(ref x, ref v, 1.0, 0.0, BeamSpring.OmegaFor(0.62));
            Assert.Equal(0.4, x, 12);
            Assert.Equal(0.2, v, 12);
        }
    }
}
