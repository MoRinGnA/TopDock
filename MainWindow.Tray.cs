using System.Windows;
using System.Windows.Interop;
using TopDock.Models;
using TopDock.Services;

namespace TopDock
{
    public partial class MainWindow : Window
    {
        /// <summary>트레이 메뉴의 알림 기록 항목 — 안 읽은 개수를 여기에 붙인다.</summary>
        private System.Windows.Forms.ToolStripMenuItem? _trayInboxItem;

        /// <summary>트레이에 걸린 그림. NotifyIcon은 자기 Icon을 정리하지 않으므로 따로 들고 있다가 걷는다.</summary>
        private System.Drawing.Icon? _trayIconImage;

        /// <summary>
        /// 트레이 아이콘 — 노치 실루엣 그대로(Assets/topdock.ico, 실행 파일 아이콘과 같은 그림).
        ///
        /// 예전에는 윈도우 기본 아이콘을 썼다: 트레이에 남의 앱이 하나 늘어난 것처럼 보였고,
        /// 그 아이콘은 이 앱이 무엇인지 아무것도 말하지 않았다. 리소스를 못 읽으면(패키징 사고)
        /// 기본 아이콘으로 물러선다 — 트레이 아이콘이 없어서 창을 다시 열 수 없게 되는 일은 없다.
        /// </summary>
        private static System.Drawing.Icon LoadAppIcon()
        {
            try
            {
                var resource = System.Windows.Application.GetResourceStream(
                    new Uri("pack://application:,,,/Assets/topdock.ico"));
                if (resource != null)
                {
                    using var stream = resource.Stream;
                    // 16px는 트레이의 실제 크기다 — 아이콘 파일의 여러 크기 중 가장 가까운 것을 고른다
                    return new System.Drawing.Icon(stream, new System.Drawing.Size(16, 16));
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Tray icon load failed: {ex.Message}");
            }
            // SystemIcons의 아이콘은 공용이라 우리가 정리하면 안 된다 — 복제해서 우리 것으로 만든다
            return (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
        }

        private void InitializeTrayIcon()
        {
            _trayIconImage = LoadAppIcon();
            _trayIcon = new System.Windows.Forms.NotifyIcon
            {
                Icon = _trayIconImage,
                Visible = true,
                Text = "TopDock"
            };

            var contextMenu = new System.Windows.Forms.ContextMenuStrip();
            contextMenu.Items.Add("열기/포커스", null, (s, e) =>
            {
                this.Activate();
            });

            contextMenu.Items.Add("설정", null, (s, e) => OpenSettings());

            _trayInboxItem = new System.Windows.Forms.ToolStripMenuItem("알림 기록", null,
                (s, e) => RunOnUi(OpenNotificationInbox));
            contextMenu.Items.Add(_trayInboxItem);

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

            // 핫키를 지금 설정에 맞게 다시 건다 — 형제를 함께 내렸다 함께 올려야 한다.
            // (예전에는 클립보드 핫키를 내리고 비서 핫키만 다시 걸어서, 설정을 한 번 적용하면
            //  Ctrl+Shift+V가 영영 듣지 않았다. 핫키는 아이디별로 한 번만 등록되므로
            //  되올리지 않은 쪽은 그대로 죽는다.)
            // 그리고 걸어 놓기만 하고 끝내지 않는다 — 시작 상태와 견주어 죽은 자리를 로그에 남긴다.
            // 이 회귀는 화면에 아무 흔적을 남기지 않아서, 로그가 없으면 아무도 모른다.
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                var after = RegisterGlobalHotkeys(hwnd);
                LogHotkeyAudit(_hotkeyAtStart, after, ConfigService.Current.AssistantEnabled);
            }

            UpdatePosition();

            // 배터리 표현은 설정 변경과 무관하게 최신 상태로 갱신
            if (!HasMedia)
            {
                _batteryService.ForceUpdate();
            }
        }

        /// <summary>
        /// 트레이 메뉴에 안 읽은 개수를 붙인다 — 지나간 알림이 있다는 사실을 노치를 보지 않고도
        /// 알 수 있는 자리는 여기뿐이다(노치에 배지를 하나 더 얹으면 화면이 시끄러워진다).
        /// </summary>
        private void UpdateTrayInboxLabel()
        {
            if (_trayInboxItem == null) return;
            _trayInboxItem.Text = _inbox.Unread > 0 ? $"알림 기록 ({_inbox.Unread})" : "알림 기록";
        }

        private void TrayExit_Click()
        {
            // 정리는 MainWindow_Closed가 한 곳에서 맡는다 — 여기서는 아이콘만 먼저 숨긴다
            // (트레이에 유령 아이콘이 남는 시간을 줄이기 위해)
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
            }
            System.Windows.Application.Current.Shutdown();
        }

    }
}
