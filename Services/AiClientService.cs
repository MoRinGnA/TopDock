using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TopDock.Services
{
    /// <summary>AI 응답 델리게이트. onDelta는 토큰 단위로 여러 번 호출된다.</summary>
    public delegate void AiDeltaHandler(string delta);

    /// <summary>
    /// OpenAI 호환 /chat/completions SSE 스트리밍 클라이언트.
    /// Gemini 무료 티어(https://generativelanguage.googleapis.com/v1beta/openai),
    /// Upstage(https://api.upstage.ai/v1/solar), OpenRouter(https://openrouter.ai/api/v1) 공용.
    /// </summary>
    public class AiClientService : IDisposable
    {
        private const int RequestTimeoutSeconds = 90;

        private readonly HttpClient _httpClient;
        private readonly AiDeltaHandler? _onDelta;

        public AiClientService(AiDeltaHandler? onDelta = null)
        {
            _onDelta = onDelta;
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(RequestTimeoutSeconds) };
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        }

        private static (string BaseUrl, string DefaultModel) ResolveEndpoint(string provider)
        {
            return provider switch
            {
                // OpenAI 호환 엔드포인트 (공식: https://ai.google.dev/gemini-api/docs/openai)
                "gemini" => ("https://generativelanguage.googleapis.com/v1beta/openai/", "gemini-3.8-flash"),
                "upstage" => ("https://api.upstage.ai/v1/solar/", "solar-mini4"),
                "openrouter" => ("https://openrouter.ai/api/v1/", "google/gemini-2.0-flash-exp:free"),
                // LLM7 무료 게이트웨이 — 익명 키("unused")로 사용 가능, 기본값
                "llm7" => ("https://api.llm7.io/v1/", "codestral-latest"),
                // 로컬 Ollama (게임 미실행 시 실험용; RAM 점유 큼)
                "ollama" => ("http://localhost:11434/v1/", "qwen3:4b"),
                _ => ("https://api.llm7.io/v1/", "codestral-latest"),
            };
        }

        public static string GetDefaultModel(string provider) => ResolveEndpoint(provider).DefaultModel;

        /// <summary>
        /// 대화를 스트리밍으로 실행한다. onDelta로 조각이 도착할 때마다 콜백하며,
        /// 완성된 전체 답변을 반환한다. 실패 시 AiException.
        /// </summary>
        public async Task<string> StreamChatAsync(
            IReadOnlyList<(string Role, string Content)> messages,
            string? systemPrompt,
            CancellationToken ct)
        {
            string provider = ConfigService.Current.AiProvider;
            string apiKey = ConfigService.Current.AiApiKey;
            bool isLocal = provider == "ollama";
            // LLM7 익명 티어: 문서화된 익명 키 "unused" 사용 (30 RPM)
            if (provider == "llm7" && string.IsNullOrWhiteSpace(apiKey))
            {
                apiKey = "unused";
            }
            if (string.IsNullOrWhiteSpace(apiKey) && !isLocal)
            {
                throw new AiException("AI API 키가 설정되지 않았습니다. 설정에서 키를 입력해 주세요.");
            }

            (string baseUrl, string defaultModel) = ResolveEndpoint(provider);
            string model = ConfigService.Current.AiModel;
            if (string.IsNullOrWhiteSpace(model)) model = defaultModel;

            var body = new List<Dictionary<string, string>>();
            if (!string.IsNullOrWhiteSpace(systemPrompt))
            {
                body.Add(new Dictionary<string, string> { ["role"] = "system", ["content"] = systemPrompt });
            }
            foreach (var (role, content) in messages)
            {
                body.Add(new Dictionary<string, string> { ["role"] = role, ["content"] = content });
            }

            var payload = new Dictionary<string, object?>
            {
                ["model"] = model,
                ["messages"] = body,
                ["stream"] = true,
                ["temperature"] = 0.7,
                ["max_tokens"] = 1024,
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}chat/completions");
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            if (provider == "openrouter")
            {
                // OpenRouter 권장 헤더 (앱 식별용, 선택)
                request.Headers.TryAddWithoutValidation("X-Title", "TopDock");
            }
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            try
            {
                using HttpResponseMessage response = await _httpClient.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    string errorBody = await SafeReadErrorAsync(response, ct).ConfigureAwait(false);
                    Log.Error($"AI request failed: {(int)response.StatusCode} {errorBody}");
                    throw new AiException(TranslateError((int)response.StatusCode, errorBody));
                }

                using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                return await ParseSseStreamAsync(stream, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new AiException("AI 응답이 시간 초과되었습니다.");
            }
            catch (AiException) { throw; }
            catch (HttpRequestException ex)
            {
                Log.Error("AI network error", ex);
                throw new AiException("네트워크에 연결할 수 없습니다. 인터넷 연결을 확인해 주세요.");
            }
        }

        private static async Task<string> SafeReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
        {
            try
            {
                string raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                // OpenAI 호환 오류는 {"error":{"message":"..."}} 형태인 경우가 많다
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.TryGetProperty("error", out var err) &&
                    err.TryGetProperty("message", out var msg))
                {
                    return msg.GetString() ?? raw;
                }
                return raw;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string TranslateError(int status, string detail)
        {
            return status switch
            {
                401 or 403 => "API 키가 올바르지 않습니다. 설정에서 키를 확인해 주세요.",
                404 => $"모델을 찾을 수 없습니다. 설정에서 모델 ID를 확인해 주세요. ({Trunc(detail, 120)})",
                429 => "무료 사용량 한도를 초과했습니다. 잠시 후 다시 시도해 주세요.",
                _ => $"AI 요청 실패 (HTTP {status}) {Trunc(detail, 160)}",
            };
        }

        private static string Trunc(string s, int max) =>
            string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= max ? s : s[..max] + "…");

        private async Task<string> ParseSseStreamAsync(Stream stream, CancellationToken ct)
        {
            var sb = new StringBuilder();
            using var reader = new StreamReader(stream, Encoding.UTF8);

            // CA2024: 비동기 스트림에서는 EndOfStream 대신 ReadLineAsync의 null 반환으로 종료 판단
            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0 || !line.StartsWith("data:")) continue;

                string data = line["data:".Length..].Trim();
                if (data == "[DONE]") break;

                string? delta = ExtractDelta(data);
                if (!string.IsNullOrEmpty(delta))
                {
                    sb.Append(delta);
                    _onDelta?.Invoke(delta);
                }
            }
            return sb.ToString();
        }

        private static string? ExtractDelta(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("choices", out var choices) ||
                    choices.ValueKind != JsonValueKind.Array ||
                    choices.GetArrayLength() == 0)
                {
                    return null;
                }

                JsonElement first = choices[0];
                if (first.TryGetProperty("delta", out var delta) &&
                    delta.TryGetProperty("content", out var content) &&
                    content.ValueKind == JsonValueKind.String)
                {
                    return content.GetString();
                }

                // 일부 공급자는 비스트리밍 형태(choices[].message)로 응답하기도 한다
                if (first.TryGetProperty("message", out var message) &&
                    message.TryGetProperty("content", out var mcontent) &&
                    mcontent.ValueKind == JsonValueKind.String)
                {
                    return mcontent.GetString();
                }
            }
            catch (JsonException)
            {
                // 부분 JSON/주석 라인은 무시
            }
            return null;
        }

        public void Dispose()
        {
            _httpClient.Dispose();
        }
    }

    /// <summary>사용자에게 안내 가능한 형태의 AI 오류.</summary>
    public class AiException : Exception
    {
        public AiException(string message) : base(message) { }
    }
}
