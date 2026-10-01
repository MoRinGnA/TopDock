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
                "gemini" => "aistudio.google.com에서 무료 키 발급 가능 (하루 1,500회). 모델 칸은 비워두면 gemini-3.5-flash를 씁니다.",
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
