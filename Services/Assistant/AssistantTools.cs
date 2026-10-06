using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TopDock.Services
{
    /// <summary>
    /// 비서가 실제로 조작할 수 있는 기기 도구 모음.
    /// UI를 전혀 모른다 — 무엇을 했는지 문장으로만 돌려주고, 시각 연출은 호스트가 이벤트로 붙인다.
    /// </summary>
    public sealed class AssistantTools
    {
        /// <summary>도구 실행 직전/직후에 울린다. 호스트(UI)가 오브 같은 피드백을 붙이는 지점.</summary>
        public event Action<string>? ToolStarted;
        public event Action<string>? ToolFinished;

        private readonly MediaService _media;
        private readonly AudioService _audio;
        private readonly YouTubeSearchService _youTube = new();
        private readonly WeatherService _weather = new();

        public AssistantTools(MediaService media, AudioService audio)
        {
            _media = media;
            _audio = audio;
        }

        /// <summary>
        /// 모델에게 보낼 도구 스키마 목록. 허용 앱 목록은 설정에서 언제든 바뀌므로
        /// 캐시하지 않고 요청 때마다 현재 설정으로 만든다(도구 7개라 비용은 무시할 만하다).
        /// </summary>
        public IReadOnlyList<object?> Schema => Build().Select(t => (object?)t.ToSchema()).ToList();

        /// <summary>모델이 요청한 도구를 실행하고, 모델에게 돌려줄 결과 문장을 만든다.</summary>
        public async Task<string> ExecuteAsync(AiToolCall call, CancellationToken ct)
        {
            AssistantTool? tool = Build().FirstOrDefault(t => t.Name == call.Name);
            if (tool == null)
            {
                Log.Warn($"Assistant requested unknown tool: {call.Name}");
                return $"알 수 없는 도구: {call.Name}";
            }

            Log.Info($"Assistant tool call: {tool.Name} {call.ArgumentsJson}");
            ToolStarted?.Invoke(tool.Name);
            try
            {
                return await tool.Run(ParseArgs(call.ArgumentsJson), ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error($"Assistant tool failed: {tool.Name}", ex);
                return $"도구 실행 중 오류: {ex.Message}";
            }
            finally
            {
                ToolFinished?.Invoke(tool.Name);
            }
        }

        // ────────────────────────── 도구 정의 ──────────────────────────

        private List<AssistantTool> Build() => new()
        {
            new("media_play_pause", "현재 재생 중인 미디어를 재생/일시정지 토글한다",
                NoArgs(), Array.Empty<string>(),
                async (_, _) => await _media.TryTogglePlayPauseAsync().ConfigureAwait(false)
                    ? "재생/일시정지 토글 성공" : "제어할 미디어 세션이 없다"),

            new("media_next", "다음 곡으로 건너뛴다",
                NoArgs(), Array.Empty<string>(),
                async (_, _) => await _media.TrySkipNextAsync().ConfigureAwait(false)
                    ? "다음 곡으로 넘어갔다" : "제어할 미디어 세션이 없다"),

            new("media_previous", "이전 곡으로 돌아간다",
                NoArgs(), Array.Empty<string>(),
                async (_, _) => await _media.TrySkipPreviousAsync().ConfigureAwait(false)
                    ? "이전 곡으로 돌아갔다" : "제어할 미디어 세션이 없다"),

            new("media_seek", "현재 재생 위치를 앞뒤로 이동한다. offset_seconds 양수=앞으로, 음수=뒤로 (예: 30, -60)",
                Args(("offset_seconds", AssistantTool.Prop("integer", "이동할 초. 양수=앞으로, 음수=뒤로"))),
                new[] { "offset_seconds" },
                async (args, _) =>
                {
                    if (!TryGetInt(args, "offset_seconds", out int offset))
                        return "offset_seconds 인자가 올바르지 않다";
                    if (!_media.GetExactPosition(out TimeSpan pos, out TimeSpan duration))
                        return "제어할 미디어 세션이 없다";

                    // 길이를 알면 [0, 길이]로, 모르면 0 아래로만 막는다
                    TimeSpan target = pos + TimeSpan.FromSeconds(offset);
                    if (target < TimeSpan.Zero) target = TimeSpan.Zero;
                    if (duration > TimeSpan.Zero && target > duration) target = duration;

                    bool ok = await _media.TrySeekAsync(target).ConfigureAwait(false);
                    if (!ok) return "이 미디어는 탐색을 지원하지 않는다";

                    string dir = offset >= 0 ? "앞으로" : "뒤로";
                    string at = duration > TimeSpan.Zero
                        ? $"{FormatTime(target)} / {FormatTime(duration)}"
                        : FormatTime(target);
                    return $"{Math.Abs(offset)}초 {dir} 이동했다 ({at})";
                }),

            new("set_volume", "시스템 마스터 볼륨을 지정한 값으로 설정한다",
                Args(("volume", AssistantTool.Prop("integer", "0~100"))), new[] { "volume" },
                (args, _ct) =>
                {
                    if (!TryGetInt(args, "volume", out int vol)) return Done("volume 인자가 올바르지 않다");
                    int clamped = Math.Clamp(vol, 0, 100);
                    float delta = (clamped / 100f) - _audio.GetCurrentVolume(out _) / 100f;
                    _audio.StepVolume(delta, out _);
                    return Done($"볼륨을 {clamped}%로 설정했다");
                }),

            new("open_app", $"허용 목록에 있는 앱을 실행한다. 가능: {AllowedAppList}",
                Args(("app", AssistantTool.Prop("string", "앱 이름 (예: brave, notepad, 계산기, spotify)"))), new[] { "app" },
                (args, _) =>
                {
                    string app = GetString(args, "app");
                    if (string.IsNullOrWhiteSpace(app)) return Done("app 인자가 비었다");
                    string? exe = ResolveApp(app);
                    if (exe == null)
                    {
                        Log.Warn($"Assistant app launch blocked (not allow-listed): {app}");
                        return Done($"'{app}'은(는) 허용 목록에 없다. 실행 가능: {AllowedAppList}");
                    }
                    return Done(TryStart(exe)
                        ? $"{app} 실행을 시작했다"
                        : $"{app}을(를) 실행하지 못했다");
                }),

            new("open_url", "기본 브라우저(또는 지정한 브라우저)로 URL을 연다",
                Args(
                    ("url", AssistantTool.Prop("string", "http/https URL")),
                    ("browser", AssistantTool.Prop("string", "선택. brave/chrome/edge/firefox"))),
                new[] { "url" },
                (args, _) =>
                {
                    string url = NormalizeUrl(GetString(args, "url"));
                    if (url.Length == 0) return Done("url 인자가 비었다");
                    string browser = GetString(args, "browser");
                    return Done(OpenIn(url, browser));
                }),

            new("get_weather", "사용자가 날씨를 물었을 때만 인터넷에서 현재 날씨와 오늘·내일 예보를 새로 조회한다",
                NoArgs(), Array.Empty<string>(),
                async (_, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    string weather = await _weather.GetSummaryAsync().ConfigureAwait(false);
                    return string.IsNullOrWhiteSpace(weather)
                        ? "날씨 정보를 가져오지 못했다. 위치 또는 날씨 서비스에 연결할 수 없다."
                        : weather;
                }),

            new("play_youtube", "유튜브에서 검색해 첫 영상(검색 실패 시 검색 결과 페이지)을 연다",
                Args(
                    ("query", AssistantTool.Prop("string", "검색할 곡/영상 제목")),
                    ("browser", AssistantTool.Prop("string", "선택. brave/chrome/edge/firefox"))),
                new[] { "query" },
                async (args, _) =>
                {
                    string query = GetString(args, "query");
                    if (string.IsNullOrWhiteSpace(query)) return "query 인자가 비었다";
                    string target = await ResolveYouTubeUrlAsync(query).ConfigureAwait(false);
                    string browser = GetString(args, "browser");
                    return $"유튜브에서 '{query}'을(를) 열었다: {OpenIn(target, browser)} → {target}";
                }),
        };

        // ────────────────────────── 실행 허용 목록 ──────────────────────────

        /// <summary>
        /// 사람이 쓰는 별칭(한글 포함)을 실행 파일명으로 번역하는 사전.
        /// 실행 허용 여부는 이 사전이 아니라 설정의 허용 목록이 결정한다 — 여기서는 이름만 바꿔준다.
        /// </summary>
        private static readonly Dictionary<string, string> AppAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["brave"] = "brave", ["브레이브"] = "brave",
            ["chrome"] = "chrome", ["크롬"] = "chrome", ["구글크롬"] = "chrome",
            ["edge"] = "msedge", ["엣지"] = "msedge",
            ["firefox"] = "firefox", ["파이어폭스"] = "firefox",
            ["notepad"] = "notepad", ["메모장"] = "notepad",
            ["calc"] = "calc", ["calculator"] = "calc", ["계산기"] = "calc",
            ["spotify"] = "spotify", ["스포티파이"] = "spotify",
            ["discord"] = "discord", ["디스코드"] = "discord",
            ["explorer"] = "explorer", ["파일탐색기"] = "explorer",
            ["steam"] = "steam", ["스팀"] = "steam",
        };

        /// <summary>설정에서 지금 허용한 앱 목록. 스키마 설명과 오류 문구에 넣는다.</summary>
        private static string AllowedAppList
        {
            get
            {
                List<string> apps = ConfigService.Current.AssistantAllowedApps;
                return apps.Count == 0 ? "(허용된 앱 없음)" : string.Join(", ", apps);
            }
        }

        /// <summary>
        /// 앱 이름을 실행 파일명으로 번역하고 설정의 허용 목록으로 검증한다. 허용 목록에 없으면 null.
        /// (프롬프트에 실려 오는 웹·클립보드 텍스트로 인젝션이 가능하므로 이름 그대로 실행하지 않는다.)
        /// </summary>
        private static string? ResolveApp(string name)
        {
            string trimmed = name.Trim();
            if (trimmed.Length == 0) return null;

            // "메모장"/"구글 크롬"처럼 공백·한글 별칭은 실행 파일명으로 번역하고,
            // 그 밖의 이름은 설정에 적힌 그대로(실행 파일명 또는 전체 경로)로 본다.
            string candidate = AppAliases.TryGetValue(trimmed.Replace(" ", string.Empty), out string? exe)
                ? exe
                : trimmed;

            foreach (string allowed in ConfigService.Current.AssistantAllowedApps)
            {
                if (string.Equals(allowed.Trim(), candidate, StringComparison.OrdinalIgnoreCase)) return candidate;
            }
            return null;
        }

        private static bool TryStart(string fileName, string? arguments = null)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments ?? string.Empty,
                    UseShellExecute = true,
                });
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn($"Assistant launch failed: {fileName} ({ex.Message})");
                return false;
            }
        }

        /// <summary>http/https만 열린다. 스킴이 없으면 https를 붙이고, 그 밖의 스킴은 거부한다.</summary>
        private static string NormalizeUrl(string raw)
        {
            string url = raw.Trim();
            if (url.Length == 0) return string.Empty;
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return url;
            }
            // "://"가 있는데 http/https가 아니면 file:, ms-settings: 같은 스킴이므로 열지 않는다
            if (url.Contains("://", StringComparison.Ordinal)) return string.Empty;
            return "https://" + url;
        }

        /// <summary>지정 브라우저가 허용 목록에 있으면 그 앱으로, 아니면 기본 브라우저로 연다.</summary>
        private static string OpenIn(string url, string browser)
        {
            if (!string.IsNullOrWhiteSpace(browser))
            {
                string? exe = ResolveApp(browser);
                if (exe != null && TryStart(exe, $"\"{url}\"")) return $"{browser}에서 열었다";
            }
            return TryStart(url) ? "기본 브라우저로 열었다" : "브라우저를 열지 못했다";
        }

        /// <summary>YouTube 검색(키 있으면 API, 없으면 HTML)으로 1위 영상 URL을 얻는다. 실패 시 검색 결과 페이지.</summary>
        private async Task<string> ResolveYouTubeUrlAsync(string query)
        {
            try
            {
                string? videoId = await _youTube.SearchVideoIdAsync(query).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(videoId))
                {
                    return $"https://www.youtube.com/watch?v={videoId}";
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"YouTube search failed: {ex.Message}");
            }
            return "https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(query);
        }

        // ────────────────────────── 인자 유틸 ──────────────────────────

        private static Task<string> Done(string message) => Task.FromResult(message);

        /// <summary>재생 시각 표시용 m:ss.</summary>
        private static string FormatTime(TimeSpan t) => t.ToString(@"m\:ss");

        private static Dictionary<string, object?> NoArgs() => new();

        private static Dictionary<string, object?> Args(params (string Name, Dictionary<string, object?> Item)[] items)
        {
            var map = new Dictionary<string, object?>();
            foreach (var (name, item) in items) map[name] = item;
            return map;
        }

        private static JsonElement ParseArgs(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
                return doc.RootElement.Clone();
            }
            catch
            {
                using var empty = JsonDocument.Parse("{}");
                return empty.RootElement.Clone();
            }
        }

        private static string GetString(JsonElement args, string key)
            => args.ValueKind == JsonValueKind.Object
               && args.TryGetProperty(key, out var el)
               && el.ValueKind == JsonValueKind.String
                ? el.GetString() ?? string.Empty
                : string.Empty;

        private static bool TryGetInt(JsonElement args, string key, out int value)
        {
            value = 0;
            return args.ValueKind == JsonValueKind.Object
                   && args.TryGetProperty(key, out var el)
                   && el.ValueKind == JsonValueKind.Number
                   && el.TryGetInt32(out value);
        }
    }
}
