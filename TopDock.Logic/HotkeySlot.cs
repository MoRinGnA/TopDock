namespace TopDock.Logic
{
    /// <summary>
    /// 앱이 거는 전역 핫키 세 자리.
    ///
    /// 이름을 여기(UI 밖)에 두는 이유는, "설정을 적용했더니 어느 핫키가 죽었는가"를 창과 회귀
    /// 하네스가 같은 말로 주고받아야 하기 때문이다. 창은 이 이름으로 등록 결과를 적어 두고,
    /// 테스트는 같은 이름으로 생존을 확인한다.
    /// </summary>
    public enum HotkeySlot
    {
        Clipboard,   // Ctrl+Shift+V — 복사 기록
        Assistant,   // Ctrl+Shift+Space — AI 비서 (설정에서 끌 수 있는 유일한 자리)
        Inbox,       // Ctrl+Shift+N — 놓친 알림 기록
    }
}
