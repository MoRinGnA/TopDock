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

        // 비서가 실행할 수 있는 앱(실행 파일명 또는 전체 경로). 목록에 없으면 실행하지 않는다.
        [JsonPropertyName("assistantAllowedApps")]
        public List<string> AssistantAllowedApps { get; set; } = new()
        {
            "brave", "chrome", "msedge", "firefox",
            "notepad", "calc", "spotify", "discord", "explorer", "steam",
        };
    }
}
