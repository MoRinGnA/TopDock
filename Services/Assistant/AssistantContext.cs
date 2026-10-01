namespace TopDock.Services
{
    /// <summary>시스템 프롬프트에 주입할 현재 상태 스냅샷.</summary>
    public record AssistantContext(
        string NowText = "",
        string MediaText = "",
        string LyricText = "",
        string BatteryText = "",
        string ClipboardText = "");
}
