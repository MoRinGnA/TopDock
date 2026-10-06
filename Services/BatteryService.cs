using System;
using System.Windows.Forms;
using System.Windows.Threading;

namespace TopDock.Services
{
    public class BatteryStatusArgs : EventArgs
    {
        public float BatteryPercent { get; }
        public bool IsCharging { get; }

        public BatteryStatusArgs(float percent, bool isCharging)
        {
            BatteryPercent = percent;
            IsCharging = isCharging;
        }
    }

    public class BatteryService : IDisposable
    {
        private readonly DispatcherTimer _timer;
        private float _lastPercent = -1;
        private bool _lastChargingStatus = false;

        public event EventHandler<BatteryStatusArgs>? BatteryStatusChanged;

        public BatteryService()
        {
            _timer = new DispatcherTimer
            {
                // 전원 변경은 Windows 메시지로 즉시 받고, 예외 상황 복구용으로만 드물게 확인한다.
                Interval = TimeSpan.FromSeconds(60)
            };
            _timer.Tick += Timer_Tick;
        }

        public void Start()
        {
            _timer.Start();
            CheckBatteryStatus(); // Initial check
        }

        public void Stop()
        {
            _timer.Stop();
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            CheckBatteryStatus();
        }

        private void CheckBatteryStatus()
        {
            var powerStatus = SystemInformation.PowerStatus;

            // 배터리가 없는 데스크톱은 PowerLineStatus가 항상 Online이라 '충전 중'으로 오인된다.
            // 그대로 두면 노치에 초록 라이트가 영구히 켜지므로 배터리 유무를 먼저 확인한다.
            bool hasBattery = (powerStatus.BatteryChargeStatus & BatteryChargeStatus.NoSystemBattery) == 0;

            float currentPercent = hasBattery ? powerStatus.BatteryLifePercent : 1.0f;
            bool currentCharging = hasBattery && powerStatus.PowerLineStatus == PowerLineStatus.Online;

            // BatteryLifePercent is 1.0 = 100%, 0.5 = 50%. Sometimes returns 255 if unknown.
            if (currentPercent > 1.0f) currentPercent = 1.0f; 

            if (Math.Abs(_lastPercent - currentPercent) > 0.001 || _lastChargingStatus != currentCharging)
            {
                _lastPercent = currentPercent;
                _lastChargingStatus = currentCharging;
                BatteryStatusChanged?.Invoke(this, new BatteryStatusArgs(currentPercent, currentCharging));
            }
        }

        /// <summary>전원 상태 변경 메시지를 받은 즉시 현재 배터리 상태를 다시 읽는다.</summary>
        public void RefreshNow()
        {
            CheckBatteryStatus();
        }

        public void ForceUpdate()
        {
            if (_lastPercent >= 0)
            {
                BatteryStatusChanged?.Invoke(this, new BatteryStatusArgs(_lastPercent, _lastChargingStatus));
            }
        }

        public void Dispose()
        {
            _timer.Stop();
        }
    }
}
