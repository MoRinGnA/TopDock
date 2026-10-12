using TopDock.Logic;

namespace TopDock.Tests
{
    /// <summary>
    /// 기록 패널의 타일이 무엇을 담을지 정하는 규칙 — 읽는 쪽(ReadClipboard)의 우선순위와
    /// 어긋나면 같은 복사가 화면마다 다르게 불린다.
    /// </summary>
    public class ClipboardKindTests
    {
        /// <summary>캡처(Win+Shift+S)는 이미지가 본체 — 경로 문자열이 함께 실려도 이미지다.</summary>
        [Fact]
        public void ImageWinsOverEverythingElse()
        {
            Assert.Equal(ClipboardKind.Image, ClipboardIdentity.Kind(isImage: true, isFiles: true));
            Assert.Equal(ClipboardKind.Image, ClipboardIdentity.Kind(isImage: true, isFiles: false));
        }

        /// <summary>탐색기에서 파일을 복사하면 경로 문자열이 함께 실린다 — 텍스트가 아니라 파일이다.</summary>
        [Fact]
        public void FileDropWinsOverText()
            => Assert.Equal(ClipboardKind.Files, ClipboardIdentity.Kind(isImage: false, isFiles: true));

        [Fact]
        public void PlainTextIsTheLastResort()
            => Assert.Equal(ClipboardKind.Text, ClipboardIdentity.Kind(isImage: false, isFiles: false));

        /// <summary>타일 글자는 앱 타일과 같은 규칙(첫 글자)을 쓴다 — 규칙이 둘이면 타일이 두 종류로 보인다.</summary>
        [Theory]
        [InlineData(ClipboardKind.Text, "텍")]
        [InlineData(ClipboardKind.Image, "이")]
        [InlineData(ClipboardKind.Files, "파")]
        public void TileTextIsTheFirstLetterOfTheKind(ClipboardKind kind, string expected)
        {
            Assert.Equal(expected, ClipboardIdentity.TileText(kind));
            Assert.Equal(expected, NoticeIdentity.Monogram(ClipboardIdentity.Name(kind)));
        }

        /// <summary>종류마다 글자가 다르다 — 셋이 같은 글자면 타일이 하는 일이 없다.</summary>
        [Fact]
        public void KindsDoNotShareTheSameMark()
        {
            string text = ClipboardIdentity.TileText(ClipboardKind.Text);
            string image = ClipboardIdentity.TileText(ClipboardKind.Image);
            string files = ClipboardIdentity.TileText(ClipboardKind.Files);

            Assert.NotEqual(text, image);
            Assert.NotEqual(image, files);
            Assert.NotEqual(text, files);
        }
    }
}
