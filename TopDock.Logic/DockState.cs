namespace TopDock.Logic
{
    /// <summary>
    /// 도크 모양을 정하는 데 필요한 입력 한 벌. 창은 이 값만 만들어 주면 되고,
    /// "그래서 어떤 모양이어야 하는가"는 전부 <see cref="DockState"/>가 답한다.
    /// </summary>
    /// <param name="HasMedia">재생 중인 미디어가 있는가.</param>
    /// <param name="VolumeHudActive">볼륨 HUD(링)가 떠 있는 동안인가 — 이때는 크기를 건드리지 않는다.</param>
    /// <param name="NoticeActive">알림이 떠 있는가(계속 떠 있는 sticky 알림 포함).</param>
    /// <param name="AssistantView">비서 확장 화면이거나 작업 중 아일랜드인가.</param>
    /// <param name="MouseOver">마우스가 도크 위에 있는가.</param>
    /// <param name="InboxOpen">알림 기록(인박스)을 사용자가 열어 두었는가.</param>
    public readonly record struct DockSignals(
        bool HasMedia,
        bool VolumeHudActive,
        bool NoticeActive,
        bool AssistantView,
        bool MouseOver,
        bool InboxOpen);

    /// <summary>
    /// 도크가 붙잡고 있는 상태 — 지금 보여 주는 모양과, 열려 있기를 바라는 의도(Expanded).
    ///
    /// WPF를 모른다. 신호를 받아 "다음에 어떤 모양이어야 하는가"까지가 전부라서, 창을 띄우지 않고도
    /// 전환을 테스트할 수 있다(tests/TopDock.Tests).
    ///
    /// 왜 필요한가: 예전에는 이 두 값이 창 클래스의 필드로 흩어져 있었고, 볼륨 HUD가 크기 변경을
    /// 미뤄 둔 사이 마우스가 벗어나면 그 이탈을 되돌리는 코드가 어디에도 없어서 확장된 모양이
    /// 그대로 남았다. 이제 "미뤄 둔 의도를 다시 반영한다"는 길은 VolumeHudExpired 하나뿐이다.
    /// </summary>
    public sealed class DockState
    {
        /// <summary>지금 보여 주는 모양. 뷰가 실제로 바뀐 뒤 창이 이 값을 맞춘다.</summary>
        public ViewMode Mode { get; private set; } = ViewMode.IdleCompact;

        /// <summary>열려 있기를 바라는 의도 — 마우스가 올라와 있거나 사용자가 비서 화면을 연 상태.</summary>
        public bool Expanded { get; private set; }

        /// <summary>뷰가 실제로 바뀌었을 때 상태를 맞춘다.</summary>
        public void Show(ViewMode mode) => Mode = mode;

        /// <summary>의도를 직접 세운다 — 마우스가 아니라 사용자가 연 경우(비서 화면 등).</summary>
        public void SetExpanded(bool expanded) => Expanded = expanded;

        /// <summary>
        /// 마우스가 도크에 올라왔다. 옮겨야 할 모양을, 지금 옮길 수 없다면 null을 돌려준다.
        /// 비서 화면에서는 크기를 바꾸지 않되 들어온 의도는 기록한다(예전 동작 그대로).
        /// </summary>
        public ViewMode? Hover(DockSignals signals)
        {
            // 인박스는 사용자가 연 기록 화면 — 마우스가 스쳐도 크기를 바꾸지 않고 의도도 건드리지 않는다.
            // (닫을 때 InboxClosed가 그때의 마우스 위치로 의도를 다시 세운다.)
            if (signals.InboxOpen) return null;

            Expanded = true;
            if (signals.AssistantView) return null;
            if (signals.VolumeHudActive) return null;
            return Resolve(signals);
        }

        /// <summary>
        /// 마우스가 도크를 벗어났다. 비서 화면은 마우스가 스쳐도 닫히지 않는다 —
        /// 닫기는 ✕·Esc가 맡으므로 여기서는 의도도 건드리지 않는다(예전 동작 그대로).
        /// 볼륨 HUD가 떠 있으면 이탈을 기록만 해 두고, 끝날 때 <see cref="VolumeHudExpired"/>가 반영한다.
        /// </summary>
        public ViewMode? Leave(DockSignals signals)
        {
            if (signals.InboxOpen) return null;
            if (signals.AssistantView) return null;
            Expanded = false;
            if (signals.VolumeHudActive) return null;
            return Resolve(signals);
        }

        /// <summary>
        /// 볼륨 HUD가 끝났다. 그동안 미뤄 둔 마우스 상태로 의도를 다시 맞춘다 —
        /// 이 되돌림이 없으면 HUD를 만지는 사이 벗어난 마우스를 아무도 기억하지 못해 확장이 남는다.
        /// </summary>
        public ViewMode? VolumeHudExpired(DockSignals signals)
        {
            if (signals.InboxOpen) return null;
            if (signals.AssistantView) return null;
            // HUD를 만지는 동안의 의도는 마우스 위치로 다시 세운다 — 남아 있던 의도를 그대로 믿으면
            // (예: 알림이 비서 화면을 잠시 가린 사이) 화면 밖 마우스 때문에 펼쳐진 채로 남는다.
            Expanded = signals.MouseOver;
            return Resolve(signals);
        }

        /// <summary>
        /// 사용자가 인박스를 닫았다. 열려 있는 동안 마우스가 스쳐 나간 일은 아무도 기억하지 못하므로,
        /// 닫는 순간의 마우스 위치로 의도를 다시 세운다 — 이 되돌림이 없으면 화면 밖 마우스 때문에
        /// 펼쳐진 채로 남는다(VolumeHudExpired가 HUD에 대해 하는 일과 같은 이유).
        /// </summary>
        public ViewMode? InboxClosed(DockSignals signals)
        {
            Expanded = signals.MouseOver;
            if (signals.VolumeHudActive) return null;   // 볼륨 HUD가 크기를 쥐고 있으면 끝날 때 반영된다
            return Resolve(signals);
        }

        /// <summary>지금 의도와 신호로 도크가 가져야 할 모양 (미루지 않고 바로 정한다).</summary>
        public ViewMode Resolve(DockSignals signals) => signals.InboxOpen
            ? ViewMode.Inbox
            : signals.NoticeActive
                ? (Expanded ? ViewMode.NotificationExpanded : ViewMode.NotificationCompact)
                : Resting(signals.HasMedia, Expanded);

        /// <summary>
        /// 알림·비서 화면이 아닐 때의 평상시 모양 — 미디어 유무와 확장 여부 둘로만 정해진다.
        /// 이 두 축이 도크 모양의 전부라서, 나머지 전환은 모두 이 함수로 표현된다.
        /// </summary>
        public static ViewMode Resting(bool hasMedia, bool expanded) => hasMedia
            ? (expanded ? ViewMode.MediaExpanded : ViewMode.MediaCompact)
            : (expanded ? ViewMode.IdleExpanded : ViewMode.IdleCompact);
    }
}
