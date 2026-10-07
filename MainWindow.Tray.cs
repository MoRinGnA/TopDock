using System.Windows;
using System.Windows.Interop;
using TopDock.Models;
using TopDock.Services;

namespace TopDock
{
    public partial class MainWindow : Window
    {
        private void InitializeTrayIcon()
        {
            _trayIcon = new System.Windows.Forms.NotifyIcon
            {
                Icon = System.Drawing.SystemIcons.Application,
                Visible = true,
                Text = "TopDock"
            };

            var contextMenu = new System.Windows.Forms.ContextMenuStrip();
            contextMenu.Items.Add("열기/포커스", null, (s, e) =>
            {
                this.Activate();
            });

            contextMenu.Items.Add("설정", null, (s, e) => OpenSettings());

            contextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            contextMenu.Items.Add("종료", null, (s, e) => TrayExit_Click());

            _trayIcon.ContextMenuStrip = contextMenu;
            _trayIcon.DoubleClick += (s, e) => this.Activate();
        }

        private void OpenSettings()
        {
            if (_settingsWindow != null)
            {
                _settingsWindow.Activate();
                return;
            }

            _settingsWindow = new SettingsWindow();
            _settingsWindow.SettingsSaved += (s, e) => ApplySettings();
            _settingsWindow.Closed += (s, e) => _settingsWindow = null;
            _settingsWindow.Show();
        }

        private void AssistantAiButton_Click(object sender, RoutedEventArgs e)
        {
            // 새 지시: 기록을 비우고 얼굴을 기본 상태로 초기화
            if (_assistantBusy) return;
            _assistant.ResetConversation();
            ClearAssistantConversation();
            ShowAssistantFace(Controls.OrbKind.Breathing);
            ActivateSelfAndFocusInput();
        }

        private void ApplySettings()
        {
            var cfg = ConfigService.Current;

            // AI 비서 핫키 등록 상태를 설정과 동기화
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                UnregisterHotKey(hwnd, HOTKEY_ID_ASSISTANT);
                UnregisterHotKey(hwnd, HOTKEY_ID_CLIPBOARD);
                if (cfg.AssistantEnabled)
                {
                    RegisterHotKey(hwnd, HOTKEY_ID_ASSISTANT, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, 0x20);
                }
            }

            UpdatePosition();

            // 배터리 표현은 설정 변경과 무관하게 최신 상태로 갱신
            if (!HasMedia)
            {
                _batteryService.ForceUpdate();
            }
        }

        private void TrayExit_Click()
        {
            _trayIcon!.Visible = false;
            _trayIcon.Dispose();
            System.Windows.Application.Current.Shutdown();
        }

    }
}
