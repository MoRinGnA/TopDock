namespace TopDock.Controls
{
    /// <summary>
    /// 비서 얼굴이 말하는 진행 단계 9종.
    ///
    /// 원래는 thinking-orbs v0.3.2(MIT, Jakub Antalik)를 WPF로 포팅한 오브 렌더러의 상태 이름이었고,
    /// 그 파일(ThinkingOrb)과 함께 여기 있었다. 얼굴이 AiFace(눈 두 개)로 바뀌면서 렌더러는
    /// 통째로 쓰이지 않게 됐지만, 이 이름은 여전히 창과 얼굴 사이의 어휘다 —
    /// 도구 이름을 단계로 옮기는 자리(MainWindow.Assistant.OrbForTool)와 얼굴 표정 매핑이 이 값을 쓴다.
    /// 그래서 렌더러와 함께 지우지 않고 여기 남긴다.
    /// </summary>
    public enum OrbKind
    {
        Working,     // 사고
        Searching,   // 검색
        Solving,     // 도구 실행
        Listening,   // 재생·볼륨 조작
        Connecting,   // 링크 열기
        Weaving,
        Composing,   // 응답 스트리밍
        Breathing,   // 대기(기본 얼굴)
        Shaping,     // 앱 실행
    }
}
