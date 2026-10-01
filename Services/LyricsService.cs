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
        /// 제목에 붙은 꼬리표를 걷어낸다 — 브래킷 그룹(위치 무관), 가사/번역·Lyrics 같은
        /// 단어, 구분자. 검색 질의용이고 화면 제목은 ParseTitleAndArtist가 따로 만든다.
        /// </summary>
        private static string StripTags(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;

            string stripped = Regex.Replace(s, @"\[[^\]]*\]|【[^】]*】|\{[^}]*\}", " ");
            // 구분자를 먼저 띄운다 — 그래야 "가사/번역"이 한 단어로 굳지 않고 "가사", "번역"으로 갈난다
            stripped = Regex.Replace(stripped, @"[/\\|_]+|(?<=\s)-(?=\s)", " ");
            stripped = Regex.Replace(stripped,
                @"(?i)(?<=\s|^)(가사|번역|해석|lyrics?|eng\s*sub|sub|color\s*coded|romanized)(?=\s|$)", " ");
            return Regex.Replace(stripped, @"\s+", " ").Trim();
        }

        // ────────────────────────── 싱크 보정 ──────────────────────────

        /// <summary>
        /// 뮤직비디오 인트로만큼 가사가 앞서 나가는 것을 보정한다.
        /// 소스(음원) 길이를 모르면 손대지 않고, 보정 폭이 1초 미만이거나 30초를 넘으면
        /// 추측이 위험하므로 역시 손대지 않는다.
        /// </summary>
        public static TimeSpan AdjustForSourceOffset(TimeSpan realPosition, TimeSpan mediaDuration, TimeSpan? sourceDuration)
        {
            if (sourceDuration is not { } source) return realPosition;
            if (mediaDuration.TotalSeconds <= 0 || source.TotalSeconds <= 0) return realPosition;

            double offset = mediaDuration.TotalSeconds - source.TotalSeconds;
            if (offset < 1.0 || offset > 30.0) return realPosition;

            return TimeSpan.FromSeconds(Math.Max(0, realPosition.TotalSeconds - offset));
        }

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
        // 주의: 이 함수의 결과는 "검색 질의"와 "화면에 보이는 제목" 양쪽에 쓰인다.
        // 그래서 규칙을 바꿀 때는 가사 검색만이 아니라 노치에 뜨는 제목도 함께 확인해야 한다.

        public static (string Title, string Artist) ParseTitleAndArtist(string rawTitle, string rawArtist)
        {
            var res = ParseTitleAndArtistFull(rawTitle, rawArtist);
            return (res.CleanTitle, res.CleanArtist);
        }

        public static (string CleanTitle, string CleanArtist, string SubTitle, string SubArtist) ParseTitleAndArtistFull(string rawTitle, string rawArtist)
        {
            string title = rawTitle?.Trim() ?? string.Empty;
            string artist = rawArtist?.Trim() ?? string.Empty;

            // 1. Ignore distributor/broadcasting channels
            if (ChannelsToIgnore.Contains(artist) ||
                artist.EndsWith("Entertainment", StringComparison.OrdinalIgnoreCase) ||
                artist.EndsWith("Records", StringComparison.OrdinalIgnoreCase) ||
                artist.EndsWith("Labels", StringComparison.OrdinalIgnoreCase) ||
                artist.EndsWith("Official", StringComparison.OrdinalIgnoreCase))
            {
                artist = string.Empty;
            }

            if (artist.EndsWith("- Topic", StringComparison.OrdinalIgnoreCase))
            {
                artist = artist.Substring(0, artist.Length - 7).Trim();
            }

            // 2. Strip ALL leading brackets (e.g. "[MV]", "[M/V]", "[가사/Lyrics]", "[Official Audio]", "【MV】")
            title = Regex.Replace(title, @"^\s*(\[[^\]]*\]|【[^】]*】)\s*", "");
            title = Regex.Replace(title, @"\s*(\[[^\]]*\]|【[^】]*】)\s*$", "");

            // 3. Strip trailing keywords & brackets
            title = Regex.Replace(title, @"(?i)\b(Official\s*M/?V|M/?V|Music\s*Video|Performance\s*Video|Official\s*Audio|Lyric\s*Video|Special\s*Clip)\b", " ");
            title = Regex.Replace(title, @"\s*\((Official|MV|M/V|Audio|Video|가사|Lyrics|Special\s*Clip)[^\)]*\)\s*$", "", RegexOptions.IgnoreCase);

            // 4. Match quotation pattern: Artist 'Song Title' (e.g. Hearts2Hearts 'The Chase', QWER '고민중독')
            var quoteMatch = Regex.Match(title, @"^(.*?)\s*[\u0027\u0022\u2018\u201C]([^\u0027\u0022\u2019\u201D]+)[\u0027\u0022\u2019\u201D]");
            if (quoteMatch.Success)
            {
                string before = quoteMatch.Groups[1].Value.Trim();
                string inside = quoteMatch.Groups[2].Value.Trim();

                if (!string.IsNullOrEmpty(inside))
                {
                    title = inside;
                    if (!string.IsNullOrEmpty(before))
                    {
                        artist = before;
                    }
                }
            }
            // 5. Match separator pattern: "Artist - Title" or "Artist _ Title" or "Artist | Title"
            else
            {
                string[] separators = new[] { " - ", " – ", " — ", " _ ", " | " };
                foreach (var sep in separators)
                {
                    if (title.Contains(sep))
                    {
                        var parts = title.Split(new[] { sep }, 2, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length == 2)
                        {
                            artist = parts[0].Trim();
                            title = parts[1].Trim();
                            break;
                        }
                    }
                }
            }

            // 6. Extract sub-title and sub-artist from bilingual parentheses (e.g. "Through the night(밤편지)", "DAY6 (데이식스)")
            string subTitle = string.Empty;
            var titleParen = Regex.Match(title, @"^(.*?)\s*\(([^\)]+)\)");
            if (titleParen.Success)
            {
                string mainPart = titleParen.Groups[1].Value.Trim();
                string parenPart = titleParen.Groups[2].Value.Trim();
                if (!string.IsNullOrEmpty(mainPart) && !string.IsNullOrEmpty(parenPart))
                {
                    title = mainPart;
                    subTitle = parenPart;
                }
            }

            string subArtist = string.Empty;
            var artistParen = Regex.Match(artist, @"^(.*?)\s*\(([^\)]+)\)");
            if (artistParen.Success)
            {
                string mainPart = artistParen.Groups[1].Value.Trim();
                string parenPart = artistParen.Groups[2].Value.Trim();
                if (!string.IsNullOrEmpty(mainPart) && !string.IsNullOrEmpty(parenPart))
                {
                    artist = mainPart;
                    subArtist = parenPart;
                }
            }

            // Cleanup quotes & whitespace
            title = Regex.Replace(title, @"[\u0027\u0022\u2018\u2019\u201C\u201D]", " ").Trim();
            artist = Regex.Replace(artist, @"[\u0027\u0022\u2018\u2019\u201C\u201D]", " ").Trim();
            subTitle = Regex.Replace(subTitle, @"[\u0027\u0022\u2018\u2019\u201C\u201D]", " ").Trim();
            subArtist = Regex.Replace(subArtist, @"[\u0027\u0022\u2018\u2019\u201C\u201D]", " ").Trim();

            title = Regex.Replace(title, @"\s+", " ").Trim();
            artist = Regex.Replace(artist, @"\s+", " ").Trim();
            subTitle = Regex.Replace(subTitle, @"\s+", " ").Trim();
            subArtist = Regex.Replace(subArtist, @"\s+", " ").Trim();

            return (title, artist, subTitle, subArtist);
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
                        if (!string.IsNullOrEmpty(text))
                        {
                            result.Add(new LyricLine(time, text));
                        }
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
