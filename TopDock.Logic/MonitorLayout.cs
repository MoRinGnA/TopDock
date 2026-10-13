namespace TopDock.Logic
{
    /// <summary>
    /// 모니터 하나의 사각형. WPF를 모른다 — 창을 띄우지 않고 배치 결정을 검증하기 위해 여기서는 숫자만 다룬다.
    ///
    /// 좌표는 <b>장치 픽셀</b>이고, 원점은 가상 화면의 좌상단이다. 주 모니터가 왼쪽 위에 있지 않은
    /// 배치(주 모니터를 오른쪽에 둔 경우)에서는 Left/Top이 음수일 수 있다 — 그 음수를 그대로
    /// 창 위치로 넘겨야 노치가 제자리에 앉는다.
    /// </summary>
    public readonly record struct MonitorRect(string Device, int Left, int Top, int Width, int Height, bool IsPrimary)
    {
        public int Right => Left + Width;
        public int Bottom => Top + Height;

        /// <summary>모니터가 하나도 없을 때(원격 접속 직후 등)의 빈 자리 — 창은 아무 데도 앉지 않고 물러선다.</summary>
        public static MonitorRect Empty => new(string.Empty, 0, 0, 0, 0, false);

        public bool IsEmpty => Width <= 0 || Height <= 0;
    }

    /// <summary>
    /// 노치가 어느 모니터에 앉을지 정하는 규칙. 설정이 고른 모니터를 따르되, 그 모니터가 사라졌으면
    /// 조용히 주 모니터로 물러선다 — 노치 앱에게 "화면 밖에서 사라짐"보다 나쁜 결과는 없기 때문이다.
    ///
    /// 기존에는 창이 `SystemParameters.PrimaryScreenWidth`와 `Left = 0`으로 주 화면에 못 박혀 있었다.
    /// 그 둘은 주 모니터가 (0,0)에 있을 때만 맞는 값이라, 모니터를 고를 수 있게 하는 것과 별개로
    /// 주 모니터를 오른쪽에 둔 배치에서도 노치가 엉뚱한 화면에 앉았다.
    /// </summary>
    public static class MonitorLayout
    {
        /// <summary>설정에서 '주 모니터'를 가리키는 낱말 — 장치 이름이 아니라 이 값으로 적는다.</summary>
        public const string Primary = "primary";

        /// <summary>
        /// 노치를 앉힐 모니터 하나를 고른다.
        ///
        /// 순서는 이렇다: 설정이 장치 이름을 가리키고 그 모니터가 지금 붙어 있으면 그것, 아니면 주 모니터,
        /// 주 모니터 표시가 없는 배치(원격 세션 등)에서는 첫 번째. 모니터가 하나도 없으면 빈 자리를 돌려준다.
        /// </summary>
        public static MonitorRect Choose(IReadOnlyList<MonitorRect> monitors, string? preference)
        {
            if (monitors.Count == 0) return MonitorRect.Empty;

            string wanted = (preference ?? string.Empty).Trim();
            if (wanted.Length > 0 && !IsPrimaryPreference(wanted))
            {
                foreach (MonitorRect monitor in monitors)
                {
                    if (string.Equals(monitor.Device, wanted, StringComparison.OrdinalIgnoreCase)) return monitor;
                }
                // 설정이 가리키는 모니터가 없다 — 아래에서 주 모니터로 물러선다.
            }

            return PrimaryOf(monitors);
        }

        /// <summary>주 모니터 — 표시가 없으면 첫 번째. (표시가 둘 이상이면 첫 번째 주 모니터를 믿는다.)</summary>
        public static MonitorRect PrimaryOf(IReadOnlyList<MonitorRect> monitors)
        {
            if (monitors.Count == 0) return MonitorRect.Empty;

            foreach (MonitorRect monitor in monitors)
            {
                if (monitor.IsPrimary) return monitor;
            }
            return monitors[0];
        }

        /// <summary>
        /// 설정이 가리킨 모니터가 지금 없어서 주 모니터로 물러섰는가 — 물러선 사실은 로그로 한 번 알린다.
        /// (모니터를 뽑은 채로 앱을 켰을 때, 노치가 왜 다른 화면에 떴는지 사용자가 알 길이 없으면 안 된다.)
        /// </summary>
        public static bool FellBackToPrimary(IReadOnlyList<MonitorRect> monitors, string? preference)
        {
            string wanted = (preference ?? string.Empty).Trim();
            if (wanted.Length == 0 || IsPrimaryPreference(wanted)) return false;
            if (monitors.Count == 0) return false;

            foreach (MonitorRect monitor in monitors)
            {
                if (string.Equals(monitor.Device, wanted, StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }

        /// <summary>이 설정값이 '주 모니터'를 뜻하는가. 빈 값도 주 모니터로 본다(옛 설정 파일에 필드가 없을 때).</summary>
        public static bool IsPrimaryPreference(string? preference)
        {
            string value = (preference ?? string.Empty).Trim();
            return value.Length == 0 || string.Equals(value, Primary, StringComparison.OrdinalIgnoreCase);
        }
    }
}
