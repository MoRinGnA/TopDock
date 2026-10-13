using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TopDock.Models;
using TopDock.Services;

namespace TopDock
{
    public partial class SettingsWindow : Window
    {
        /// <summary>적용 후 MainWindow가 설정을 다시 적용할 수 있도록 알린다.</summary>
        public event System.EventHandler? SettingsSaved;

        /// <summary>모니터 목록의 한 줄 — ComboBox에는 라벨이 보이고, 설정에는 값(장치 이름)이 저장된다.</summary>
        private sealed record MonitorChoice(string Value, string Label)
        {
            public override string ToString() => Label;
        }

        public SettingsWindow()
        {
            InitializeComponent();
            LoadCurrentSettings();
        }

        private void LoadCurrentSettings()
        {
            var cfg = ConfigService.Current;

            ShowNotificationsCheck.IsChecked = cfg.ShowNotifications;
            ShowClipboardToastCheck.IsChecked = cfg.ShowClipboardToast;

            TopMarginSlider.Value = cfg.TopMargin;
            TopMarginValueText.Text = ((int)cfg.TopMargin).ToString();

            LoadDockScreenOptions(cfg.DockScreen);

            ApiKeyBox.Password = cfg.YouTubeApiKey;

            AssistantEnabledCheck.IsChecked = cfg.AssistantEnabled;
            AiProviderCombo.SelectedIndex = cfg.AiProvider switch
            {
                "gemini" => 1,
                "openrouter" => 2,
                "upstage" => 3,
                "ollama" => 4,
                _ => 0 // llm7
            };
            AiApiKeyBox.Password = cfg.AiApiKey;
            AiModelBox.Text = cfg.AiModel;
            AssistantAllowedAppsBox.Text = string.Join(Environment.NewLine, cfg.AssistantAllowedApps);
            UpdateAiProviderUi();
        }

        private static string ProviderFromIndex(int index) => index switch
        {
            1 => "gemini",
            2 => "openrouter",
            3 => "upstage",
            4 => "ollama",
            _ => "llm7"
        };

        private void UpdateAiProviderUi()
        {
            string provider = ProviderFromIndex(AiProviderCombo.SelectedIndex);
            bool needsKey = provider is "gemini" or "openrouter" or "upstage";

            AiProviderHintText.Text = provider switch
            {
                "llm7" => "API 키 없이 바로 사용됩니다 (무료, 분당 30회).",
                "gemini" => "aistudio.google.com에서 무료 키 발급 가능. 모델 칸은 비워두면 gemini-3.5-flash-lite를 씁니다 (무료 한도가 모델마다 달라, 부족하면 자동으로 다른 모델로 넘어갑니다).",
                "openrouter" => "openrouter.ai에서 무료 키 발급 가능 (무료 모델 하루 50회).",
                "upstage" => "console.upstage.ai에서 유료 키를 입력하세요.",
                "ollama" => "ollama 실행 중이어야 합니다. 예: ollama pull qwen3:4b",
                _ => string.Empty
            };
            AiApiKeyLabel.Visibility = needsKey ? Visibility.Visible : Visibility.Collapsed;
            AiApiKeyBox.Visibility = needsKey ? Visibility.Visible : Visibility.Collapsed;
        }

        private void AiProvider_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (AiProviderHintText != null)
            {
                UpdateAiProviderUi();
            }
        }

        private void AssistantEnabled_Changed(object sender, RoutedEventArgs e)
        {
            // UI만 토글; 실제 저장은 Apply_Click에서
        }

        private void TopMarginSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TopMarginValueText != null)
            {
                TopMarginValueText.Text = ((int)e.NewValue).ToString();
            }
        }

        private void SaveConfigFromUi()
        {
            var cfg = ConfigService.Current;

            cfg.ShowNotifications = ShowNotificationsCheck.IsChecked == true;
            cfg.ShowClipboardToast = ShowClipboardToastCheck.IsChecked == true;

            cfg.TopMargin = (int)TopMarginSlider.Value;
            cfg.DockScreen = (DockScreenCombo.SelectedItem as MonitorChoice)?.Value ?? MonitorLayout.Primary;

            cfg.YouTubeApiKey = ApiKeyBox.Password.Trim();

            cfg.AssistantEnabled = AssistantEnabledCheck.IsChecked == true;
            cfg.AiProvider = ProviderFromIndex(AiProviderCombo.SelectedIndex);
            cfg.AiApiKey = AiApiKeyBox.Password.Trim();
            cfg.AiModel = AiModelBox.Text.Trim();
            cfg.AssistantAllowedApps = ParseAllowedApps(AssistantAllowedAppsBox.Text);

            ConfigService.Save();
        }

        /// <summary>줄바꿈·쉼표로 구분된 앱 목록을 정리한다. 빈 줄은 버리고 중복은 하나만 남긴다.</summary>
        private static List<string> ParseAllowedApps(string raw)
        {
            var result = new List<string>();
            foreach (string part in raw.Split(new[] { '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string app = part.Trim();
                if (app.Length == 0) continue;
                if (!result.Exists(x => string.Equals(x, app, StringComparison.OrdinalIgnoreCase))) result.Add(app);
            }
            return result;
        }

        /// <summary>
        /// 노치를 앉힐 모니터 목록을 지금 붙어 있는 화면으로 채운다.
        ///
        /// 첫 줄('주 모니터')은 장치 이름이 아니라 "주 화면을 따라간다"는 뜻이다 — 사용자가 주 디스플레이를
        /// 바꾸면 노치도 따라가야 하므로, 특정 모니터를 콕 집는 것과 구분해 둔다.
        /// 저장된 모니터가 지금 없으면(뽑은 채로 설정을 열었을 때) 목록에 없다 — 그때는 '주 모니터'를
        /// 기본으로 보여 준다. 창은 어차피 그 모니터로 물러선다.
        /// </summary>
        private void LoadDockScreenOptions(string selected)
        {
            DockScreenCombo.Items.Clear();
            DockScreenCombo.Items.Add(new MonitorChoice(MonitorLayout.Primary, "주 모니터 (자동)"));

            int selectIndex = 0;
            foreach (MonitorRect monitor in ScreenService.All())
            {
                DockScreenCombo.Items.Add(new MonitorChoice(monitor.Device, ScreenService.LabelFor(monitor)));
                if (string.Equals(monitor.Device, (selected ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    selectIndex = DockScreenCombo.Items.Count - 1;
                }
            }
            DockScreenCombo.SelectedIndex = selectIndex;
        }

        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            // 6번 피드백: 적용 후에도 창을 닫지 않는다
            SaveConfigFromUi();
            SettingsSaved?.Invoke(this, System.EventArgs.Empty);
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                try { DragMove(); } catch { }
            }
        }
    }
}
