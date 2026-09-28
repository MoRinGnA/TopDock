using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Windows.Media.Control;
using Windows.Storage.Streams;
using TopDock.Models;

namespace TopDock.Services
{
    public class MediaService : IDisposable
    {
        private GlobalSystemMediaTransportControlsSessionManager? _sessionManager;
        private GlobalSystemMediaTransportControlsSession? _currentSession;
        private readonly object _sessionLock = new();
        private bool _disposed;

        public event Action<MediaMetadata?>? MediaChanged;
        public event Action<bool>? PlaybackStatusChanged;
        public event Action<TimeSpan, TimeSpan>? TimelineChanged;

        public MediaMetadata? CurrentMedia { get; private set; }

        public async Task InitializeAsync()
        {
            try
            {
                _sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                if (_sessionManager != null)
                {
                    _sessionManager.CurrentSessionChanged += SessionManager_CurrentSessionChanged;
                }
                await RefreshSessionAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MediaService Initialize error: {ex.Message}");
            }
        }

        private async void SessionManager_CurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
        {
            await RefreshSessionAsync();
        }

        private async Task RefreshSessionAsync()
        {
            if (_disposed) return;

            GlobalSystemMediaTransportControlsSession? newSession = null;
            try
            {
                newSession = _sessionManager?.GetCurrentSession();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"GetCurrentSession error: {ex.Message}");
            }

            lock (_sessionLock)
            {
                if (_currentSession != null)
                {
                    _currentSession.MediaPropertiesChanged -= CurrentSession_MediaPropertiesChanged;
                    _currentSession.PlaybackInfoChanged -= CurrentSession_PlaybackInfoChanged;
                    _currentSession.TimelinePropertiesChanged -= CurrentSession_TimelinePropertiesChanged;
                }

                _currentSession = newSession;

                if (_currentSession != null)
                {
                    _currentSession.MediaPropertiesChanged += CurrentSession_MediaPropertiesChanged;
                    _currentSession.PlaybackInfoChanged += CurrentSession_PlaybackInfoChanged;
                    _currentSession.TimelinePropertiesChanged += CurrentSession_TimelinePropertiesChanged;
                }
            }

            await UpdateMediaPropertiesAsync();
        }

        private async void CurrentSession_MediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
        {
            await UpdateMediaPropertiesAsync();
        }

        private void CurrentSession_PlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
        {
            try
            {
                var playbackInfo = sender.GetPlaybackInfo();
                bool isPlaying = playbackInfo?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                if (CurrentMedia != null)
                {
                    CurrentMedia.IsPlaying = isPlaying;
                }
                PlaybackStatusChanged?.Invoke(isPlaying);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"PlaybackInfoChanged error: {ex.Message}");
            }
        }

        private void CurrentSession_TimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
        {
            try
            {
                var timeline = sender.GetTimelineProperties();
                if (timeline != null && CurrentMedia != null)
                {
                    CurrentMedia.Position = timeline.Position;
                    CurrentMedia.Duration = timeline.EndTime;
                    CurrentMedia.LastUpdatedTime = timeline.LastUpdatedTime;
                    TimelineChanged?.Invoke(timeline.Position, timeline.EndTime);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"TimelinePropertiesChanged error: {ex.Message}");
            }
        }

        public async Task UpdateMediaPropertiesAsync()
        {
            if (_disposed) return;

            GlobalSystemMediaTransportControlsSession? session;
            lock (_sessionLock)
            {
                session = _currentSession;
            }

            if (session == null)
            {
                CurrentMedia = null;
                MediaChanged?.Invoke(null);
                PlaybackStatusChanged?.Invoke(false);
                return;
            }

            try
            {
                var mediaProps = await session.TryGetMediaPropertiesAsync();
                var playbackInfo = session.GetPlaybackInfo();
                var timeline = session.GetTimelineProperties();

                if (mediaProps == null)
                {
                    CurrentMedia = null;
                    MediaChanged?.Invoke(null);
                    PlaybackStatusChanged?.Invoke(false);
                    return;
                }

                var thumbnail = await LoadThumbnailAsync(mediaProps.Thumbnail);
                bool isPlaying = playbackInfo?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

                var meta = new MediaMetadata
                {
                    Title = mediaProps.Title ?? string.Empty,
                    Artist = mediaProps.Artist ?? string.Empty,
                    Thumbnail = thumbnail,
                    IsPlaying = isPlaying,
                    Position = timeline?.Position ?? TimeSpan.Zero,
                    Duration = timeline?.EndTime ?? TimeSpan.Zero,
                    LastUpdatedTime = timeline?.LastUpdatedTime ?? DateTimeOffset.UtcNow
                };

                CurrentMedia = meta;
                MediaChanged?.Invoke(meta);
                PlaybackStatusChanged?.Invoke(isPlaying);
                TimelineChanged?.Invoke(meta.Position, meta.Duration);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"UpdateMediaPropertiesAsync error: {ex.Message}");
            }
        }

        private static async Task<BitmapImage?> LoadThumbnailAsync(IRandomAccessStreamReference? thumbnailRef)
        {
            if (thumbnailRef == null) return null;

            try
            {
                using var stream = await thumbnailRef.OpenReadAsync();
                using var netStream = stream.AsStream();
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = netStream;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 지원 앱(YouTube 등)에서 재생 위치를 변경한다. (SMTC UAP/None 권한 앱에서는 실패할 수 있음)
        /// </summary>
        public async Task<bool> TrySeekAsync(TimeSpan position)
        {
            try
            {
                GlobalSystemMediaTransportControlsSession? session;
                lock (_sessionLock) { session = _currentSession; }
                session ??= _sessionManager?.GetCurrentSession();
                if (session != null)
                {
                    return await session.TryChangePlaybackPositionAsync(position.Ticks);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"TrySeekAsync error: {ex.Message}");
            }
            return false;
        }

        public async Task<bool> TryTogglePlayPauseAsync()
        {
            try
            {
                GlobalSystemMediaTransportControlsSession? session;
                lock (_sessionLock) { session = _currentSession; }
                session ??= _sessionManager?.GetCurrentSession();
                if (session != null)
                {
                    return await session.TryTogglePlayPauseAsync();
                }
            }
            catch { }
            return false;
        }

        public async Task<bool> TrySkipNextAsync()
        {
            try
            {
                GlobalSystemMediaTransportControlsSession? session;
                lock (_sessionLock) { session = _currentSession; }
                session ??= _sessionManager?.GetCurrentSession();
                if (session != null)
                {
                    return await session.TrySkipNextAsync();
                }
            }
            catch { }
            return false;
        }

        public async Task<bool> TrySkipPreviousAsync()
        {
            try
            {
                GlobalSystemMediaTransportControlsSession? session;
                lock (_sessionLock) { session = _currentSession; }
                session ??= _sessionManager?.GetCurrentSession();
                if (session != null)
                {
                    return await session.TrySkipPreviousAsync();
                }
            }
            catch { }
            return false;
        }

        // 곡 종료 직전 갱신 요청의 쿨다운 타임스탬프 (GetExactPosition은 50ms 타이머에서 호출됨)
        private DateTime _lastEndRefreshAt = DateTime.MinValue;

        public bool GetExactPosition(out TimeSpan currentPos, out TimeSpan duration)
        {
            currentPos = TimeSpan.Zero;
            duration = TimeSpan.Zero;

            try
            {
                GlobalSystemMediaTransportControlsSession? session;
                lock (_sessionLock) { session = _currentSession; }
                session ??= _sessionManager?.GetCurrentSession();

                if (session != null)
                {
                    var timeline = session.GetTimelineProperties();
                    if (timeline != null)
                    {
                        currentPos = timeline.Position;
                        duration = timeline.EndTime;

                        var elapsed = DateTimeOffset.UtcNow - timeline.LastUpdatedTime;
                        if (elapsed > TimeSpan.Zero && elapsed < TimeSpan.FromHours(1))
                        {
                            currentPos += elapsed;
                            if (duration > TimeSpan.Zero && currentPos > duration)
                            {
                                currentPos = duration;
                            }
                        }

                        // 곡 종료 임박 시 다음 트랙 감지를 위해 갱신하지만,
                        // 50ms 타이머가 초당 20번 조건에 걸리므로 쿨다운으로 1초당 최대 1회만 실행한다.
                        if (duration > TimeSpan.Zero && currentPos >= duration - TimeSpan.FromMilliseconds(200))
                        {
                            var now = DateTime.UtcNow;
                            if (now - _lastEndRefreshAt >= TimeSpan.FromSeconds(1))
                            {
                                _lastEndRefreshAt = now;
                                _ = Task.Run(UpdateMediaPropertiesAsync);
                            }
                        }

                        if (CurrentMedia != null)
                        {
                            CurrentMedia.Position = currentPos;
                            CurrentMedia.Duration = duration;
                        }

                        return true;
                    }
                }
            }
            catch { }

            if (CurrentMedia != null)
            {
                currentPos = CurrentMedia.CurrentEstimatedPosition;
                duration = CurrentMedia.Duration;
                return true;
            }

            return false;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            lock (_sessionLock)
            {
                if (_currentSession != null)
                {
                    _currentSession.MediaPropertiesChanged -= CurrentSession_MediaPropertiesChanged;
                    _currentSession.PlaybackInfoChanged -= CurrentSession_PlaybackInfoChanged;
                    _currentSession.TimelinePropertiesChanged -= CurrentSession_TimelinePropertiesChanged;
                    _currentSession = null;
                }
            }

            if (_sessionManager != null)
            {
                _sessionManager.CurrentSessionChanged -= SessionManager_CurrentSessionChanged;
                _sessionManager = null;
            }
        }
    }
}