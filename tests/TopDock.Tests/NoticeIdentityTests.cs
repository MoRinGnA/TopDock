using TopDock.Logic;

namespace TopDock.Tests
{
    /// <summary>알림 정체성 칩에 들어갈 한 글자 — 이름이 무엇이든 규칙이 흔들리지 않는지 본다.</summary>
    public class NoticeIdentityTests
    {
        [Theory]
        [InlineData("Slack", "S")]
        [InlineData("slack", "S")]
        [InlineData("Microsoft Teams", "M")]
        [InlineData("카카오톡", "카")]
        [InlineData("Zoom 5", "Z")]
        // 구두점은 건너뛰고 '첫 글자'를 쓴다 — 대괄호 안의 글자도 글자다("[긴급] 배포" → "긴").
        // 앱 이름에 대괄호가 붙는 일은 드물지만, 규칙이 이름마다 흔들리면 정체성이 아니다.
        [InlineData("(광고) 쿠팡", "광")]
        [InlineData("[긴급] 배포 알림", "긴")]
        [InlineData("• Spotify", "S")]
        [InlineData("  windows terminal  ", "W")]
        public void FirstLetterBecomesTheMonogram(string appName, string expected)
            => Assert.Equal(expected, NoticeIdentity.Monogram(appName));

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("!!!")]
        [InlineData("···")]
        [InlineData(null)]
        public void NamesWithoutLettersFallBackToTheUnknownMark(string? appName)
            => Assert.Equal(NoticeIdentity.Unknown, NoticeIdentity.Monogram(appName));

        /// <summary>숫자만 있는 이름도 정체성은 있다 — "?"로 뭉뚱그리면 서로 구분되지 않는다.</summary>
        [Fact]
        public void DigitsCountAsIdentity()
            => Assert.Equal("7", NoticeIdentity.Monogram("7-Zip"));

        /// <summary>같은 이름은 언제나 같은 글자 — 알림마다 표식이 바뀌면 정체성이 아니다.</summary>
        [Fact]
        public void MonogramIsStable()
        {
            Assert.Equal(NoticeIdentity.Monogram("Slack"), NoticeIdentity.Monogram("Slack"));
            Assert.NotEqual(NoticeIdentity.Monogram("Slack"), NoticeIdentity.Monogram("Discord"));
        }
    }
}
