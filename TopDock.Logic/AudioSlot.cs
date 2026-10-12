namespace TopDock.Logic
{
    /// <summary>
    /// 노치 오른쪽 끝, 그 한 자리를 누가 쓰는가.
    ///
    /// 원래 이 자리는 볼륨을 만질 때만 뜨는 링의 것이었다. 이제 소리가 흐르는 동안 링이 상주하고,
    /// 그 위에 배터리·AI 상태 점이 뜬다. 자리는 하나인데 주인이 여럿이면 규칙이 필요하다 —
    /// 창 안에 if를 흩어 두면 화면마다 다르게 굴러간다.
    ///
    /// 규칙은 두 줄이다:
    ///   · 링은 소리 위에 있다 (재생 중이면 늘, 볼륨을 만지는 동안에도)
    ///   · 계기는 상태를 이기지 못한다 (점이 떠 있으면 바늘이 비켜선다)
    /// 두 번째 줄이 중요한 이유: 점의 후광(12px)이 링의 빈 원(지름 18px)을 거의 다 채워
    /// 같이 그리면 겹친다. 물리적으로 자리가 없으니 규칙으로도 없앤다.
    /// </summary>
    public static class AudioSlot
    {
        /// <summary>링이 떠 있는가 — 소리가 흐르는 동안, 그리고 볼륨을 만지는 동안.</summary>
        public static bool ShowRing(bool playing, bool volumeHudActive) => playing || volumeHudActive;

        /// <summary>바늘이 보이는가 — 재생 중이고 값이 있고, 상태 점이 자리를 쓰지 않을 때만.</summary>
        public static bool ShowBars(bool playing, bool hasLevels, bool statusDotVisible)
            => playing && hasLevels && !statusDotVisible;
    }
}
