using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace TopDock.Services
{
    /// <summary>
    /// 요청 시에만 수행하는 실시간 날씨 조회. 위치는 IP 기반(ipwho.is), 날씨는 Open-Meteo(둘 다 무료·오픈 API).
    /// 캐시하지 않아 도구 호출 때마다 새 데이터를 가져온다.
    /// </summary>
    public class WeatherService : IDisposable
    {
        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };

        /// <summary>호출될 때마다 위치와 현재·오늘·내일 날씨를 인터넷에서 새로 조회한다. 실패 시 빈 문자열.</summary>
        public async Task<string> GetSummaryAsync()
        {
            try
            {
                (double lat, double lon, string city) = await ResolveLocationAsync().ConfigureAwait(false);
                string summary = await FetchWeatherAsync(lat, lon, city).ConfigureAwait(false);
                Log.Info($"Weather updated: {summary}");
                return summary;
            }
            catch (Exception ex)
            {
                Log.Warn($"Weather fetch failed: {ex.Message}");
                return string.Empty;
            }
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
            string url = $"https://api.open-meteo.com/v1/forecast?latitude={lat.ToString(System.Globalization.CultureInfo.InvariantCulture)}&longitude={lon.ToString(System.Globalization.CultureInfo.InvariantCulture)}&current=temperature_2m,weather_code&daily=weather_code,temperature_2m_max,temperature_2m_min&forecast_days=2&timezone=auto";
            using var resp = await _http.GetAsync(url).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
            JsonElement root = doc.RootElement;
            JsonElement cur = root.GetProperty("current");
            double currentTemp = cur.GetProperty("temperature_2m").GetDouble();
            int currentCode = cur.GetProperty("weather_code").GetInt32();
            string observedAt = cur.GetProperty("time").GetString() ?? "시각 미상";

            JsonElement daily = root.GetProperty("daily");
            JsonElement dates = daily.GetProperty("time");
            JsonElement codes = daily.GetProperty("weather_code");
            JsonElement highs = daily.GetProperty("temperature_2m_max");
            JsonElement lows = daily.GetProperty("temperature_2m_min");
            string today = FormatDailyForecast(dates, codes, highs, lows, 0);
            string tomorrow = FormatDailyForecast(dates, codes, highs, lows, 1);

            return $"{city} 현지 시각 {observedAt} 현재 {currentTemp:0}°C, {DescribeCode(currentCode)} · 오늘 {today} · 내일 {tomorrow}";
        }

        private static string FormatDailyForecast(JsonElement dates, JsonElement codes, JsonElement highs, JsonElement lows, int index)
        {
            if (dates.GetArrayLength() <= index || codes.GetArrayLength() <= index ||
                highs.GetArrayLength() <= index || lows.GetArrayLength() <= index)
            {
                return "예보 없음";
            }

            string date = dates[index].GetString() ?? (index == 0 ? "오늘" : "내일");
            double high = highs[index].GetDouble();
            double low = lows[index].GetDouble();
            int code = codes[index].GetInt32();
            return $"{date} {DescribeCode(code)}, 최저 {low:0}°C / 최고 {high:0}°C";
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
