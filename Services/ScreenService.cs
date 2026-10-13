using System.Runtime.InteropServices;
using TopDock.Logic;

namespace TopDock.Services
{
    /// <summary>
    /// 지금 붙어 있는 모니터를 <see cref="MonitorRect"/>(장치 픽셀)로 읽어 준다.
    ///
    /// WPF의 <c>SystemParameters.PrimaryScreenWidth</c>는 주 화면 하나만 말한다 — 여러 모니터를
    /// 고르려면 목록이 필요하고, 목록의 출처는 WinForms의 <c>Screen.AllScreens</c>다(트레이에서 이미
    /// WinForms를 쓰고 있어 새 의존성이 아니다).
    ///
    /// 좌표는 <b>이 프로세스가 보는 공간</b> 그대로다. DPI를 인식하는 프로세스에게는 물리 픽셀이,
    /// 인식하지 못하는 프로세스에게는 가상화된 좌표가 온다. 어느 쪽이든 그 값을 그대로 창에 넘기면
    /// 같은 공간이므로 어긋나지 않는다 — 아래 <see cref="ScaleFor"/>도 같은 규칙을 따른다.
    /// </summary>
    public static class ScreenService
    {
        private const int MdtEffectiveDpi = 0;
        private const uint MonitorDefaultToNearest = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct Point32
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(Point32 point, uint flags);

        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint x, out uint y);

        /// <summary>
        /// 지금 붙어 있는 모니터 전부. 창을 띄우지 않고도 부를 수 있다(설정 창의 목록도 이걸 쓴다).
        ///
        /// 실패하면 빈 목록을 돌려준다 — 모니터를 못 읽었다고 앱이 죽는 것보다, 노치가 주 화면 기본값에
        /// 남는 편이 낫다.
        /// </summary>
        public static IReadOnlyList<MonitorRect> All()
        {
            var monitors = new List<MonitorRect>();
            try
            {
                foreach (System.Windows.Forms.Screen screen in System.Windows.Forms.Screen.AllScreens)
                {
                    var bounds = screen.Bounds;
                    monitors.Add(new MonitorRect(
                        screen.DeviceName,
                        bounds.Left,
                        bounds.Top,
                        bounds.Width,
                        bounds.Height,
                        screen.Primary));
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Monitor enumeration failed: {ex.Message}");
            }
            return monitors;
        }

        /// <summary>
        /// 이 모니터의 배율(96dpi = 1.0). 설정의 상단 여백은 DIP로 적혀 있는데 창은 장치 좌표로 앉으므로,
        /// 여백만큼은 장치 픽셀로 환산해야 배율이 다른 모니터에서도 같은 간격으로 보인다.
        ///
        /// 셀 수 없을 때(구형 원격 세션 등)는 1.0 — 그러면 여백이 장치 픽셀 그대로 쓰인다.
        /// </summary>
        public static double ScaleFor(MonitorRect monitor)
        {
            if (monitor.IsEmpty) return 1.0;
            try
            {
                // MonitorFromPoint는 창 사각형이 아니라 점을 받는다 — 좌상단은 다른 모니터와 맞닿아
                // 경계 모호할 수 있으니 안쪽으로 한 픽셀 들어간 점을 쓴다.
                IntPtr handle = MonitorFromPoint(
                    new Point32 { X = monitor.Left + 1, Y = monitor.Top + 1 }, MonitorDefaultToNearest);
                if (handle == IntPtr.Zero) return 1.0;

                if (GetDpiForMonitor(handle, MdtEffectiveDpi, out uint dpiX, out _) == 0 && dpiX > 0)
                {
                    return dpiX / 96.0;
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Monitor DPI query failed: {ex.Message}");
            }
            return 1.0;
        }

        /// <summary>설정 목록에 쓸 이름 — 사용자는 장치 이름(`\\.\DISPLAY2`)이 아니라 번호로 알아본다.</summary>
        public static string LabelFor(MonitorRect monitor)
        {
            string number = DisplayNumber(monitor.Device);
            string size = $"{monitor.Width}×{monitor.Height}";
            return monitor.IsPrimary
                ? $"디스플레이 {number} · {size}  (주 모니터)"
                : $"디스플레이 {number} · {size}";
        }

        /// <summary>`\\.\DISPLAY2` → `2`. 이름이 예상과 다르면 이름 전체를 그대로 보여 준다.</summary>
        private static string DisplayNumber(string device)
        {
            const string prefix = @"\\.\DISPLAY";
            return device.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(device[prefix.Length..], out int number)
                ? number.ToString()
                : device;
        }
    }
}
