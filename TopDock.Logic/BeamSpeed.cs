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

        /// <summary>기준 둘레의 바퀴 수를 이 표면의 둘레로 환산한 배율.</summary>
        public static double ScaleFor(double perimeter)
            => Math.Min(RefPerimeter / Math.Max(1.0, perimeter), MaxScale);

        /// <summary>이 표면에서 빛이 흐르는 픽셀 속도(px/s) — 크기와 무관하게 240 × Speed.</summary>
        public static double PixelSpeed(double speed, double perimeter)
            => Math.Abs(speed) * ScaleFor(perimeter) * Math.Max(1.0, perimeter);

        /// <summary>60fps로 그릴 이유가 없을 만큼 느린 빛인가 — 굵은 빛과 번쩍임은 예외.</summary>
        public static bool IsSlowMotion(double speed, double perimeter, bool largeOrFlashing)
            => !largeOrFlashing && PixelSpeed(speed, perimeter) < SlowMotionPxPerSec;
    }
}
