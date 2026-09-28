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

    public class LyricsService : IDisposable
    {
        private readonly HttpClient _httpClient;
        private CancellationTokenSource? _currentCts;
        private double _lrcDuration = 0; // LRCLIB에서 받은 음원 길이(초)

        private sealed record LyricsEntry(List<LyricLine> Lines, double Duration);

        // 메모리 캐시 (세션 동안 유효)
        private readonly ConcurrentDictionary<string, LyricsEntry> _memoryCache = new();

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

        public async Task<List<LyricLine>?> GetLyricsAsync(string rawTitle, string rawArtist)
        {
            if (string.IsNullOrWhiteSpace(rawTitle) || rawTitle == "재생 중인 미디어 없음")
                return null;

            _lrcDuration = 0;

            var (cleanTitle, cleanArtist, subTitle, subArtist) = ParseTitleAndArtistFull(rawTitle, rawArtist);

            string cacheKey = $"{cleanArtist.ToLowerInvariant()}:::{cleanTitle.ToLowerInvariant()}";

            // 캐시 히트 시에도 duration을 함께 복원해야 오프셋 보정(GetAdjustedPosition)이 유지된다
            if (TryGetCached(cacheKey, out var cached))
            {
                _lrcDuration = cached.Duration;
                return cached.Lines;
            }

            _currentCts?.Cancel();
            _currentCts = new CancellationTokenSource();
            var ct = _currentCts.Token;

            try
            {
                // Build multi-tier search queries designed specifically to match LRCLIB's Korean/English catalog
                var candidates = new List<string>();

                void AddCandidate(string? q)
                {
                    if (!string.IsNullOrWhiteSpace(q))
                    {
                        string trimmed = q.Trim();
                        if (trimmed.Length >= 2 && !candidates.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                        {
                            candidates.Add(trimmed);
                        }
                    }
                }

                // 1. "Artist Title" variations
                if (!string.IsNullOrEmpty(cleanArtist) && !string.IsNullOrEmpty(cleanTitle))
                {
                    AddCandidate($"{cleanArtist} {cleanTitle}");
                }
                if (!string.IsNullOrEmpty(cleanArtist) && !string.IsNullOrEmpty(subTitle))
                {
                    AddCandidate($"{cleanArtist} {subTitle}");
                }
                if (!string.IsNullOrEmpty(subArtist) && !string.IsNullOrEmpty(cleanTitle))
                {
                    AddCandidate($"{subArtist} {cleanTitle}");
                }
                if (!string.IsNullOrEmpty(subArtist) && !string.IsNullOrEmpty(subTitle))
                {
                    AddCandidate($"{subArtist} {subTitle}");
                }

                // 2. Title combined variations
                if (!string.IsNullOrEmpty(cleanTitle) && !string.IsNullOrEmpty(subTitle))
                {
                    AddCandidate($"{cleanTitle} {subTitle}");
                }

                // 3. Title-only variations (CRITICAL for Korean songs where LRCLIB has English artist name!)
                AddCandidate(cleanTitle);
                AddCandidate(subTitle);

                // 4. Raw title cleaned fallback
                string rawCleaned = Regex.Replace(rawTitle, @"(?i)\[[^\]]*\]|\b(MV|M/V|Official|Music Video|Audio|Lyrics|가사)\b", " ").Trim();
                rawCleaned = Regex.Replace(rawCleaned, @"\s+", " ").Trim();
                AddCandidate(rawCleaned);

                foreach (var query in candidates)
                {
                    if (ct.IsCancellationRequested) break;

                    string searchUrl = $"https://lrclib.net/api/search?q={Uri.EscapeDataString(query)}";
                    var entry = await FetchAndParseAsync(searchUrl, ct);
                    if (entry != null)
                    {
                        StoreInCache(cacheKey, entry);
                        _lrcDuration = entry.Duration;
                        return entry.Lines;
                    }
                }

                // Final exact match attempt
                if (!string.IsNullOrEmpty(cleanArtist) && !string.IsNullOrEmpty(cleanTitle) && !ct.IsCancellationRequested)
                {
                    string targetUrl = $"https://lrclib.net/api/search?track_name={Uri.EscapeDataString(cleanTitle)}&artist_name={Uri.EscapeDataString(cleanArtist)}";
                    var entry = await FetchAndParseAsync(targetUrl, ct);
                    if (entry != null)
                    {
                        StoreInCache(cacheKey, entry);
                        _lrcDuration = entry.Duration;
                        return entry.Lines;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"LyricsService fetch error: {ex.Message}");
            }

            return null;
        }

        private bool TryGetCached(string cacheKey, out LyricsEntry entry)
        {
            // 1) 메모리 캐시
            if (_memoryCache.TryGetValue(cacheKey, out entry!))
            {
                return true;
            }

            // 2) 디스크 캐시
            var diskEntry = LoadFromDiskCache(cacheKey);
            if (diskEntry != null)
            {
                _memoryCache[cacheKey] = diskEntry;
                entry = diskEntry;
                return true;
            }

            entry = new LyricsEntry(new List<LyricLine>(), 0);
            return false;
        }

        private void StoreInCache(string cacheKey, LyricsEntry entry)
        {
            _memoryCache[cacheKey] = entry;
            SaveToDiskCache(cacheKey, entry);
        }

        private static string GetDiskCachePath(string cacheKey)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(cacheKey));
            var sb = new StringBuilder(hash.Length * 2);
            foreach (byte b in hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return Path.Combine(DiskCacheDir, sb + ".json");
        }

        private static void SaveToDiskCache(string cacheKey, LyricsEntry entry)
        {
            try
            {
                Directory.CreateDirectory(DiskCacheDir);

                var dto = new DiskCacheDto
                {
                    Duration = entry.Duration,
                    Lines = new List<string[]>(entry.Lines.Count)
                };
                foreach (var line in entry.Lines)
                {
                    dto.Lines.Add(new[] { line.Time.TotalSeconds.ToString(CultureInfo.InvariantCulture), line.Text });
                }

                string json = JsonSerializer.Serialize(dto, CacheJsonOptions);
                File.WriteAllText(GetDiskCachePath(cacheKey), json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Lyrics disk cache save error: {ex.Message}");
            }
        }

        private static LyricsEntry? LoadFromDiskCache(string cacheKey)
        {
            try
            {
                string path = GetDiskCachePath(cacheKey);
                if (!File.Exists(path)) return null;

                string json = File.ReadAllText(path);
                var dto = JsonSerializer.Deserialize<DiskCacheDto>(json, CacheJsonOptions);
                if (dto?.Lines == null || dto.Lines.Count == 0) return null;

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
                return new LyricsEntry(lines, dto.Duration);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Lyrics disk cache load error: {ex.Message}");
                return null;
            }
        }

        private sealed class DiskCacheDto
        {
            public double Duration { get; set; }
            public List<string[]> Lines { get; set; } = new();
        }

        private async Task<LyricsEntry?> FetchAndParseAsync(string url, CancellationToken ct)
        {
            try
            {
                using var response = await _httpClient.GetAsync(url, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return null;

                string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
                {
                    foreach (var item in root.EnumerateArray())
                    {
                        if (item.TryGetProperty("syncedLyrics", out var synProp) &&
                            !string.IsNullOrEmpty(synProp.GetString()))
                        {
                            var parsed = ParseLrc(synProp.GetString()!);
                            if (parsed.Count > 0)
                            {
                                // LRCLIB의 음원 길이를 캐시 엔트리에 함께 저장
                                double duration = 0;
                                if (item.TryGetProperty("duration", out var durProp) &&
                                    durProp.ValueKind == JsonValueKind.Number)
                                {
                                    duration = durProp.GetDouble();
                                }
                                return new LyricsEntry(parsed, duration);
                            }
                        }
                    }
                }
            }
            catch { }
            return null;
        }

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

        // mediaDuration: SMTC에서 받아온 실제 재생 중인 영상의 총 길이
        public TimeSpan GetAdjustedPosition(TimeSpan realPosition, TimeSpan mediaDuration)
        {
            // LRCLIB 음원 길이를 모르거나 미디어 길이가 없으면 보정 안 함
            if (_lrcDuration <= 0 || mediaDuration.TotalSeconds <= 0)
                return realPosition;

            double offset = mediaDuration.TotalSeconds - _lrcDuration;

            // 오프셋이 너무 크거나(60초 이상), 음수거나 거의 0이면 무시 (같은 음원 재생 시 오작동 방지)
            if (offset < 1.0 || offset > 60.0)
                return realPosition;

            double adjusted = realPosition.TotalSeconds - offset;
            return TimeSpan.FromSeconds(Math.Max(0, adjusted));
        }

        public void Dispose()
        {
            _currentCts?.Cancel();
            _currentCts?.Dispose();
            _httpClient.Dispose();
        }
    }
}
