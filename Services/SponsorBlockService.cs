using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using TopDock.Models;

namespace TopDock.Services
{
    public class SponsorBlockService
    {
        private readonly HttpClient _httpClient;

        public SponsorBlockService()
        {
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(5)
            };
        }

        /// <summary>
        /// 설정에서 활성화한 카테고리만 조합해 SponsorBlock에서 구간을 가져온다.
        /// </summary>
        public async Task<List<SkipSegment>> GetSegmentsAsync(
            string videoId,
            bool includeIntro,
            bool includeOutro,
            bool includeIntermission,
            bool includeMusicOfftopic)
        {
            var segments = new List<SkipSegment>();

            var categories = new List<string>();
            if (includeMusicOfftopic) categories.Add("music_offtopic");
            if (includeIntro) categories.Add("intro");
            if (includeOutro) categories.Add("outro");
            if (includeIntermission) categories.Add("intermission");

            if (categories.Count == 0 || string.IsNullOrWhiteSpace(videoId)) return segments;

            // categories를 JSON 배열 형태로 URL 인코딩
            string categoriesJson = System.Text.Json.JsonSerializer.Serialize(categories);
            string url = $"https://sponsor.ajay.app/api/skipSegments?videoID={Uri.EscapeDataString(videoId)}&categories={Uri.EscapeDataString(categoriesJson)}";

            try
            {
                using var response = await _httpClient.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                {
                    // 404는 해당 영상에 구간 정보가 없다는 정상 응답
                    if (response.StatusCode != System.Net.HttpStatusCode.NotFound)
                    {
                        System.Diagnostics.Debug.WriteLine($"SponsorBlock API returned {response.StatusCode}");
                    }
                    return segments;
                }

                string json = await response.Content.ReadAsStringAsync();
                using JsonDocument doc = JsonDocument.Parse(json);

                foreach (JsonElement element in doc.RootElement.EnumerateArray())
                {
                    if (!element.TryGetProperty("segment", out var segment) ||
                        segment.ValueKind != JsonValueKind.Array ||
                        segment.GetArrayLength() < 2)
                    {
                        continue;
                    }

                    float startSeconds = segment[0].GetSingle();
                    float endSeconds = segment[1].GetSingle();
                    string category = element.TryGetProperty("category", out var catProp)
                        ? catProp.GetString() ?? string.Empty
                        : string.Empty;

                    segments.Add(new SkipSegment
                    {
                        StartTime = TimeSpan.FromSeconds(startSeconds),
                        EndTime = TimeSpan.FromSeconds(endSeconds),
                        Category = category
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to fetch SponsorBlock segments: {ex.Message}");
            }

            segments.Sort((a, b) => a.StartTime.CompareTo(b.StartTime));
            return segments;
        }
    }
}
