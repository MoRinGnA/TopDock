namespace TopDock.Logic
{
    /// <summary>
    /// 살아있는 가장자리(테두리 빛)의 값이 목표로 '이완'하는 수학 — 화면이 아니라 여기서 정한다.
    ///
    /// 왜 필요한가: 빛의 속도·밝기·형태·색은 상태(대기/재생/일시정지/스트리밍/작업)가 바뀔 때
    /// 목표값으로 <b>즉시</b> 튀었다. 그래서 곡을 넘길 때마다 빛이 색과 굵기와 빠르기를 한 프레임에
    /// 갈아끼우는 것처럼 보였다 — '이질감'의 정체가 이 순간이동이었다.
    ///
    /// 대안은 선형 보간이 아니라 스프링이다. 감쇠비를 1보다 살짝 작게 두면 값이 목표를 아주 조금
    /// 넘겼다가(≈4.6%) 앉는다 — 그 오버슈트가 '쫀득함'이고, 선형 보간에는 없는 것이다. 너무 넘기면
    /// 튀어 보이므로 감쇠비로 상한을 잠근다.
    ///
    /// 적분은 반암시적 오일러가 아니라 <b>해석해</b>다. 빔의 시계는 빛이 느리면 30fps, 빠르면
    /// 60fps로 돈다 — 근사 적분은 프레임 간격에 따라 궤적이 달라져, 같은 이완이 두 속도로 갈린다.
    /// 해석해는 한 스텝이 정확이라 dt가 달라도 궤적이 같다(테스트가 이 불변식을 잠근다).
    /// </summary>
    public static class BeamSpring
    {
        /// <summary>감쇠비 ζ. 1 = 딱 멈춤(오버슈트 없음), 1보다 작으면 넘겼다 앉는다.
        /// 0.70 → 첫 오버슈트 ≈ 4.6% (e^(−πζ/√(1−ζ²))). 더 낮추면 튀고, 1에 붙이면 무기력해진다.</summary>
        public const double DampingRatio = 0.70;

        /// <summary>
        /// 목표에 '사실상' 앉는 데 걸리는 시간(초)에서 각진동수를 환산한다.
        /// ζωt ≈ 4면 남은 진폭이 2% 아래 — 눈으로는 앉은 것과 같다.
        /// </summary>
        public static double OmegaFor(double settleSeconds)
            => 4.0 / (DampingRatio * Math.Max(0.02, settleSeconds));

        /// <summary>
        /// 감쇠 조화진동자의 해석해 한 스텝. x·v를 제자리에서 갱신한다.
        /// target은 이 스텝 동안 일정하다고 본다(틱 간격이 짧아 그 근사가 유효하다).
        /// </summary>
        public static void Step(ref double x, ref double v, double target, double dt, double omega)
        {
            if (dt <= 0) return;

            double z = DampingRatio;
            double a = z * omega;                    // 감쇠 계수
            double w2 = omega * omega;
            double wd = omega * Math.Sqrt(1.0 - z * z);   // 감쇠 진동수
            double d = x - target;                   // 목표 기준 변위
            double e = Math.Exp(-a * dt);
            double c = Math.Cos(wd * dt);
            double s = Math.Sin(wd * dt);

            // x(t) = T + e^{−at}[ d·cos + ((v + a·d)/wd)·sin ]
            double nd = e * (d * c + ((v + a * d) / wd) * s);
            // v(t) = e^{−at}[ v·cos − ((a·v + ω²·d)/wd)·sin ]
            double nv = e * (v * c - ((a * v + w2 * d) / wd) * s);

            x = target + nd;
            v = nv;
        }
    }
}
