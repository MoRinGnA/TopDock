using System;

namespace TopDock.Logic
{
    /// <summary>
    /// 바늘의 손 — 소리는 순간순간 튀므로 계산한 값을 그대로 그리면 바가 떨기만 한다.
    /// 올라갈 때는 재빨리 붙고(어택), 내려올 때는 천천히 흘러내린다(릴리즈).
    ///
    /// 비대칭이 핵심이다. 대칭으로 두면 박수 한 번에 바가 튀었다 사라져 깜빡임으로 보이고,
    /// 릴리즈가 없으면 조용한 구간에서 바가 바닥으로 떨어져 곡이 죽은 것처럼 보인다.
    ///
    /// 순수 계산이라 화면 없이 검증된다 — 시계를 주입받으므로 테스트가 시간을 지배한다.
    /// </summary>
    public sealed class MeterSmoother
    {
        private readonly float[] _value;

        public MeterSmoother(int count)
        {
            if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
            _value = new float[count];
        }

        public int Count => _value.Length;

        /// <summary>지금 그리고 있는 값 — 애니메이션 없이 한 번 읽을 때.</summary>
        public float Current(int index) => _value[index];

        /// <summary>
        /// 목표값을 향해 elapsedMs만큼 움직인다. 시간을 인자로 받으므로 프레임이 밀려도
        /// 같은 속도로 따라간다(프레임 수로 세면 부하에 따라 느려졌다 빨라졌다 한다).
        /// </summary>
        public void Step(ReadOnlySpan<float> target, double elapsedMs, Span<float> output)
        {
            if (output.Length < _value.Length) throw new ArgumentException("바늘 수만큼 자리가 필요하다", nameof(output));

            double rise = Blend(elapsedMs, Tokens.MeterAttackMs);
            double fall = Blend(elapsedMs, Tokens.MeterReleaseMs);

            for (int i = 0; i < _value.Length; i++)
            {
                float goal = target[i];
                if (float.IsNaN(goal) || goal < 0f) goal = 0f;
                else if (goal > 1f) goal = 1f;

                double k = goal > _value[i] ? rise : fall;
                double next = _value[i] + (goal - _value[i]) * k;

                // 바닥에 닿으면 정확히 0으로 붙인다 — 지수 감쇠는 0에 영영 닿지 않아
                // 조용한 곡에서 바가 1px짜리로 계속 남는다
                if (next < 0.002) next = 0;

                _value[i] = (float)next;
                output[i] = _value[i];
            }
        }

        public void Reset()
        {
            Array.Clear(_value);
        }

        /// <summary>시간 상수 하나로 "얼마나 따라갔는가"를 낸다 — 0(안 움직임)~1(완전히 붙음).</summary>
        private static double Blend(double elapsedMs, double timeConstantMs)
        {
            if (elapsedMs <= 0) return 0;
            return 1.0 - Math.Exp(-elapsedMs / Math.Max(1.0, timeConstantMs));
        }
    }
}
