using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace TopDock.Services
{
    /// <summary>
    /// 재생 중인 미디어의 videoId를 찾는 서비스.
    /// 1순위: YouTube Data API v3 (설정에서 API 키 입력 시)
    /// 2순위: youtube.com 검색 결과 HTML 스크레이핑 (폴백)
    /// </summary>
    public class YouTubeSearchService
    {
        private const int MaxScrapeTitleLength = 90;

        private readonly HttpClient _httpClient;
        private readonly HttpClient _scrapeClient;

        public YouTubeSearchService()
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            _scrapeClient = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            // 스크래이핑 시 브라우저 UA를 넣어야 정상적인 HTML을 반환받을 확률이 높다
            _scrapeClient.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            _scrapeClient.DefaultRequestHeaders.Add("Accept-Language", "ko-KR,ko;q=0.9,en-US;q=0.8,en;q=0.7");
        }

        public async Task<string?> SearchVideoIdAsync(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return null;

            string apiKey = ConfigService.Current.YouTubeApiKey;
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                var viaApi = await SearchViaApiAsync(query, apiKey);
                if (viaApi != null) return viaApi;
            }

            return await SearchViaScrapingAsync(query);
        }

        private async Task<string?> SearchViaApiAsync(string query, string apiKey)
        {
            try
            {
                string url = $"https://www.googleapis.com/youtube/v3/search?part=id&type=video&maxResults=1&q={Uri.EscapeDataString(query)}&key={Uri.EscapeDataString(apiKey)}";
                using var response = await _httpClient.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                {
                    System.Diagnostics.Debug.WriteLine($"YouTube API returned {response.StatusCode}");
                    return null;
                }

                string json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                foreach (var item in root.EnumerateArray())
                {
                    if (item.TryGetProperty("id", out var id) &&
                        id.TryGetProperty("videoId", out var videoId))
                    {
                        string? vid = videoId.GetString();
                        if (!string.IsNullOrWhiteSpace(vid)) return vid;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"YouTube API search failed: {ex.Message}");
            }
            return null;
        }

        private async Task<string?> SearchViaScrapingAsync(string query)
        {
            try
            {
                string url = $"https://www.youtube.com/results?search_query={Uri.EscapeDataString(query)}";
                using var response = await _scrapeClient.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                {
                    System.Diagnostics.Debug.WriteLine($"YouTube scrape returned {response.StatusCode}");
                    return null;
                }

                string html = await response.Content.ReadAsStringAsync();

                // ytInitialPlayerResponse/ytInitialData의 watchEndpoint에서 videoId 추출
                foreach (var token in FindAllMatches(html, "\"watchEndpoint\":{\"videoId\":\""))
                {
                    int start = token + "\"watchEndpoint\":{\"videoId\":\"".Length;
                    if (start + 11 <= html.Length)
                    {
                        string candidate = html.Substring(start, 11);
                        if (candidate.All(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_'))
                        {
                            return candidate;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to scrape YouTube: {ex.Message}");
            }
            return null;
        }

        private static System.Collections.Generic.IEnumerable<int> FindAllMatches(string text, string needle)
        {
            int index = 0;
            while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
            {
                yield return index;
                index += needle.Length;
            }
        }
    }
}
