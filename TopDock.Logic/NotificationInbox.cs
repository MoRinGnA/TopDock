using System.Collections.Generic;

namespace TopDock.Logic
{
    /// <summary>인박스에 남는 알림 한 건 — 노치 화면에서 걷힌 뒤에도 남는 기록.</summary>
    /// <param name="Id">기록 안에서 유일한 번호. 같은 앱의 같은 제목이라도 다른 알림은 다른 번호를 갖는다.</param>
    public sealed record InboxItem(long Id, string AppName, string Title, string Body, DateTime Timestamp);

    /// <summary>
    /// 놓친 알림을 담아 두는 기록. WPF를 모른다 — 몇 개를 기억하고, 지금 몇 번째를 보고 있고,
    /// 몇 개가 아직 안 읽혔는지까지가 전부라서 창을 띄우지 않고도 검증할 수 있다(tests/TopDock.Tests).
    ///
    /// 왜 필요한가: 알림은 5초 뒤 스스로 걷히고, 노치 알림 타이머는 하나뿐이라 새 알림이 오면
    /// 앞의 것을 밀어낸다. 잠깐 자리를 비운 사이에 온 알림은 그대로 사라졌다 — 이제 노치에서 다시 볼 수 있다.
    /// </summary>
    public sealed class NotificationInbox
    {
        /// <summary>기억하는 최대 개수 — 넘치면 가장 오래된 것부터 잊는다.</summary>
        public const int Capacity = 40;

        private readonly List<InboxItem> _items = new();
        private long _nextId = 1;

        /// <summary>최신이 앞(0번)에 온다 — 인박스를 열면 언제나 최신부터 본다.</summary>
        public IReadOnlyList<InboxItem> Items => _items;

        public int Count => _items.Count;

        public bool IsEmpty => _items.Count == 0;

        /// <summary>아직 펼쳐 보지 않은 알림 수 — 인박스를 열면 0이 된다.</summary>
        public int Unread { get; private set; }

        /// <summary>지금 화면에 앉힌 자리 — -1이면 아무것도 고르지 않았다.</summary>
        public int SelectedIndex { get; private set; } = -1;

        public InboxItem? Selected =>
            SelectedIndex >= 0 && SelectedIndex < _items.Count ? _items[SelectedIndex] : null;

        /// <summary>알림 한 건을 기록한다. 노치에 띄웠든 아니든 도착한 것은 모두 여기로 온다.</summary>
        public InboxItem Add(string appName, string title, string body, DateTime? at = null)
        {
            var item = new InboxItem(_nextId++, appName, title, body, at ?? DateTime.Now);
            _items.Insert(0, item);
            Unread++;

            // 읽고 있던 항목에 머문다 — 새 알림이 읽던 글을 밀어내면 열어 둔 화면이 제멋대로 움직인다.
            // 다만 최신(0번)을 보고 있었으면 새 알림을 따라간다: 켜 둔 사람은 최신을 기다리는 사람이다.
            if (SelectedIndex > 0) SelectedIndex++;

            if (_items.Count > Capacity)
            {
                _items.RemoveAt(_items.Count - 1);
                if (SelectedIndex >= _items.Count) SelectedIndex = _items.Count - 1;
            }

            return item;
        }

        /// <summary>자리를 고른다 — 범위를 넘으면 가까운 끝으로 당긴다. 빈 기록에서는 아무것도 고르지 않는다.</summary>
        public void Select(int index) =>
            SelectedIndex = _items.Count == 0 ? -1 : Math.Clamp(index, 0, _items.Count - 1);

        /// <summary>
        /// 한 칸 옮긴다 — 끝에서 반대편으로 돈다. 알림은 많지 않아 순환이 곧바로 최신까지 데려간다.
        /// </summary>
        public void Move(int delta)
        {
            if (_items.Count == 0)
            {
                SelectedIndex = -1;
                return;
            }
            if (SelectedIndex < 0)
            {
                SelectedIndex = 0;
                return;
            }

            int count = _items.Count;
            SelectedIndex = ((SelectedIndex + delta) % count + count) % count;
        }

        /// <summary>한 건을 지운다 — 보고 있던 자리는 살아남은 이웃으로 옮겨간다.</summary>
        public bool Remove(long id)
        {
            int index = _items.FindIndex(i => i.Id == id);
            if (index < 0) return false;

            _items.RemoveAt(index);

            if (_items.Count == 0)
            {
                SelectedIndex = -1;
                return true;
            }

            if (SelectedIndex > index) SelectedIndex--;
            else if (SelectedIndex == index) SelectedIndex = Math.Min(index, _items.Count - 1);
            return true;
        }

        /// <summary>전부 본 것으로 친다 — 트레이에 붙는 안 읽은 개수가 사라지는 자리.</summary>
        public void MarkAllRead() => Unread = 0;

        public void Clear()
        {
            _items.Clear();
            SelectedIndex = -1;
            Unread = 0;
        }

        /// <summary>
        /// 얼마나 지난 알림인지. 인박스가 시간을 말하는 유일한 방법이라 규칙을 여기서 정한다 —
        /// 화면에는 글자만 나가므로 이 문자열이 곧 표현이다.
        /// </summary>
        public static string DescribeAge(DateTime at, DateTime now)
        {
            TimeSpan age = now - at;
            if (age < TimeSpan.Zero) age = TimeSpan.Zero;   // 시계가 뒤로 간 경우까지 방어

            if (age < TimeSpan.FromMinutes(1)) return "방금";
            if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes}분 전";
            if (age < TimeSpan.FromDays(1)) return $"{(int)age.TotalHours}시간 전";
            if (age < TimeSpan.FromDays(2)) return "어제";
            return $"{at.Month}월 {at.Day}일";
        }
    }
}
