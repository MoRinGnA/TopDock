using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TopDock.Models;

namespace TopDock.Services
{
    /// <summary>
    /// SponsorBlock 구간 조회와 자동 스킵 판단을 담당하는 코디네이터.
    /// videoId는 YouTubeSearchService가, 실제 seek는 MediaService가 수행한다.
    /// </summary>
    public class SponsorSkipService : IDisposable
    {
        private readonly SponsorBlockService _sponsorBlock = new();
        private readonly YouTubeSearchService _youTubeSearch = new();

        private List<SkipSegment> _segments = new();
        private string _segmentsKey = string.Empty;
        private bool _fetchInFlight;
        private DateTime _lastSkipAt = DateTime.MinValue;
        private DateTime _lastFailedLookupAt = DateTime.MinValue;

        // 스킵 직후에는 타임라인 반영 지연 때문에 같은 구간으로 재판정되는 것을 막는 쿨다운
        private static readonly TimeSpan SkipCooldown = TimeSpan.FromSeconds(4);
        // 세그먼트 조회 실패/결과 없음 후 재시도 간격
        private static readonly TimeSpan FailedLookupRetry = TimeSpan.FromMinutes(2);

        public bool IsEnabled { get; set; } = true;

        /// <summary>"auto"면 구간 진입 시 자동 seek, "manual"이면 진행바 마커와 건너뛰기 버튼만 제공.</summary>
        public string Mode { get; set; } = "auto";

        /// <summary>현재 재생 위치가 스킵 대상 구간 안에 있는지 (수동 스킵 버튼 표시용).</summary>
        public bool IsInSkipSegment(TimeSpan position, TimeSpan duration)
        {
            if (!IsEnabled || _segments.Count == 0) return false;
            foreach (var seg in _segments)
            {
                if (duration > TimeSpan.Zero && seg.StartTime >= duration) continue;
                if (position >= seg.StartTime && position < seg.EndTime)
                {
                    // 잔여 구간이 1초 미만이면 굳이 버튼을 띄우지 않는다
                    if (duration > TimeSpan.Zero && seg.EndTime - position < TimeSpan.FromSeconds(1)) return false;
                    return true;
                }
            }
            return false;
        }

        /// <summary>현재 위치가 속한 구간의 끝 시각 (수동 스킵 실행용). 구간 밖이면 null.</summary>
        public TimeSpan? GetManualSkipTarget(TimeSpan position, TimeSpan duration)
        {
            if (!IsEnabled || _segments.Count == 0) return null;
            foreach (var seg in _segments)
            {
                if (duration > TimeSpan.Zero && seg.StartTime >= duration) continue;
                if (position >= seg.StartTime && position < seg.EndTime)
                {
                    return seg.EndTime;
                }
            }
            return null;
        }

        /// <summary>현재 트랙의 구간 정보가 준비되었는지 (진행바 마커 렌더링용)</summary>
        public bool HasSegments => _segments.Count > 0;

        /// <summary>진행바 위에 그릴 구간 목록 (설정에서 켠 카테고리만 포함)</summary>
        public IReadOnlyList<SkipSegment> Segments => _segments;

        /// <summary>
        /// 트랙이 바뀔 때 호출. 새 트랙의 SponsorBlock 구간을 백그라운드로 조회한다.
        /// </summary>
        public async Task LoadSegmentsForTrackAsync(string? mediaKey, string title, string artist)
        {
            _segments = new List<SkipSegment>();
            _segmentsKey = mediaKey ?? string.Empty;

            if (!IsEnabled || string.IsNullOrWhiteSpace(title)) return;

            // SMTC 메타데이터를 검색 쿼리로 정제
            var (cleanTitle, cleanArtist) = LyricsService.ParseTitleAndArtist(title, artist);
            if (string.IsNullOrWhiteSpace(cleanTitle)) return;

            string query = string.IsNullOrWhiteSpace(cleanArtist) ? cleanTitle : $"{cleanArtist} {cleanTitle}";

            // 동일 트랙 재조회 방지
            if (_fetchInFlight) return;
            _fetchInFlight = true;

            try
            {
                string? videoId = await _youTubeSearch.SearchVideoIdAsync(query);
                if (string.IsNullOrEmpty(videoId))
                {
                    _lastFailedLookupAt = DateTime.UtcNow;
                    return;
                }

                var cfg = ConfigService.Current;
                var segments = await _sponsorBlock.GetSegmentsAsync(
                    videoId,
                    cfg.SponsorSkipIntro,
                    cfg.SponsorSkipOutro,
                    cfg.SponsorSkipIntermission,
                    cfg.SponsorSkipMusicOfftopic);

                // 조회 동안 트랙이 바뀌었으면 폐기
                if (_segmentsKey != (mediaKey ?? string.Empty)) return;

                _segments = segments;
                if (segments.Count == 0)
                {
                    _lastFailedLookupAt = DateTime.UtcNow;
                }
            }
            finally
            {
                _fetchInFlight = false;
            }
        }

        /// <summary>
        /// 재생 위치가 스킵 대상 구간 안에 들어왔는지 판정한다.
        /// 자동 모드일 때만 seek 대상을 반환하고, 수동 모드면 항상 null(버튼 표시는 IsInSkipSegment가 담당).
        /// </summary>
        /// <returns>스킵해야 하면 구간 끝 시각, 아니면 null</returns>
        public TimeSpan? GetSkipTarget(TimeSpan position, TimeSpan duration)
        {
            if (Mode != "auto") return null;
            if (!IsEnabled || _segments.Count == 0) return null;

            // seek/트랙 전환 직후 오탐 방지 쿨다운
            if (DateTime.UtcNow - _lastSkipAt < SkipCooldown) return null;

            foreach (var seg in _segments)
            {
                // 구간이 미디어 길이보다 뒤에 있으면(길이 불일치) 무의미
                if (duration > TimeSpan.Zero && seg.StartTime >= duration) continue;

                if (position >= seg.StartTime && position < seg.EndTime)
                {
                    // 짧게 스쳐가는 잔여 구간(1초 미만)은 굳이 seek하지 않음
                    if (duration > TimeSpan.Zero && seg.EndTime - position < TimeSpan.FromSeconds(1)) return null;

                    _lastSkipAt = DateTime.UtcNow;
                    return seg.EndTime;
                }
            }
            return null;
        }

        /// <summary>조회 실패 후 재시도 가능 여부</summary>
        public bool ShouldRetryLookup => DateTime.UtcNow - _lastFailedLookupAt >= FailedLookupRetry;

        public void Dispose()
        {
            _segments.Clear();
        }
    }
}
