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

    /// <summary>모델이 호출한 도구 1건 (OpenAI 호환 function calling). Gemini 3 서명은 ThoughtSignature에 보관.</summary>
    public record AiToolCall(string Id, string Name, string ArgumentsJson, string ThoughtSignature = "");

    /// <summary>도구 지원 응답 1턴의 결과.</summary>
    public record AiTurnResult(string Content, IReadOnlyList<AiToolCall> ToolCalls);

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
                // 3.8-flash는 무료 티어 한도가 빠듯(429 잦음), 3.5-flash는 한도 여유+도구 호출·한국어 실측 통과.
                "gemini" => ("https://generativelanguage.googleapis.com/v1beta/openai/", "gemini-3.5-flash"),
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

        /// <summary>도구 없이 대화 1턴을 스트리밍으로 실행하고 완성된 답변을 반환한다. 실패 시 AiException.</summary>
        public async Task<string> StreamChatAsync(
            IReadOnlyList<(string Role, string Content)> messages,
            string? systemPrompt,
            CancellationToken ct)
        {
            var dicts = new List<Dictionary<string, object?>>();
            if (!string.IsNullOrWhiteSpace(systemPrompt))
            {
                dicts.Add(new() { ["role"] = "system", ["content"] = systemPrompt });
            }
            foreach (var (role, content) in messages)
            {
                dicts.Add(new() { ["role"] = role, ["content"] = content });
            }

            AiTurnResult result = await SendCoreAsync(dicts, tools: null, ct).ConfigureAwait(false);
            return result.Content;
        }

        /// <summary>
        /// 도구(function calling)를 포함한 대화 1턴. messages는 role/content(+tool 프로토콜 필드)를
        /// 갖춘 원본 딕셔너리다. 게이트웨이가 tools 필드를 거부하면(400) 도구 없이 재시도한다.
        /// </summary>
        public async Task<AiTurnResult> StreamChatWithToolsAsync(
            List<Dictionary<string, object?>> messages,
            string? systemPrompt,
            IReadOnlyList<object?>? tools,
            CancellationToken ct)
        {
            var all = new List<Dictionary<string, object?>>();
            if (!string.IsNullOrWhiteSpace(systemPrompt))
            {
                all.Add(new() { ["role"] = "system", ["content"] = systemPrompt });
            }
            all.AddRange(messages);

            try
            {
                return await SendCoreAsync(all, tools, ct).ConfigureAwait(false);
            }
            catch (AiException ex) when (tools != null && ex.StatusCode == 400)
            {
                Log.Warn("AI provider rejected tools payload, retrying without tools");
                return await SendCoreAsync(all, tools: null, ct).ConfigureAwait(false);
            }
        }

        private async Task<AiTurnResult> SendCoreAsync(
            List<Dictionary<string, object?>> messages,
            IReadOnlyList<object?>? tools,
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
            string model = ConfigService.Current.AiModel.Trim();
            // AI Studio 화면 표기(models/gemini-…)를 그대로 붙여넣은 경우 보정 —
            // 이 접두어가 있으면 Google이 404가 아닌 오해하기 쉬운 503을 반환한다 (실측 확인)
            if (model.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
            {
                model = model["models/".Length..];
            }
            if (string.IsNullOrWhiteSpace(model)) model = defaultModel;
            Log.Info($"AI 요청: provider={provider}, model={model}");

            var payload = new Dictionary<string, object?>
            {
                ["model"] = model,
                ["messages"] = messages,
                ["stream"] = true,
                ["temperature"] = 0.7,
                ["max_tokens"] = 1024,
            };
            if (tools is { Count: > 0 })
            {
                payload["tools"] = tools;
            }

            async Task<AiTurnResult> SendOnceAsync()
            {
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

                using HttpResponseMessage response = await _httpClient.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    string errorBody = await SafeReadErrorAsync(response, ct).ConfigureAwait(false);
                    Log.Error($"AI request failed: {(int)response.StatusCode} {errorBody}");
                    throw new AiException(TranslateError((int)response.StatusCode, errorBody), (int)response.StatusCode);
                }

                using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                return await ParseSseStreamAsync(stream, ct).ConfigureAwait(false);
            }

            try
            {
                try
                {
                    return await SendOnceAsync().ConfigureAwait(false);
                }
                catch (AiException ex) when (ex.StatusCode is 503 or 429)
                {
                    // Gemini 계열은 간헐적 과부하(503)/한도(429)가 잦다 — 1.5초 뒤 한 번만 재시도
                    Log.Info($"AI request got HTTP {ex.StatusCode}, retrying once");
                    await Task.Delay(1500, ct).ConfigureAwait(false);
                    return await SendOnceAsync().ConfigureAwait(false);
                }
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
                404 => $"모델을 찾을 수 없습니다. 설정의 모델 칸을 비워두면 기본 모델을 사용합니다. ({Trunc(detail, 120)})",
                429 => "무료 사용량 한도를 초과했습니다. 잠시 후 다시 시도해 주세요.",
                503 => "AI 서버가 일시적으로 과부하 상태입니다. 잠시 후 다시 시도해 주세요.",
                _ => $"AI 요청 실패 (HTTP {status}) {Trunc(detail, 160)}",
            };
        }

        private static string Trunc(string s, int max) =>
            string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= max ? s : s[..max] + "…");

        private async Task<AiTurnResult> ParseSseStreamAsync(Stream stream, CancellationToken ct)
        {
            var sb = new StringBuilder();
            var toolAcc = new SortedDictionary<int, ToolAccum>();

            using var reader = new StreamReader(stream, Encoding.UTF8);

            // CA2024: 비동기 스트림에서는 EndOfStream 대신 ReadLineAsync의 null 반환으로 종료 판단
            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0 || !line.StartsWith("data:")) continue;

                string data = line["data:".Length..].Trim();
                if (data == "[DONE]") break;

                try
                {
                    using var doc = JsonDocument.Parse(data);
                    JsonElement root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object ||
                        !root.TryGetProperty("choices", out var choices) ||
                        choices.ValueKind != JsonValueKind.Array ||
                        choices.GetArrayLength() == 0)
                    {
                        continue;
                    }

                    JsonElement first = choices[0];

                    // 일부 공급자는 비스트리밍 형태(choices[].message)로 응답하기도 한다
                    if (first.TryGetProperty("message", out var message) &&
                        message.TryGetProperty("content", out var mcontent) &&
                        mcontent.ValueKind == JsonValueKind.String)
                    {
                        string mtext = mcontent.GetString() ?? string.Empty;
                        sb.Append(mtext);
                        _onDelta?.Invoke(mtext);
                    }

                    if (!first.TryGetProperty("delta", out var delta)) continue;

                    if (delta.TryGetProperty("content", out var content) &&
                        content.ValueKind == JsonValueKind.String)
                    {
                        string deltaText = content.GetString() ?? string.Empty;
                        sb.Append(deltaText);
                        _onDelta?.Invoke(deltaText);
                    }

                    // 도구 호출 델타 병합: Gemini는 1청크에 통째로, OpenAI는 index별로 조각낸다
                    if (delta.TryGetProperty("tool_calls", out var toolCalls) &&
                        toolCalls.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement tc in toolCalls.EnumerateArray())
                        {
                            int index = tc.TryGetProperty("index", out var idxEl) && idxEl.TryGetInt32(out int i) ? i : 0;
                            if (!toolAcc.TryGetValue(index, out var entry))
                            {
                                entry = new ToolAccum();
                                toolAcc[index] = entry;
                            }
                            if (tc.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                            {
                                string id = idEl.GetString() ?? string.Empty;
                                if (id.Length > 0) entry.Id = id;
                            }
                            // Gemini 3 계열: thought_signature를 응답에 담아 주고 다음 요청에 되돌려야 400이 안 난다
                            if (tc.TryGetProperty("extra_content", out var extra) &&
                                extra.ValueKind == JsonValueKind.Object &&
                                extra.TryGetProperty("google", out var g) &&
                                g.TryGetProperty("thought_signature", out var sig) &&
                                sig.ValueKind == JsonValueKind.String)
                            {
                                entry.Signature = sig.GetString() ?? string.Empty;
                            }
                            if (tc.TryGetProperty("function", out var fn))
                            {
                                if (fn.TryGetProperty("name", out var nameEl) &&
                                    nameEl.ValueKind == JsonValueKind.String &&
                                    !string.IsNullOrEmpty(nameEl.GetString()))
                                {
                                    entry.Name = nameEl.GetString()!;
                                }
                                if (fn.TryGetProperty("arguments", out var argsEl) &&
                                    argsEl.ValueKind == JsonValueKind.String)
                                {
                                    entry.Args += argsEl.GetString();
                                }
                            }
                        }
                    }
                }
                catch (JsonException)
                {
                    // 부분 JSON/주석 라인은 무시
                }
            }

            var calls = new List<AiToolCall>();
            foreach (var kv in toolAcc)
            {
                ToolAccum a = kv.Value;
                if (string.IsNullOrEmpty(a.Name)) continue;
                calls.Add(new AiToolCall(
                    a.Id.Length > 0 ? a.Id : $"call_{kv.Key}",
                    a.Name,
                    a.Args,
                    a.Signature));
            }
            return new AiTurnResult(sb.ToString(), calls);
        }

        private sealed class ToolAccum
        {
            public string Id { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public string Args { get; set; } = string.Empty;
            public string Signature { get; set; } = string.Empty;
        }

        public void Dispose()
        {
            _httpClient.Dispose();
        }
    }

    /// <summary>사용자에게 안내 가능한 형태의 AI 오류.</summary>
    public class AiException : Exception
    {
        public int StatusCode { get; }

        public AiException(string message, int statusCode = 0) : base(message)
        {
            StatusCode = statusCode;
        }
    }
}
