namespace TopDock.Logic
{
    /// <summary>
    /// 도크가 화면에 보여 주는 모양. 창 클래스에서 옮겨 왔다 — 전환 결정이 UI 밖(순수 로직)으로
    /// 나오면서 이름도 함께 나왔다. 각 이름의 크기·반경 같은 그리기 값은 창이 계속 들고 있다.
    /// </summary>
    public enum ViewMode
    {
        IdleCompact,
        IdleExpanded,
        MediaCompact,
        MediaExpanded,
        NotificationCompact,
        NotificationExpanded,
        Inbox,   // 알림 기록을 한 건씩 다시 보는 화면 — 사용자가 열고 닫는다
        Assistant,
        AssistantCompact,
    }
}
