using TopDock.Logic;

namespace TopDock.Tests
{
    /// <summary>
    /// 알림 인박스의 규칙을 창 없이 검증한다. 화면(노치 카드)은 한 번에 한 건만 보여주므로,
    /// "무엇을 몇 번째로 보고 있는가"가 전부 로직이다 — 그 로직이 여기서 증명된다.
    /// </summary>
    public class NotificationInboxTests
    {
        private static readonly DateTime Now = new(2026, 10, 12, 15, 0, 0);

        private static NotificationInbox WithItems(int count)
        {
            var inbox = new NotificationInbox();
            for (int i = 1; i <= count; i++)
            {
                inbox.Add("Slack", $"제목 {i}", $"본문 {i}", Now);
            }
            return inbox;
        }

        [Fact]
        public void NewestComesFirstAndCountsAsUnread()
        {
            var inbox = new NotificationInbox();

            inbox.Add("Slack", "첫 번째", "", Now);
            inbox.Add("Discord", "두 번째", "", Now);

            Assert.Equal(2, inbox.Count);
            Assert.Equal(2, inbox.Unread);
            Assert.Equal("Discord", inbox.Items[0].AppName);
            Assert.Equal("Slack", inbox.Items[1].AppName);
        }

        [Fact]
        public void IdsAreUniqueSoIdenticalNotificationsStayApart()
        {
            var inbox = new NotificationInbox();

            var first = inbox.Add("Slack", "같은 제목", "같은 본문", Now);
            var second = inbox.Add("Slack", "같은 제목", "같은 본문", Now);

            Assert.NotEqual(first.Id, second.Id);
            Assert.True(inbox.Remove(first.Id));
            Assert.Single(inbox.Items);
            Assert.Equal(second.Id, inbox.Items[0].Id);
        }

        [Fact]
        public void OverflowForgetsTheOldest()
        {
            var inbox = WithItems(NotificationInbox.Capacity + 1);

            Assert.Equal(NotificationInbox.Capacity, inbox.Count);
            Assert.Equal($"제목 {NotificationInbox.Capacity + 1}", inbox.Items[0].Title);
            Assert.DoesNotContain(inbox.Items, i => i.Title == "제목 1");
        }

        /// <summary>최신을 보고 있으면 새 알림을 따라가고, 옛 것을 읽는 중이면 읽던 자리에 머문다.</summary>
        [Fact]
        public void ArrivalFollowsTheNewestButLeavesOlderReadingAlone()
        {
            var inbox = WithItems(3);

            inbox.Select(0);
            var newest = inbox.Add("Mail", "새 알림", "", Now);
            Assert.Equal(newest.Id, inbox.Selected?.Id);

            inbox.Select(2);   // 옛 알림을 읽는 중
            var reading = inbox.Selected;
            inbox.Add("Mail", "또 새 알림", "", Now);
            Assert.Equal(reading?.Id, inbox.Selected?.Id);
            Assert.Equal(3, inbox.SelectedIndex);
        }

        [Fact]
        public void SelectionWrapsAround()
        {
            var inbox = WithItems(3);
            inbox.Select(0);

            inbox.Move(-1);
            Assert.Equal(2, inbox.SelectedIndex);   // 위로 한 칸 = 가장 오래된 것으로 돈다

            inbox.Move(1);
            Assert.Equal(0, inbox.SelectedIndex);
        }

        [Fact]
        public void SelectionOnAnEmptyInboxIsNothing()
        {
            var inbox = new NotificationInbox();

            inbox.Move(1);
            Assert.Equal(-1, inbox.SelectedIndex);
            Assert.Null(inbox.Selected);

            inbox.Select(0);
            Assert.Equal(-1, inbox.SelectedIndex);
        }

        [Fact]
        public void RemovingWhatIsBeingReadMovesToTheNeighbour()
        {
            var inbox = WithItems(3);
            inbox.Select(1);   // 가운데를 읽는 중
            var neighbour = inbox.Items[2];

            Assert.True(inbox.Remove(inbox.Selected!.Id));

            Assert.Equal(2, inbox.Count);
            Assert.Equal(neighbour.Id, inbox.Selected?.Id);
            Assert.Equal(1, inbox.SelectedIndex);
        }

        [Fact]
        public void RemovingEverythingLeavesNothingSelected()
        {
            var inbox = WithItems(2);

            Assert.True(inbox.Remove(inbox.Items[0].Id));
            Assert.True(inbox.Remove(inbox.Items[0].Id));

            Assert.True(inbox.IsEmpty);
            Assert.Equal(-1, inbox.SelectedIndex);
            Assert.Null(inbox.Selected);
            Assert.False(inbox.Remove(999));   // 없는 번호는 아무 일도 하지 않는다
        }

        [Fact]
        public void OpeningClearsTheUnreadCount()
        {
            var inbox = WithItems(2);
            Assert.Equal(2, inbox.Unread);

            inbox.MarkAllRead();
            Assert.Equal(0, inbox.Unread);
            Assert.Equal(2, inbox.Count);   // 읽었다고 지워지지는 않는다
        }

        [Fact]
        public void ClearEmptiesEverything()
        {
            var inbox = WithItems(3);
            inbox.Select(1);

            inbox.Clear();

            Assert.True(inbox.IsEmpty);
            Assert.Equal(-1, inbox.SelectedIndex);
            Assert.Equal(0, inbox.Unread);
        }

        [Theory]
        [InlineData(0, 0, 0, 0, "방금")]
        [InlineData(0, 0, 0, 30, "방금")]
        [InlineData(0, 0, 5, 0, "5분 전")]
        [InlineData(0, 0, 59, 0, "59분 전")]
        [InlineData(0, 3, 0, 0, "3시간 전")]
        [InlineData(0, 23, 0, 0, "23시간 전")]
        [InlineData(1, 0, 0, 0, "어제")]
        [InlineData(3, 0, 0, 0, "10월 9일")]
        public void AgeIsWrittenTheWayTheCardShowsIt(int days, int hours, int minutes, int seconds, string expected)
        {
            DateTime at = Now - new TimeSpan(days, hours, minutes, seconds);

            Assert.Equal(expected, NotificationInbox.DescribeAge(at, Now));
        }

        /// <summary>시계가 뒤로 가도 "방금"보다 미래를 말하지 않는다.</summary>
        [Fact]
        public void FutureTimestampsReadAsJustNow()
            => Assert.Equal("방금", NotificationInbox.DescribeAge(Now.AddMinutes(5), Now));
    }
}
