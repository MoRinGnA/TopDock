namespace TopDock.Logic
{
    /// <summary>
    /// 설정을 적용했을 때 전역 핫키가 조용히 죽는 회귀를 규칙으로 못 박는다.
    ///
    /// 핫키는 아이디 하나에 한 번만 걸린다 — 다시 걸려면 먼저 내려야 한다. 그런데 내린 것과 올린 것이
    /// 어긋나면(셋을 내리고 하나만 올리면) 아무 신호도 없이 단축기가 죽는다. 화면에는 아무 일도 일어나지
    /// 않고, 사용자는 한참 뒤에 "왜 Ctrl+Shift+V가 안 되지" 하고서야 알게 된다. 그래서 '지금 살아 있는가'
    /// (자리 → bool)를 적용 전후로 견주어, 죽은 자리를 이름으로 돌려준다.
    /// </summary>
    public static class HotkeyPlan
    {
        /// <summary>지금 설정에서 살아 있어야 하는 자리들.</summary>
        public static IReadOnlyList<HotkeySlot> Desired(bool assistantEnabled)
            => assistantEnabled
                ? new[] { HotkeySlot.Clipboard, HotkeySlot.Assistant, HotkeySlot.Inbox }
                : new[] { HotkeySlot.Clipboard, HotkeySlot.Inbox };

        /// <summary>
        /// 적용 전후의 생존을 견준다. 결과를 세 갈래로 나눠 돌려주는 까닭은 셋의 원인과 대응이
        /// 다르기 때문이다:
        /// <list type="bullet">
        /// <item><c>Lost</c> — 적용 전엔 살아 있었는데 적용 뒤 죽었다. 우리 코드의 회귀다.
        /// 이 규칙이 존재하는 이유가 이것이다.</item>
        /// <item><c>Unavailable</c> — 처음부터 못 걸었다(다른 앱이 쥐고 있다). 우리 잘못은 아니지만
        /// 사용자 눈에는 같은 증상이라, 회귀와 섞어 세면 원인을 잘못 짚는다.</item>
        /// <item><c>Stale</c> — 설정에서 껐는데 아직 살아 있다. 반대 방향의 회귀.</item>
        /// </list>
        /// 설정에서 꺼 둔 자리(비서)가 죽어 있는 것은 회귀로 세지 않는다 — 그건 의도한 상태다.
        /// </summary>
        public static HotkeyAudit Audit(
            IReadOnlyDictionary<HotkeySlot, bool> before,
            IReadOnlyDictionary<HotkeySlot, bool> after,
            bool assistantEnabled)
        {
            IReadOnlyList<HotkeySlot> desired = Desired(assistantEnabled);
            var lost = new List<HotkeySlot>();
            var unavailable = new List<HotkeySlot>();
            var stale = new List<HotkeySlot>();

            static bool Alive(IReadOnlyDictionary<HotkeySlot, bool> map, HotkeySlot slot)
                => map.TryGetValue(slot, out bool alive) && alive;

            foreach (HotkeySlot slot in desired)
            {
                bool wasAlive = Alive(before, slot);
                bool isAlive = Alive(after, slot);

                if (wasAlive && !isAlive) lost.Add(slot);
                else if (!wasAlive && !isAlive) unavailable.Add(slot);
            }

            foreach (HotkeySlot slot in new[] { HotkeySlot.Clipboard, HotkeySlot.Assistant, HotkeySlot.Inbox })
            {
                if (!desired.Contains(slot) && Alive(after, slot)) stale.Add(slot);
            }

            return new HotkeyAudit(lost, unavailable, stale);
        }
    }

    /// <summary>
    /// <see cref="HotkeyPlan.Audit"/>의 결과. 로그 한 줄과 테스트 실패 메시지가 같은 말을 쓰도록
    /// <see cref="Describe"/> 하나를 둔다.
    /// </summary>
    public sealed record HotkeyAudit(
        IReadOnlyList<HotkeySlot> Lost,
        IReadOnlyList<HotkeySlot> Unavailable,
        IReadOnlyList<HotkeySlot> Stale)
    {
        /// <summary>적용 뒤 상태가 설정과 맞는가 — 회귀(Lost·Stale)가 하나도 없어야 참이다.</summary>
        public bool Ok => Lost.Count == 0 && Stale.Count == 0;

        /// <summary>사람이 읽는 한 줄. 빈 갈래는 말하지 않는다.</summary>
        public string Describe()
        {
            var parts = new List<string>();
            if (Lost.Count > 0) parts.Add($"lost after apply: {string.Join(", ", Lost)}");
            if (Stale.Count > 0) parts.Add($"still held although disabled: {string.Join(", ", Stale)}");
            if (Unavailable.Count > 0) parts.Add($"never registered (taken by another app?): {string.Join(", ", Unavailable)}");
            return parts.Count == 0 ? "all desired hotkeys are live" : string.Join("; ", parts);
        }
    }
}
