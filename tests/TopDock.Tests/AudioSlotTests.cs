using TopDock.Logic;

namespace TopDock.Tests
{
    /// <summary>
    /// 오른쪽 끝 한 자리의 주인 규칙. 여기서 잠그는 것은 우선순위다 — 누가 누구를 이기는가.
    /// </summary>
    public class AudioSlotTests
    {
        [Theory]
        [InlineData(true, false, true)]    // 재생 중 — 늘 떠 있다
        [InlineData(false, true, true)]    // 볼륨만 만지는 중
        [InlineData(true, true, true)]
        [InlineData(false, false, false)]  // 아무 일도 없으면 링도 없다
        public void TheRingIsThereWhileSoundIsOrWhileYouTouchTheVolume(bool playing, bool hud, bool expected)
            => Assert.Equal(expected, AudioSlot.ShowRing(playing, hud));

        [Fact]
        public void BarsAppearOnlyWhileSomethingIsActuallyPlaying()
        {
            Assert.True(AudioSlot.ShowBars(playing: true, hasLevels: true, statusDotVisible: false));
            Assert.False(AudioSlot.ShowBars(playing: false, hasLevels: true, statusDotVisible: false));
            Assert.False(AudioSlot.ShowBars(playing: true, hasLevels: false, statusDotVisible: false));
        }

        /// <summary>상태가 계기를 이긴다 — 점의 후광이 링의 빈 자리를 거의 다 채우기 때문이다.</summary>
        [Fact]
        public void AStatusDotBeatsTheMeter()
        {
            Assert.False(AudioSlot.ShowBars(playing: true, hasLevels: true, statusDotVisible: true));
        }

        /// <summary>링은 점에게 자리를 내주지 않는다 — 바늘만 비켜서고 호는 남는다.</summary>
        [Fact]
        public void TheRingStaysEvenWhenADotShowsUp()
        {
            Assert.True(AudioSlot.ShowRing(playing: true, volumeHudActive: false));
        }
    }
}
