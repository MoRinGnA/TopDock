using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace TopDock.Services
{
    /// <summary>
    /// 키 불필요 실시간 날씨. 위치는 IP 기반(ipwho.is), 날씨는 Open-Meteo(둘 다 무료·오픈 API).
    /// 30분 캐시. 실패해도 비서 기능을 막지 않게 Summary가 빈 문자열이면 주입을 건너뛴다.
    /// </summary>
    public class WeatherService : IDisposable
    {
        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
        private DateTime _fetchedAtUtc = DateTime.MinValue;
        private string _summary = string.Empty;

        /// <summary>마지막으로 받은 날씨 요약. 예: "서울 18°C, 흐림". 실패 시 빈 문자열.</summary>
        public string CurrentSummary => _summary;

        /// <summary>필요 시(30분 경과) 새로 받아 요약을 반환한다.</summary>
        public async Task<string> GetSummaryAsync(bool forceRefresh = false)
        {
            if (!forceRefresh && _summary.Length > 0 &&
                DateTime.UtcNow - _fetchedAtUtc < TimeSpan.FromMinutes(30))
            {
                return _summary;
            }

            try
            {
                (double lat, double lon, string city) = await ResolveLocationAsync().ConfigureAwait(false);
                _summary = await FetchWeatherAsync(lat, lon, city).ConfigureAwait(false);
                _fetchedAtUtc = DateTime.UtcNow;
                Log.Info($"Weather updated: {_summary}");
            }
            catch (Exception ex)
            {
                Log.Warn($"Weather fetch failed: {ex.Message}");
            }
            return _summary;
        }

        private async Task<(double Lat, double Lon, string City)> ResolveLocationAsync()
        {
            using var resp = await _http.GetAsync("https://ipwho.is/").ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("success", out var ok) || ok.ValueKind != JsonValueKind.True)
            {
                throw new HttpRequestException("ipwho.is lookup failed");
            }
            return (
                root.GetProperty("latitude").GetDouble(),
                root.GetProperty("longitude").GetDouble(),
                root.TryGetProperty("city", out var city) ? city.GetString() ?? "현재 위치" : "현재 위치");
        }

        private async Task<string> FetchWeatherAsync(double lat, double lon, string city)
        {
            string url = $"https://api.open-meteo.com/v1/forecast?latitude={lat.ToString(System.Globalization.CultureInfo.InvariantCulture)}&longitude={lon.ToString(System.Globalization.CultureInfo.InvariantCulture)}&current=temperature_2m,weather_code";
            using var resp = await _http.GetAsync(url).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
            JsonElement cur = doc.RootElement.GetProperty("current");
            double temp = cur.GetProperty("temperature_2m").GetDouble();
            int code = cur.GetProperty("weather_code").GetInt32();
            return $"{city} {temp:0}°C, {DescribeCode(code)}";
        }

        /// <summary>WMO 표준 날씨 코드 → 한국어.</summary>
        private static string DescribeCode(int code) => code switch
        {
            0 => "맑음",
            1 => "대체로 맑음",
            2 => "구름 조금",
            3 => "흐림",
            45 or 48 => "안개",
            51 or 53 or 55 => "이슬비",
            56 or 57 => "얼어붙는 이슬비",
            61 => "약한 비",
            63 => "비",
            65 => "강한 비",
            66 or 67 => "얼어붙는 비",
            71 => "약한 눈",
            73 => "눈",
            75 => "강한 눈",
            77 => "진눈깨비",
            80 => "소나기",
            81 or 82 => "강한 소나기",
            85 or 86 => "소낙눈",
            95 => "뇌우",
            96 or 99 => "뇌우(우박)",
            _ => "알 수 없음"
        };

        public void Dispose() => _http.Dispose();
    }
}
