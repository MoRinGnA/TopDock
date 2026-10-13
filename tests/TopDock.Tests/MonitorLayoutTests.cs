using TopDock.Logic;

namespace TopDock.Tests
{
    /// <summary>
    /// 노치가 어느 모니터에 앉을지 정하는 규칙을 화면 없이 검증한다.
    ///
    /// 실제 다중 모니터 배치를 이 기계에서 만들 수 없으므로, 여기서는 합성 배치를 쓴다
    /// (주 모니터를 오른쪽에 둔 배치, 왼쪽으로 뻗은 음수 좌표 배치, 세로로 쌓은 배치).
    /// 창 코드는 이 결과를 그대로 SetWindowPos에 넘기므로, 여기서 증명된 규칙이 곧 앱의 동작이다.
    /// </summary>
    public class MonitorLayoutTests
    {
        private static MonitorRect Monitor(string device, int left, int top, int width, int height, bool primary = false)
            => new(device, left, top, width, height, primary);

        /// <summary>노트북(주) + 오른쪽 외부 모니터 — 흔한 배치.</summary>
        private static MonitorRect[] LaptopAndRight()
        {
            return new[]
            {
                Monitor(@"\\.\DISPLAY1", 0, 0, 1920, 1080, primary: true),
                Monitor(@"\\.\DISPLAY2", 1920, 0, 2560, 1440),
            };
        }

        /// <summary>외부 모니터를 왼쪽에 둔 배치 — 가상 화면 원점이 음수로 간다.</summary>
        private static MonitorRect[] LeftOfPrimary()
        {
            return new[]
            {
                Monitor(@"\\.\DISPLAY1", 0, 0, 1920, 1080, primary: true),
                Monitor(@"\\.\DISPLAY2", -2560, 0, 2560, 1440),
            };
        }

        [Fact]
        public void DefaultsToThePrimaryMonitor()
        {
            var monitors = LaptopAndRight();

            Assert.Equal(@"\\.\DISPLAY1", MonitorLayout.Choose(monitors, null).Device);
            Assert.Equal(@"\\.\DISPLAY1", MonitorLayout.Choose(monitors, "").Device);
            Assert.Equal(@"\\.\DISPLAY1", MonitorLayout.Choose(monitors, "primary").Device);
            Assert.Equal(@"\\.\DISPLAY1", MonitorLayout.Choose(monitors, "  PRIMARY ").Device);
        }

        [Fact]
        public void NamedMonitorWins()
        {
            var chosen = MonitorLayout.Choose(LaptopAndRight(), @"\\.\DISPLAY2");

            Assert.Equal(@"\\.\DISPLAY2", chosen.Device);
            Assert.Equal(1920, chosen.Left);      // 주 모니터 오른쪽에서 시작한다
            Assert.Equal(2560, chosen.Width);
            Assert.False(chosen.IsPrimary);
        }

        /// <summary>장치 이름 비교는 대소문자를 가리지 않는다 — 저장된 이름의 표기가 흔들려도 같은 모니터다.</summary>
        [Fact]
        public void DeviceNameMatchIgnoresCaseAndPadding()
        {
            Assert.Equal(@"\\.\DISPLAY2", MonitorLayout.Choose(LaptopAndRight(), @"  \\.\display2  ").Device);
        }

        /// <summary>주 모니터가 목록의 첫 번째가 아니어도(주 모니터를 오른쪽에 둔 배치) 주 모니터를 고른다.</summary>
        [Fact]
        public void FindsThePrimaryMonitorWhereverItSitsInTheList()
        {
            var monitors = new[]
            {
                Monitor(@"\\.\DISPLAY2", -2560, 0, 2560, 1440),
                Monitor(@"\\.\DISPLAY1", 0, 0, 1920, 1080, primary: true),
            };

            Assert.Equal(@"\\.\DISPLAY1", MonitorLayout.Choose(monitors, "primary").Device);
            Assert.Equal(0, MonitorLayout.Choose(monitors, "primary").Left);
        }

        /// <summary>음수 원점은 그대로 남는다 — 0으로 눌러 두면 노치가 주 화면 왼쪽 끝으로 끌려간다.</summary>
        [Fact]
        public void KeepsNegativeOriginsForMonitorsLeftOfPrimary()
        {
            var chosen = MonitorLayout.Choose(LeftOfPrimary(), @"\\.\DISPLAY2");

            Assert.Equal(-2560, chosen.Left);
            Assert.Equal(0, chosen.Right);
        }

        [Fact]
        public void MissingMonitorFallsBackToPrimaryAndSaysSo()
        {
            var monitors = LaptopAndRight();

            var chosen = MonitorLayout.Choose(monitors, @"\\.\DISPLAY9");

            Assert.Equal(@"\\.\DISPLAY1", chosen.Device);
            Assert.True(MonitorLayout.FellBackToPrimary(monitors, @"\\.\DISPLAY9"));
        }

        /// <summary>물러선 것이 아니면 굳이 알리지 않는다 — 정상 동작에서 로그를 쓰지 않는다.</summary>
        [Fact]
        public void StaysQuietWhenNothingWasMissing()
        {
            var monitors = LaptopAndRight();

            Assert.False(MonitorLayout.FellBackToPrimary(monitors, null));
            Assert.False(MonitorLayout.FellBackToPrimary(monitors, "primary"));
            Assert.False(MonitorLayout.FellBackToPrimary(monitors, @"\\.\DISPLAY2"));
        }

        /// <summary>원격 세션처럼 주 모니터 표시가 없는 배치에서는 첫 번째를 쓴다 (아무 데도 못 앉는 일은 없다).</summary>
        [Fact]
        public void LayoutWithoutPrimaryMarkUsesTheFirstMonitor()
        {
            var monitors = new[] { Monitor(@"\\.\DISPLAY1", 0, 0, 1280, 720) };

            Assert.Equal(@"\\.\DISPLAY1", MonitorLayout.Choose(monitors, "primary").Device);
            Assert.Equal(1280, MonitorLayout.Choose(monitors, "primary").Width);
        }

        /// <summary>모니터가 하나도 없으면 빈 자리 — 창 코드는 이때 아무 일도 하지 않는다.</summary>
        [Fact]
        public void NoMonitorsYieldsTheEmptyRect()
        {
            var chosen = MonitorLayout.Choose(Array.Empty<MonitorRect>(), "primary");

            Assert.True(chosen.IsEmpty);
            Assert.Equal(MonitorRect.Empty, chosen);
            Assert.False(MonitorLayout.FellBackToPrimary(Array.Empty<MonitorRect>(), @"\\.\DISPLAY2"));
        }

        /// <summary>세로로 쌓은 배치 — 위 모니터가 음수 Top을 가질 때도 그 값을 그대로 쓴다.</summary>
        [Fact]
        public void StackedLayoutKeepsYOrigins()
        {
            var monitors = new[]
            {
                Monitor(@"\\.\DISPLAY1", 0, 0, 1920, 1080, primary: true),
                Monitor(@"\\.\DISPLAY2", 0, -1200, 1920, 1200),
            };

            var above = MonitorLayout.Choose(monitors, @"\\.\DISPLAY2");
            Assert.Equal(-1200, above.Top);
            Assert.Equal(0, above.Bottom);
        }
    }
}
