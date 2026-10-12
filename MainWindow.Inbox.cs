using System;
using System.Windows;
using TopDock.Logic;
using TopDock.Services;

namespace TopDock
{
    public partial class MainWindow : Window
    {
        // ── 알림 인박스 (Ctrl+Shift+N) ──
        // 노치에 뜬 알림은 5초 뒤 스스로 걷히고, 노치 알림 타이머는 하나뿐이라 새 알림이 앞의 것을
        // 밀어낸다. 그렇게 지나간 알림을 다시 보는 화면 — 기록은 NotificationInbox가 들고 있고,
        // 여기서는 "지금 몇 번째를 카드에 앉힐까"만 정한다.

        /// <summary>화면에서 걷힌 알림의 기록. 노치 표시 여부와 무관하게 모든 알림이 여기로 온다.</summary>
        private readonly NotificationInbox _inbox = new();

        private bool _inboxOpen;

        /// <summary>인박스 열기/닫기 — 단축키·트레이가 공유하는 입구.</summary>
        private void ToggleNotificationInbox()
        {
            if (_inboxOpen) CloseNotificationInbox();
            else OpenNotificationInbox();
        }

        private void OpenNotificationInbox()
        {
            if (_inboxOpen || _inbox.IsEmpty) return;

            // 비서와 대화·작업 중에는 화면을 빼앗지 않는다 — 알림이 비서 화면을 건너뛰는 것과 같은 규칙
            if (_currentViewMode is ViewMode.Assistant or ViewMode.AssistantCompact) return;

            // 기록 패널도 노치 아래에 내려온다 — 두 화면이 겹치지 않게 하나만 남긴다
            if (_clipboardHistoryOpen) HideClipboardHistoryPanel();

            _inboxOpen = true;
            _inbox.MarkAllRead();   // 열었으면 본 것이다 — 트레이에 붙던 안 읽은 개수가 여기서 사라진다
            _inbox.Select(0);       // 언제나 최신부터

            Log.Info($"Inbox opened: {_inbox.Count} item(s) kept");
            UpdateTrayInboxLabel();
            RenderInbox();
            ApplyMode(_dock.Resolve(Signals));
            PlayContentSettle(InboxItemBlock, null, InboxItemSlide);

            // 노치는 스스로 활성화되지 않는다 — ↑↓·Delete가 듣도록 포커스를 가져온다.
            // (휠·클릭은 포커스 없이도 이미 넘어간다.)
            try { Activate(); } catch { }
        }

        private void CloseNotificationInbox()
        {
            if (!_inboxOpen) return;

            _inboxOpen = false;
            _inbox.MarkAllRead();
            Log.Info("Inbox closed");
            UpdateTrayInboxLabel();

            // 열려 있는 동안 마우스가 스쳐 나갔을 수 있다 — 닫는 순간의 마우스 위치로 의도를 다시 세운다
            ApplyMode(_dock.InboxClosed(Signals));
        }

        /// <summary>지금 고른 알림 하나를 카드에 앉힌다 — 노치 크기에서는 목록보다 한 건씩이 읽힌다.</summary>
        private void RenderInbox()
        {
            if (_inbox.IsEmpty)
            {
                CloseNotificationInbox();   // 마지막 항목까지 지웠다
                return;
            }

            InboxItem item = _inbox.Selected ?? _inbox.Items[0];
            Log.Info($"Inbox card {_inbox.SelectedIndex + 1}/{_inbox.Count}: {item.AppName} · {item.Title}");
            InboxChip.Background = NoticeInfoBrush;
            InboxChipText.Text = NoticeIdentity.Monogram(item.AppName);
            InboxAppText.Text = item.AppName;
            InboxAgeText.Text = NotificationInbox.DescribeAge(item.Timestamp, DateTime.Now);
            InboxTitleText.Text = item.Title;
            InboxBodyText.Text = item.Body;
            InboxCounterText.Text = $"{_inbox.SelectedIndex + 1} / {_inbox.Count}";
        }

        /// <summary>↑↓·휠·클릭이 모두 이리로 온다 — 어느 손짓이든 "다음 알림"이다.</summary>
        private void MoveInboxSelection(int delta)
        {
            if (!_inboxOpen) return;

            _inbox.Move(delta);
            RenderInbox();
            SlideInboxItem(delta);
        }

        private void RemoveSelectedFromInbox()
        {
            if (!_inboxOpen || _inbox.Selected is not { } item) return;

            _inbox.Remove(item.Id);
            RenderInbox();   // 하나뿐이었다면 여기서 닫힌다
            SlideInboxItem(1);   // 지운 자리로 이웃이 올라온다
        }

        /// <summary>
        /// 넘어간 방향대로 내용이 미끄러진다 — 옛 알림으로 가면 아래에서 올라오고, 최신으로 가면
        /// 위에서 내려온다. 글자만 갈아끼우면 인박스가 아니라 라벨이 바뀐 것처럼 보인다.
        /// </summary>
        private void SlideInboxItem(int delta)
        {
            if (!_inboxOpen) return;
            PlayContentSlide(InboxItemBlock, InboxItemSlide, delta);
        }

        /// <summary>알림 한 건을 기록에 남긴다 — 노치에 띄웠든 아니든 도착한 것은 모두 남는다.</summary>
        private void RecordNotification(string appName, string title, string body)
        {
            _inbox.Add(appName, title, body);
            UpdateTrayInboxLabel();

            // 열어 둔 채 도착하면 카드가 바로 갱신된다(읽던 항목이 있으면 그대로 — NotificationInbox가 안다).
            // 노치 알림은 인박스에 자리를 내주므로, 왔다는 사실은 가장자리 빛이 대신 말해 준다.
            if (_inboxOpen)
            {
                RenderInbox();
                FlashNotchEdge();
            }
        }

        private void InboxClearButton_Click(object sender, RoutedEventArgs e)
        {
            ClearInbox();
            e.Handled = true;
        }

        private void ClearInbox()
        {
            _inbox.Clear();
            Log.Info("Inbox cleared");
            UpdateTrayInboxLabel();

            // 볼 것이 없으면 열어 둘 이유도 없다
            if (_inboxOpen) CloseNotificationInbox();
        }

        private void InboxCloseButton_Click(object sender, RoutedEventArgs e)
        {
            CloseNotificationInbox();
            e.Handled = true;
        }
    }
}
