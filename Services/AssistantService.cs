using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TopDock.Services
{
    /// <summary>비서 대화 1턴.</summary>
    public record AssistantTurn(string Role, string Content);

    /// <summary>
    /// TopDock AI 비서의 대화 관리자.
    /// 시스템 프롬프트에 현재 컨텍스트(시각/재생 곡/배터리/클립보드)를 주입하고,
    /// 세션 동안 최근 N턴의 히스토리만 유지해 토큰 누적을 제한한다.
    /// </summary>
    public class AssistantService
    {
        private const int MaxHistoryTurns = 10;

        private readonly AiClientService _client;
        private readonly WeatherService _weather = new();
        private readonly List<AssistantTurn> _history = new();
        private readonly object _historyLock = new();

        public event Action<string>? DeltaReceived;

        // MainWindow가 갱신해서 넣어주는 최신 컨텍스트 (스레드 안전 위해 스냅샷으로만 읽음)
        private volatile AssistantContext _context = new();

        public AssistantService()
        {
            _client = new AiClientService(delta => DeltaReceived?.Invoke(delta));
        }

        public void UpdateContext(AssistantContext context)
        {
            _context = context;
        }

        public void ResetConversation()
        {
            lock (_historyLock)
            {
                _history.Clear();
            }
        }

        public IReadOnlyList<AssistantTurn> GetHistorySnapshot()
        {
            lock (_historyLock)
            {
                return _history.ToArray();
            }
        }

        /// <summary>사용자 메시지를 보내고 스트리밍으로 답변을 받는다(DeltaReceived로 조각 전달).</summary>
        public async Task<string> SendAsync(string userMessage, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(userMessage)) return string.Empty;

            List<AssistantTurn> historySnapshot;
            lock (_historyLock)
            {
                historySnapshot = new List<AssistantTurn>(_history);
            }

            var messages = new List<(string Role, string Content)>(ToPairs(historySnapshot))
            {
                ("user", userMessage)
            };

            string full = await _client.StreamChatAsync(messages, await BuildSystemPromptAsync().ConfigureAwait(false), ct).ConfigureAwait(false);

            lock (_historyLock)
            {
                _history.Add(new AssistantTurn("user", userMessage));
                _history.Add(new AssistantTurn("assistant", full));
                // 최근 N턴만 유지 (system 프롬프트는 매 요청 새로 생성되므로 저장하지 않음)
                if (_history.Count > MaxHistoryTurns * 2)
                {
                    _history.RemoveRange(0, _history.Count - MaxHistoryTurns * 2);
                }
            }
            return full;
        }

        private static IReadOnlyList<(string Role, string Content)> ToPairs(List<AssistantTurn> turns)
        {
            var pairs = new List<(string, string)>(turns.Count);
            foreach (AssistantTurn turn in turns)
            {
                pairs.Add((turn.Role, turn.Content));
            }
            return pairs;
        }

        private async Task<string> BuildSystemPromptAsync()
        {
            AssistantContext c = _context;

            // 날씨는 캐시(30분)에서 즉시, 없으면 여기서 한 번 받는다. 실패해도 계속 진행.
            string weather = await _weather.GetSummaryAsync().ConfigureAwait(false);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("너는 TopDock의 개인 비서다. TopDock은 사용자 화면 상단 중앙에 떠 있는 다이나믹 아일랜드형 노치 앱이다.");
            sb.AppendLine("답변 규칙:");
            sb.AppendLine("- 한국어로 대답한다.");
            sb.AppendLine("- 노치라는 좁은 공간에 표시되므로 핵심만 간결하게. 기본 2~4문장, 목록은 최대 3개 항목.");
            sb.AppendLine("- 불필요한 사족(\"네, 알겠습니다\" 등)과 이모지를 쓰지 않는다.");
            sb.AppendLine("- 실시간 정보(날씨, 뉴스, 주가, 검색)에는 접근할 수 없다. 그런 질문엔 지어내지 말고 확인할 방법이 없다고 솔직히 말한다.");
            sb.AppendLine("- 모르는 것은 모른다고 말한다. 그럴듯하게 꾸며내지 않는다.");
            sb.AppendLine();
            sb.AppendLine("[현재 컨텍스트]");
            sb.AppendLine($"- 시각: {c.NowText}");
            if (!string.IsNullOrWhiteSpace(c.MediaText)) sb.AppendLine($"- 재생 중: {c.MediaText}");
            if (!string.IsNullOrWhiteSpace(c.LyricText)) sb.AppendLine($"- 현재 가사: {c.LyricText}");
            if (!string.IsNullOrWhiteSpace(c.BatteryText)) sb.AppendLine($"- 배터리: {c.BatteryText}");
            if (!string.IsNullOrWhiteSpace(weather)) sb.AppendLine($"- 현재 날씨(실측): {weather}");
            if (!string.IsNullOrWhiteSpace(c.ClipboardText)) sb.AppendLine($"- 사용자가 최근에 복사한 텍스트: {Truncate(c.ClipboardText, 600)}");
            return sb.ToString();
        }

        private static string Truncate(string s, int max) =>
            s.Length <= max ? s : s[..max] + "…(이하 생략)";
    }

    /// <summary>시스템 프롬프트에 주입할 현재 상태 스냅샷.</summary>
    public record AssistantContext(
        string NowText = "",
        string MediaText = "",
        string LyricText = "",
        string BatteryText = "",
        string ClipboardText = "");
}
