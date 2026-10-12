namespace TopDock.Logic
{
    /// <summary>클립보드에 담긴 것의 종류 — 기록 패널의 정체성 타일이 무엇을 담을지 정한다.</summary>
    public enum ClipboardKind
    {
        Text,
        Image,
        Files,
    }

    /// <summary>
    /// 복사한 것의 정체성. 알림 카드의 앱 타일과 같은 언어를 쓰기 위해, 종류를 한 글자로 줄이는
    /// 규칙도 <see cref="NoticeIdentity.Monogram"/> 하나만 쓴다 — 타일 글자를 만드는 규칙이
    /// 화면마다 다르면 같은 타일이 화면마다 다르게 읽힌다.
    ///
    /// 화면 없이 검증할 수 있게 여기 둔다 — "무엇이 우선인가"는 취향이 아니라 규칙이다.
    /// </summary>
    public static class ClipboardIdentity
    {
        /// <summary>타일에 앉는 글자 (텍스트 → 텍, 이미지 → 이, 파일 → 파).</summary>
        public static string TileText(ClipboardKind kind) => NoticeIdentity.Monogram(Name(kind));

        /// <summary>종류를 부르는 이름 — 타일 글자와 로그가 함께 쓴다.</summary>
        public static string Name(ClipboardKind kind) => kind switch
        {
            ClipboardKind.Image => "이미지",
            ClipboardKind.Files => "파일",
            _ => "텍스트",
        };

        /// <summary>
        /// 한 번의 복사가 여러 형식을 함께 담고 있을 때 무엇으로 볼지 정한다.
        ///
        /// 우선순위는 클립보드를 읽는 순서와 같아야 한다(이미지 → 파일 → 텍스트): Win+Shift+S
        /// 캡처는 이미지가 본체이고, 탐색기에서 파일을 복사하면 CF_HDROP에 경로 문자열까지
        /// 함께 실린다. 순서가 어긋나면 같은 복사가 읽는 쪽과 보여주는 쪽에서 다르게 불린다.
        /// </summary>
        public static ClipboardKind Kind(bool isImage, bool isFiles)
        {
            if (isImage) return ClipboardKind.Image;
            if (isFiles) return ClipboardKind.Files;
            return ClipboardKind.Text;
        }
    }
}
