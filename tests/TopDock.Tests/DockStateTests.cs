using TopDock.Logic;

namespace TopDock.Tests
{
    /// <summary>
    /// 도크 모양 전환의 결정을 화면 없이 검증한다. 창은 WPF를 띄우지 않고는 확인할 수 없지만,
    /// "어떤 신호에서 어떤 모양이어야 하는가"는 여기서 전부 증명된다.
    ///
    /// 특히 <see cref="LeaveDuringVolumeHudIsDeferredAndCollapsesWhenHudExpires"/>가 실제로 났던
    /// 회귀 — 볼륨 HUD 때문에 미뤄 둔 마우스 이탈을 아무도 되돌리지 않아 확장된 도크가 그대로
    /// 남던 버그 — 를 막는다.
    /// </summary>
    public class DockStateTests
    {
        /// <summary>신호 한 벌 — 각 테스트는 필요한 축만 바꿔 쓴다.</summary>
        private static DockSignals Signals(
            bool hasMedia = false,
            bool mouseOver = false,
            bool volumeHud = false,
            bool notice = false,
            bool assistantView = false,
            bool inbox = false)
            => new(hasMedia, volumeHud, notice, assistantView, mouseOver, inbox);

        /// <summary>창의 ApplyMode와 같은 규칙 — 옮겨야 할 모양이 있으면 상태에 반영한다.</summary>
        private static void Apply(DockState dock, ViewMode? next)
        {
            if (next is { } mode) dock.Show(mode);
        }

        [Fact]
        public void StartsCollapsedInIdle()
        {
            var dock = new DockState();

            Assert.Equal(ViewMode.IdleCompact, dock.Mode);
            Assert.False(dock.Expanded);
        }

        [Fact]
        public void HoverExpandsToTheShapeOfWhatIsPlaying()
        {
            Assert.Equal(ViewMode.IdleExpanded, new DockState().Hover(Signals()));
            Assert.Equal(ViewMode.MediaExpanded, new DockState().Hover(Signals(hasMedia: true)));
        }

        [Fact]
        public void LeaveReturnsToTheRestingShape()
        {
            var dock = new DockState();

            dock.Hover(Signals(hasMedia: true));
            Assert.Equal(ViewMode.MediaCompact, dock.Leave(Signals(hasMedia: true)));
            Assert.False(dock.Expanded);
        }

        /// <summary>회귀 방지 — 확장된 상태에서 HUD가 떠 있는 동안 벗어나도, HUD가 끝나면 접혀야 한다.</summary>
        [Fact]
        public void LeaveDuringVolumeHudIsDeferredAndCollapsesWhenHudExpires()
        {
            var dock = new DockState();

            Apply(dock, dock.Hover(Signals()));
            Assert.Equal(ViewMode.IdleExpanded, dock.Mode);

            // 볼륨 HUD가 뜬 채로 벗어난다 — HUD 동안엔 크기를 건드리지 않는다
            Apply(dock, dock.Leave(Signals(volumeHud: true)));
            Assert.Equal(ViewMode.IdleExpanded, dock.Mode);

            // HUD가 끝난다 — 그때 마우스가 어디 있었는지로 모양이 정해진다
            Apply(dock, dock.VolumeHudExpired(Signals(mouseOver: false)));
            Assert.Equal(ViewMode.IdleCompact, dock.Mode);
        }

        [Fact]
        public void LeaveDuringVolumeHudWhileMediaPlaysCollapsesToMedia()
        {
            var dock = new DockState();

            Apply(dock, dock.Hover(Signals(hasMedia: true)));
            Apply(dock, dock.Leave(Signals(hasMedia: true, volumeHud: true)));
            Apply(dock, dock.VolumeHudExpired(Signals(hasMedia: true, mouseOver: false)));

            Assert.Equal(ViewMode.MediaCompact, dock.Mode);
        }

        [Fact]
        public void HoverDuringVolumeHudExpandsWhenHudExpires()
        {
            var dock = new DockState();

            Apply(dock, dock.Hover(Signals(volumeHud: true)));
            Assert.Equal(ViewMode.IdleCompact, dock.Mode);   // 아직 미뤄져 있다

            Apply(dock, dock.VolumeHudExpired(Signals(mouseOver: true)));
            Assert.Equal(ViewMode.IdleExpanded, dock.Mode);
        }

        [Fact]
        public void HudExpiryWithMouseStillOverStaysExpanded()
        {
            var dock = new DockState();

            Apply(dock, dock.Hover(Signals(hasMedia: true)));
            Apply(dock, dock.VolumeHudExpired(Signals(hasMedia: true, mouseOver: true)));

            Assert.Equal(ViewMode.MediaExpanded, dock.Mode);
        }

        /// <summary>HUD가 끝나면 의도는 마우스 위치로 다시 세워진다 — 예전 의도를 그대로 믿지 않는다.</summary>
        [Fact]
        public void HudExpiryRebuildsTheIntentFromThePointer()
        {
            var dock = new DockState();
            dock.SetExpanded(true);   // 사용자가 연 상태 (남아 있는 의도)

            Apply(dock, dock.VolumeHudExpired(Signals(notice: true, mouseOver: false)));

            Assert.False(dock.Expanded);
            Assert.Equal(ViewMode.NotificationCompact, dock.Mode);
        }

        [Fact]
        public void NoticeShapesFollowTheHoverIntent()
        {
            var dock = new DockState();

            // 접혀 있으면 알림도 접힌 채로, 펼쳐져 있으면 본문까지 보여준다
            Apply(dock, dock.Resolve(Signals(notice: true)));
            Assert.Equal(ViewMode.NotificationCompact, dock.Mode);

            Apply(dock, dock.Hover(Signals(notice: true)));
            Assert.Equal(ViewMode.NotificationExpanded, dock.Mode);

            Apply(dock, dock.Leave(Signals(notice: true)));
            Assert.Equal(ViewMode.NotificationCompact, dock.Mode);
        }

        [Fact]
        public void AssistantViewKeepsItsShapeForHoverAndHud()
        {
            var dock = new DockState();

            // 비서 화면에서는 들어와도 크기를 바꾸지 않는다 (의도만 기록된다)
            Assert.Null(dock.Hover(Signals(assistantView: true)));
            Assert.True(dock.Expanded);

            // 벗어나도 아무 일도 없다 — 닫기는 ✕·Esc가 맡는다
            Assert.Null(dock.Leave(Signals(assistantView: true)));
            Assert.True(dock.Expanded);

            // 볼륨 HUD가 끝나도 비서 화면은 건드리지 않는다
            Assert.Null(dock.VolumeHudExpired(Signals(assistantView: true, mouseOver: false)));
            Assert.True(dock.Expanded);
        }

        /// <summary>인박스는 알림·마우스보다 위에 있다 — 사용자가 연 기록 화면을 새 알림이 밀어내면 안 된다.</summary>
        [Fact]
        public void InboxOutranksNoticeAndHover()
        {
            var dock = new DockState();

            Apply(dock, dock.Resolve(Signals(notice: true, inbox: true)));
            Assert.Equal(ViewMode.Inbox, dock.Mode);

            // 열려 있는 동안 마우스가 들어와도 모양은 그대로 (의도도 건드리지 않는다)
            Assert.Null(dock.Hover(Signals(inbox: true, mouseOver: true)));
            Assert.False(dock.Expanded);

            // 벗어나도 닫히지 않는다 — 닫기는 ✕·Esc·트레이가 맡는다
            Assert.Null(dock.Leave(Signals(inbox: true)));

            // 볼륨 HUD가 끝나도 인박스를 걷지 않는다
            Assert.Null(dock.VolumeHudExpired(Signals(inbox: true)));
        }

        /// <summary>회귀 방지 — 인박스를 열어 둔 사이 벗어난 마우스가 닫을 때 반영되지 않으면 확장이 남는다.</summary>
        [Fact]
        public void ClosingTheInboxRebuildsTheIntentFromThePointer()
        {
            var dock = new DockState();
            dock.SetExpanded(true);   // 열기 전 마우스가 올라와 있던 상태

            // 열려 있는 동안 마우스가 나갔다 — 그 사실은 어디에도 기록되지 않는다
            Apply(dock, dock.Resolve(Signals(inbox: true)));
            Apply(dock, dock.InboxClosed(Signals(mouseOver: false)));

            Assert.False(dock.Expanded);
            Assert.Equal(ViewMode.IdleCompact, dock.Mode);
        }

        /// <summary>인박스가 가리고 있던 알림·미디어는 닫을 때 제 모습으로 돌아온다.</summary>
        [Fact]
        public void ClosingTheInboxRevealsWhatItCovered()
        {
            var dock = new DockState();
            Apply(dock, dock.InboxClosed(Signals(notice: true)));
            Assert.Equal(ViewMode.NotificationCompact, dock.Mode);

            Apply(dock, dock.InboxClosed(Signals(hasMedia: true, mouseOver: true)));
            Assert.Equal(ViewMode.MediaExpanded, dock.Mode);
        }

        [Theory]
        [InlineData(false, false, ViewMode.IdleCompact)]
        [InlineData(false, true, ViewMode.IdleExpanded)]
        [InlineData(true, false, ViewMode.MediaCompact)]
        [InlineData(true, true, ViewMode.MediaExpanded)]
        public void RestingShapeIsDecidedByMediaAndExpanded(bool hasMedia, bool expanded, ViewMode expected)
            => Assert.Equal(expected, DockState.Resting(hasMedia, expanded));
    }
}
