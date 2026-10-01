using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace TopDock.Controls
{
    public enum BeamVariant { Colorful, Mono, Ocean, Sunset, Forest, Candy, Ice, Gold }

    /// <summary>
    /// border-beam v1.4.1(MIT, Jakub Antalik) md 프리셋의 렌더 파이프라인을 WPF로 1:1 포팅.
    /// 원본 3레이어 구조 그대로:
    ///  z1 inner  — 팔레트 블롭 9개(크기 0.9배, alpha 0.45) + inset 5px 흰 그림자,
    ///              회전 conic 넓은 창(투명 0-22% → white 46-82% → 투명 97%) 마스크, 레이어 불투명도 0.42
    ///  z2 stroke — (블롭 full → 흰 스윕 conic over)를 1px 링에, 회전 conic 좁은 창(52-80% 첨두) 마스크, 0.26
    ///  z3 bloom  — 흰 헤드 conic(0.85 peak @70%)를 링에, blur(8px), 0.24
    /// 공통: hue-rotate ±30° 12s ease-in-out 왕복 + brightness(1.3) saturate(1.2),
    ///       fade-in 0.6s ease / fade-out 0.5s ease, 회전 1바퀴 1.96s linear.
    /// CSS conic 마스크는 1° 단위 쐐기 비트맵 + 회전 브러시로 정확히 재현한다.
    /// </summary>
    public class BorderBeam : FrameworkElement
    {
        private const double TickSeconds = 1.0 / 60.0;

        // ── 원본 md dark 프리셋 상수 ──
        private const double StrokeOpacity = 0.26;
        private const double InnerOpacity = 0.42;
        private const double BloomOpacity = 0.24;
        private const double Brightness = 1.3;
        private const double Saturation = 1.2;
        private const double HueRange = 30.0;   // deg
        private const double HuePeriod = 12.0;  // s
        private const double SpinPeriod = 1.96; // s / 360°
        private const double FadeInSec = 0.6;
        private const double FadeOutSec = 0.5;
        private const double GlowBlur = 8.0;    // px (bloom blur)

        public BorderBeam()
        {
            IsHitTestVisible = false;
            _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromSeconds(TickSeconds) };
            _timer.Tick += OnTick;
            IsVisibleChanged += (s, e) => UpdateTimer();
            SizeChanged += (s, e) => { _maskStrokeBmp = null; _maskInnerBmp = null; _ring.Clear(); Rebuild(); };
        }

        // ── 속성 ──
        public static readonly DependencyProperty VariantProperty = DependencyProperty.Register(
            nameof(Variant), typeof(BeamVariant), typeof(BorderBeam),
            new PropertyMetadata(BeamVariant.Colorful, (s, e) => ((BorderBeam)s).OnTintInvalidated()));
        public BeamVariant Variant { get => (BeamVariant)GetValue(VariantProperty); set => SetValue(VariantProperty, value); }

        public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
            nameof(CornerRadius), typeof(double), typeof(BorderBeam),
            new PropertyMetadata(double.NaN, (s, e) => ((BorderBeam)s).Rebuild()));
        /// <summary>NaN이면 min(W,H)/2 (알약 형태).</summary>
        public double CornerRadius { get => (double)GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }

        public static readonly DependencyProperty BorderWidthProperty = DependencyProperty.Register(
            nameof(BorderWidth), typeof(double), typeof(BorderBeam),
            new PropertyMetadata(1.0, (s, e) => ((BorderBeam)s).Rebuild()));
        public double BorderWidth { get => (double)GetValue(BorderWidthProperty); set => SetValue(BorderWidthProperty, value); }

        public static readonly DependencyProperty StrengthProperty = DependencyProperty.Register(
            nameof(Strength), typeof(double), typeof(BorderBeam), new PropertyMetadata(1.0));
        /// <summary>원본 strength(0~1) — beam 레이어만 비춘다.</summary>
        public double Strength { get => (double)GetValue(StrengthProperty); set => SetValue(StrengthProperty, value); }

        public static readonly DependencyProperty ActiveProperty = DependencyProperty.Register(
            nameof(Active), typeof(bool), typeof(BorderBeam),
            new PropertyMetadata(false, (s, e) => ((BorderBeam)s).OnActiveChanged()));
        public bool Active { get => (bool)GetValue(ActiveProperty); set => SetValue(ActiveProperty, value); }

        /// <summary>레이어 알파 총배수 — TopDock에서 원본보다 눈에 띄게 쓰기 위한 강도 속성.</summary>
        public static readonly DependencyProperty IntensityProperty = DependencyProperty.Register(
            nameof(Intensity), typeof(double), typeof(BorderBeam), new PropertyMetadata(1.0));
        public double Intensity { get => (double)GetValue(IntensityProperty); set => SetValue(IntensityProperty, value); }

        /// <summary>설정 시 8종 프리셋 대신 이 색에서 파생한 팔레트를 쓴다(블롭 좌표·크기는 원본 그대로).
        /// 앨범 지배색·배터리 상태 등을 beam 빛으로 표현할 때 사용.</summary>
        public static readonly DependencyProperty AccentColorProperty = DependencyProperty.Register(
            nameof(AccentColor), typeof(Color?), typeof(BorderBeam),
            new PropertyMetadata(null, (s, e) => ((BorderBeam)s).OnTintInvalidated()));
        public Color? AccentColor { get => (Color?)GetValue(AccentColorProperty); set => SetValue(AccentColorProperty, value); }

        /// <summary>TopDock 밝기 부스트 — 원본 md dark 대비 밝게(어두운 배경 가독성). 1.0이면 원본 그대로.</summary>
        public static readonly DependencyProperty BoostProperty = DependencyProperty.Register(
            nameof(Boost), typeof(double), typeof(BorderBeam), new PropertyMetadata(1.0));
        public double Boost { get => (double)GetValue(BoostProperty); set => SetValue(BoostProperty, value); }

        // ── 시계/페이드 ──
        private readonly DispatcherTimer _timer;
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private double _t;                // 애니메이션 시계(초)
        private double _fade;             // --beam-opacity
        private double _fadeFrom;         // 페이드 시작 시점 값
        private double _fadeStartTime = -1;
        private bool _fadingIn;

        private void OnActiveChanged()
        {
            _fadeFrom = _fade;
            _fadeStartTime = Now;
            _fadingIn = Active;
            UpdateTimer();
        }

        private double Now => _clock.Elapsed.TotalSeconds;

        private void OnTick(object? sender, EventArgs e)
        {
            _t = Now;

            if (_fadeStartTime >= 0)
            {
                double dur = _fadingIn ? FadeInSec : FadeOutSec;
                double k = Math.Min(1.0, (_t - _fadeStartTime) / dur);
                double eased = CssEase(k);
                _fade = _fadeFrom + ((_fadingIn ? 1.0 : 0.0) - _fadeFrom) * eased;
                if (k >= 1.0) { _fade = _fadingIn ? 1.0 : 0.0; _fadeStartTime = -1; UpdateTimer(); }
            }
            InvalidateVisual();
        }

        private void UpdateTimer()
        {
            bool need = IsVisible && (Active || _fade > 0 || _fadeStartTime >= 0);
            if (need && !_timer.IsEnabled) _timer.Start();
            else if (!need && _timer.IsEnabled) _timer.Stop();
        }

        /// <summary>CSS ease = cubic-bezier(0.25, 0.1, 0.25, 1)</summary>
        private static double CssEase(double t)
        {
            const double x1 = 0.25, y1 = 0.1, x2 = 0.25, y2 = 1.0;
            double lo = 0, hi = 1, x = t;
            for (int i = 0; i < 10; i++)
            {
                x = (lo + hi) / 2;
                double bx = 3 * (1 - x) * x * x * x1 + 3 * (1 - x) * (1 - x) * x * x2 + x * x * x;
                if (bx < t) lo = x; else hi = x;
            }
            return 3 * (1 - x) * (1 - x) * x * y1 + 3 * (1 - x) * x * x * y2 + x * x * x;
        }

        // ── 링 지오메트리: 라운드 rect 테두리를 균등 호長로 샘플 ──
        private struct RingPt { public double X, Y, Ang; } // Ang: 12시 방향 시계각 [0,1)
        private readonly List<RingPt> _ring = new();
        private double _radius;

        private void Rebuild()
        {
            _ring.Clear();
            double w = ActualWidth, h = ActualHeight;
            if (w < 4 || h < 4) { InvalidateVisual(); return; }
            double r = double.IsNaN(CornerRadius) ? Math.Min(w, h) / 2 : Math.Clamp(CornerRadius, 0.5, Math.Min(w, h) / 2);
            _radius = r;

            var pts = new List<Point> { new(r, 0), new(w - r, 0) };
            Arc(pts, w - r, r, 0, Math.PI / 2, r);          // TR: 동→남
            pts.Add(new Point(w, r)); pts.Add(new Point(w, h - r));
            Arc(pts, w - r, h - r, Math.PI / 2, Math.PI, r); // BR
            pts.Add(new Point(w - r, h)); pts.Add(new Point(r, h));
            Arc(pts, r, h - r, Math.PI, Math.PI * 1.5, r);   // BL
            pts.Add(new Point(0, h - r)); pts.Add(new Point(0, r));
            Arc(pts, r, r, Math.PI * 1.5, Math.PI * 2, r);   // TL

            double per = 0;
            var cum = new double[pts.Count];
            for (int i = 0; i < pts.Count - 1; i++) { per += Dist(pts[i], pts[i + 1]); cum[i + 1] = per; }

            int n = Math.Max(72, (int)(per / 1.2));
            double cx = w / 2, cy = h / 2;
            int seg = 0;
            for (int i = 0; i < n; i++)
            {
                double d = per * i / n;
                while (seg < pts.Count - 2 && cum[seg + 1] < d) seg++;
                double segLen = cum[seg + 1] - cum[seg];
                double f = segLen < 1e-9 ? 0 : (d - cum[seg]) / segLen;
                double x = pts[seg].X + (pts[seg + 1].X - pts[seg].X) * f;
                double y = pts[seg].Y + (pts[seg + 1].Y - pts[seg].Y) * f;
                double ang = Math.Atan2(x - cx, cy - y) / (Math.PI * 2); // 12시=0, 시계+
                ang -= Math.Floor(ang);
                _ring.Add(new RingPt { X = x, Y = y, Ang = ang });
            }
            InvalidateVisual();
        }

        private static void Arc(List<Point> pts, double cx, double cy, double a0, double a1, double r)
        {
            const int steps = 8;
            for (int i = 1; i <= steps; i++)
            {
                double a = a0 + (a1 - a0) * i / steps;
                pts.Add(new Point(cx + r * Math.Sin(a), cy - r * Math.Cos(a)));
            }
        }
        private static double Dist(Point a, Point b) { double dx = a.X - b.X, dy = a.Y - b.Y; return Math.Sqrt(dx * dx + dy * dy); }

        // ── conic stop 곡선 LUT (원본 md dark stops 그대로) ──
        private static double[] Lut((double at, double a)[] stops)
        {
            var lut = new double[512];
            for (int i = 0; i < 512; i++)
            {
                double p = i / 511.0;
                double a;
                if (p <= stops[0].at) a = stops[0].a;
                else if (p >= stops[^1].at) a = stops[^1].a;
                else
                {
                    int j = 0;
                    while (j < stops.Length - 2 && stops[j + 1].at < p) j++;
                    double t = (p - stops[j].at) / (stops[j + 1].at - stops[j].at);
                    a = stops[j].a + (stops[j + 1].a - stops[j].a) * t;
                }
                lut[i] = a;
            }
            return lut;
        }

        // stroke 회전 마스크: 투명 0-30% → 0.1@36 → 0.35@44 → 1 @52-80 → 0.35@86 → 0.1@92 → 투명 95
        private static readonly double[] MaskStroke = Lut(new[]
        {
            (0.0,0.0),(0.30,0.0),(0.36,0.10),(0.44,0.35),(0.52,1.0),(0.80,1.0),(0.86,0.35),(0.92,0.10),(0.95,0.0),(1.0,0.0)
        });
        // inner 회전 마스크: 투명 0-22 → 0.12@28 → 0.4@36 → 1 @46-82 → 0.4@88 → 0.12@94 → 투명 97
        private static readonly double[] MaskInner = Lut(new[]
        {
            (0.0,0.0),(0.22,0.0),(0.28,0.12),(0.36,0.40),(0.46,1.0),(0.82,1.0),(0.88,0.40),(0.94,0.12),(0.97,0.0),(1.0,0.0)
        });
        // stroke 배경 흰 스윕(dark): 투명 0-54 → 0.1@57 → 0.3@60 → 0.6@63 → 0.75@66 → 0.6@69 → 0.3@72 → 0.1@75 → 투명 78
        private static readonly double[] Sweep = Lut(new[]
        {
            (0.0,0.0),(0.54,0.0),(0.57,0.10),(0.60,0.30),(0.63,0.60),(0.66,0.75),(0.69,0.60),(0.72,0.30),(0.75,0.10),(0.78,0.0),(1.0,0.0)
        });
        // bloom 헤드(dark): 투명 0-58 → 0.03@62 → 0.08@65 → 0.2@67 → 0.45@69 → 0.85@70-70.5 → 0.45@71.5 → 0.2@73 → 0.08@75 → 0.03@78 → 투명 82
        private static readonly double[] Head = Lut(new[]
        {
            (0.0,0.0),(0.58,0.0),(0.62,0.03),(0.65,0.08),(0.67,0.20),(0.69,0.45),(0.70,0.85),(0.705,0.85),(0.715,0.45),(0.73,0.20),(0.75,0.08),(0.78,0.03),(0.82,0.0),(1.0,0.0)
        });

        private static double LutAt(double[] lut, double p)
        {
            p -= Math.Floor(p);
            return lut[(int)(p * 511.999)];
        }

        // ── 팔레트(md 프리셋 border blobs: pos %, size px 원본 그대로) ──
        private sealed record Blob(byte R, byte G, byte B, double Px, double Py, double Rx, double Ry);

        private static Blob[] Palette(BeamVariant v) => v switch
        {
            BeamVariant.Mono => new Blob[]
            {
                new(180,180,180, .33,-.074, 70,40), new(140,140,140, .12,-.05, 60,35),
                new(160,160,160, .021,.683, 40,70), new(130,130,130, .021,.683, 20,35),
                new(170,170,170, .744,1.0, 180,32), new(150,150,150, .55,1.0, 85,26),
                new(190,190,190, .939,0.0, 74,32),  new(145,145,145, 1.0,.271, 26,42),
                new(165,165,165, 1.0,.271, 52,48),
            },
            BeamVariant.Ocean => new Blob[]
            {
                new(100,80,220, .33,-.074, 70,40), new(60,120,255, .12,-.05, 60,35),
                new(80,100,200, .021,.683, 40,70), new(50,140,220, .021,.683, 20,35),
                new(120,80,255, .744,1.0, 180,32), new(70,130,255, .55,1.0, 85,26),
                new(140,100,240, .939,0.0, 74,32), new(90,110,230, 1.0,.271, 26,42),
                new(130,70,255, 1.0,.271, 52,48),
            },
            BeamVariant.Sunset => new Blob[]
            {
                new(255,80,50, .33,-.074, 70,40), new(255,160,40, .12,-.05, 60,35),
                new(255,120,60, .021,.683, 40,70), new(255,200,50, .021,.683, 20,35),
                new(255,100,80, .744,1.0, 180,32), new(255,180,60, .55,1.0, 85,26),
                new(255,60,60, .939,0.0, 74,32),  new(255,140,50, 1.0,.271, 26,42),
                new(255,90,70, 1.0,.271, 52,48),
            },
            BeamVariant.Forest => new Blob[]
            {
                new(46,160,90, .33,-.074, 70,40), new(30,190,120, .12,-.05, 60,35),
                new(70,180,70, .021,.683, 40,70), new(20,150,130, .021,.683, 20,35),
                new(90,200,80, .744,1.0, 180,32), new(40,170,110, .55,1.0, 85,26),
                new(120,210,70, .939,0.0, 74,32), new(35,145,100, 1.0,.271, 26,42),
                new(60,195,140, 1.0,.271, 52,48),
            },
            BeamVariant.Candy => new Blob[]
            {
                new(240,70,170, .33,-.074, 70,40), new(255,90,140, .12,-.05, 60,35),
                new(215,60,200, .021,.683, 40,70), new(255,110,180, .021,.683, 20,35),
                new(200,80,240, .744,1.0, 180,32), new(250,60,150, .55,1.0, 85,26),
                new(230,120,220, .939,0.0, 74,32), new(245,85,165, 1.0,.271, 26,42),
                new(210,70,230, 1.0,.271, 52,48),
            },
            BeamVariant.Ice => new Blob[]
            {
                new(90,200,240, .33,-.074, 70,40), new(60,175,230, .12,-.05, 60,35),
                new(130,220,250, .021,.683, 40,70), new(70,190,215, .021,.683, 20,35),
                new(110,210,255, .744,1.0, 180,32), new(50,165,220, .55,1.0, 85,26),
                new(150,230,250, .939,0.0, 74,32), new(85,195,235, 1.0,.271, 26,42),
                new(65,180,245, 1.0,.271, 52,48),
            },
            BeamVariant.Gold => new Blob[]
            {
                new(240,190,60, .33,-.074, 70,40), new(255,210,90, .12,-.05, 60,35),
                new(225,165,40, .021,.683, 40,70), new(250,200,70, .021,.683, 20,35),
                new(255,225,120, .744,1.0, 180,32), new(230,175,50, .55,1.0, 85,26),
                new(245,205,85, .939,0.0, 74,32), new(215,155,35, 1.0,.271, 26,42),
                new(255,215,100, 1.0,.271, 52,48),
            },
            _ => new Blob[] // Colorful (기본)
            {
                new(255,50,100, .33,-.074, 70,40), new(40,140,255, .12,-.05, 60,35),
                new(50,200,80, .021,.683, 40,70),  new(30,185,170, .021,.683, 20,35),
                new(100,70,255, .744,1.0, 180,32), new(40,140,255, .55,1.0, 85,26),
                new(255,120,40, .939,0.0, 74,32),  new(240,50,180, 1.0,.271, 26,42),
                new(180,40,240, 1.0,.271, 52,48),
            },
        };

        // ── CSS 필터 체인: hue-rotate(h) → brightness(1.3) → saturate(1.2)를 색에 선적분 ──
        private struct M3 { public double M00, M01, M02, M10, M11, M12, M20, M21, M22; }
        private static M3 HueMatrix(double deg)
        {
            double a = deg * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
            return new M3
            {
                M00 = 0.213 + 0.787 * c - 0.213 * s, M01 = 0.715 - 0.715 * c - 0.715 * s, M02 = 0.072 - 0.072 * c + 0.928 * s,
                M10 = 0.213 - 0.213 * c + 0.143 * s, M11 = 0.715 + 0.285 * c + 0.140 * s, M12 = 0.072 - 0.072 * c - 0.283 * s,
                M20 = 0.213 - 0.213 * c - 0.787 * s, M21 = 0.715 - 0.715 * c + 0.715 * s, M22 = 0.072 + 0.928 * c + 0.072 * s,
            };
        }
        private static readonly M3 SatMat = Saturate(Saturation);
        private static M3 Saturate(double s) => new()
        {
            M00 = 0.213 + 0.787 * s, M01 = 0.715 - 0.715 * s, M02 = 0.072 - 0.072 * s,
            M10 = 0.213 - 0.213 * s, M11 = 0.715 + 0.285 * s, M12 = 0.072 - 0.072 * s,
            M20 = 0.213 - 0.213 * s, M21 = 0.715 - 0.715 * s, M22 = 0.072 + 0.928 * s,
        };

        private sealed record TintBlob(double Px, double Py, double Rx, double Ry, byte R, byte G, byte B);
        private readonly Dictionary<int, TintBlob[]> _tintCache = new();

        private void OnTintInvalidated()
        {
            _tintCache.Clear();
            _accentPalette = null;
            InvalidateVisual();
        }

        // 액센트 색에서 파생한 베이스 팔레트(원본 블롭 좌표 그대로).
        // 어두운 앨범 지배색을 그대로 쓰면 블롭 전체가 어두워져 beam이 읽히지 않으므로,
        // 색상(hue)은 유지하되 채도·명도를 원본 팔레트급으로 정규화한다.
        private Blob[]? _accentPalette;
        private Blob[] BasePalette()
        {
            if (AccentColor is not Color acc) return Palette(Variant);
            if (_accentPalette != null) return _accentPalette;

            // 1) 가장 밝은 채널을 205로 스케일 — 어두운 색도 원본급 명도로
            double max = Math.Max(acc.R, Math.Max(acc.G, acc.B));
            double scale = max < 24 ? 0 : Math.Min(2.6, 205.0 / max);
            if (max < 24)
            {
                // 거의 검정이면 선명한 중립(원본 mono 톤)
                scale = 1.0;
                acc = Color.FromRgb(205, 205, 215);
            }
            double br0 = Math.Min(255, acc.R * scale);
            double bg0 = Math.Min(255, acc.G * scale);
            double bb0 = Math.Min(255, acc.B * scale);

            double[] mul = { 1.15, 0.90, 1.25, 0.85, 1.05, 0.80, 1.20, 0.95, 1.10 };
            var geo = Palette(BeamVariant.Colorful); // 좌표·크기 원본 그대로
            var result = new Blob[geo.Length];
            for (int i = 0; i < geo.Length; i++)
            {
                var g = geo[i];
                double f = mul[i % mul.Length];
                double r = br0 * f, gg = bg0 * f, b = bb0 * f;
                // 2) 블롭별 최소 명도 보장 — 어두운 변주도 beam이 읽히는 선까지 끌어올린다
                double m = Math.Max(r, Math.Max(gg, b));
                if (m > 0 && m < 140) { double k = 140 / m; r *= k; gg *= k; b *= k; }
                result[i] = new Blob(
                    (byte)Math.Clamp(r + 0.5, 0, 255),
                    (byte)Math.Clamp(gg + 0.5, 0, 255),
                    (byte)Math.Clamp(b + 0.5, 0, 255),
                    g.Px, g.Py, g.Rx, g.Ry);
            }
            _accentPalette = result;
            return result;
        }

        /// <summary>hueStep 0..60 → hue-rotate(-30°..+30°) 후 brightness·saturate 적용한 팔레트.</summary>
        private TintBlob[] TintedPalette(int hueStep)
        {
            if (_tintCache.TryGetValue(hueStep, out var cached)) return cached;
            var src = BasePalette();
            var hue = HueMatrix(-HueRange + 2 * HueRange * hueStep / 60.0);
            var result = new TintBlob[src.Length];
            for (int i = 0; i < src.Length; i++)
            {
                var b = src[i];
                double r1 = hue.M00 * b.R + hue.M01 * b.G + hue.M02 * b.B;
                double g1 = hue.M10 * b.R + hue.M11 * b.G + hue.M12 * b.B;
                double b1 = hue.M20 * b.R + hue.M21 * b.G + hue.M22 * b.B;
                double r2 = (r1 * Brightness) * SatMat.M00 + (g1 * Brightness) * SatMat.M01 + (b1 * Brightness) * SatMat.M02;
                double g2 = (r1 * Brightness) * SatMat.M10 + (g1 * Brightness) * SatMat.M11 + (b1 * Brightness) * SatMat.M12;
                double b2 = (r1 * Brightness) * SatMat.M20 + (g1 * Brightness) * SatMat.M21 + (b1 * Brightness) * SatMat.M22;
                result[i] = new TintBlob(b.Px, b.Py, b.Rx, b.Ry,
                    (byte)Math.Clamp(r2, 0, 255), (byte)Math.Clamp(g2, 0, 255), (byte)Math.Clamp(b2, 0, 255));
            }
            _tintCache[hueStep] = result;
            return result;
        }

        // ── 회전 conic 마스크 비트맵(1° 쐐기) — inner/stroke 창 공용 생성기 ──
        private static BitmapSource BuildConicMask(double[] lut)
        {
            const int D = 512;
            var dv = new DrawingVisual();
            using (var ctx = dv.RenderOpen())
            {
                double cx = D / 2.0, cy = D / 2.0, r = D; // 반경을 여유 있게
                for (int i = 0; i < 360; i++)
                {
                    double a0 = i * Math.PI / 180 - Math.PI / 2, a1 = (i + 1.02) * Math.PI / 180 - Math.PI / 2;
                    byte a = (byte)(LutAt(lut, (i + 0.5) / 360.0) * 255);
                    var geo = new StreamGeometry();
                    using (var sg = geo.Open())
                    {
                        sg.BeginFigure(new Point(cx, cy), true, true);
                        sg.LineTo(new Point(cx + r * Math.Cos(a0), cy + r * Math.Sin(a0)), true, false);
                        sg.ArcTo(new Point(cx + r * Math.Cos(a1), cy + r * Math.Sin(a1)),
                            new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
                    }
                    var brush = new SolidColorBrush(Color.FromArgb(a, 255, 255, 255));
                    brush.Freeze();
                    ctx.DrawGeometry(brush, null, geo);
                }
            }
            var rtb = new RenderTargetBitmap(D, D, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }

        private ImageBrush MaskBrush(double[] lut, double w, double h, double angleFrac, ref BitmapSource? cache)
        {
            cache ??= BuildConicMask(lut);
            double diag = Math.Sqrt(w * w + h * h) + 4;
            var brush = new ImageBrush(cache)
            {
                Viewbox = new Rect(0, 0, 512, 512),
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewport = new Rect((w - diag) / 2, (h - diag) / 2, diag, diag),
                ViewportUnits = BrushMappingMode.Absolute,
                Stretch = Stretch.Fill,
            };
            brush.Transform = new RotateTransform(angleFrac * 360.0, w / 2, h / 2);
            return brush;
        }
        private BitmapSource? _maskStrokeBmp, _maskInnerBmp;

        // ── 펜 캐시 ──
        private readonly Dictionary<long, Pen> _penCache = new();
        private Pen PenFor(byte r, byte g, byte b, byte a, double thickness)
        {
            long key = ((long)Math.Round(thickness * 4) << 29) | ((long)a << 21) | ((long)(r >> 2) << 14) | ((long)(g >> 2) << 7) | (long)(b >> 2);
            if (_penCache.TryGetValue(key, out var p)) return p;
            var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            brush.Freeze();
            var pen = new Pen(brush, thickness);
            pen.Freeze();
            _penCache[key] = pen;
            return pen;
        }

        // ── 렌더 ──
        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            double w = ActualWidth, h = ActualHeight;
            if (w < 4 || h < 4 || _ring.Count < 2 || _fade <= 0.001) return;

            double bw = BorderWidth;
            double monoMul = Variant == BeamVariant.Mono ? 0.5 : 1.0; // 원본: mono는 opacity 절반
            double fade = _fade * Math.Clamp(Strength, 0, 1);
            double inten = Math.Max(0.1, Intensity);
            double boost = Math.Max(0.1, Boost); // 밝기 부스트 — 1.0이면 원본 md dark 그대로
            double strokeA = StrokeOpacity * monoMul * fade * 2.0 * inten * boost;
            double innerA = InnerOpacity * monoMul * fade * 1.55 * inten * boost;
            double bloomA = BloomOpacity * monoMul * fade * 1.9 * inten * boost;

            // hue-shift: ±30° ease-in-out 왕복(원본 Oe 사이클) → 61단계 양자화
            int hueStep = (int)Math.Round((0.5 - 0.5 * Math.Cos(2 * Math.PI * (_t / HuePeriod))) * 60.0);
            var blobs = TintedPalette(Math.Clamp(hueStep, 0, 60));

            double beamAngle = (_t / SpinPeriod) % 1.0; // 회전 위상 [0,1)
            double cx = w / 2, cy = h / 2;

            // 전체 클립: clip-path inset(0 round R)
            var clip = new RectangleGeometry(new Rect(0, 0, w, h), _radius, _radius);
            clip.Freeze();
            dc.PushClip(clip);

            var ringGeo = RingGeometry(w, h, bw);

            // ═══ z1 inner: 블롭(0.9배, α.45) + inset 흰 그림자 × 회전 넓은 창 × 0.42 ═══
            dc.PushOpacityMask(MaskBrush(MaskInner, w, h, beamAngle, ref _maskInnerBmp));
            dc.PushOpacity(innerA);
            foreach (var b in blobs)
            {
                var brush = new RadialGradientBrush(Color.FromArgb(115, b.R, b.G, b.B), Colors.Transparent) // α 0.45
                {
                    Center = new Point(0.5, 0.5), GradientOrigin = new Point(0.5, 0.5),
                    RadiusX = 0.5, RadiusY = 0.5,
                };
                brush.Freeze();
                dc.DrawEllipse(brush, null, new Point(b.Px * w, b.Py * h), b.Rx * 0.9, b.Ry * 0.9);
            }
            // box-shadow: inset 0 0 5px 1px rgba(255,255,255,0.27) — 테두리 안쪽 5px 소프트 림
            var innerGeo = new RectangleGeometry(
                new Rect(bw * 0.5, bw * 0.5, Math.Max(1, w - bw), Math.Max(1, h - bw)),
                Math.Max(0, _radius - bw * 0.5), Math.Max(0, _radius - bw * 0.5));
            innerGeo.Freeze();
            dc.DrawGeometry(null, PenFor(255, 255, 255, 69, 1.6), innerGeo);  // 0.27 * fade는 PushOpacity가 담당
            dc.DrawGeometry(null, PenFor(255, 255, 255, 46, 3.2), innerGeo);
            dc.DrawGeometry(null, PenFor(255, 255, 255, 24, 5.0), innerGeo);
            dc.Pop(); // opacity
            dc.Pop(); // opacityMask

            // ═══ z2 stroke: (블롭 full → 흰 스윕 over) 링 1px × 회전 좁은 창 × 0.26 ═══
            dc.PushClip(ringGeo);
            dc.PushOpacityMask(MaskBrush(MaskStroke, w, h, beamAngle, ref _maskStrokeBmp));
            dc.PushOpacity(strokeA);
            for (int i = 0; i < _ring.Count; i++)
            {
                var p = _ring[i];
                // 블롭 누적(뒤→앞 source-over) — 링 위에서의 정확한 색 계산
                double ar = 0, ag = 0, ab = 0, aa = 0;
                for (int k = blobs.Length - 1; k >= 0; k--)
                {
                    var b = blobs[k];
                    double dx = (p.X - b.Px * w) / b.Rx, dy = (p.Y - b.Py * h) / b.Ry; // 스트로크 bg는 풀 사이즈 블롭
                    double rr = Math.Sqrt(dx * dx + dy * dy);
                    if (rr >= 1) continue;
                    double la = 1 - rr; // color→transparent 선형 램프
                    ar = b.R * la + ar * (1 - la);
                    ag = b.G * la + ag * (1 - la);
                    ab = b.B * la + ab * (1 - la);
                    aa = la + aa * (1 - la);
                }
                // 흰 스윕(source-over) — 회전 conic이므로 지점각 - 위상으로 평가
                double sw = LutAt(Sweep, p.Ang - beamAngle);
                if (sw > 0.001)
                {
                    ar = 255 * sw + ar * (1 - sw);
                    ag = 255 * sw + ag * (1 - sw);
                    ab = 255 * sw + ab * (1 - sw);
                    aa = sw + aa * (1 - sw);
                }
                byte aOut = (byte)(aa * 255);
                if (aOut < 3) continue;
                var q = _ring[(i + 1) % _ring.Count];
                dc.DrawLine(PenFor((byte)ar, (byte)ag, (byte)ab, aOut, bw), new Point(p.X, p.Y), new Point(q.X, q.Y));
            }
            dc.Pop(); // opacity
            dc.Pop(); // opacityMask
            dc.Pop(); // clip(ring)

            // ═══ z3 bloom: 흰 헤드 conic × 링 × blur(8px) 4패스 가우시안 근사 × 0.24 ═══
            dc.PushClip(ringGeo);
            dc.PushOpacity(bloomA);
            for (int i = 0; i < _ring.Count; i++)
            {
                var p = _ring[i];
                double head = LutAt(Head, p.Ang - beamAngle);
                if (head < 0.02) continue;
                byte aBase = (byte)(head * 255);
                var q = _ring[(i + 1) % _ring.Count];
                var pt1 = new Point(p.X, p.Y);
                var pt2 = new Point(q.X, q.Y);
                dc.DrawLine(PenFor(255, 255, 255, (byte)(aBase * 0.13), GlowBlur * 1.3), pt1, pt2);
                dc.DrawLine(PenFor(255, 255, 255, (byte)(aBase * 0.26), GlowBlur * 0.8), pt1, pt2);
                dc.DrawLine(PenFor(255, 255, 255, (byte)(aBase * 0.5), GlowBlur * 0.42), pt1, pt2);
                dc.DrawLine(PenFor(255, 255, 255, (byte)(aBase * 0.9), GlowBlur * 0.16), pt1, pt2);
            }
            dc.Pop(); // opacity
            dc.Pop(); // clip(ring)

            dc.Pop(); // clip(all)
        }

        // ── 1px 링 지오메트리(외곽 라운드 rect − 내부 라운드 rect) ──
        private Geometry? _ringGeoCache;
        private double _geoW, _geoH, _geoBw;

        private Geometry RingGeometry(double w, double h, double bw)
        {
            if (_ringGeoCache != null && Math.Abs(_geoW - w) < 0.5 && Math.Abs(_geoH - h) < 0.5 && Math.Abs(_geoBw - bw) < 0.01)
                return _ringGeoCache;
            double r = _radius;
            var outer = new RectangleGeometry(new Rect(0, 0, w, h), r, r);
            double ir = Math.Max(0, r - bw);
            var inner = new RectangleGeometry(new Rect(bw, bw, Math.Max(0, w - 2 * bw), Math.Max(0, h - 2 * bw)), ir, ir);
            var geo = new CombinedGeometry(GeometryCombineMode.Exclude, outer, inner);
            geo.Freeze();
            _ringGeoCache = geo; _geoW = w; _geoH = h; _geoBw = bw;
            return geo;
        }
    }
}
