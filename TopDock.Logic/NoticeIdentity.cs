namespace TopDock.Logic
{
    /// <summary>
    /// 알림이 어느 앱에서 왔는지 한 글자로 줄여 보여 주는 규칙. 앱 아이콘을 꺼내오는 일은
    /// AUMID와 실행 파일을 이어 붙이는 별개의 큰 작업이라, 그 전까지는 이름의 첫 글자로 정체성을 만든다.
    ///
    /// 화면 없이 검증할 수 있게 여기 둔다 — "무엇이 첫 글자인가"는 취향이 아니라 규칙이다.
    /// </summary>
    public static class NoticeIdentity
    {
        /// <summary>글자가 하나도 없는 이름에 쓰는 표식.</summary>
        public const string Unknown = "?";

        /// <summary>
        /// 이름의 첫 글자. 구두점·공백은 건너뛴다("(광고) 쿠팡" → "쿠", "카카오톡" → "카").
        /// 라틴 문자는 대문자로 올린다 — 작은 원 안에서 "s"와 "S"는 다르게 읽힌다.
        /// </summary>
        public static string Monogram(string? appName)
        {
            if (string.IsNullOrWhiteSpace(appName)) return Unknown;

            foreach (char c in appName)
            {
                if (!char.IsLetterOrDigit(c)) continue;
                return char.IsAsciiLetter(c) ? char.ToUpperInvariant(c).ToString() : c.ToString();
            }

            return Unknown;
        }
    }
}
