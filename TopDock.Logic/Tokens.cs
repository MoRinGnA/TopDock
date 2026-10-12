namespace TopDock.Logic
{
    /// <summary>
    /// 화면이 함께 쓰는 시각 값 — 타일 크기 · 글자 크기 · 모션 길이.
    ///
    /// 화면마다 이 값을 따로 적어 두면 하나를 고칠 때 나머지가 어긋난다. 실제로 그랬다:
    /// 같은 22px 타일인데 클립보드 타일 글자는 11, 알림 타일 글자는 12였고, 목록이 정착하는
    /// 시간은 클립보드 200ms · 알림 220ms로 갈라져 있었다. 여기 한 곳만 고치면
    /// 알림 · 인박스 · 클립보드 · 볼륨 · 비서가 함께 움직인다.
    ///
    /// 색과 WPF 타입(CornerRadius·Duration·Brush)은 여기 두지 않는다 — 이 프로젝트는 화면 없이
    /// 도는 순수 로직이라 WPF를 참조하지 않는다. 창 쪽 DesignTokens가 이 숫자로 타입 있는 값을
    /// 만들고, XAML은 그 값을 x:Static으로 읽는다.
    /// </summary>
    public static class Tokens
    {
        // ── 타일 ── 타일은 두 크기뿐이다: 노치 안 작은 자리와 펼친 카드.
        public const double TileSizeCompact = 18;
        public const double TileSizeRegular = 22;
        public const double TileRadiusCompact = 5;
        public const double TileRadiusRegular = 7;

        // ── 글자 ── 한 화면에서 무엇을 먼저 읽어야 하는지가 크기 순서로 드러난다.
        // 제목 > 본문 > 출처(앱 이름) > 언제 > 안내 > 구역 이름 순으로 줄어든다.
        public const double FontMarkCompact = 10;    // 타일 글자 — 작은 타일(18)
        public const double FontMarkRegular = 12;    // 타일 글자 — 펼친 타일(22)
        public const double FontKicker = 12;         // 출처 — 접힌 한 줄 카드
        public const double FontKickerLarge = 12.5;  // 출처 — 펼친 카드·인박스
        public const double FontTitleCompact = 13.5; // 한 줄 카드의 제목 — 읽어야 할 문장
        public const double FontTitle = 16;          // 펼친 카드의 제목
        public const double FontBody = 13;           // 본문·비서 응답
        public const double FontMeta = 11.5;         // 언제
        public const double FontHint = 10.5;         // 조작 안내
        public const double FontSection = 9.5;       // ALERTS · CLIPBOARD
        public const double LineBody = 19;           // 본문 줄높이 — 글자보다 커야 줄이 뭉치지 않는다

        // ── 모션 ── 상자가 먼저 자리를 만들고 내용이 뒤따라 앉는다. 모든 화면이 같은 리듬을 쓴다.
        public const int EntranceMs = 220;         // 내용이 앉는 시간
        public const int EntranceDelayMs = 90;     // 상자가 자리를 만들 시간
        public const double EntranceScale = 0.97;  // 살짝 작은 자리에서 제 크기로
        public const double EntranceSlideY = -6;
        public const int ExitMs = 140;             // 내용이 먼저 물러나는 시간
        public const double ExitScale = 0.985;
        public const double ExitSlideY = -5;
        public const int QuickSlideMs = 170;       // 방향대로 넘길 때 — 기다리지 않고 곧바로
        public const int PanelOpenMs = 280;
        public const int PanelCloseMs = 200;
        public const int NoticeMorphMs = 300;      // 알림은 빨리 자리를 잡고 빨리 걷힌다
        public const int MorphExpandMs = 520;
        public const int MorphCollapseMs = 380;
        public const int ViewFadeInMs = 450;       // 화면이 나타나는 시간(모프가 정한다)
        public const int ViewFadeOutMs = 150;      // 물러나는 화면은 늘 같은 속도로 사라진다
        public const int RingInMs = 180;           // 볼륨 링 — 등장이 아니라 정착이라 아주 작게
        public const int RingOutMs = 160;
        public const double RingInScale = 0.9;
        public const double RingOutScale = 0.94;

        // ── 볼륨 링의 기하 ── 링 안에 소리 바늘 세 칸이 들어간다. 이 값들이 서로 어긋나면
        //    바가 링 밖으로 삐져나오거나(최대 높이) 링 안에서 뭉친다(간격).
        public const double RingDiameter = 24;
        public const double RingThickness = 2;
        /// <summary>링 안의 칸 수 — 소리 분석기가 내놓는 밴드 수와 같아야 한다(테스트가 잠근다).</summary>
        public const int RingBarCount = 3;
        public const double RingBarWidth = 1.6;
        public const double RingBarGap = 1.3;
        public const double RingBarMinHeight = 2;   // 조용할 때 남는 바닥 — 0이면 바가 사라져 링만 남는다
        public const double RingBarMaxHeight = 10;

        // ── 소리 바늘 ── 값이 아니라 '손'이다. 올라가는 것과 내려오는 것이 달라야 바가 산다.
        public const int MeterAttackMs = 40;      // 소리가 나면 재빨리 붙는다
        public const int MeterReleaseMs = 170;    // 소리가 멎으면 천천히 흘러내린다
        public const int MeterRenderMs = 33;      // 30fps — 소리는 화면 모프보다 자주 봐야 산다

        /// <summary>
        /// 볼륨을 만지는 동안 남겨 두는 '안쪽 빛'. 0이면 확장 카드의 분위기가 통째로 사라져
        /// 빛이 꺼진 것처럼 보인다(실제로 그랬다) — 낮추되 끄지는 않는다.
        /// </summary>
        public const double InteriorLightWhileVolume = 0.35;
        /// <summary>안쪽 빛이 낮아지고 돌아오는 시간 — 링이 물러나는 박자와 같게.</summary>
        public const int InteriorFadeMs = 160;
        public const int FeedbackFadeInMs = 130;   // 복사 피드백
        public const int FeedbackPopMs = 240;
        public const double FeedbackPopFrom = 0.65;
        public const int FeedbackFadeOutMs = 220;
        public const int FrameRate = 60;           // 기본값은 화면 주사율을 못 따라가 모프가 끊겨 보인다
        public const int MorphEaseExponent = 6;    // 도크 상자 자체가 늘고 줄는 곡선(내용과 다른 손짓)
        public const int EaseOutExponent = 5;      // 정착 — 빠르게 출발해 부드럽게 멈춘다
        public const int EaseInExponent = 3;       // 퇴장 — 천천히 출발해 빠르게 빠진다
        public const int PopExponent = 4;          // 작은 배지의 등장
    }
}
