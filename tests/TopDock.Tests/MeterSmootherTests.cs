using System;
using TopDock.Logic;

namespace TopDock.Tests
{
    /// <summary>
    /// 바늘의 손. 여기서 잠그는 것은 비대칭이다: 소리는 즉시 붙고, 멎으면 천천히 흘러내린다.
    /// </summary>
    public class MeterSmootherTests
    {
        private static float Step(MeterSmoother smoother, float target, double ms)
        {
            Span<float> output = stackalloc float[1];
            Span<float> input = stackalloc float[1];
            input[0] = target;
            smoother.Step(input, ms, output);
            return output[0];
        }

        [Fact]
        public void RisingIsQuickerThanFalling()
        {
            var up = new MeterSmoother(1);
            float risen = Step(up, 1f, 60);

            var down = new MeterSmoother(1);
            Step(down, 1f, 1000);
            float left = Step(down, 0f, 60);

            Assert.True(risen > 0.7, $"소리가 났는데 바가 안 붙었다 ({risen:F2})");
            Assert.True(risen > (1 - left) * 1.5,
                $"같은 60ms에 올라간 폭({risen:F2})이 내려온 폭({1 - left:F2})보다 확실히 커야 한다");
        }

        [Fact]
        public void ASustainedSoundSettlesAtTheTop()
        {
            var smoother = new MeterSmoother(1);
            float value = 0;
            for (int i = 0; i < 10; i++) value = Step(smoother, 1f, 40);

            Assert.InRange(value, 0.95f, 1.0f);
        }

        /// <summary>조용해지면 바닥까지 내려가고 정확히 0이 된다 — 0에 영영 닿지 않으면 유령 바가 남는다.</summary>
        [Fact]
        public void SilenceFallsToTheFloorAndStops()
        {
            var smoother = new MeterSmoother(1);
            for (int i = 0; i < 10; i++) Step(smoother, 1f, 40);

            float previous = 1f;
            for (int i = 0; i < 90; i++)
            {
                float value = Step(smoother, 0f, 33);
                Assert.True(value <= previous, "내려오는 중에 올라갔다");
                previous = value;
            }

            Assert.Equal(0f, previous);
        }

        /// <summary>프레임이 밀려도 같은 속도로 움직인다 — 시간을 인자로 받는 이유다.</summary>
        [Fact]
        public void TheSpeedDoesNotDependOnTheFrameCount()
        {
            var coarse = new MeterSmoother(1);
            float oneBigStep = Step(coarse, 1f, 100);

            var fine = new MeterSmoother(1);
            float manySmallSteps = 0;
            for (int i = 0; i < 10; i++) manySmallSteps = Step(fine, 1f, 10);

            Assert.InRange(Math.Abs(oneBigStep - manySmallSteps), 0f, 0.01f);
        }

        [Fact]
        public void GarbageTargetsStayInRange()
        {
            var smoother = new MeterSmoother(3);
            Span<float> output = stackalloc float[3];
            Span<float> input = stackalloc float[3];
            input[0] = float.NaN;
            input[1] = 5f;
            input[2] = -1f;

            for (int i = 0; i < 5; i++)
            {
                smoother.Step(input, 33, output);
                foreach (float value in output)
                {
                    Assert.False(float.IsNaN(value));
                    Assert.InRange(value, 0f, 1f);
                }
            }

            Assert.Equal(0f, output[0]);
            Assert.True(output[1] > 0.5f);
            Assert.Equal(0f, output[2]);
        }

        [Fact]
        public void EveryBarGetsItsOwnNeedle()
        {
            var smoother = new MeterSmoother(3);
            Span<float> output = stackalloc float[3];
            Span<float> input = stackalloc float[3];
            input[0] = 0.2f;
            input[1] = 0.6f;
            input[2] = 1.0f;
            smoother.Step(input, 200, output);

            Assert.True(output[0] < output[1] && output[1] < output[2], "세 칸이 따로 움직여야 한다");
        }
    }
}
