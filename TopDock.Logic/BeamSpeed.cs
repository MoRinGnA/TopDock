namespace TopDock.Logic
{
    /// <summary>
    /// 살아있는 가장자리(테두리 빛)가 도는 속도의 수학 — 화면이 아니라 여기서 정한다.
    ///
    /// 빛의 '바퀴'는 표면이 커지면 훨씬 길어진다. Speed를 둘레에 그대로 곱하면 큰 카드에서
    /// 빛이 눈에 띄게 빨라진다(한 바퀴가 3배 길어지는데 픽셀은 더 빨라지는, 주석의 의도와
    /// 반대의 결과). 그래서 Speed의 뜻을 '기준 둘레(240px)에서의 초당 바퀴 수'로 두고,
    /// 이 표면의 둘레로 환산해 픽셀 속도(240 × Speed px/s)를 모든 크기에서 같게 만든다.
    /// 큰 표면에서 달라지는 것은 한 바퀴에 걸리는 시간뿐이다.
    ///
    /// 실측(2026-10, 화면 밖 하네스 %TEMP%\lightprobe의 A/B — 빛이 스스로 적분한 위상을 읽음):
    ///
    /// | 상태(Speed)      | 알약 100×38 | 카드 260×120 | 카드 430×144 | 가사 카드 748×144* |
    /// |---|---|---|---|---|
    /// | 대기 0.09 이전   | 21px/s | 22px/s | 29px/s | 47px/s |
    /// | 대기 0.09 지금   | 22px/s | 22px/s | 22px/s | 22px/s |
    /// | 재생 0.13 이전   | 31px/s | 31px/s | 43px/s | 68px/s |
    /// | 재생 0.13 지금   | 31px/s | 31px/s | 31px/s | 31px/s |
    ///
    /// (*가사 카드는 같은 식으로 계산한 값 — 환산이 둘레로만 결정되므로 크기를 늘려도 같다.)
    /// </summary>
    public static class BeamSpeed
    {
        /// <summary>Speed의 뜻 — 이 둘레에서의 초당 바퀴 수.</summary>
        public const double RefPerimeter = 240.0;

        /// <summary>아주 작은 표면에서 프레임당 이동이 폭주하지 않게 하는 상한.</summary>
        public const double MaxScale = 4.0;

        /// <summary>이보다 느린 빛은 60fps로 그려도 프레임당 1px도 못 간다.</summary>
        public const double SlowMotionPxPerSec = 60.0;

        /// <summary>
        /// 이보다 느린 빛은 '거의 멈춘 빛'이다 — 30fps도 과하다.
        ///
        /// 문턱을 26px/s로 둔 이유는 실제로 앉는 두 값 사이이기 때문이다: 대기 21.6px/s,
        /// 일시정지 16.8px/s는 아래고, 재생 31.2px/s는 위다. 그래서 대기·일시정지는 이 아래에,
        /// 재생은 위에 남는다. 스프링의 오버슈트(≈4.6%)를 더해도 22.6px/s라 문턱을 넘지 않아
        /// 계단이 흔들리지 않는다.
        ///
        /// 실측(2026-10, 화면 밖 하네스 audioprobe의 beam 모드 — 같은 빛을 프레임 수만 바꿔
        /// 가며 CPU 시간을 잼):
        ///
        /// | 프레임 | CPU |
        /// |---|---|
        /// | 30fps (이전의 정지 상태) | 135.3 ms/s (코어 1개의 13.5%) |
        /// | 15fps (지금의 정지 상태) | 33.3 ms/s (3.3%) |
        /// | 10fps | 26.1 ms/s (2.6%) |
        ///
        /// 대기 빛은 초당 22px밖에 움직이지 않는다 — 30fps에서도 프레임당 0.72px이다.
        /// 하루 종일 켜져 있는 앱에서 그 0.72px을 13.5%의 CPU로 그릴 이유는 없었다.
        /// </summary>
        public const double StillMotionPxPerSec = 26.0;

        /// <summary>기준 둘레의 바퀴 수를 이 표면의 둘레로 환산한 배율.</summary>
        public static double ScaleFor(double perimeter)
            => Math.Min(RefPerimeter / Math.Max(1.0, perimeter), MaxScale);

        /// <summary>이 표면에서 빛이 흐르는 픽셀 속도(px/s) — 크기와 무관하게 240 × Speed.</summary>
        public static double PixelSpeed(double speed, double perimeter)
            => Math.Abs(speed) * ScaleFor(perimeter) * Math.Max(1.0, perimeter);

        /// <summary>
        /// 이 빛을 몇 fps로 그릴까 — 굵은 빛(Large)과 번쩍임은 프레임 사이가 실제로 보이므로
        /// 언제나 60fps다. 나머지는 프레임당 이동량이 정한다(상태 이름이 아니라 픽셀).
        /// </summary>
        public static int FramesPerSecond(double speed, double perimeter, bool largeOrFlashing)
        {
            if (largeOrFlashing) return 60;
            double px = PixelSpeed(speed, perimeter);
            if (px < StillMotionPxPerSec) return 15;
            return px < SlowMotionPxPerSec ? 30 : 60;
        }
    }
}
