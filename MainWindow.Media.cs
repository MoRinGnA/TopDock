using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using TopDock.Models;
using TopDock.Services;

namespace TopDock
{
    public partial class MainWindow : Window
    {
        private void MediaService_MediaChanged(MediaMetadata? media)
        {
            Dispatcher.Invoke(async () =>
            {
                if (media == null || string.IsNullOrWhiteSpace(media.Title))
                {
                    // 트랙 전환 시 SMTC가 순간적으로 빈 미디어를 보고하는 것을 유예 시간으로 필터링.
                    // 그 안에 새 곡이 도착하면 리셋 없이 기존 라이트에서 바로 크로스페이드됨.
                    StartEmptyMediaDebounce();
                    return;
                }

                _emptyMediaDebounceTimer?.Stop();

                string key = $"{media.Artist}:::{media.Title}";
                bool isNewTrack = _lastMediaKey != key;

                if (isNewTrack)
                {
                    _lastMediaKey = key;

                    var parsed = LyricsService.ParseTitleAndArtist(media.Title, media.Artist);
                    string displayTitle = string.IsNullOrEmpty(parsed.Title) ? media.Title : parsed.Title;
                    string displayArtist = string.IsNullOrEmpty(parsed.Artist) ? (string.IsNullOrEmpty(media.Artist) ? "YouTube" : media.Artist) : parsed.Artist;

                    CompactTitleText.Text = displayTitle;
                    if (!_isVolumeAdjusting)
                    {
                        ExpandedTitleText.Text = displayTitle;
                        ExpandedArtistText.Text = displayArtist;
                    }

                    // 뷰와 폭은 제목만으로 정해진다 — 가사를 기다리지 않는다. 예전에는 여기서
                    // await LoadLyricsAsync를 하고 그 뒤에 뷰를 바꿔서, 후보 질의가 전부
                    // 끝날 때까지(최악 수십 초) 이전 크기로 남았다.
                    // 가사를 찾는 동안에도 자리는 미리 내준다 — 나중에 폭이 자라면 그게 더 눈에 띈다.
                    _lyricsPending = true;
                    if (_volumeHudTimer == null || !_volumeHudTimer.IsEnabled)
                    {
                        SwitchViewMode(_isExpanded ? ViewMode.MediaExpanded : ViewMode.MediaCompact);
                    }

                    string searchArtist = displayArtist == "YouTube" ? string.Empty : displayArtist;
                    _ = LoadLyricsAsync(displayTitle, searchArtist);
                }

                bool isValidThumbnail = false;
                BitmapSource? validBmp = null;
                if (media.HasThumbnail && media.Thumbnail is BitmapSource bmp)
                {
                    if (bmp.PixelWidth >= 48 && bmp.PixelHeight >= 48 && !IsLikelyFavicon(bmp))
                    {
                        isValidThumbnail = true;
                        validBmp = bmp;
                    }
                }

                if (isValidThumbnail && validBmp != null)
                {
                    ExpandedAlbumArtImage.Source = media.Thumbnail;
                    ExpandedDefaultIcon.Visibility = Visibility.Collapsed;
                    // 앨범 지배색 — 미디어 재생 중 테두리 빛을 이 색으로 물들인다.
                    _albumColor = ExtractDominantColor(validBmp);
                }
                else
                {
                    ExpandedAlbumArtImage.Source = null;
                    ExpandedDefaultIcon.Visibility = Visibility.Visible;
                    _albumColor = null; // 이전 트랙 색이 남지 않게

                    if (isNewTrack)
                    {
                        _ = RetryThumbnailAsync(key);
                    }
                }

                if (isNewTrack)
                    Log.Info($"Media track: {key} thumb={isValidThumbnail} playing={media.IsPlaying}");

                UpdatePlaybackState(media.IsPlaying);
                UpdateTimelineDisplay(media.CurrentEstimatedPosition, media.Duration);
            });
        }

        private async Task RetryThumbnailAsync(string expectedKey)
        {
            for (int i = 0; i < 6; i++)
            {
                await Task.Delay(500);

                if (_lastMediaKey != expectedKey) return;

                await _mediaService.UpdateMediaPropertiesAsync();

                var media = _mediaService.CurrentMedia;
                if (media?.HasThumbnail == true && media.Thumbnail is BitmapSource bmp)
                {
                    if (bmp.PixelWidth >= 48 && bmp.PixelHeight >= 48 && !IsLikelyFavicon(bmp))
                    {
                        Dispatcher.Invoke(() =>
                        {
                            if (_lastMediaKey == expectedKey)
                            {
                                ExpandedAlbumArtImage.Source = media.Thumbnail;
                                ExpandedDefaultIcon.Visibility = Visibility.Collapsed;
                                _albumColor = ExtractDominantColor(bmp);
                                UpdateBeamState();
                            }
                        });
                        return;
                    }
                }
            }
        }

        private static bool IsLikelyFavicon(BitmapSource bmp)
        {
            try
            {
                if (bmp.PixelWidth < 48 || bmp.PixelHeight < 48) return true;

                var converted = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
                int stride = converted.PixelWidth * 4;
                byte[] pixels = new byte[converted.PixelHeight * stride];
                converted.CopyPixels(pixels, stride, 0);

                int w = converted.PixelWidth;
                int h = converted.PixelHeight;
                (int x, int y)[] corners = { (0, 0), (w - 1, 0), (0, h - 1), (w - 1, h - 1) };

                int transparentCorners = 0;
                foreach (var (x, y) in corners)
                {
                    int idx = (y * stride) + (x * 4);
                    if (idx + 3 < pixels.Length && pixels[idx + 3] < 250)
                    {
                        transparentCorners++;
                    }
                }

                return transparentCorners >= 4;
            }
            catch
            {
                return false;
            }
        }

        private void MediaService_PlaybackStatusChanged(bool isPlaying)
        {
            Dispatcher.Invoke(() =>
            {
                UpdatePlaybackState(isPlaying);
            });
        }

        private void MediaService_TimelineChanged(TimeSpan position, TimeSpan duration)
        {
            Dispatcher.Invoke(() =>
            {
                UpdateTimelineDisplay(position, duration);
            });
        }

        private void UpdatePlaybackState(bool isPlaying)
        {
            if (isPlaying)
            {
                _progressTimer.Start();
                StartEqualizerAnimation();
                PlayPauseIcon.Data = Geometry.Parse("M6 19h4V5H6v14zm8-14v14h4V5h-4z");
            }
            else
            {
                _progressTimer.Stop();
                StopEqualizerAnimation();
                PlayPauseIcon.Data = Geometry.Parse("M8 5v14l11-7z");
            }

            UpdateBeamState();
        }

        private static readonly TimeSpan LyricLookahead = TimeSpan.FromMilliseconds(500);

        /// <summary>이 곡의 가사를 아직 찾는 중인가.</summary>
        private bool _lyricsPending;

        /// <summary>
        /// 가사를 놓을 자리를 지금 내줄지. 찾는 중에는 낙관적으로 내준다 — 그래야 가사가
        /// 도착했을 때 노치 폭이 뒤늦게 자라는 일이 없다. 찾아보니 없다고 밝혀지면 그때 한 번 줄인다.
        /// </summary>
        private bool LyricsExpected => _syncedLyrics.Count > 0 || _lyricsPending;

        private void ProgressTimer_Tick(object? sender, EventArgs e)
        {
            if (_mediaService.GetExactPosition(out var currentPos, out var duration))
            {
                // 드래그 중에는 실제 위치 대신 손끝 미리보기를 유지한다 (타이머가 덮어쓰지 않게)
                if (!_seeking) UpdateTimelineDisplay(currentPos, duration);

                // 가사 표시는 500ms 미리 룩업하여 실제 음악과 싱크 맞춤.
                // 오프셋은 뮤직비디오의 앞뒤 여백(인트로)을 보정한 값이다.
                var lyricPos = currentPos + LyricLookahead - _lyricOffset;
                if (lyricPos < TimeSpan.Zero) lyricPos = TimeSpan.Zero;
                UpdateLyricsDisplay(lyricPos);
            }
        }

        // ────────────────────────── 진행바 탐색(seek) ──────────────────────────
        // 진행바 히트존에서 누르고 끌면 손끝 위치로 미리보기를 그리고, 놓을 때 한 번 실제로 이동한다.
        // (드래그 내내 TrySeekAsync를 쐬면 SMTC가 요동치므로 이동은 손을 뗄 때만 보낸다)

        private bool _seeking;
        private TimeSpan _seekDuration;

        private void SeekBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!TryGetSeekDuration(out _seekDuration)) return;
            _seeking = true;
            (sender as UIElement)?.CaptureMouse();
            PreviewSeek(sender, e);
            e.Handled = true;
        }

        private void SeekBar_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_seeking) return;
            PreviewSeek(sender, e);
        }

        private void SeekBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_seeking) return;
            _seeking = false;
            (sender as UIElement)?.ReleaseMouseCapture();
            CommitSeek(PreviewSeek(sender, e));
            e.Handled = true;
        }

        /// <summary>탐색할 길이를 알 수 있을 때만 시작한다(길이 없는 스트림은 탐색 불가).</summary>
        private bool TryGetSeekDuration(out TimeSpan duration)
        {
            duration = TimeSpan.Zero;
            if (_mediaService.GetExactPosition(out _, out var total) && total > TimeSpan.Zero)
            {
                duration = total;
                return true;
            }
            return false;
        }

        /// <summary>손끝 X를 시각으로 환산해 진행바·시간 텍스트를 즉시 갱신한다(낙관적 미리보기).</summary>
        private TimeSpan PreviewSeek(object sender, MouseEventArgs e)
        {
            if (sender is not FrameworkElement el || _seekDuration <= TimeSpan.Zero) return TimeSpan.Zero;
            double width = el.ActualWidth;
            if (width <= 0) return TimeSpan.Zero;

            double fraction = Math.Clamp(e.GetPosition(el).X / width, 0, 1);
            var target = TimeSpan.FromSeconds(fraction * _seekDuration.TotalSeconds);
            ExpandedProgressBar.Value = fraction * 100;
            CurrentTimeText.Text = target.ToString(@"m\:ss");
            return target;
        }

        private async void CommitSeek(TimeSpan target)
        {
            bool ok = await _mediaService.TrySeekAsync(target);
            Log.Info(ok
                ? $"Seek: {target:m\\:ss} / {_seekDuration:m\\:ss}"
                : $"Seek ignored (session unsupported): {target:m\\:ss}");
        }

        private void UpdateTimelineDisplay(TimeSpan currentPos, TimeSpan duration)
        {
            if (duration.TotalSeconds > 0)
            {
                double progress = (currentPos.TotalSeconds / duration.TotalSeconds) * 100;
                progress = Math.Clamp(progress, 0, 100);
                ExpandedProgressBar.Value = progress;

                CurrentTimeText.Text = currentPos.ToString(@"m\:ss");
                TotalTimeText.Text = duration.ToString(@"m\:ss");
            }
            else
            {
                ExpandedProgressBar.Value = 0;
                CurrentTimeText.Text = "0:00";
                TotalTimeText.Text = "0:00";
            }
        }

        /// <summary>
        /// 가사를 받아 표시를 갱신한다. 뷰 전환과는 무관하게 뒤에서 돈다 — 예전에는 곡이 바뀔 때
        /// 이걸 기다린 뒤에 뷰를 바꿔서, 검색이 끝날 때까지(최악 수십 초) 이전 곡의 크기로 남아 있었다.
        /// </summary>
        private async Task LoadLyricsAsync(string rawTitle, string rawArtist)
        {
            string requestKey = _lastMediaKey;

            _syncedLyrics.Clear();
            _lyricsPending = true;
            SetLyricsVisibility(false);
            RefreshCompactWidth();

            // 이 영상의 길이 — 아티스트를 못 믿을 때(YouTube 업로더명) 같은 제목의 다른 곡을 가려내는 근거가 된다
            _mediaService.GetExactPosition(out _, out var mediaDuration);
            var result = await _lyricsService.GetLyricsAsync(rawTitle, rawArtist, mediaDuration);

            // 기다리는 사이에 곡이 바뀌었으면 이 결과는 버린다
            if (_lastMediaKey != requestKey) return;

            _lyricsPending = false;

            if (result != null && result.Lines.Count > 0)
            {
                _syncedLyrics = new List<LyricLine>(result.Lines);
                SetLyricsVisibility(true);

                // 뮤직비디오는 음원보다 앞(인트로)·뒤(아웃트로)가 길 수 있다. 가사는 음원 기준
                // 시각이므로 그 앞 여백만큼 늦춰야 맞는다.
                //
                // 소리를 듣고 재는 방법도 만들어 봤지만, 실제 곡에서는 "노래하는 구간"을 가릴
                // 신호가 너무 약해 같은 곡에 2~11초로 들쭉날쭉했다(합성에선 잘 됐지만 착시였다).
                // 그래서 규칙으로 간다 — 길이 차이는 (인트로 + 아웃트로)라 인트로만 따로 알 수
                // 없으니, 차이를 인트로로 보되 상한을 둬서 과하게 늦지 않게 한다.
                double videoSec = mediaDuration.TotalSeconds;
                double sourceSec = result.SourceDuration?.TotalSeconds ?? 0;
                double offsetSeconds = EstimateIntroOffset(videoSec, sourceSec);
                _lyricOffset = TimeSpan.FromSeconds(offsetSeconds);
                Log.Info($"Lyrics sync: 영상 {videoSec:F0}초 · 음원 {sourceSec:F0}초 · 차이 {videoSec - sourceSec:F0}초" +
                         $" · 적용한 인트로 {offsetSeconds:F1}초");
            }
            else if (_currentViewMode == ViewMode.MediaExpanded)
            {
                // 가사가 없다고 밝혀졌으면 미리 내준 자리를 거둬들인다 (276폭으로 줄고 내용은 가운데로)
                SwitchViewMode(ViewMode.MediaExpanded);
            }

            RefreshCompactWidth();
        }

        private void ClipboardToggleButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleClipboardHistory();
        }

        /// <summary>인트로로 보정할 상한(초). 차이가 이보다 크면 대부분 아웃트로가 긴 것이라 여기서 자른다.</summary>
        private const double MaxIntroSeconds = 12.0;

        /// <summary>
        /// 영상 길이와 음원 길이만으로 인트로를 어림한다. 뮤직비디오의 앞뒤 여백을 정확히 나눌
        /// 근거는 없으니, 과하게 늦추지 않는 선까지만 보정한다.
        /// 음원 길이가 영상에 비해 지나치게 짧으면(다른 곡을 잘못 찾은 경우) 보정하지 않는다.
        /// </summary>
        private static double EstimateIntroOffset(double videoSec, double sourceSec)
        {
            if (videoSec <= 0 || sourceSec <= 0) return 0;
            if (sourceSec < videoSec * 0.8) return 0;      // 못 믿을 매칭 — 밀지 않는다
            double diff = videoSec - sourceSec;
            if (diff <= 0) return 0;
            return Math.Min(diff, MaxIntroSeconds);
        }

        /// <summary>가사 영역의 내용을 보일지. 자리(폭·열)는 ApplyLyricsLayout이 따로 맡는다.</summary>
        private void SetLyricsVisibility(bool visible)
        {
            LyricsDivider.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            LyricsContainer.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// 확장 뷰에서 가사 자리를 내줄지 정한다. 자리를 비울 때는 내용을 가운데로 모은다 —
        /// 그러지 않으면 276폭에 왼쪽으로 몰린 모습이 된다.
        /// </summary>
        private void ApplyLyricsLayout(bool reserve)
        {
            if (reserve)
            {
                MediaExpandedView.HorizontalAlignment = HorizontalAlignment.Stretch;
                LyricsDividerCol.Width = GridLength.Auto;
                LyricsCol.Width = new GridLength(1, GridUnitType.Star);
            }
            else
            {
                MediaExpandedView.HorizontalAlignment = HorizontalAlignment.Center;
                LyricsDividerCol.Width = new GridLength(0);
                LyricsCol.Width = new GridLength(0);
            }
        }
        private void UpdateLyricsDisplay(TimeSpan currentPos)
        {
            if (_syncedLyrics.Count == 0)
            {
                CompactLyricText.Text = string.Empty;
                return;
            }

            int activeIndex = -1;
            for (int i = 0; i < _syncedLyrics.Count; i++)
            {
                if (_syncedLyrics[i].Time <= currentPos)
                {
                    activeIndex = i;
                }
                else
                {
                    break;
                }
            }

            if (activeIndex >= 0)
            {
                // LRC의 빈 항목은 "여기서부터 반주"라는 표시다. 그 자리에서는 현재 줄을 비우고
                // 앞뒤 줄만 남긴다 — 반주가 흐르는 동안 이전 가사가 붙어 있으면 멈춘 것처럼 보인다.
                string activeText = _syncedLyrics[activeIndex].Text;
                bool lyricChanged = CurrentLyricText.Text != activeText;
                CompactLyricText.Text = activeText;
                PrevLyricText.Text = NearestText(activeIndex - 1, -1);
                CurrentLyricText.Text = activeText;
                NextLyricText.Text = NearestText(activeIndex + 1, 1);

                // 가사가 바뀔 때 살짝 떠오르는 트랜지션
                if (lyricChanged)
                {
                    AnimateLyricLine(CurrentLyricText);
                }

            }
            else
            {
                CompactLyricText.Text = string.Empty;
                PrevLyricText.Text = string.Empty;
                CurrentLyricText.Text = "...";
                NextLyricText.Text = _syncedLyrics.Count > 0 ? _syncedLyrics[0].Text : string.Empty;
            }

            // 가사가 도착했거나 없다고 밝혀졌을 때 컴팩트 노치 폭을 맞춘다
            RefreshCompactWidth();
        }

        /// <summary>
        /// 컴팩트 노치 폭을 지금 상태에 맞춘다. 가사 줄이 바뀔 때마다가 아니라
        /// 가사 자리가 늘거나 줄 때만 움직인다(가사를 못 찾았을 때 등).
        /// </summary>
        private void RefreshCompactWidth()
        {
            if (_isExpanded || _currentViewMode != ViewMode.MediaCompact) return;

            double target = CalculateCompactWidth();
            if (Math.Abs(NotchBorder.Width - target) <= 5) return;

            var duration = new Duration(TimeSpan.FromMilliseconds(200));
            var ease = new QuadraticEase();
            NotchBorder.BeginAnimation(Border.WidthProperty,
                new DoubleAnimation { To = target, Duration = duration, EasingFunction = ease });
            UpdateGlowDimensions(target, 38, duration, ease);
        }

        /// <summary>가까운 쪽에서 빈 항목이 아닌 가사를 찾는다(반주 표시를 건너뛰기 위해).</summary>
        private string NearestText(int from, int step)
        {
            for (int i = from; i >= 0 && i < _syncedLyrics.Count; i += step)
            {
                if (!string.IsNullOrWhiteSpace(_syncedLyrics[i].Text)) return _syncedLyrics[i].Text;
            }
            return string.Empty;
        }

        private void AnimateLyricLine(TextBlock text)
        {
            var transform = text.RenderTransform as TranslateTransform;
            if (transform == null)
            {
                transform = new TranslateTransform();
                text.RenderTransform = transform;
            }

            var fade = new DoubleAnimation
            {
                From = 0.35,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(260),
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut }
            };
            var slide = new DoubleAnimation
            {
                From = 7,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(300),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Timeline.SetDesiredFrameRate(fade, 60);
            Timeline.SetDesiredFrameRate(slide, 60);

            text.BeginAnimation(UIElement.OpacityProperty, fade);
            transform.BeginAnimation(TranslateTransform.YProperty, slide);
        }

        private void StartEmptyMediaDebounce()
        {
            if (_emptyMediaDebounceTimer == null)
            {
                _emptyMediaDebounceTimer = new DispatcherTimer { Interval = EmptyMediaDebounceDelay };
                _emptyMediaDebounceTimer.Tick += (s, e) =>
                {
                    _emptyMediaDebounceTimer.Stop();

                    // 유예 시간이 끝난 시점에도 여전히 미디어가 없으면 진짜 종료로 판단
                    var current = _mediaService.CurrentMedia;
                    if (current == null || string.IsNullOrWhiteSpace(current.Title))
                    {
                        ResetToEmptyMedia();
                    }
                };
            }
            _emptyMediaDebounceTimer.Stop();
            _emptyMediaDebounceTimer.Start();
        }

        private void ResetToEmptyMedia()
        {
            _lastMediaKey = string.Empty;
            _albumColor = null;
            UpdateBeamState();
            CompactTitleText.Text = "재생 중인 미디어 없음";
            CompactLyricText.Text = string.Empty;
            ExpandedTitleText.Text = "재생 중인 미디어 없음";
            ExpandedArtistText.Text = string.Empty;
            ExpandedAlbumArtImage.Source = null;
            ExpandedDefaultIcon.Visibility = Visibility.Visible;
            StopEqualizerAnimation();

            _syncedLyrics.Clear();
            _lyricsPending = false;
            _lyricOffset = TimeSpan.Zero;
            SetLyricsVisibility(false);

            ExpandedProgressBar.Value = 0;
            CurrentTimeText.Text = "0:00";
            TotalTimeText.Text = "0:00";
            _progressTimer.Stop();

            if (_volumeHudTimer == null || !_volumeHudTimer.IsEnabled)
            {
                SwitchViewMode(_isExpanded ? ViewMode.IdleExpanded : ViewMode.IdleCompact);
            }
        }

        /// <summary>이퀄라이저 바 색을 현재 곡의 색으로 맞춘다 (테두리 빛과 동일한 색).</summary>
        private void UpdateEqualizerAccent(Color color)
        {
            if (_equalizerAccent == color) return;
            _equalizerAccent = color;
            _equalizerBrush.Color = color;   // 이미 붙어 있는 브러시 색만 바꾼다 (재할당 없음)
        }

        private void StartEqualizerAnimation()
        {
            StopEqualizerAnimation();

            _eqStoryboard = new Storyboard();

            var anim1 = new DoubleAnimation(4, 13, TimeSpan.FromMilliseconds(380))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };
            Storyboard.SetTarget(anim1, EqBar1);
            Storyboard.SetTargetProperty(anim1, new PropertyPath(Border.HeightProperty));

            var anim2 = new DoubleAnimation(14, 5, TimeSpan.FromMilliseconds(460))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };
            Storyboard.SetTarget(anim2, EqBar2);
            Storyboard.SetTargetProperty(anim2, new PropertyPath(Border.HeightProperty));

            var anim3 = new DoubleAnimation(6, 12, TimeSpan.FromMilliseconds(330))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };
            Storyboard.SetTarget(anim3, EqBar3);
            Storyboard.SetTargetProperty(anim3, new PropertyPath(Border.HeightProperty));

            _eqStoryboard.Children.Add(anim1);
            _eqStoryboard.Children.Add(anim2);
            _eqStoryboard.Children.Add(anim3);
            _eqStoryboard.Begin();
        }

        private void StopEqualizerAnimation()
        {
            if (_eqStoryboard != null)
            {
                _eqStoryboard.Stop();
                _eqStoryboard = null;
            }
            EqBar1.Height = 3;
            EqBar2.Height = 3;
            EqBar3.Height = 3;
        }

        private async void PrevButton_Click(object sender, RoutedEventArgs e)
        {
            await _mediaService.TrySkipPreviousAsync();
        }

        private async void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            if (_mediaService.CurrentMedia != null)
            {
                bool toggled = !_mediaService.CurrentMedia.IsPlaying;
                _mediaService.CurrentMedia.IsPlaying = toggled;
                UpdatePlaybackState(toggled);
            }
            await _mediaService.TryTogglePlayPauseAsync();
        }

        private async void NextButton_Click(object sender, RoutedEventArgs e)
        {
            await _mediaService.TrySkipNextAsync();
        }
    }
}
