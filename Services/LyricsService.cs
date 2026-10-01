using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TopDock.Models;

namespace TopDock.Services
{
    /// <summary>
    /// 한 곡의 가사 조회 결과.
    /// 서비스가 곡별 상태(예: 음원 길이)를 들고 있지 않도록 필요한 값을 전부 여기 담아 돌려준다 —
    /// 예전에는 _lrcDuration 하나를 인스턴스에 두고 썼는데, 앞선 조회가 살아남아 값을 덮어쓰면
    /// 엉뚱한 보정이 적용됐다.
    /// </summary>
    public sealed record LyricsResult(
        IReadOnlyList<LyricLine> Lines,
        TimeSpan? SourceDuration,
        string MatchedTrack,
        string MatchedArtist);

    public class LyricsService : IDisposable
    {
        // 캐시 스키마 버전. 올리면 기존 캐시 파일은 전부 버려진다 —
        // 매칭 로직을 고쳐도 예전에 잘못 저장된 결과가 계속 쓰이던 문제를 막는다.
        private const int CacheSchemaVersion = 2;

        // 이 점수 아래는 "다른 곡"으로 본다
        private const double MinMatchScore = 0.6;

        // 길이가 이만큼 넘게 차이 나면 다른 판(연장판·루프 영상)이다 — 제목이 같아도 받지 않는다
        private static readonly TimeSpan DurationTolerance = TimeSpan.FromSeconds(40);

        // 사실상 같은 녹음으로 볼 수 있는 차이 (뮤직비디오 인트로는 여기에 안 들어온다)
        private static readonly TimeSpan DurationTight = TimeSpan.FromSeconds(5);

        private readonly HttpClient _httpClient;
        private CancellationTokenSource? _currentCts;

        // 메모리 캐시 (세션 동안 유효)
        private readonly ConcurrentDictionary<string, LyricsResult> _memoryCache = new();

        // 디스크 캐시 (%APPDATA%\TopDock\LyricsCache) — 오프라인/재생 시 즉시 표시용 영구 캐시
        private static readonly string DiskCacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TopDock", "LyricsCache");

        private static readonly JsonSerializerOptions CacheJsonOptions = new();

        private static readonly HashSet<string> ChannelsToIgnore = new(StringComparer.OrdinalIgnoreCase)
        {
            "SMTOWN", "1theK (원더케이)", "1theK", "HYBE LABELS", "JYP Entertainment",
            "Stone Music Entertainment", "GENIE MUSIC", "starshipTV", "YG ENTERTAINMENT",
            "Mnet K-POP", "KBS Kpop", "ALL THE K-POP", "스튜디오 춤 (STUDIO CHOOM)",
            "스튜디오 춤", "STUDIO CHOOM", "딩고 뮤직 / dingo music", "dingo music",
            "워너뮤직코리아 (Warner Music Korea)", "워너뮤직코리아", "BIGHIT MUSIC",
            "BANGTANTV", "SEVENTEEN", "Vevo", "Official Channel", "YouTube", "Bugs",
            "BPM Entertainment", "쇼파르엔터테인먼트", "카카오엔터테인먼트", "카카오M",
            "Kakao Entertainment", "DanalEntertainment", "오감엔터테인먼트", "포켓돌스튜디오"
        };

        public LyricsService()
        {
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(6)
            };
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("TopDock/1.0 (https://github.com/TopDock)");
        }

        /// <summary>
        /// 곡 하나의 가사를 찾는다. 못 찾으면 null.
        /// mediaDuration은 지금 재생 중인 영상의 길이 — YouTube에서는 업로더(채널명)가 아티스트로 오기 때문에
        /// 아티스트를 못 믿을 때 "제목이 같고 길이도 같은가"를 대신 확인하는 근거로 쓴다.
        /// </summary>
        public async Task<LyricsResult?> GetLyricsAsync(string rawTitle, string rawArtist, TimeSpan? mediaDuration = null)
        {
            if (string.IsNullOrWhiteSpace(rawTitle) || rawTitle == "재생 중인 미디어 없음")
                return null;

            var (cleanTitle, cleanArtist, subTitle, subArtist) = ParseTitleAndArtistFull(rawTitle, rawArtist);
            string cacheKey = $"{cleanArtist.ToLowerInvariant()}:::{cleanTitle.ToLowerInvariant()}";

            if (TryGetCached(cacheKey, out var cached)) return cached;

            _currentCts?.Cancel();
            _currentCts = new CancellationTokenSource();
            var ct = _currentCts.Token;

            try
            {
                Candidate? best = null;
                foreach (string query in BuildSearchQueries(cleanTitle, cleanArtist, subTitle, subArtist, rawTitle))
                {
                    if (ct.IsCancellationRequested) break;

                    string url = $"https://lrclib.net/api/search?q={Uri.EscapeDataString(query)}";
                    var (candidate, resultCount) = await SearchAsync(url, cleanTitle, cleanArtist, mediaDuration, ct).ConfigureAwait(false);

                    Log.Info($"Lyrics search \"{query}\" → {resultCount}건, 최고점 " +
                             (candidate == null ? "없음" : candidate.Score.ToString("F2", CultureInfo.InvariantCulture)));

                    if (candidate != null && (best == null || candidate.Score > best.Score)) best = candidate;

                    // 완전 일치를 찾았으면 더 뒤질 이유가 없다 (예전에는 후보 8개를 끝까지 순차로 쐈다)
                    if (best is { Score: >= 0.999 }) break;
                }

                if (best == null)
                {
                    Log.Info($"Lyrics not found: {cleanArtist} - {cleanTitle}");
                    return null;
                }

                var result = new LyricsResult(best.Lines, best.Duration, best.Track, best.Artist);
                StoreInCache(cacheKey, result);
                Log.Info($"Lyrics matched: {best.Artist} - {best.Track} " +
                         $"(점수 {best.Score.ToString("F2", CultureInfo.InvariantCulture)}, " +
                         $"음원 {result.SourceDuration?.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture) ?? "?"}초)");
                return result;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log.Warn($"Lyrics fetch failed: {cleanArtist} - {cleanTitle} ({ex.Message})");
            }

            return null;
        }

        // ────────────────────────── 매칭 ──────────────────────────

        private sealed record Candidate(IReadOnlyList<LyricLine> Lines, TimeSpan? Duration, string Track, string Artist, double Score);

        /// <summary>
        /// 검색 결과 한 건이 찾는 곡인지 점수를 매긴다.
        /// 예전에는 syncedLyrics가 있는 첫 항목을 그대로 채택했는데, 결과가 20건씩 오는 탓에
        /// 조금만 어긋나도 다른 곡 가사를 자신 있게 보여줬다.
        /// </summary>
        private static double ScoreMatch(JsonElement item, string wantTitle, string wantArtist, TimeSpan? mediaDuration)
        {
            // 검색 질의와 같은 형태로 견준다 — 화면 제목에는 "[명조 카르티시아 테마곡] 가사/번역"
            // 같은 꼬리표가 남아 있어서, 그대로 견주면 LRCLIB의 깔끔한 제목과 영영 만나지 못한다
            string title = Normalize(StripTags(wantTitle));
            string artist = Normalize(wantArtist);
            if (title.Length == 0) return 0;

            string gotTitle = Normalize(GetString(item, "trackName"));
            double titleScore = Similarity(title, gotTitle);
            if (titleScore <= 0) return 0;   // 제목이 안 맞으면 무조건 탈락

            // 아티스트를 알고 그 아티스트까지 맞으면 가장 강한 신호 — 제목이 조금 어긋나도 받아들인다
            if (artist.Length > 0)
            {
                double artistScore = Similarity(artist, Normalize(GetString(item, "artistName")));
                if (artistScore > 0) return titleScore * 0.6 + artistScore * 0.4;
            }

            // 아티스트를 못 믿는 경우(YouTube 업로더명)에는 제목이 완전히 같아야 한다.
            // 그리고 재생 중인 영상 길이를 알면 길이까지 확인한다 — 같은 제목의 다른 곡을 집는 걸 막는다.
            if (gotTitle != title) return 0;
            if (mediaDuration is { TotalSeconds: > 0 } want && TryGetDuration(item, out TimeSpan got))
            {
                double diff = Math.Abs(got.TotalSeconds - want.TotalSeconds);
                if (diff > DurationTolerance.TotalSeconds) return 0;
                return diff <= DurationTight.TotalSeconds ? 0.85 : 0.72;
            }

            return 0.65;
        }

        private static bool TryGetDuration(JsonElement item, out TimeSpan duration)
        {
            duration = TimeSpan.Zero;
            if (item.TryGetProperty("duration", out var el) && el.ValueKind == JsonValueKind.Number)
            {
                duration = TimeSpan.FromSeconds(el.GetDouble());
                return duration > TimeSpan.Zero;
            }
            return false;
        }

        /// <summary>대소문자·공백·구두점·괄호를 걷어내 비교 가능한 형태로 만든다.</summary>
        private static string Normalize(string s) =>
            string.IsNullOrEmpty(s) ? string.Empty : Regex.Replace(s.ToLowerInvariant(), @"[^\p{L}\p{N}]", "");

        private static double Similarity(string want, string got)
        {
            if (got.Length == 0 || want.Length == 0) return 0;
            if (want == got) return 1.0;

            // 짧은 이름의 우연한 포함을 막는다 — 아티스트 "IVE"가 "FIVE..."에 걸리는 식
            if (Math.Min(want.Length, got.Length) < 4) return 0;

            if (got.Contains(want, StringComparison.Ordinal) || want.Contains(got, StringComparison.Ordinal)) return 0.6;
            return 0;
        }

        private static string GetString(JsonElement item, string name) =>
            item.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
                ? el.GetString() ?? string.Empty
                : string.Empty;

        private async Task<(Candidate? Candidate, int ResultCount)> SearchAsync(
            string url, string wantTitle, string wantArtist, TimeSpan? mediaDuration, CancellationToken ct)
        {
            try
            {
                using var response = await _httpClient.GetAsync(url, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    Log.Warn($"Lyrics search HTTP {(int)response.StatusCode}: {url}");
                    return (null, 0);
                }

                string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Array) return (null, 0);

                int count = root.GetArrayLength();
                Candidate? best = null;

                foreach (var item in root.EnumerateArray())
                {
                    double score = ScoreMatch(item, wantTitle, wantArtist, mediaDuration);
                    if (score < MinMatchScore) continue;
                    if (best != null && score <= best.Score) continue;

                    // 반주 트랙은 가사가 없다 — 점수가 높아도 채택하지 않는다
                    if (item.TryGetProperty("instrumental", out var inst) && inst.ValueKind == JsonValueKind.True) continue;

                    if (!item.TryGetProperty("syncedLyrics", out var syn) || syn.ValueKind != JsonValueKind.String) continue;
                    string? lrc = syn.GetString();
                    if (string.IsNullOrWhiteSpace(lrc)) continue;

                    var lines = ParseLrc(lrc);
                    if (lines.Count == 0) continue;

                    TimeSpan? duration = TryGetDuration(item, out TimeSpan parsedDuration) ? parsedDuration : null;

                    best = new Candidate(lines, duration, GetString(item, "trackName"), GetString(item, "artistName"), score);
                }

                return (best, count);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Warn($"Lyrics request failed: {url} ({ex.Message})");
                return (null, 0);
            }
        }

        /// <summary>
        /// LRCLIB의 한국어/영어 카탈로그를 모두 노리기 위한 후보 질의.
        /// 표시용 정리(ParseTitleAndArtist)와 달리 여기서는 "맞을 확률이 높은 순서"가 중요하다.
        /// </summary>
        private static List<string> BuildSearchQueries(
            string title, string artist, string subTitle, string subArtist, string rawTitle)
        {
            var queries = new List<string>();

            void Add(string? q)
            {
                if (string.IsNullOrWhiteSpace(q)) return;
                string trimmed = q.Trim();
                if (trimmed.Length < 2) return;
                if (!queries.Contains(trimmed, StringComparer.OrdinalIgnoreCase)) queries.Add(trimmed);
            }

            // YouTube 제목에는 "[명조 카르티시아 테마곰] 가사/번역" 같은 꼬리표가 흔하다.
            // 브래킷이 제목 중간에 있으면 위의 정리가 걷어내지 못해서, 검색용으로만 한 번 더 벗긴다.
            string bareTitle = StripTags(title);
            string bareSub = StripTags(subTitle);

            foreach (string a in new[] { artist, subArtist })
            {
                if (string.IsNullOrEmpty(a)) continue;
                Add($"{a} {bareTitle}");
                if (bareSub != bareTitle) Add($"{a} {bareSub}");
            }

            if (!string.IsNullOrEmpty(bareTitle) && bareSub != bareTitle) Add($"{bareTitle} {bareSub}");

            // 한국 곡은 LRCLIB에 영어 아티스트명으로만 올라온 경우가 많다 — 아티스트 없이 제목만으로도 찾는다
            Add(bareTitle);
            Add(bareSub);
            Add(StripTags(rawTitle));

            return queries;
        }

        /// <summary>
        /// 검색 질의용으로 태그를 걷어낸다. 화면 제목과 같은 규칙(RemoveTags)을 쓴다 —
        /// 예전에는 여기서 대괄호만 지워서 "(Official Video)"가 질의에 그대로 남았다.
        /// </summary>
        private static string StripTags(string s) => RemoveTags(s);

        // ────────────────────────── 캐시 ──────────────────────────

        private bool TryGetCached(string cacheKey, out LyricsResult result)
        {
            if (_memoryCache.TryGetValue(cacheKey, out result!)) return true;

            var fromDisk = LoadFromDiskCache(cacheKey);
            if (fromDisk != null)
            {
                _memoryCache[cacheKey] = fromDisk;
                result = fromDisk;
                return true;
            }

            result = null!;
            return false;
        }

        private void StoreInCache(string cacheKey, LyricsResult result)
        {
            _memoryCache[cacheKey] = result;
            SaveToDiskCache(cacheKey, result);
        }

        private static string GetDiskCachePath(string cacheKey)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(cacheKey));
            var sb = new StringBuilder(hash.Length * 2);
            foreach (byte b in hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return Path.Combine(DiskCacheDir, sb + ".json");
        }

        private static void SaveToDiskCache(string cacheKey, LyricsResult result)
        {
            try
            {
                Directory.CreateDirectory(DiskCacheDir);

                var dto = new DiskCacheDto
                {
                    V = CacheSchemaVersion,
                    Duration = result.SourceDuration?.TotalSeconds ?? 0,
                    Track = result.MatchedTrack,
                    Artist = result.MatchedArtist,
                    Lines = new List<string[]>(result.Lines.Count)
                };
                foreach (var line in result.Lines)
                {
                    dto.Lines.Add(new[] { line.Time.TotalSeconds.ToString(CultureInfo.InvariantCulture), line.Text });
                }

                File.WriteAllText(GetDiskCachePath(cacheKey), JsonSerializer.Serialize(dto, CacheJsonOptions));
            }
            catch (Exception ex)
            {
                Log.Warn($"Lyrics disk cache save failed: {ex.Message}");
            }
        }

        private static void TryDeleteStaleCacheFile(string path)
        {
            try { File.Delete(path); }
            catch (Exception ex) { Log.Warn($"Lyrics cache cleanup failed: {ex.Message}"); }
        }

        private static LyricsResult? LoadFromDiskCache(string cacheKey)
        {
            try
            {
                string path = GetDiskCachePath(cacheKey);
                if (!File.Exists(path)) return null;

                var dto = JsonSerializer.Deserialize<DiskCacheDto>(File.ReadAllText(path), CacheJsonOptions);

                // 버전이 다른 캐시는 버린다 — 예전 매칭 로직이 남긴 잘못된 결과를 물려받지 않기 위해
                // 버전이 다른 캐시는 버리고 파일도 지운다 — 예전 매칭 로직이 남긴 결과가
                // 디스크에 계속 쌓여 있으면 캐시 폴더만 부풀고 다시 쓰이지도 않는다
                if (dto == null || dto.V != CacheSchemaVersion)
                {
                    TryDeleteStaleCacheFile(path);
                    return null;
                }
                if (dto.Lines == null || dto.Lines.Count == 0) return null;

                var lines = new List<LyricLine>(dto.Lines.Count);
                foreach (var pair in dto.Lines)
                {
                    if (pair.Length == 2 &&
                        double.TryParse(pair[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
                    {
                        lines.Add(new LyricLine(TimeSpan.FromSeconds(seconds), pair[1]));
                    }
                }

                if (lines.Count == 0) return null;
                lines.Sort((a, b) => a.Time.CompareTo(b.Time));

                TimeSpan? duration = dto.Duration > 0 ? TimeSpan.FromSeconds(dto.Duration) : null;
                return new LyricsResult(lines, duration, dto.Track, dto.Artist);
            }
            catch (Exception ex)
            {
                Log.Warn($"Lyrics disk cache load failed: {ex.Message}");
                return null;
            }
        }

        private sealed class DiskCacheDto
        {
            public int V { get; set; }
            public double Duration { get; set; }
            public string Track { get; set; } = string.Empty;
            public string Artist { get; set; } = string.Empty;
            public List<string[]> Lines { get; set; } = new();
        }

        // ────────────────────────── 제목/아티스트 정리 ──────────────────────────
        //
        // YouTube 제목은 "아티스트 - 곡명" 하나로 끝나지 않는다. 앞뒤에 [MV]·(Official Video)
        // 같은 태그가 붙고, 곡명이 따옴표로 묶이고, 한글/영문 병기가 괄호로 따라붙는다.
        // 예전에는 정규식 여섯 개가 앞의 결과를 덮어쓰는 방식이라 규칙 하나가 어긋나면 뒤가
        // 전부 무너졌다 — 어퍼스트로피 하나에 "Guns N' Roses - Sweet Child O' Mine"이
        // "Guns N" / "Roses - Sweet Child O"로 갈라졌고, "(Official Audio)"가 단어째 지워진
        // 자리에 빈 괄호만 남아 제목이 "Blinding Lights ( )"가 됐다.
        //
        // 지금은 순서가 분명하다: ① 폭 정규화 ② 태그 제거 ③ 아티스트/곡명 분리
        // ④ 괄호 병기 분리 ⑤ 구두점 정리. 각 단계는 앞 단계의 결과만 본다.

        private static readonly string[] ArtistSeparators = { " - ", " – ", " — ", " _ ", " | " };

        /// <summary>
        /// 대괄호·중괄호 그룹은 안에 뭐가 있든 버린다 — 업로더가 붙인 꼬리표다
        /// ("[MV]", "[4K]", "[명조 카르티시아 테마곡]", "[가사/번역]" 모두 같은 취급).
        /// </summary>
        private static readonly Regex BracketGroup = new(@"\s*[\[\{【][^\]\}】]*[\]\}】]", RegexOptions.Compiled);

        /// <summary>괄호는 안이 태그일 때만 버린다 — "(밤편지)" 같은 병기 이름은 남겨야 한다.</summary>
        private static readonly Regex TaggedParen = new(
            @"\s*\(\s*(?i:official|mv|m/v|music\s*video|video|audio|lyrics?|lyric\s*video|performance|dance\s*practice|special\s*clip|teaser|shorts|live|4k|8k|hd|uhd|color\s*coded|가사|번역|해석|eng\s*sub|sub|playlist|loop)\b[^)]*\)",
            RegexOptions.Compiled);

        /// <summary>
        /// 괄호 밖에 맨몸으로 붙은 태그. 낱말 경계가 분명할 때만 지운다 —
        /// "Video Games"의 video, "Live and Learn"의 live를 곡명에서 떼어내지 않기 위해서다.
        /// </summary>
        private static readonly Regex BareTag = new(
            @"(?<=^|[\s\|·,])[-–—_]?\s*(?i:official\s*m/?v|official\s*(?:audio|video)|music\s*video|lyric\s*video|color\s*coded|m/?v|가사\s*/?\s*번역|가사|번역|해석|eng\s*sub)(?=$|[\s\|·,])",
            RegexOptions.Compiled);

        /// <summary>
        /// 곡명이 따옴표로 묶인 형식 — 앞은 아티스트로 본다.
        /// 여는 따옴표 앞에 낱말이 붙어 있으면 따옴표가 아니다: "Guns N' Roses"의 어퍼스트로피,
        /// "Don't"의 어퍼스트로피, "O' Mine"의 어퍼스트로피는 전부 낱말 안에 박혀 있다.
        /// </summary>
        private static readonly Regex QuotedTitle = new(
            @"^(?<before>.*?)(?<=^|[\s\-–—_|,·])[""'‘’“”](?<inside>[^""'‘’“”]+?)[""'‘’“”](?=$|[\s\-–—_|,·\)])",
            RegexOptions.Compiled);

        public static (string Title, string Artist) ParseTitleAndArtist(string rawTitle, string rawArtist)
        {
            var parsed = ParseTitleAndArtistFull(rawTitle, rawArtist);
            return (parsed.CleanTitle, parsed.CleanArtist);
        }

        public static (string CleanTitle, string CleanArtist, string SubTitle, string SubArtist) ParseTitleAndArtistFull(string rawTitle, string rawArtist)
        {
            string originalTitle = NormalizeWidth((rawTitle ?? string.Empty).Trim());
            string title = RemoveTags(originalTitle);
            string artist = CleanChannelName(NormalizeWidth((rawArtist ?? string.Empty).Trim()));

            // 곡명이 따옴표로 묶여 있으면 그게 가장 확실한 단서다 (IVE 아이브 'I WANT' MV)
            var quoted = QuotedTitle.Match(title);

            if (quoted.Success)
            {
                string before = quoted.Groups["before"].Value;
                string inside = quoted.Groups["inside"].Value;
                if (!string.IsNullOrWhiteSpace(inside))
                {
                    title = inside;
                    if (!string.IsNullOrWhiteSpace(before)) artist = before;
                }
            }
            else
            {
                foreach (string separator in ArtistSeparators)
                {
                    int at = title.IndexOf(separator, StringComparison.Ordinal);
                    if (at <= 0) continue;

                    string before = title[..at];
                    string after = title[(at + separator.Length)..];
                    if (string.IsNullOrWhiteSpace(before) || string.IsNullOrWhiteSpace(after)) continue;

                    artist = before;
                    title = after;
                    break;
                }
            }

            var titleParts = SplitAltName(title);
            var artistParts = SplitAltName(artist);

            // 태그를 다 걷어내고 남는 게 없으면 원본을 그대로 보여준다 ("M/V" 같은 제목)
            string cleanTitle = CleanName(titleParts.Main);
            if (cleanTitle.Length == 0) cleanTitle = CleanName(originalTitle);

            return (
                cleanTitle,
                CleanName(artistParts.Main),
                CleanName(titleParts.Alt),
                CleanName(artistParts.Alt));
        }

        /// <summary>업로더 이름은 아티스트가 아니다 — 배급·방송 채널이면 버리고 "- Topic"은 떼어낸다.</summary>
        private static string CleanChannelName(string artist)
        {
            if (string.IsNullOrEmpty(artist)) return string.Empty;

            if (artist.EndsWith("- Topic", StringComparison.OrdinalIgnoreCase))
                artist = artist[..^7].Trim();

            if (artist.EndsWith("VEVO", StringComparison.Ordinal))
                return string.Empty;

            bool isChannel =
                ChannelsToIgnore.Contains(artist) ||
                artist.EndsWith("Entertainment", StringComparison.OrdinalIgnoreCase) ||
                artist.EndsWith("Records", StringComparison.OrdinalIgnoreCase) ||
                artist.EndsWith("Labels", StringComparison.OrdinalIgnoreCase) ||
                artist.EndsWith("Official", StringComparison.OrdinalIgnoreCase);

            return isChannel ? string.Empty : artist;
        }

        /// <summary>전각 괄호·따옴표를 반각으로 — 일본·중국 곡 제목에 ［］가 흔하다.</summary>
        private static string NormalizeWidth(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;

            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                sb.Append(c switch
                {
                    '［' => '[', '］' => ']',
                    '【' => '[', '】' => ']',
                    '（' => '(', '）' => ')',
                    '｛' => '{', '｝' => '}',
                    '“' => '"', '”' => '"',
                    '‘' => '\'', '’' => '\'',
                    '／' => '/',
                    _ => c
                });
            }
            return sb.ToString();
        }

        /// <summary>태그를 걷어낸다. 괄호 안 태그를 먼저 지워야 "Blinding Lights ( )" 같은 빈 괄호가 남지 않는다.</summary>
        private static string RemoveTags(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;

            string result = NormalizeWidth(s);
            for (int pass = 0; pass < 3; pass++)
            {
                string next = BracketGroup.Replace(result, " ");
                next = TaggedParen.Replace(next, " ");
                next = BareTag.Replace(next, " ");
                if (next == result) break;
                result = next;
            }

            result = Regex.Replace(result, @"\s+", " ");
            return Regex.Replace(result, @"\s*[-–—_|·,]+\s*$", string.Empty).Trim();
        }

        /// <summary>
        /// 마지막 괄호 그룹을 (앞부분, 괄호 안)으로 가른다 — "예뻤어 (You Were Beautiful)"처럼
        /// 한글/영문 병기가 괄호로 붙는 형식. 닫는 괄호 뒤에 글자가 붙어 있으면 이름의 일부로 본다.
        /// </summary>
        private static (string Main, string Alt) SplitAltName(string s)
        {
            if (string.IsNullOrEmpty(s)) return (s ?? string.Empty, string.Empty);

            int depth = 0;
            int close = -1;
            for (int i = s.Length - 1; i >= 0; i--)
            {
                char c = s[i];
                if (c == ')') { depth++; if (close < 0) close = i; }
                else if (c == '(')
                {
                    depth--;
                    if (depth > 0) continue;

                    // "(G)I-DLE"의 "(G)"처럼 뒤에 글자가 붙어 있으면 병기가 아니라 이름이다
                    bool partOfName = close < s.Length - 1 && !char.IsWhiteSpace(s[close + 1]);
                    if (!partOfName)
                    {
                        string main = s[..i].TrimEnd();
                        string alt = s[(i + 1)..close].Trim();
                        if (main.Length > 0 && alt.Length > 0) return (main, alt);
                    }
                    return (s, string.Empty);
                }
            }

            return (s, string.Empty);
        }

        /// <summary>남은 구두점을 정리한다. 곡명 안의 어퍼스트로피는 살린다.</summary>
        private static string CleanName(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;

            string cleaned = Regex.Replace(s, @"\(\s*\)|\[\s*\]", " ");
            cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
            // 어퍼스트로피는 남긴다 — "O' Mine", "Believin'"처럼 곡명의 일부인 경우가 많다
            cleaned = cleaned.Trim('"', '“', '”', '-', '–', '—', '|', '_', ',', '·', ' ');

            return cleaned.Trim().Trim('/', ' ');
        }


        private static List<LyricLine> ParseLrc(string lrcContent)
        {
            var result = new List<LyricLine>();
            var lines = lrcContent.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var regex = new Regex(@"\[(\d+):(\d+(?:\.\d+)?)\](.*)");

            foreach (var line in lines)
            {
                var match = regex.Match(line);
                if (match.Success)
                {
                    if (int.TryParse(match.Groups[1].Value, out int minutes) &&
                        double.TryParse(match.Groups[2].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double seconds))
                    {
                        string text = match.Groups[3].Value.Trim();
                        var time = TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);

                        // 빈 시각 표시도 그대로 담는다. LRC에서 빈 줄은 "여기서부터 반주"라는
                        // 뜻이고, 싱크를 소리로 맞출 때 이 표시가 노래/쉼을 가르는 근거가 된다.
                        result.Add(new LyricLine(time, text));
                    }
                }
            }

            result.Sort((a, b) => a.Time.CompareTo(b.Time));
            return result;
        }

        public void Dispose()
        {
            _currentCts?.Cancel();
            _currentCts?.Dispose();
            _httpClient.Dispose();
        }
    }
}
