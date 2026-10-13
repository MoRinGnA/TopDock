using TopDock.Logic;

namespace TopDock.Tests
{
    /// <summary>
    /// 설정을 적용한 뒤 전역 핫키가 조용히 죽는 회귀(13번)를 잡는 <b>규칙</b>을 합성 상태로 잠근다.
    ///
    /// 규칙만으로는 부족하다 — 규칙을 통과하는 사전을 누가 채우느냐가 관건이라, 진짜 앱을 띄워
    /// 적용 전후의 생존을 밖에서 확인하는 짝이 있다(<see cref="HotkeyRegressionTests"/>).
    /// 이 파일은 그 짝이 판단에 쓰는 규칙 자체가 틀리지 않았음을 증명한다.
    /// </summary>
    public class HotkeyPlanTests
    {
        /// <summary>자리 → 생존 사전 한 벌. 적지 않은 자리는 '안 걸림'이다.</summary>
        private static Dictionary<HotkeySlot, bool> Live(params (HotkeySlot Slot, bool Alive)[] entries)
        {
            var map = new Dictionary<HotkeySlot, bool>();
            foreach (var (slot, alive) in entries) map[slot] = alive;
            return map;
        }

        [Fact]
        public void AssistantEnabledRequiresAllThreeSlots()
        {
            Assert.Equal(
                new[] { HotkeySlot.Clipboard, HotkeySlot.Assistant, HotkeySlot.Inbox },
                HotkeyPlan.Desired(assistantEnabled: true));
        }

        /// <summary>비서를 꺼 두었으면 비서 자리는 '죽어 있어야 정상'이다 — 요구 목록에서 빠진다.</summary>
        [Fact]
        public void DisabledAssistantIsNotRequired()
        {
            Assert.Equal(
                new[] { HotkeySlot.Clipboard, HotkeySlot.Inbox },
                HotkeyPlan.Desired(assistantEnabled: false));
        }

        /// <summary>13번 그대로 — 적용 전엔 셋 다 살아 있었는데 적용 뒤 클립보드만 죽었다.</summary>
        [Fact]
        public void SlotThatDiesAcrossApplyIsReportedLost()
        {
            var before = Live(
                (HotkeySlot.Clipboard, true), (HotkeySlot.Assistant, true), (HotkeySlot.Inbox, true));
            var after = Live(
                (HotkeySlot.Clipboard, false), (HotkeySlot.Assistant, true), (HotkeySlot.Inbox, true));

            HotkeyAudit audit = HotkeyPlan.Audit(before, after, assistantEnabled: true);

            Assert.Equal(new[] { HotkeySlot.Clipboard }, audit.Lost);
            Assert.Empty(audit.Unavailable);
            Assert.Empty(audit.Stale);
            Assert.False(audit.Ok);
        }

        /// <summary>
        /// 처음부터 못 건 자리(다른 앱이 그 조합을 쥐고 있다)는 회귀가 아니다. 둘을 섞어 세면
        /// 사용자가 자기 잘못이 아닌 일로 "회귀" 보고를 받는다.
        /// </summary>
        [Fact]
        public void SlotThatNeverRegisteredIsUnavailableNotLost()
        {
            var before = Live(
                (HotkeySlot.Clipboard, true), (HotkeySlot.Assistant, false), (HotkeySlot.Inbox, true));
            var after = Live(
                (HotkeySlot.Clipboard, true), (HotkeySlot.Assistant, false), (HotkeySlot.Inbox, true));

            HotkeyAudit audit = HotkeyPlan.Audit(before, after, assistantEnabled: true);

            Assert.Empty(audit.Lost);
            Assert.Equal(new[] { HotkeySlot.Assistant }, audit.Unavailable);
            Assert.True(audit.Ok);
        }

        /// <summary>비서를 껐다가 적용하면 그 자리가 죽는 것이 정상이다 — 회귀로 세지 않는다.</summary>
        [Fact]
        public void TurningAssistantOffIsNotALoss()
        {
            var before = Live(
                (HotkeySlot.Clipboard, true), (HotkeySlot.Assistant, true), (HotkeySlot.Inbox, true));
            var after = Live(
                (HotkeySlot.Clipboard, true), (HotkeySlot.Assistant, false), (HotkeySlot.Inbox, true));

            HotkeyAudit audit = HotkeyPlan.Audit(before, after, assistantEnabled: false);

            Assert.Empty(audit.Lost);
            Assert.Empty(audit.Stale);
            Assert.True(audit.Ok);
        }

        /// <summary>반대 방향의 회귀 — 껐는데 아직 살아 있으면(해제를 빠뜨리면) 실패로 센다.</summary>
        [Fact]
        public void SlotStillHeldAlthoughDisabledIsStale()
        {
            var before = Live(
                (HotkeySlot.Clipboard, true), (HotkeySlot.Assistant, true), (HotkeySlot.Inbox, true));
            var after = Live(
                (HotkeySlot.Clipboard, true), (HotkeySlot.Assistant, true), (HotkeySlot.Inbox, true));

            HotkeyAudit audit = HotkeyPlan.Audit(before, after, assistantEnabled: false);

            Assert.Empty(audit.Lost);
            Assert.Equal(new[] { HotkeySlot.Assistant }, audit.Stale);
            Assert.False(audit.Ok);
        }

        [Fact]
        public void HealthyApplyKeepsEverythingLive()
        {
            var before = Live(
                (HotkeySlot.Clipboard, true), (HotkeySlot.Assistant, true), (HotkeySlot.Inbox, true));
            var after = Live(
                (HotkeySlot.Clipboard, true), (HotkeySlot.Assistant, true), (HotkeySlot.Inbox, true));

            HotkeyAudit audit = HotkeyPlan.Audit(before, after, assistantEnabled: true);

            Assert.True(audit.Ok);
            Assert.Equal("all desired hotkeys are live", audit.Describe());
        }

        /// <summary>로그 한 줄이 곧 원인 설명이 되도록 — 빈 갈래는 말하지 않는다.</summary>
        [Fact]
        public void DescribeNamesOnlyTheBrokenSlots()
        {
            var audit = HotkeyPlan.Audit(
                Live((HotkeySlot.Clipboard, true), (HotkeySlot.Assistant, true), (HotkeySlot.Inbox, true)),
                Live((HotkeySlot.Clipboard, false), (HotkeySlot.Assistant, true), (HotkeySlot.Inbox, false)),
                assistantEnabled: true);

            Assert.Equal("lost after apply: Clipboard, Inbox", audit.Describe());
        }
    }
}
