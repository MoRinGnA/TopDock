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
                // 무료 티어 일일 한도는 모델별로 다르다 — 실측: gemini-3.5-flash는 21회째에 429(한도 20),
                // gemini-3.5-flash-lite는 22회 연속 통과. 도구 호출·한국어 응답은 셋 다 정상.
                "gemini" => ("https://generativelanguage.googleapis.com/v1beta/openai/", "gemini-3.5-flash-lite"),
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

            // 무료 티어 한도는 공급자·모델별로 따로 잡힌다 — 한 모델이 소진되면 다음 후보 모델로 넘어간다.
            IReadOnlyList<string> candidates = BuildModelCandidates(provider, model);
            for (int i = 0; i < candidates.Count; i++)
            {
                string candidate = candidates[i];
                Log.Info($"AI 요청: provider={provider}, model={candidate}");
                try
                {
                    return await SendWithModelAsync(baseUrl, candidate, provider, apiKey, messages, tools, ct)
                        .ConfigureAwait(false);
                }
                catch (AiException ex) when (ex.StatusCode == 429 && i + 1 < candidates.Count)
                {
                    Log.Info($"AI 무료 한도 소진({candidate}) → {candidates[i + 1]}로 전환");
                }
                catch (AiException ex) when (ex.StatusCode == 429 && i > 0)
                {
                    throw new AiException(
                        BuildAllExhaustedMessage(candidates, ex),
                        429, ex.RetryDelay, ex.IsDailyQuota, ex.Model);
                }
            }
            throw new AiException("AI 요청에 실패했습니다.");
        }

        /// <summary>
        /// 모델 하나로 요청을 보낸다. 곧 풀리는 제한(503 과부하, 분 단위 429)은 같은 모델로 짧게 기다렸다
        /// 한 번만 재시도하고, 일일 한도처럼 오래 걸리는 제한은 재시도 없이 올려보내 상위에서 모델을 바꾼다.
        /// </summary>
        private async Task<AiTurnResult> SendWithModelAsync(
            string baseUrl,
            string model,
            string provider,
            string apiKey,
            List<Dictionary<string, object?>> messages,
            IReadOnlyList<object?>? tools,
            CancellationToken ct)
        {

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
                    AiErrorInfo? info = await TryParseErrorAsync(response, ct).ConfigureAwait(false);
                    Log.Error($"AI request failed: {(int)response.StatusCode} status={info?.Status} " +
                              $"quota={info?.QuotaId} retry={info?.RetryDelaySeconds}s detail={info?.Message}");
                    throw new AiException(
                        TranslateError((int)response.StatusCode, info, provider, model),
                        (int)response.StatusCode,
                        info?.RetryDelaySeconds is int seconds ? TimeSpan.FromSeconds(seconds) : null,
                        info?.IsDailyQuota ?? false,
                        model);
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
                catch (AiException ex) when (ex.StatusCode == 503)
                {
                    // Gemini 계열은 간헐적 과부하(503)가 잦다 — 1.5초 뒤 한 번만 재시도
                    Log.Info("AI 서버 과부하(503) → 1.5초 뒤 1회 재시도");
                    await Task.Delay(1500, ct).ConfigureAwait(false);
                    return await SendOnceAsync().ConfigureAwait(false);
                }
                catch (AiException ex) when (ex.StatusCode == 429 && ex.IsRetrySoon)
                {
                    // 분당 한도처럼 금방 풀리는 제한만 같은 모델로 한 번 더 시도한다 (일일 한도는 기다려도 소용없다)
                    TimeSpan wait = ex.RetryDelay ?? TimeSpan.FromSeconds(1.5);
                    Log.Info($"AI 분당 한도(429) → {wait.TotalSeconds:0.#}초 뒤 1회 재시도");
                    await Task.Delay(wait, ct).ConfigureAwait(false);
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

        /// <summary>후보 모델을 모두 써봤는데 전부 한도였다 — 시도한 모델과 리셋 시각을 함께 알려준다.</summary>
        private static string BuildAllExhaustedMessage(IReadOnlyList<string> candidates, AiException last)
        {
            string reset = last.RetryDelay is { } delay
                ? $"약 {Humanize(delay)} 뒤에 다시 채워집니다. "
                : string.Empty;
            return $"무료 사용량 한도를 초과했습니다. {string.Join("·", candidates)} 모두 오늘 몫을 다 썼습니다. " +
                   reset + "설정에서 다른 공급자로 바꾸면 계속 쓸 수 있습니다.";
        }

        /// <summary>
        /// 시도할 모델 목록. Gemini 무료 티어는 모델별로 일일 한도가 따로 잡히므로 기본 모델이 소진되면
        /// 한도가 남아 있는 다음 모델로 넘어간다 (실측: 셋 다 도구 호출·한국어 응답 정상).
        /// 한도가 큰 lite 계열을 앞에, 품질이 높지만 일일 20회뿐인 3.5-flash를 마지막에 둔다.
        /// </summary>
        private static IReadOnlyList<string> BuildModelCandidates(string provider, string requestedModel)
        {
            if (provider != "gemini") return new[] { requestedModel };

            var candidates = new List<string> { requestedModel };
            foreach (string fallback in new[] { "gemini-3.5-flash-lite", "gemini-3.1-flash-lite", "gemini-3.5-flash" })
            {
                if (!candidates.Contains(fallback, StringComparer.OrdinalIgnoreCase)) candidates.Add(fallback);
            }
            return candidates;
        }

        /// <summary>
        /// 공급자 오류 본문에서 안내에 쓸 정보를 뽑는다. Google은 오류를 JSON 배열로 감싸 보내고
        /// ([{"error":{...}}]) 상세에 한도 종류·재시도 시각을 담는다 — 이걸 못 읽으면 원인이 통째로 사라진다.
        /// </summary>
        private static async Task<AiErrorInfo?> TryParseErrorAsync(HttpResponseMessage response, CancellationToken ct)
        {
            string raw;
            try
            {
                raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
            if (string.IsNullOrWhiteSpace(raw)) return null;

            try
            {
                using var doc = JsonDocument.Parse(raw);
                JsonElement root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Array)
                {
                    if (root.GetArrayLength() == 0) return null;
                    root = root[0];
                }
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("error", out var err) ||
                    err.ValueKind != JsonValueKind.Object)
                {
                    return new AiErrorInfo(Trunc(raw, 200), string.Empty, string.Empty, null);
                }

                string message = StringProp(err, "message");
                string status = StringProp(err, "status");
                string quotaId = string.Empty;
                int? retrySeconds = null;

                if (err.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement detail in details.EnumerateArray())
                    {
                        if (detail.ValueKind != JsonValueKind.Object) continue;

                        if (detail.TryGetProperty("violations", out var violations) &&
                            violations.ValueKind == JsonValueKind.Array)
                        {
                            foreach (JsonElement violation in violations.EnumerateArray())
                            {
                                string id = StringProp(violation, "quotaId");
                                if (id.Length > 0) quotaId = id;
                            }
                        }

                        if (retrySeconds is null)
                        {
                            // QuotaFailure.RetryInfo.retryDelay 는 "65558.42s" 형태
                            retrySeconds = ParseDurationSeconds(StringProp(detail, "retryDelay"));
                        }
                    }
                }

                retrySeconds ??= ParseRetryFromMessage(message);
                return new AiErrorInfo(message, status, quotaId, retrySeconds);
            }
            catch (JsonException)
            {
                return new AiErrorInfo(Trunc(raw, 200), string.Empty, string.Empty, null);
            }
        }

        private static string StringProp(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;

        /// <summary>"18h12m38.4s" / "45s" 같은 지연 표기를 초로 바꾼다 (소수부는 버림).</summary>
        private static int? ParseDurationSeconds(string text)
        {
            int total = 0;
            int value = 0;
            bool hasNumber = false;
            bool hasUnit = false;
            bool fractional = false;

            foreach (char c in text)
            {
                if (char.IsDigit(c))
                {
                    if (!fractional) value = value * 10 + (c - '0');
                    hasNumber = true;
                    continue;
                }
                if (c == '.' && hasNumber)
                {
                    fractional = true;
                    continue;
                }
                if (!hasNumber) continue;

                int multiplier = c switch { 'h' => 3600, 'm' => 60, 's' => 1, _ => 0 };
                if (multiplier == 0)
                {
                    value = 0;
                    hasNumber = false;
                    fractional = false;
                    continue;
                }
                total += value * multiplier;
                hasUnit = true;
                value = 0;
                hasNumber = false;
                fractional = false;
            }
            return hasUnit ? total : null;
        }

        /// <summary>본문 문장에서 "Please retry in 18h12m38.422299609s" 부분을 뽑아낸다.</summary>
        private static int? ParseRetryFromMessage(string message)
        {
            const string marker = "retry in ";
            int index = message.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return null;
            return ParseDurationSeconds(message[(index + marker.Length)..]);
        }

        private static string Humanize(TimeSpan delay)
        {
            if (delay.TotalHours >= 1) return $"{(int)delay.TotalHours}시간 {delay.Minutes}분";
            if (delay.TotalMinutes >= 1) return $"{delay.Minutes}분";
            return $"{Math.Max(1, (int)Math.Ceiling(delay.TotalSeconds))}초";
        }

        private static string TranslateError(int status, AiErrorInfo? info, string provider, string model)
        {
            if (status == 429) return TranslateQuotaError(info, provider, model);
            return status switch
            {
                401 or 403 => "API 키가 올바르지 않습니다. 설정에서 키를 확인해 주세요.",
                404 => $"모델을 찾을 수 없습니다. 설정의 모델 칸을 비워두면 기본 모델을 사용합니다. ({Trunc(info?.Message ?? string.Empty, 120)})",
                503 => "AI 서버가 일시적으로 과부하 상태입니다. 잠시 후 다시 시도해 주세요.",
                _ => $"AI 요청 실패 (HTTP {status}) {Trunc(info?.Message ?? string.Empty, 160)}",
            };
        }

        /// <summary>무료 한도 초과(429)를 한도 종류에 맞게 설명한다. "잠시 후"로 뭉개면 일일 한도에선 거짓말이 된다.</summary>
        private static string TranslateQuotaError(AiErrorInfo? info, string provider, string model)
        {
            if (info is { IsDailyQuota: true, RetryDelaySeconds: int daily })
            {
                return $"{model}의 무료 일일 한도를 다 썼습니다. 약 {Humanize(TimeSpan.FromSeconds(daily))} 뒤에 다시 채워집니다.";
            }
            if (info?.RetryDelaySeconds is int brief)
            {
                return $"무료 사용량 한도(분당 제한)를 초과했습니다. 약 {Humanize(TimeSpan.FromSeconds(brief))} 후 다시 시도해 주세요.";
            }
            return provider switch
            {
                "llm7" => "LLM7 무료 게이트웨이의 분당 한도(30회)를 초과했습니다. 잠시 후 다시 시도해 주세요.",
                "openrouter" => "OpenRouter 무료 모델의 한도를 초과했습니다. 잠시 후 다시 시도해 주세요.",
                _ => "무료 사용량 한도를 초과했습니다. 잠시 후 다시 시도해 주세요.",
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

    /// <summary>공급자 오류 본문(JSON)을 해석한 결과.</summary>
    internal sealed record AiErrorInfo(string Message, string Status, string QuotaId, int? RetryDelaySeconds)
    {
        /// <summary>일일 한도 소진 — 분 단위 제한과 달리 몇 초 기다려도 풀리지 않는다.</summary>
        public bool IsDailyQuota =>
            QuotaId.Contains("PerDay", StringComparison.OrdinalIgnoreCase) ||
            RetryDelaySeconds is > 3600;
    }

    /// <summary>사용자에게 안내 가능한 형태의 AI 오류.</summary>
    public class AiException : Exception
    {
        public int StatusCode { get; }

        /// <summary>공급자가 알려준 재시도 지연. 알 수 없으면 null.</summary>
        public TimeSpan? RetryDelay { get; }

        /// <summary>일일 단위 한도 소진 여부.</summary>
        public bool IsDailyQuota { get; }

        /// <summary>오류를 낸 모델 이름.</summary>
        public string Model { get; }

        /// <summary>금방 풀리는 제한 — 같은 모델로 짧게 기다렸다 한 번 더 시도해도 된다.</summary>
        public bool IsRetrySoon => !IsDailyQuota && RetryDelay is null or { TotalSeconds: <= 8 };

        public AiException(
            string message,
            int statusCode = 0,
            TimeSpan? retryDelay = null,
            bool isDailyQuota = false,
            string model = "")
            : base(message)
        {
            StatusCode = statusCode;
            RetryDelay = retryDelay;
            IsDailyQuota = isDailyQuota;
            Model = model;
        }
    }
}
