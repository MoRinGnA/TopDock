using System;

namespace TopDock.Logic
{
    /// <summary>
    /// 소리(파형)를 세 칸의 높이로 줄이는 계산. 장치도 화면도 모른다 — 파형만 주면 답한다.
    ///
    /// 예전의 이퀄라이저는 서로 다른 주기로 오르내리는 장식이었다: 무엇이 재생 중이든 같은 춤을
    /// 췄고, 소리가 없어도 움직였다. 여기서는 실제 파형을 주파수로 나눠 세 밴드의 세기를 낸다.
    /// 그래서 노래가 조용한 구간에선 바가 내려앉고, 심벌이 터지면 오른쪽 칸이 튄다.
    ///
    /// 밴드는 세 개 — 저음(킥·베이스) · 중음(목소리·기타) · 고음(심벌·숨소리). 하나 더 늘리면
    /// 링 안(지름 24px)에서 칸이 1px대로 얇아져 읽히지 않는다.
    ///
    /// 화면 없이 검증할 수 있게 여기 둔다 — "100Hz 사인은 왼쪽 칸" 같은 것은 취향이 아니라 규칙이다.
    /// </summary>
    public sealed class AudioAnalyzer
    {
        /// <summary>한 번에 보는 파형 길이. 48kHz에서 21ms — 소리의 '지금'을 보기에 충분히 짧다.</summary>
        public const int FftSize = 1024;

        /// <summary>이 아래는 없는 소리로 본다. 방의 숨소리·저역 럼블을 바닥에 붙여 둔다.</summary>
        public const double FloorDb = -60;

        /// <summary>밴드 경계(Hz) — 저/중/고. 값 자체가 규칙이라 테스트가 이 경계를 잠근다.</summary>
        private static readonly (double Low, double High)[] Bands =
        {
            (60, 250),
            (250, 2000),
            (2000, 12000),
        };

        public static int BandCount => Bands.Length;

        public int SampleRate { get; }
        public int Size { get; }

        private readonly float[] _re;
        private readonly float[] _im;
        private readonly int[] _bitReversed;
        private readonly float[] _cos;
        private readonly float[] _sin;      // 부호를 뒤집어 담는다(회전이 곱셈 두 번이 되도록)
        private readonly float[] _window;   // Blackman — 잘린 파형의 끝단이 옆 칸으로 번지는 것을 줄인다
        private readonly int[] _binLow;     // 밴드별 첫 빈
        private readonly int[] _binHigh;    // 밴드별 마지막 빈

        /// <summary>윈도우를 씌운 사인의 크기에서 진폭을 되찾는 배율 (Blackman의 일관 이득 0.42).</summary>
        private const double AmplitudeScale = 2.0 / 0.42;

        public AudioAnalyzer(int sampleRate, int fftSize = FftSize)
        {
            if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));

            Size = fftSize;
            SampleRate = sampleRate;
            _re = new float[fftSize];
            _im = new float[fftSize];
            _cos = new float[fftSize / 2];
            _sin = new float[fftSize / 2];
            _bitReversed = new int[fftSize];
            _window = new float[fftSize];

            for (int i = 0; i < fftSize; i++)
            {
                // Hann의 곁가지(sidelobe)는 -31dB라 저음이 가운데 칸을 0.4까지 켰다(실측).
                // Blackman은 -58dB로 그 누설이 사라진다 — 대신 곁칸이 조금 넓어질 뿐이다.
                double angle = 2.0 * Math.PI * i / fftSize;
                _window[i] = (float)(0.42 - 0.5 * Math.Cos(angle) + 0.08 * Math.Cos(2.0 * angle));
                _bitReversed[i] = Reverse(i, fftSize);
            }

            for (int i = 0; i < fftSize / 2; i++)
            {
                double angle = -2.0 * Math.PI * i / fftSize;
                _cos[i] = (float)Math.Cos(angle);
                _sin[i] = (float)Math.Sin(angle);
            }

            double binHz = (double)sampleRate / fftSize;
            _binLow = new int[Bands.Length];
            _binHigh = new int[Bands.Length];
            for (int b = 0; b < Bands.Length; b++)
            {
                // DC(0번 빈)는 소리가 아니다 — 저음 밴드는 1번 빈부터 본다
                _binLow[b] = Math.Max(1, (int)Math.Ceiling(Bands[b].Low / binHz));
                _binHigh[b] = Math.Min(fftSize / 2 - 1, (int)Math.Floor(Bands[b].High / binHz));
            }
        }

        /// <summary>
        /// 파형(모노)의 마지막 한 조각을 주파수로 나눠 밴드별 세기를 0~1로 채운다.
        /// 파형이 짧으면 앞쪽을 0으로 채운다 — "아직 소리가 없다"와 같은 뜻이다.
        /// </summary>
        public void Analyze(ReadOnlySpan<float> monoSamples, Span<float> bars)
        {
            if (bars.Length < BandCount) throw new ArgumentException("밴드 수만큼 자리가 필요하다", nameof(bars));

            int take = Math.Min(monoSamples.Length, Size);
            int padding = Size - take;
            int offset = monoSamples.Length - take;

            // 파형에 실린 직류를 먼저 건더낸다 — 믹스에는 오프셋이 실리기 쉬운데,
            // 윈도우를 씌우면 그 직류가 저역 곁칸으로 번져 소리 없이도 바닥이 들린다.
            double sum = 0;
            for (int i = 0; i < Size; i++)
            {
                float sample = i < padding ? 0f : monoSamples[offset + i - padding];
                if (float.IsNaN(sample) || float.IsInfinity(sample)) sample = 0f;
                _re[i] = sample;
                sum += sample;
            }

            float mean = (float)(sum / Size);

            for (int i = 0; i < Size; i++)
            {
                _re[i] = (_re[i] - mean) * _window[i];
                _im[i] = 0f;
            }

            Fft();

            for (int b = 0; b < BandCount; b++)
            {
                double peak = 0;
                int low = _binLow[b];
                int high = Math.Max(low, _binHigh[b]);

                for (int k = low; k <= high && k < Size / 2; k++)
                {
                    double magnitude = Math.Sqrt((double)_re[k] * _re[k] + (double)_im[k] * _im[k]);
                    double amplitude = magnitude * AmplitudeScale / Size;
                    if (amplitude > peak) peak = amplitude;
                }

                bars[b] = (float)ToLevel(peak);
            }
        }

        /// <summary>진폭 → 0~1. dB로 옮겨야 조용한 구간의 변화가 보인다(선형이면 다 0에 붙는다).</summary>
        private static double ToLevel(double amplitude)
        {
            if (amplitude <= 0) return 0;
            double db = 20.0 * Math.Log10(amplitude);
            if (double.IsNaN(db) || db <= FloorDb) return 0;
            return Math.Clamp((db - FloorDb) / -FloorDb, 0.0, 1.0);
        }

        /// <summary>제자리(in-place) 2의 거듭제곱 FFT — 반복형 Cooley–Tukey.</summary>
        private void Fft()
        {
            int n = Size;

            for (int i = 0; i < n; i++)
            {
                int j = _bitReversed[i];
                if (j <= i) continue;
                (_re[i], _re[j]) = (_re[j], _re[i]);
                (_im[i], _im[j]) = (_im[j], _im[i]);
            }

            for (int length = 2; length <= n; length <<= 1)
            {
                int half = length >> 1;
                int twiddleStep = n / length;

                for (int start = 0; start < n; start += length)
                {
                    for (int k = 0; k < half; k++)
                    {
                        int twiddle = k * twiddleStep;
                        float wr = _cos[twiddle];
                        float wi = _sin[twiddle];

                        int a = start + k;
                        int b = a + half;

                        float xr = _re[b] * wr - _im[b] * wi;
                        float xi = _re[b] * wi + _im[b] * wr;

                        _re[b] = _re[a] - xr;
                        _im[b] = _im[a] - xi;
                        _re[a] += xr;
                        _im[a] += xi;
                    }
                }
            }
        }

        /// <summary>인덱스의 비트를 뒤집는다 — 가장 낮은 비트가 가장 높은 자리로 간다.</summary>
        private static int Reverse(int value, int size)
        {
            int reversed = 0;
            for (int bits = 1; bits < size; bits <<= 1)
            {
                reversed = (reversed << 1) | ((value & bits) != 0 ? 1 : 0);
            }
            return reversed;
        }
    }
}
