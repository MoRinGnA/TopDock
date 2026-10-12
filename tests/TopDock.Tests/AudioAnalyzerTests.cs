using System;
using TopDock.Logic;

namespace TopDock.Tests
{
    /// <summary>
    /// 소리 → 세 칸. 여기서 잠그는 것은 밴드의 뜻이다: 저음은 왼쪽, 고음은 오른쪽,
    /// 소리가 없으면 아무것도 켜지지 않는다. 값이 맞는지보다 '어느 칸이 반응하는가'가 규칙이다.
    /// </summary>
    public class AudioAnalyzerTests
    {
        private const int Rate = 48000;

        private static float[] Sine(double hz, double amplitude, int samples = AudioAnalyzer.FftSize * 4, int rate = Rate)
        {
            var buffer = new float[samples];
            for (int i = 0; i < samples; i++)
            {
                buffer[i] = (float)(amplitude * Math.Sin(2.0 * Math.PI * hz * i / rate));
            }
            return buffer;
        }

        private static float[] Bands(float[] samples, int rate = Rate)
        {
            var bars = new float[AudioAnalyzer.BandCount];
            new AudioAnalyzer(rate).Analyze(samples, bars);
            return bars;
        }

        [Fact]
        public void SilenceLightsNothingUp()
        {
            var bars = Bands(new float[AudioAnalyzer.FftSize]);
            for (int i = 0; i < bars.Length; i++)
            {
                Assert.Equal(0f, bars[i]);
            }
        }

        /// <summary>각 밴드가 자기 주파수만 알아본다 — 이것이 '진짜 레벨'의 정의다.</summary>
        [Theory]
        [InlineData(100, 0)]
        [InlineData(700, 1)]
        [InlineData(1500, 1)]
        [InlineData(3000, 2)]
        [InlineData(6000, 2)]
        [InlineData(11000, 2)]
        public void EachBandAnswersToItsOwnTone(double hz, int band)
        {
            var bars = Bands(Sine(hz, 0.5));

            Assert.True(bars[band] > 0.5, $"{hz}Hz는 {band}번 칸을 켜야 한다 (실제 {bars[band]:F2})");
            for (int i = 0; i < bars.Length; i++)
            {
                if (i != band) Assert.True(bars[i] < 0.2, $"{hz}Hz가 {i}번 칸을 켰다 (실제 {bars[i]:F2})");
            }
        }

        /// <summary>
        /// 경계에 걸친 소리(200Hz)는 옆 칸도 함께 흔든다. 창(window)의 어깨가 한두 빈을 넘기 때문인데,
        /// 이것은 고장이 아니라 물리다 — 여기서 잠그는 것은 "양쪽이 같이 켜져도 자기 칸이 가장 높다"뿐이다.
        /// </summary>
        [Fact]
        public void AToneOnTheEdgeStillOwnsItsOwnBand()
        {
            var bars = Bands(Sine(200, 0.5));

            Assert.True(bars[0] > bars[1], $"200Hz는 저음 칸이 가장 높아야 한다 ({bars[0]:F2} vs {bars[1]:F2})");
            Assert.True(bars[0] > 0.5);
            Assert.True(bars[2] < 0.2, "새 칸까지 넘어오면 밴드가 아니다");
        }

        [Fact]
        public void LouderIsTallerAndQuieterIsShorter()
        {
            var quiet = Bands(Sine(700, 0.05))[1];
            var medium = Bands(Sine(700, 0.2))[1];
            var loud = Bands(Sine(700, 0.8))[1];

            Assert.True(quiet < medium, "작은 소리가 큰 소리보다 높으면 계기가 아니다");
            Assert.True(medium < loud);
        }

        /// <summary>최대치까지 찬 소리는 1을 넘지 않는다 — 그리는 쪽이 넘칠 일이 없어야 한다.</summary>
        [Fact]
        public void FullScaleFillsWithoutOverflowing()
        {
            var bars = Bands(Sine(700, 0.98));
            Assert.InRange(bars[1], 0.85f, 1.0f);
        }

        /// <summary>클리핑·NaN이 섞여 들어와도 그릴 수 있는 값이 나와야 한다(계기가 먼저 죽으면 안 된다).</summary>
        [Fact]
        public void GarbageStaysDrawable()
        {
            var samples = new float[AudioAnalyzer.FftSize * 2];
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] = (i % 7) switch
                {
                    0 => float.NaN,
                    1 => float.PositiveInfinity,
                    2 => float.NegativeInfinity,
                    3 => -3.5f,
                    _ => (i % 2 == 0) ? 1.0f : -1.0f,
                };
            }

            var bars = Bands(samples);
            foreach (float bar in bars)
            {
                Assert.False(float.IsNaN(bar), "NaN은 그릴 수 없다");
                Assert.InRange(bar, 0f, 1f);
            }
        }

        [Fact]
        public void HighFrequenciesAreNotInTheMusic()
        {
            // 12kHz 위는 이 계기의 관심 밖이다 — 샘플링 잡음이 오른쪽 칸을 켜 두면 안 된다
            var bars = Bands(Sine(20000, 0.9));
            Assert.True(bars[2] < 0.05, $"초음파가 고음 칸을 켰다 (실제 {bars[2]:F2})");
        }

        /// <summary>
        /// 파형에 실린 직류(0.7 상수)는 소리가 아니다. 저역 럼블로 새면 조용한 구간에서도
        /// 왼쪽 칸이 켜져 있게 된다 — 윈도우가 만든 1번 빈(DC 옆자리)을 밴드에서 빼 두는 이유다.
        /// </summary>
        [Fact]
        public void DirectCurrentIsNotSound()
        {
            var samples = new float[AudioAnalyzer.FftSize * 2];
            Array.Fill(samples, 0.7f);

            var bars = Bands(samples);
            foreach (float bar in bars)
            {
                Assert.True(bar < 0.05, $"직류가 칸을 켰다 (실제 {bar:F2})");
            }
        }

        /// <summary>같은 소리는 같은 답을 낸다 — 계기의 값이 실행마다 흔들리면 비교할 수 없다.</summary>
        [Fact]
        public void TheAnswerIsTheSameEveryTime()
        {
            var samples = Sine(440, 0.4);
            var first = Bands(samples);
            var second = Bands(samples);

            for (int i = 0; i < first.Length; i++)
            {
                Assert.Equal(first[i], second[i]);
            }
        }

        /// <summary>장치가 44.1kHz를 내놓아도 같은 소리는 같은 칸을 켠다 — 밴드를 Hz로 정한 이유다.</summary>
        [Theory]
        [InlineData(44100)]
        [InlineData(48000)]
        [InlineData(96000)]
        public void TheBandsFollowHertzNotBinNumbers(int rate)
        {
            var low = Bands(Sine(150, 0.5, rate: rate), rate);
            var mid = Bands(Sine(900, 0.5, rate: rate), rate);
            var high = Bands(Sine(5000, 0.5, rate: rate), rate);

            Assert.True(low[0] > 0.5 && mid[1] > 0.5 && high[2] > 0.5, $"{rate}Hz에서 밴드가 어긋났다");
        }

        /// <summary>아직 프레임이 안 찬 파형은 '아직 소리가 없다'로 읽는다 — 예외로 죽지 않는다.</summary>
        [Fact]
        public void AShortFrameIsNotACrash()
        {
            var bars = Bands(Sine(700, 0.9, samples: 16));
            foreach (float bar in bars)
            {
                Assert.InRange(bar, 0f, 0.4f);
            }
        }

        [Fact]
        public void TooFewBarsIsARefusal()
        {
            var analyzer = new AudioAnalyzer(Rate);
            Assert.Throws<ArgumentException>(() => analyzer.Analyze(new float[AudioAnalyzer.FftSize], new float[AudioAnalyzer.BandCount - 1]));
        }
    }
}
