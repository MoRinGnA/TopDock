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
    /// 도구 호출(function calling) 루프로 기기 조작 명령을 실제 실행까지 연결한다.
    /// </summary>
    public class AssistantService
    {
        private const int MaxHistoryTurns = 10;
        private const int MaxToolRounds = 3;

        private readonly AiClientService _client;
        private readonly WeatherService _weather = new();
        private readonly List<AssistantTurn> _history = new();
        private readonly object _historyLock = new();

        public event Action<string>? DeltaReceived;

        /// <summary>도구 모음 — 호스트가 만들어 등록한다. 이 클래스는 UI를 모른다.</summary>
        public AssistantTools? Tools { get; set; }

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

        /// <summary>
        /// 사용자 메시지를 보내고 스트리밍으로 답변을 받는다(DeltaReceived로 조각 전달).
        /// 모델이 도구 호출을 요청하면 Tools로 실행 → 결과를 돌려주고 최종 텍스트가 나올 때까지 반복한다.
        /// </summary>
        public async Task<string> SendAsync(string userMessage, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(userMessage)) return string.Empty;

            List<AssistantTurn> historySnapshot;
            lock (_historyLock)
            {
                historySnapshot = new List<AssistantTurn>(_history);
            }

            var messages = new List<Dictionary<string, object?>>();
            foreach (AssistantTurn turn in historySnapshot)
            {
                messages.Add(new() { ["role"] = turn.Role, ["content"] = turn.Content });
            }
            messages.Add(new() { ["role"] = "user", ["content"] = userMessage });

            string system = await BuildSystemPromptAsync().ConfigureAwait(false);

            string full = string.Empty;
            for (int round = 0; round < MaxToolRounds; round++)
            {
                AiTurnResult result = await _client.StreamChatWithToolsAsync(
                    messages, system, Tools?.Schema, ct).ConfigureAwait(false);

                if (result.ToolCalls.Count == 0)
                {
                    full = result.Content;
                    break;
                }

                // 도구 프로토콜: assistant(tool_calls) + tool(결과) 메시지를 이어 붙인다.
                // Gemini 3 계열은 thought_signature를 원형 그대로 돌려보내야 400이 안 난다
                // (공식 문서: 현재 턴의 첫 functionCall에 서명 필수, 실측 확인)
                var toolCallMessages = new List<Dictionary<string, object?>>();
                foreach (AiToolCall call in result.ToolCalls)
                {
                    var tc = new Dictionary<string, object?>
                    {
                        ["id"] = call.Id,
                        ["type"] = "function",
                        ["function"] = new Dictionary<string, object?>
                        {
                            ["name"] = call.Name,
                            ["arguments"] = call.ArgumentsJson,
                        }
                    };
                    if (!string.IsNullOrEmpty(call.ThoughtSignature))
                    {
                        tc["extra_content"] = new Dictionary<string, object?>
                        {
                            ["google"] = new Dictionary<string, object?>
                            {
                                ["thought_signature"] = call.ThoughtSignature,
                            }
                        };
                    }
                    toolCallMessages.Add(tc);
                }
                messages.Add(new() { ["role"] = "assistant", ["content"] = result.Content, ["tool_calls"] = toolCallMessages });

                foreach (AiToolCall call in result.ToolCalls)
                {
                    // 실행기(AssistantTools)가 자체적으로 오류를 잡아 문장으로 돌려준다.
                    string output = Tools != null
                        ? await Tools.ExecuteAsync(call, ct).ConfigureAwait(false)
                        : "도구 실행기가 등록되지 않았습니다.";
                    messages.Add(new() { ["role"] = "tool", ["tool_call_id"] = call.Id, ["content"] = output });
                }
            }

            if (string.IsNullOrWhiteSpace(full)) full = "(응답 없음)";

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
            sb.AppendLine("- 사용자가 기기 조작(재생/정지, 곡 넘기기, 볼륨, 앱 실행, 사이트 열기)을 요청하면 제공된 도구를 호출해 실제로 수행한다. 도구 결과를 받으면 무엇을 했는지 한두 문장으로 알린다.");
            sb.AppendLine("- 실시간 정보(뉴스, 주가, 검색 결과 본문)에는 접근할 수 없다. 그런 질문엔 지어내지 말고 확인할 방법이 없다고 솔직히 말한다.");
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

}
