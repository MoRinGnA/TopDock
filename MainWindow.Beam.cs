using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using TopDock.Models;
using TopDock.Services;

namespace TopDock
{
    public partial class MainWindow : Window
    {
        /// <summary>
        /// 노치 테두리(살아있는 가장자리)의 형태·색·속도를 현재 상태로 결정한다.
        /// 우선순위: 비서 작업 중(크고 단색) &gt; 응답 스트리밍(얇은 단색) &gt; 미디어 재생(앨범색) &gt; 대기(모노 선).
        /// </summary>
        private void UpdateBeamState()
        {
            // 트랙이 로드되면 일시정지 상태여도 그 곡의 색을 유지한다(재생 여부는 형태·속도로만).
            bool track = HasMedia;
            bool playing = _mediaService.CurrentMedia?.IsPlaying == true;
            string log = "";
            // 상태별 전체 밝기. 미디어만 눅인다 — 확장 노치에서 앨범색 빛이
            // 다크 UI 위에 과하게 번쩍이던 것을 조금 가라앉힌다(다른 상태는 그대로).
            double strength = 1.0;

            // 이퀄라이저 바도 같은 곡의 색 — 한 곡 안에서는 테두리와 바가 같은 색을 쓴다
            UpdateEqualizerAccent(track ? (_albumColor ?? ColorFromKey(_lastMediaKey)) : EqualizerIdleColor);

            if (_orbCentral)
            {
                log = "assistant-working";
                // 사고·검색·연결 등 — 크게 도는 모노 빛
                NotchBeam.Form = Controls.BeamForm.Large;
                NotchBeam.Tint = Controls.BeamTint.Mono;
                NotchBeam.AccentColor = null;
                NotchBeam.Speed = 0.30;
            }
            else if (_assistantBusy)
            {
                log = "assistant-streaming";
                // 응답 스트리밍 — 얇은 모노 선
                NotchBeam.Form = Controls.BeamForm.Line;
                NotchBeam.Tint = Controls.BeamTint.Mono;
                NotchBeam.AccentColor = null;
                NotchBeam.Speed = 0.16;
            }
            else if (track)
            {
                // 앨범아트가 없으면(브라우저 탭 오디오 등) 트랙 이름에서 안정적인 색을 파생한다.
                // 그래야 어떤 곡이든 '그 곡의 색'을 갖는다.
                Color beamColor = _albumColor ?? ColorFromKey(_lastMediaKey);
                log = $"media({(_albumColor.HasValue ? "album" : "derived")} #{beamColor.R:X2}{beamColor.G:X2}{beamColor.B:X2} {(playing ? "playing" : "paused")})";
                NotchBeam.Tint = Controls.BeamTint.Album;
                NotchBeam.AccentColor = beamColor;
                // 재생 중엔 크게 빠르게, 일시정지엔 얇게 느리게 — 곡의 색을 두고 운동만 달라진다.
                NotchBeam.Form = playing ? Controls.BeamForm.Large : Controls.BeamForm.Line;
                NotchBeam.Speed = playing ? 0.13 : 0.07;
                strength = playing ? 0.80 : 0.90;
            }
            else
            {
                log = "idle";
                // 대기 — 밝은 모노 선이 천천히 흐른다
                NotchBeam.Form = Controls.BeamForm.Line;
                NotchBeam.Tint = Controls.BeamTint.Mono;
                NotchBeam.AccentColor = null;
                NotchBeam.Speed = 0.09;
            }

            if (_chargingBeamActive)
            {
                // 충전 피드백 동안에만 초록 단색. Form·Speed·Strength와 평소 상태 로직은 그대로 둔다.
                NotchBeam.Tint = Controls.BeamTint.Mono;
                NotchBeam.AccentColor = Color.FromRgb(0x34, 0xC7, 0x59);
            }
            NotchBeam.Strength = strength;

            if (log != _lastBeamState)
            {
                _lastBeamState = log;
                Log.Info($"Beam state: {log}");
            }
        }

        /// <summary>
        /// 노치 테두리를 한 번 밝게 — "무슨 일이 일어났다"는 빛의 신호.
        /// 복사 피드백과 각종 노치 알림이 같은 리듬을 공유한다(급히 밝아졌다가 천천히 식는다).
        /// </summary>
        private void FlashNotchEdge(double peak = 1.0, double totalMs = 1100)
        {
            var flash = new DoubleAnimationUsingKeyFrames();
            flash.KeyFrames.Add(new EasingDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            flash.KeyFrames.Add(new EasingDoubleKeyFrame(peak, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(150)))
            { EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 3 } });
            flash.KeyFrames.Add(new EasingDoubleKeyFrame(peak, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(380))));
            flash.KeyFrames.Add(new EasingDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(totalMs)))
            { EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 2 } });
            Timeline.SetDesiredFrameRate(flash, 60);
            NotchBeam.BeginAnimation(Controls.BorderBeam.FlashProperty, flash);
        }

        /// <summary>
        /// 트랙 키에서 안정적인 색을 파생한다 — 앨범아트가 없는 곡도 자기 색을 갖도록.
        /// FNV-1a 해시 → 색상환. 노랑/형광이 촌스럽지 않게 채도·명도를 눌러 쓴다.
        /// </summary>
        private static Color ColorFromKey(string key)
        {
            unchecked
            {
                uint h = 2166136261;
                foreach (char ch in key) { h = (h ^ ch) * 16777619; }
                return HsvToRgb(h % 360, 0.66, 0.96);
            }
        }

        private static Color HsvToRgb(double hueDeg, double s, double v)
        {
            double h = ((hueDeg % 360) + 360) % 360 / 60.0;
            int i = (int)Math.Floor(h);
            double f = h - i;
            double p = v * (1 - s), q = v * (1 - f * s), t = v * (1 - (1 - f) * s);
            double r, g, b;
            switch (i % 6)
            {
                case 0: r = v; g = t; b = p; break;
                case 1: r = q; g = v; b = p; break;
                case 2: r = p; g = v; b = t; break;
                case 3: r = p; g = q; b = v; break;
                case 4: r = t; g = p; b = v; break;
                default: r = v; g = p; b = q; break;
            }
            return Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
        }

        /// <summary>
        /// 앨범아트에서 '지배색'을 뽑는다 — 밝고 선명한 픽셀에 가중치를 준 색 히스토그램의 최빈 구간.
        /// 24×24로 줄여 576픽셀만 훑으므로 트랙이 바뀔 때 한 번이면 충분하다.
        /// 여러 색을 평균내면 회색빛으로 죽으므로, 구간별로 모아 가장 뜨거운 색 하나를 고른다.
        /// </summary>
        private static Color? ExtractDominantColor(BitmapSource bmp)
        {
            try
            {
                if (bmp.PixelWidth < 4 || bmp.PixelHeight < 4) return null;
                var converted = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
                int sw = converted.PixelWidth, sh = converted.PixelHeight;
                int stride = sw * 4;
                byte[] px = new byte[sh * stride];
                converted.CopyPixels(px, stride, 0);

                // 24×24 격자로 솎아 표본만 본다(TransformedBitmap은 freeze 요구가 까다로워 직접 훑는다)
                const int G = 24;
                const int BINS = 4096; // 4bit/채널
                var wsum = new double[BINS];
                var ar = new double[BINS]; var ag = new double[BINS]; var ab = new double[BINS];

                for (int gy = 0; gy < G; gy++)
                {
                    int y = (gy * sh) / G;
                    for (int gx = 0; gx < G; gx++)
                    {
                        int x = (gx * sw) / G;
                        int o = y * stride + x * 4;
                        double b = px[o] / 255.0, g = px[o + 1] / 255.0, r = px[o + 2] / 255.0;
                        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                        double sat = max <= 0 ? 0 : (max - min) / max;
                        double weight = max * (0.35 + sat); // 밝고 선명할수록 세게 — 검은 여백은 자연히 제외
                        int bin = ((px[o + 2] >> 4) << 8) | ((px[o + 1] >> 4) << 4) | (px[o] >> 4);
                        wsum[bin] += weight; ar[bin] += r * weight; ag[bin] += g * weight; ab[bin] += b * weight;
                    }
                }

                int best = -1; double bestW = 0;
                for (int i = 0; i < BINS; i++)
                    if (wsum[i] > bestW) { bestW = wsum[i]; best = i; }
                if (best < 0 || bestW < 1e-4) return null;

                double r0 = ar[best] / bestW * 255, g0 = ag[best] / bestW * 255, b0 = ab[best] / bestW * 255;
                var raw = Color.FromRgb(
                    (byte)Math.Clamp(r0, 0, 255),
                    (byte)Math.Clamp(g0, 0, 255),
                    (byte)Math.Clamp(b0, 0, 255));
                if (Math.Max(raw.R, Math.Max(raw.G, raw.B)) < 40) { Log.Info($"Album color: too dark from {sw}x{sh}"); return null; }

                // 무채색에 가까운 앨범(사진·회색 배경)도 테두리에서 '색'으로 읽히도록
                // 색상은 그대로 두고 채도·명도만 끌어올린다.
                RgbToHsv(raw, out double hue, out double rawSat, out double rawVal);
                var c = HsvToRgb(hue, Math.Max(rawSat, 0.55), Math.Max(rawVal, 0.92));
                Log.Info($"Album color: #{c.R:X2}{c.G:X2}{c.B:X2} (raw #{raw.R:X2}{raw.G:X2}{raw.B:X2} s={rawSat:F2}) from {sw}x{sh}");
                return c;
            }
            catch (Exception ex)
            {
                Log.Error("Album color extraction failed", ex);
                return null;
            }
        }

        /// <summary>RGB → HSV(색상은 degree). 추출 색을 정규화할 때 쓴다.</summary>
        private static void RgbToHsv(Color c, out double hueDeg, out double s, out double v)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            double d = max - min;
            v = max;
            s = max <= 1e-6 ? 0 : d / max;
            double h = 0;
            if (d > 1e-6)
            {
                if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
                else if (max == g) h = (b - r) / d + 2;
                else h = (r - g) / d + 4;
                h /= 6;
            }
            hueDeg = h * 360;
        }
    }
}
