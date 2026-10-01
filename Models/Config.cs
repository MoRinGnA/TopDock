using System.Text.Json.Serialization;

namespace TopDock.Models
{
    public class Config
    {
        [JsonPropertyName("youtubeApiKey")]
        public string YouTubeApiKey { get; set; } = string.Empty;

        [JsonPropertyName("topMargin")]
        public double TopMargin { get; set; } = 8;

        [JsonPropertyName("showNotifications")]
        public bool ShowNotifications { get; set; } = true;

        [JsonPropertyName("showClipboardToast")]
        public bool ShowClipboardToast { get; set; } = true;

        [JsonPropertyName("sponsorSkipEnabled")]
        public bool SponsorSkipEnabled { get; set; } = true;

        // auto: 구간 진입 시 자동 seek / manual: 자동 스킵 없이 진행바 마커+건너뛰기 버튼만
        [JsonPropertyName("sponsorSkipMode")]
        public string SponsorSkipMode { get; set; } = "auto";

        [JsonPropertyName("sponsorSkipIntro")]
        public bool SponsorSkipIntro { get; set; } = true;

        [JsonPropertyName("sponsorSkipOutro")]
        public bool SponsorSkipOutro { get; set; } = true;

        [JsonPropertyName("sponsorSkipIntermission")]
        public bool SponsorSkipIntermission { get; set; } = true;

        [JsonPropertyName("sponsorSkipMusicOfftopic")]
        public bool SponsorSkipMusicOfftopic { get; set; } = true;

        // ── AI 비서 ──
        [JsonPropertyName("assistantEnabled")]
        public bool AssistantEnabled { get; set; } = true;

        // llm7(무료·키 불필요, 기본) | gemini(무료 티어) | openrouter | upstage | ollama(로컬)
        [JsonPropertyName("aiProvider")]
        public string AiProvider { get; set; } = "llm7";

        [JsonPropertyName("aiApiKey")]
        public string AiApiKey { get; set; } = string.Empty;

        // 비워두면 프로바이더 기본값 사용
        [JsonPropertyName("aiModel")]
        public string AiModel { get; set; } = string.Empty;
    }
}
