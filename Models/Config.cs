using System.Text.Json.Serialization;

namespace TopDock.Models
{
    public class Config
    {
        [JsonPropertyName("youtubeApiKey")]
        public string YouTubeApiKey { get; set; } = string.Empty;

        [JsonPropertyName("glowIntensity")]
        public string GlowIntensity { get; set; } = "standard"; // subtle | standard | vivid

        [JsonPropertyName("topMargin")]
        public double TopMargin { get; set; } = 8;

        [JsonPropertyName("showNotifications")]
        public bool ShowNotifications { get; set; } = true;

        [JsonPropertyName("showClipboardToast")]
        public bool ShowClipboardToast { get; set; } = true;

        [JsonPropertyName("sponsorSkipEnabled")]
        public bool SponsorSkipEnabled { get; set; } = true;

        [JsonPropertyName("sponsorSkipIntro")]
        public bool SponsorSkipIntro { get; set; } = true;

        [JsonPropertyName("sponsorSkipOutro")]
        public bool SponsorSkipOutro { get; set; } = true;

        [JsonPropertyName("sponsorSkipIntermission")]
        public bool SponsorSkipIntermission { get; set; } = true;

        [JsonPropertyName("sponsorSkipMusicOfftopic")]
        public bool SponsorSkipMusicOfftopic { get; set; } = true;
    }
}
