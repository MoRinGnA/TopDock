using System.Windows;
using System.Windows.Controls;
using TopDock.Models;
using TopDock.Services;

namespace TopDock
{
    public partial class SettingsWindow : Window
    {
        /// <summary>저장 후 MainWindow가 설정을 다시 적용할 수 있도록 알린다.</summary>
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

            GlowIntensityCombo.SelectedIndex = cfg.GlowIntensity switch
            {
                "subtle" => 0,
                "vivid" => 2,
                _ => 1
            };
            TopMarginSlider.Value = cfg.TopMargin;
            TopMarginValueText.Text = ((int)cfg.TopMargin).ToString();

            SponsorSkipEnabledCheck.IsChecked = cfg.SponsorSkipEnabled;
            SponsorSkipIntroCheck.IsChecked = cfg.SponsorSkipIntro;
            SponsorSkipOutroCheck.IsChecked = cfg.SponsorSkipOutro;
            SponsorSkipIntermissionCheck.IsChecked = cfg.SponsorSkipIntermission;
            SponsorSkipMusicOfftopicCheck.IsChecked = cfg.SponsorSkipMusicOfftopic;
            SponsorCategoryPanel.IsEnabled = cfg.SponsorSkipEnabled;

            ApiKeyBox.Password = cfg.YouTubeApiKey;
        }

        private void TopMarginSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TopMarginValueText != null)
            {
                TopMarginValueText.Text = ((int)e.NewValue).ToString();
            }
        }

        private void SponsorSkipEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (SponsorCategoryPanel != null)
            {
                SponsorCategoryPanel.IsEnabled = SponsorSkipEnabledCheck.IsChecked == true;
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            var cfg = ConfigService.Current;

            cfg.ShowNotifications = ShowNotificationsCheck.IsChecked == true;
            cfg.ShowClipboardToast = ShowClipboardToastCheck.IsChecked == true;

            cfg.GlowIntensity = GlowIntensityCombo.SelectedIndex switch
            {
                0 => "subtle",
                2 => "vivid",
                _ => "standard"
            };
            cfg.TopMargin = (int)TopMarginSlider.Value;

            cfg.SponsorSkipEnabled = SponsorSkipEnabledCheck.IsChecked == true;
            cfg.SponsorSkipIntro = SponsorSkipIntroCheck.IsChecked == true;
            cfg.SponsorSkipOutro = SponsorSkipOutroCheck.IsChecked == true;
            cfg.SponsorSkipIntermission = SponsorSkipIntermissionCheck.IsChecked == true;
            cfg.SponsorSkipMusicOfftopic = SponsorSkipMusicOfftopicCheck.IsChecked == true;

            cfg.YouTubeApiKey = ApiKeyBox.Password.Trim();

            ConfigService.Save();
            SettingsSaved?.Invoke(this, System.EventArgs.Empty);
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
