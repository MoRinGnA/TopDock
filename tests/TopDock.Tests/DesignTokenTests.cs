using TopDock.Logic;

namespace TopDock.Tests
{
    /// <summary>
    /// 시각 값이 한 곳(Tokens)에 모여 있다는 것만으로는 부족하다 — 그 값들이 서로 어긋나면
    /// 화면은 여전히 서로 다른 언어로 말한다. 값 사이의 관계를 여기서 잠근다.
    /// (예전에 실제로 어긋났던 것: 같은 22px 타일인데 글자가 11과 12로 갈라져 있었다.)
    /// </summary>
    public class DesignTokenTests
    {
        // ── 타일 ──

        [Fact]
        public void SmallTileIsSmallerThanTheFullTile()
        {
            Assert.True(Tokens.TileSizeCompact < Tokens.TileSizeRegular);
            Assert.True(Tokens.TileRadiusCompact < Tokens.TileRadiusRegular);
        }

        /// <summary>반경은 크기에 비례해야 한다 — 큰 타일만 각지거나 작은 타일만 동그래지면 안 된다.</summary>
        [Theory]
        [InlineData(Tokens.TileSizeCompact, Tokens.TileRadiusCompact)]
        [InlineData(Tokens.TileSizeRegular, Tokens.TileRadiusRegular)]
        public void TileRadiusStaysProportionalToItsSize(double size, double radius)
        {
            double ratio = radius / size;
            Assert.InRange(ratio, 0.22, 0.35);
        }

        /// <summary>작은 타일 글자가 타일 밖으로 넘치지 않는다 — 여백이 남아야 글자가 산다.</summary>
        [Theory]
        [InlineData(Tokens.TileSizeCompact, Tokens.FontMarkCompact)]
        [InlineData(Tokens.TileSizeRegular, Tokens.FontMarkRegular)]
        public void MarkStaysInsideItsTile(double size, double mark)
        {
            Assert.True(mark < size * 0.7, $"타일 {size}에 글자 {mark}는 너무 크다");
            Assert.True(mark >= size * 0.4, $"타일 {size}에 글자 {mark}는 너무 작다");
        }

        [Fact]
        public void SmallTileUsesTheSmallerMark()
            => Assert.True(Tokens.FontMarkCompact < Tokens.FontMarkRegular);

        // ── 글자 ── 무엇을 먼저 읽어야 하는지가 크기 순서로 드러나야 한다.

        [Fact]
        public void TitleIsTheLargestThingOnACard()
        {
            Assert.True(Tokens.FontTitle > Tokens.FontTitleCompact);
            Assert.True(Tokens.FontTitleCompact > Tokens.FontBody);
        }

        [Fact]
        public void TheSourceNeverOutgrowsTheTitle()
        {
            Assert.True(Tokens.FontKicker < Tokens.FontBody);
            Assert.True(Tokens.FontKickerLarge < Tokens.FontTitleCompact);
        }

        /// <summary>사다리 전체가 한 방향으로 줄어든다 — 어느 두 칸도 뒤집히지 않는다.</summary>
        [Fact]
        public void TheTypeScaleNeverReverses()
        {
            double[] ladder =
            {
                Tokens.FontTitle, Tokens.FontTitleCompact, Tokens.FontBody,
                Tokens.FontKickerLarge, Tokens.FontKicker,
                Tokens.FontMeta, Tokens.FontHint, Tokens.FontSection,
            };

            for (int i = 1; i < ladder.Length; i++)
                Assert.True(ladder[i] < ladder[i - 1], $"{ladder[i]}가 {ladder[i - 1]}보다 크다 — 사다리가 뒤집혔다");
        }

        [Fact]
        public void MarksSitOnTheSameScaleAsTheTypeLadder()
        {
            Assert.True(Tokens.FontMarkRegular <= Tokens.FontKicker);
            Assert.True(Tokens.FontMarkCompact < Tokens.FontMeta);
        }

        [Fact]
        public void BodyLinesHaveRoomToBreathe()
        {
            Assert.True(Tokens.LineBody > Tokens.FontBody);
            Assert.True(Tokens.LineBody <= Tokens.FontBody * 1.7);
        }

        // ── 모션 ── 순서와 길이가 어긋나면 화면마다 다른 리듬으로 움직인다.

        [Fact]
        public void TheBoxGetsTimeBeforeTheContentArrives()
        {
            Assert.InRange(Tokens.EntranceDelayMs, 1, Tokens.EntranceMs);
        }

        [Fact]
        public void LeavingIsQuickerThanArriving()
        {
            Assert.True(Tokens.ExitMs < Tokens.EntranceMs);
            Assert.True(Tokens.QuickSlideMs < Tokens.EntranceMs);
            Assert.True(Tokens.PanelCloseMs < Tokens.PanelOpenMs);
            Assert.True(Tokens.RingOutMs < Tokens.RingInMs);
        }

        /// <summary>알림은 오래 늘어나는 카드가 아니다 — 펼침 모프보다 짧아야 알림답다.</summary>
        [Fact]
        public void AlertsSettleFasterThanTheDockExpands()
        {
            Assert.True(Tokens.NoticeMorphMs < Tokens.MorphExpandMs);
            Assert.True(Tokens.NoticeMorphMs < Tokens.MorphCollapseMs);
        }

        /// <summary>정착은 눈에 띄면 안 된다 — 자리를 잡는 손짓이 크면 화면이 흔들린다.</summary>
        [Theory]
        [InlineData(Tokens.EntranceScale)]
        [InlineData(Tokens.ExitScale)]
        [InlineData(Tokens.RingInScale)]
        [InlineData(Tokens.RingOutScale)]
        public void SettleGesturesStaySubtle(double scale)
            => Assert.InRange(scale, 0.8, 1.0);

        /// <summary>
        /// 등장은 다르다 — 복사처럼 짧게 알리고 마는 배지는 눈에 보여야 한다. 그래서 정착보다
        /// 큰 폭에서 시작하되, 화면의 절반 넘게 작아졌다가 커지면 "툭" 하고 튄 것처럼 보인다.
        /// </summary>
        [Fact]
        public void TheBadgePopIsBolderThanASettleButNotAFlash()
        {
            Assert.InRange(Tokens.FeedbackPopFrom, 0.5, 0.9);
            Assert.True(Tokens.FeedbackPopFrom < Tokens.RingInScale);
            Assert.True(Tokens.FeedbackPopFrom < Tokens.EntranceScale);
        }

        [Theory]
        [InlineData(Tokens.EaseOutExponent)]
        [InlineData(Tokens.EaseInExponent)]
        [InlineData(Tokens.PopExponent)]
        public void EasingExponentsAreUsable(int exponent)
            => Assert.InRange(exponent, 1, 10);

        /// <summary>
        /// 볼륨 UI가 떠 있는 동안의 안쪽 빛은 '낮추되 끄지 않는다'.
        /// 0이면 확장 카드의 분위기가 통째로 사라져 빛이 꺼진 것처럼 보인다 — 그래서 0은 금지다.
        /// </summary>
        [Fact]
        public void VolumeDimsTheInteriorLightButNeverKillsIt()
        {
            Assert.True(Tokens.InteriorLightWhileVolume > 0, "0으로 꺼서는 안 된다 — 빛이 사라져 보인다");
            Assert.InRange(Tokens.InteriorLightWhileVolume, 0.15, 0.75);
            Assert.True(Tokens.InteriorLightWhileVolume < 1.0, "볼륨 동안에도 그대로면 링이 앨범색에 묻힌다");
        }

        /// <summary>안쪽 빛 전환은 링이 물러나는 시간보다 길면 둔해 보인다.</summary>
        [Fact]
        public void TheInteriorLightFadesInTheSameBreathAsTheRing()
            => Assert.InRange(Tokens.InteriorFadeMs, 80, Tokens.RingInMs + 60);

        /// <summary>60fps 미만으로 돌리면 모프가 끊겨 보인다 — 예전에 실제로 그랬다.</summary>
        [Fact]
        public void AnimationsRunAtSixtyFramesPerSecond()
            => Assert.True(Tokens.FrameRate >= 60);

        /// <summary>
        /// 링 안의 세 칸은 링 밖으로 나가면 안 된다. 테두리와 번짐 여유를 뺀 빈 원 안에
        /// 바의 네 모서리가 모두 들어와야 한다 — 넘치면 소리가 아니라 얼룩으로 보인다.
        /// </summary>
        [Fact]
        public void TheBarsStayInsideTheRing()
        {
            double clearRadius = Tokens.RingDiameter / 2 - Tokens.RingThickness - 2;
            double halfWidth = (Tokens.RingBarWidth * 3 + Tokens.RingBarGap * 2) / 2;
            double halfHeight = Tokens.RingBarMaxHeight / 2;

            double corner = System.Math.Sqrt(halfWidth * halfWidth + halfHeight * halfHeight);
            Assert.True(corner < clearRadius, $"바의 모서리({corner:F1})가 링 안쪽({clearRadius:F1})을 넘는다");
            Assert.True(Tokens.RingBarMaxHeight <= clearRadius * 2, "가장 높은 바가 링의 지름을 넘는다");
        }

        /// <summary>그리는 칸 수와 분석기가 내놓는 밴드 수는 같아야 한다 — 다르면 한 칸이 굶는다.</summary>
        [Fact]
        public void ThereAreAsManyBarsAsBands()
            => Assert.Equal(Tokens.RingBarCount, AudioAnalyzer.BandCount);

        /// <summary>조용할 때도 바닥은 남는다 — 0이면 바가 사라져 링만 남는다.</summary>
        [Fact]
        public void TheBarsKeepAFloorAndARange()
        {
            Assert.True(Tokens.RingBarMinHeight > 0);
            Assert.True(Tokens.RingBarMaxHeight > Tokens.RingBarMinHeight * 2);
            Assert.True(Tokens.RingBarGap > 0);
        }

        /// <summary>바늘은 즉시 붙고 천천히 흘러내린다 — 뒤집히면 깜빡임으로 보인다.</summary>
        [Fact]
        public void TheNeedleRisesFasterThanItFalls()
            => Assert.True(Tokens.MeterAttackMs < Tokens.MeterReleaseMs);

        /// <summary>소리는 화면 모프보다 자주 봐야 산다 — 25fps 아래면 바가 계단으로 움직인다.</summary>
        [Fact]
        public void TheMeterIsReadAtLeastTwentyFiveTimesASecond()
            => Assert.InRange(Tokens.MeterRenderMs, 16, 40);
    }
}
