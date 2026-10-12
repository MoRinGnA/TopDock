using System.Windows;
using System.Windows.Media;

namespace TopDock
{
    /// <summary>
    /// 화면이 함께 쓰는 시각 값의 WPF 쪽 얼굴. 숫자는 전부 <see cref="Tokens"/>(순수 로직)에서
    /// 오고, 여기서는 그것을 WPF가 XAML에서 바로 읽을 수 있는 타입으로 감싼다.
    ///
    /// XAML은 <c>{x:Static local:DesignTokens.…}</c>로 읽고, 코드는 그대로 쓴다. 그래서
    /// 타일 크기·글자 크기·색·모션 길이를 바꾸려면 <see cref="Tokens"/> 또는 이 파일 한 곳만
    /// 고치면 되고, 화면마다 값을 다시 적어 맞출 일이 없다.
    ///
    /// 왜 색은 Logic이 아니라 여기인가: 색은 WPF 타입(Brush·Color)이라 순수 로직이 참조하지 않는다.
    /// 대신 색의 '이름'과 쓰임은 여기 한 곳에 모아 두었다 — 같은 파랑을 화면마다 새로 적지 않는다.
    /// </summary>
    public static class DesignTokens
    {
        // ── 타일 ──
        public const double TileSizeCompact = Tokens.TileSizeCompact;
        public const double TileSizeRegular = Tokens.TileSizeRegular;
        public static readonly CornerRadius TileRadiusCompact = new(Tokens.TileRadiusCompact);
        public static readonly CornerRadius TileRadiusRegular = new(Tokens.TileRadiusRegular);

        // ── 글자 크기 ──
        public const double FontMarkCompact = Tokens.FontMarkCompact;
        public const double FontMarkRegular = Tokens.FontMarkRegular;
        public const double FontKicker = Tokens.FontKicker;
        public const double FontKickerLarge = Tokens.FontKickerLarge;
        public const double FontTitleCompact = Tokens.FontTitleCompact;
        public const double FontTitle = Tokens.FontTitle;
        public const double FontBody = Tokens.FontBody;
        public const double FontMeta = Tokens.FontMeta;
        public const double FontHint = Tokens.FontHint;
        public const double FontSection = Tokens.FontSection;
        public const double LineBody = Tokens.LineBody;

        // ── 글자 굵기 ── 같은 자리에 늘 같은 굵기를 쓴다.
        public static readonly FontWeight WeightBold = FontWeights.Bold;         // 타일 글자
        public static readonly FontWeight WeightSemiBold = FontWeights.SemiBold; // 출처·제목·안내
        public static readonly FontWeight WeightMedium = FontWeights.Medium;     // 본문·언제

        // ── 먹색 ── 읽는 순서대로 밝다: 제목 > 응답 > 본문 > 언제 > 안내.
        public static readonly Color InkPrimaryColor = Color.FromRgb(0xFF, 0xFF, 0xFF);
        public static readonly Color InkBodyColor = Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF);
        public static readonly Color InkBodyBrightColor = Color.FromArgb(0xE8, 0xFF, 0xFF, 0xFF);
        public static readonly Color InkMutedColor = Color.FromRgb(0x8E, 0x8E, 0x93);
        public static readonly Color InkFaintColor = Color.FromRgb(0x5C, 0x5C, 0x63);

        public static readonly Brush InkPrimary = Frozen(InkPrimaryColor);
        public static readonly Brush InkBody = Frozen(InkBodyColor);
        /// <summary>비서 응답 — 그 화면의 유일한 읽을거리라 본문보다 한 단계 밝다.</summary>
        public static readonly Brush InkBodyBright = Frozen(InkBodyBrightColor);
        public static readonly Brush InkMuted = Frozen(InkMutedColor);
        public static readonly Brush InkFaint = Frozen(InkFaintColor);

        // ── 아이콘·선의 회색 ── 글자가 아니라 획으로 그리는 것들(고스트 버튼·휴지통·✕).
        public static readonly Color IconMutedColor = Color.FromRgb(0xA8, 0xA8, 0xAD);
        public static readonly Brush IconMuted = Frozen(IconMutedColor);

        // ── 출처의 색 ── 알림이 '왜 떴는가'를 말하는 색. 복사 기록의 종류도 이 색을 나눠 쓴다
        //    (글자=정보 파랑 · 파일=도구 주황 · 이미지=사고 보라). 한 화면에 둘이 함께 뜨지 않으므로
        //    팔레트를 나눠 쓰는 편이, 복사 목록을 위해 새 색을 만드는 것보다 조용하다.
        public static readonly Color AccentInfoColor = Color.FromRgb(0x0A, 0x84, 0xFF);
        public static readonly Color AccentToolColor = Color.FromRgb(0xFF, 0x9F, 0x0A);
        public static readonly Color AccentThinkColor = Color.FromRgb(0xBF, 0x5A, 0xF2);
        public static readonly Color AccentChargingColor = Color.FromRgb(0x34, 0xC7, 0x59);

        public static readonly Brush AccentInfo = Frozen(AccentInfoColor);
        public static readonly Brush AccentTool = Frozen(AccentToolColor);
        public static readonly Brush AccentThink = Frozen(AccentThinkColor);
        public static readonly Brush AccentCharging = Frozen(AccentChargingColor);

        // ── 상태 점 ── 배터리와 AI 단계를 말하는 색. XAML의 기본값과 코드가 같은 값을
        //    두 번 적지 않도록 여기서 한 번만 정한다.
        public static readonly Color AccentLowColor = Color.FromRgb(0xFF, 0x45, 0x3A);   // 배터리 부족
        public static readonly Brush AccentLow = Frozen(AccentLowColor);

        public static readonly Color AiThinkingColor = Color.FromRgb(0xB6, 0x9C, 0xFF);  // 생각
        public static readonly Color AiToolColor = Color.FromRgb(0xFF, 0xB8, 0x6B);      // 실행
        public static readonly Color AiReplyColor = Color.FromRgb(0x76, 0xD7, 0xFF);     // 응답
        public static readonly Color AiDoneColor = Color.FromRgb(0x30, 0xD1, 0x58);      // 완료

        /// <summary>점이 아무 단계도 말하지 않을 때의 기본 색(XAML 기본값).</summary>
        public static readonly Brush AiThinkingBrush = Frozen(AiThinkingColor);

        public static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}
