using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace TopDock.Controls
{
    /// <summary>빛의 형태 — Line은 얇은 선, Large는 굵고 크게 도는 빛.</summary>
    public enum BeamForm { Line, Large }

    /// <summary>빛의 색 — Mono는 한 가지 색, Album은 앨범색에서 꼬리를 따라 색이 번진다.</summary>
    public enum BeamTint { Mono, Album }

    /// <summary>
    /// TopDock의 "살아있는 가장자리(living edge)".
    ///
    /// border-beam 원본은 9색 무지개 블롭이 테두리 전체를 훑는 장식이었지만,
    /// 그대로 쓰면 어디서나 보는 그 테두리가 된다. 이 버전은 장식 대신 '의미'를 담는다:
    ///   · 단일 액센트 색 — 무지개 대신 한 가지 빛(기본은 중립 화이트, 앨범색을 주면 그 색)
    ///   · 혜성 헤드 + 꼬리 — 좁은 호만 밝고 나머지는 어둑하게 남는다
    ///   · 상시 림 — 평소에도 테두리 자체는 은은하게 살아 있다
    ///   · 운동이 언어 — Speed가 초당 바퀴 수(음수=반시계). 상태에 따라 속도·방향만 바꾼다
    ///
    /// 원본과 달리 conic 마스크 비트맵을 쓰지 않는다. 링 세그먼트마다 헤드로부터의
    /// 각도차로 알파를 직접 계산하므로 크기 변화 때 무거운 재생성이 없다(모프 끊김 원인 제거).
    /// </summary>
    public class BorderBeam : FrameworkElement
    {
        private const double TickSeconds = 1.0 / 60.0;
        private const double FadeInSec = 0.6;
        private const double FadeOutSec = 0.5;

        // ── 살아있는 가장자리 상수 ──
        private const double BaseRim = 0.26;      // 상시 림 알파(어둑한 가장자리)
        private const double HeadSigma = 0.026;   // 헤드 코어 반폭(둘레 비율)
        private const double TailSpan = 0.46;     // 꼬리 길이(둘레 비율)
        private const double TailFalloff = 0.115; // 꼬리 감쇠
        private const double BloomGain = 1.3;
        private const double AlbumHueSpread = 180.0; // Album 색에서 꼬리 끝까지 색상이 도는 각도(deg)

        /// <summary>액센트가 없을 때의 기본 빛 — 중립 화이트(앨범색이 오면 그 색으로 바뀐다).</summary>
        private static readonly Color DefaultAccent = Color.FromRgb(0xF0, 0xF3, 0xF8);

        public BorderBeam()
        {
            IsHitTestVisible = false;
            _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromSeconds(TickSeconds) };
            _timer.Tick += OnTick;
            IsVisibleChanged += (s, e) => UpdateTimer();
            SizeChanged += (s, e) => Rebuild();
        }

        // ── 속성 ──
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
        /// <summary>전체 밝기(0~1).</summary>
        public double Strength { get => (double)GetValue(StrengthProperty); set => SetValue(StrengthProperty, value); }

        public static readonly DependencyProperty FlashProperty = DependencyProperty.Register(
            nameof(Flash), typeof(double), typeof(BorderBeam),
            new PropertyMetadata(0.0, (s, e) => ((BorderBeam)s).InvalidateVisual()));
        /// <summary>순간 강조(0~1) — 복사 같은 순간에 가장자리가 한 번 밝아진다.
        /// Strength와 달리 1을 넘는 밝기를 낼 수 있어야 해서 곱하지 않고 따로 더한다.</summary>
        public double Flash { get => (double)GetValue(FlashProperty); set => SetValue(FlashProperty, value); }

        public static readonly DependencyProperty ActiveProperty = DependencyProperty.Register(
            nameof(Active), typeof(bool), typeof(BorderBeam),
            new PropertyMetadata(false, (s, e) => ((BorderBeam)s).OnActiveChanged()));
        public bool Active { get => (bool)GetValue(ActiveProperty); set => SetValue(ActiveProperty, value); }

        /// <summary>운동의 양 — 혜성 헤드가 초당 도는 바퀴 수. 음수면 반시계, 0이면 제자리에서 호흡만.
        /// 상태(대기 느리게 / 사고 빠르게 / 오류 역방향)를 이 하나로 표현한다.</summary>
        public static readonly DependencyProperty SpeedProperty = DependencyProperty.Register(
            nameof(Speed), typeof(double), typeof(BorderBeam), new PropertyMetadata(0.05));
        public double Speed { get => (double)GetValue(SpeedProperty); set => SetValue(SpeedProperty, value); }

        /// <summary>빛의 색. null이면 중립 화이트. 앨범 지배색 등을 넘겨주면 그 색으로 빛난다.</summary>
        public static readonly DependencyProperty AccentColorProperty = DependencyProperty.Register(
            nameof(AccentColor), typeof(Color?), typeof(BorderBeam), new PropertyMetadata(null));
        public Color? AccentColor { get => (Color?)GetValue(AccentColorProperty); set => SetValue(AccentColorProperty, value); }

        /// <summary>빛의 형태 — Line(얇은 선) / Large(굵고 크게 도는 빛).</summary>
        public static readonly DependencyProperty FormProperty = DependencyProperty.Register(
            nameof(Form), typeof(BeamForm), typeof(BorderBeam), new PropertyMetadata(BeamForm.Line));
        public BeamForm Form { get => (BeamForm)GetValue(FormProperty); set => SetValue(FormProperty, value); }

        /// <summary>빛의 색 — Mono(한 가지 색) / Album(앨범색에서 꼬리를 따라 색이 번진다).</summary>
        public static readonly DependencyProperty TintProperty = DependencyProperty.Register(
            nameof(Tint), typeof(BeamTint), typeof(BorderBeam), new PropertyMetadata(BeamTint.Mono));
        public BeamTint Tint { get => (BeamTint)GetValue(TintProperty); set => SetValue(TintProperty, value); }

        // ── 시계/페이드 ──
        private readonly DispatcherTimer _timer;
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private double _t;                // 애니메이션 시계(초)
        private double _head;             // 혜성 헤드의 누적 위상 [0,1). 속도·둘레가 바뀌어도 연속이다.
        private double _lastTick;         // 직전 틱 시각 — 위상 증가분(dt) 계산용
        private double _fade;             // 현재 불투명도
        private double _fadeFrom;
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
            double now = Now;

            // 헤드 위상은 _t × Speed로 매번 새로 만들지 않고 증가분을 적분해 쌓는다.
            // 곱셈으로 만들면 노치가 커질 때 _perimeter가(그리고 speedScale이) 바뀌는 만큼
            // 이미 큰 _t가 통째로 곱해져 위상이 튼다 — 확장 순간 빛이 여러 바퀴 휙 돌던 원인.
            double dt = Math.Clamp(now - _lastTick, 0.0, 0.1);
            _lastTick = now;
            _t = now;
            _head += dt * Speed * Math.Clamp(240.0 / _perimeter, 0.30, 1.0);
            _head -= Math.Floor(_head);

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
        private double _perimeter = 240; // 링 둘레 길이 — 큰 표면일수록 빛이 천천히 흘러 눈에 담긴다

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
            _perimeter = Math.Max(1, per);

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

        // ── 펜 캐시(양자화 키로 무한 증식 방지) ──
        private readonly Dictionary<int, Pen> _penCache = new();
        private Pen PenFor(Color c, int a, double thickness)
        {
            a = Math.Clamp(a, 0, 255);
            int qa = a >> 2;                                  // 6bit
            int qr = c.R >> 3, qg = c.G >> 3, qb = c.B >> 3;  // 5bit
            int qt = Math.Clamp((int)Math.Round(thickness * 4), 0, 63); // 0.25px 단위
            int key = (qt << 21) | (qa << 15) | (qr << 10) | (qg << 5) | qb;
            if (_penCache.TryGetValue(key, out var p)) return p;
            var brush = new SolidColorBrush(Color.FromArgb((byte)(qa << 2), (byte)(qr << 3), (byte)(qg << 3), (byte)(qb << 3)));
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
            double fade = _fade * Math.Clamp(Strength, 0, 1);
            if (fade <= 0.001) return;

            Color accent = AccentColor is Color ac ? ac : DefaultAccent;
            // 순간 강조 — 림·블룸·혜성을 한꺼번에 올려 "빛났다"는 느낌을 만든다
            double edgeGain = 1.0 + Math.Clamp(Flash, 0, 1) * 1.5;
            double litFade = fade * edgeGain;   // 밝기 계산은 전부 여기에 턴다

            // 형태 — Large는 헤드가 크고 굵게, 꼬리도 조금 길게 돈다
            bool large = Form == BeamForm.Large;
            double formScale = large ? 2.0 : 1.0;

            // 크기 보정 — 확장 노치처럼 큰 표면에선 고정 px 빛이 상대적으로 티가 안 난다.
            // 밒의 굵기·블룸·헤드를 표면 크기에 비례해 키워 존재감을 유지한다.
            double lightScale = Math.Clamp(Math.Sqrt(w * h) / 65.0, 1.0, 3.2);
            double gain = Math.Sqrt(lightScale); // 굵기·헤드는 완만하게(과하면 뭉개진다)
            // 안쪽 물들임 — 빛이 패널 안으로 번진다. 작은 알약(lightScale 1.0)에서도
            // 약간은 번져야 혜성이 테두리 위의 선이 아니라 '공간을 채운 빛'으로 읽힌다.
            double wash = Math.Clamp((lightScale - 0.9) * 0.17, 0, 0.24);
            // 색 유리 틴트 — 앨범색이 테두리에서 안쪽으로 스며들어 공간이 곱의 색을 띤다
            double tint = Tint == BeamTint.Album ? Math.Clamp((lightScale - 0.95) * 0.09, 0, 0.20) : 0.0;

            double headSigma = HeadSigma * formScale * gain;
            double tailSpan = TailSpan * (large ? 1.12 : 1.0);
            double cometW = bw * (large ? 3.2 : 2.0) * Math.Min(lightScale, 2.5);

            // 헤드 위상 — OnTick이 적분해 둔 값을 그대로 쓴다. 여기서 _t × Speed로 다시 만들면
            // 노치가 커지는 동안 _perimeter가 바뀌는 만큼 위상이 튀어 빛이 여러 바퀴 휙 돈다.
            // (큰 표면일수록 같은 '바퀴'가 훨씬 빠른 픽셀 속도라, 둘레로 속도를 정규화하는 것
            //  자체는 OnTick 쪽에서 그대로 유지한다.)
            double head = _head;
            // 느린 호흡 — 빛이 살아 숨쉬는 느낌
            double breathe = 0.88 + 0.12 * Math.Sin(_t * 1.6);

            var clip = new RectangleGeometry(new Rect(0, 0, w, h), _radius, _radius);
            clip.Freeze();
            dc.PushClip(clip);

            var ringGeo = RingGeometry(w, h, bw);

            // 0) 색 유리 틴트 — 가장자리에서 안쪽으로 앨범색이 스며든다.
            //    균일 채우기 대신 비네트라 가운데는 어둡게 남아 내용이 묻히지 않는다.
            if (tint > 0.005)
            {
                byte ta = (byte)Math.Clamp(tint * fade * 255, 0, 255);
                var edge = Color.FromArgb(ta, accent.R, accent.G, accent.B);
                var clear = Color.FromArgb(0, accent.R, accent.G, accent.B);
                var tb = new RadialGradientBrush(clear, edge)
                {
                    Center = new Point(0.5, 0.5), GradientOrigin = new Point(0.5, 0.5),
                    RadiusX = 0.58, RadiusY = 0.58,
                };
                tb.GradientStops.Insert(1, new GradientStop(clear, 0.35));
                tb.Freeze();
                dc.DrawRectangle(tb, null, new Rect(0, 0, w, h));
            }

            // 1) 상시 림 — 평소에도 테두리는 어둑하게 살아 있다
            dc.DrawGeometry(null, PenFor(accent,
                (int)Math.Clamp(BaseRim * litFade * 255, 0, 255), bw), ringGeo);

            // 2) 블룸 — 헤드 주변의 accent 광이 테두리 안팎으로 번진다
            DrawBloom(dc, accent, head, litFade, breathe, formScale * gain, headSigma, tailSpan);

            // 2.5) 안쪽 물들임 — 헤드 빛이 패널 안으로 퍼진다(큰 표면 전용)
            DrawWash(dc, accent, head, fade, breathe, wash * edgeGain);

            // 3) 혜성 헤드 + 꼬리 — 링 세그먼트별 알파
            DrawComet(dc, accent, head, litFade, breathe, cometW, headSigma, tailSpan);

            dc.Pop(); // clip
        }

        private void DrawBloom(DrawingContext dc, Color accent, double head, double fade, double breathe,
            double formScale, double headSigma, double tailSpan)
        {
            double[] th = { 11.5, 6.8, 4.0, 2.2 };
            double[] am = { 0.075, 0.15, 0.27, 0.48 };
            for (int i = 0; i < _ring.Count; i++)
            {
                var p = _ring[i];
                double d = SignedDelta(p.Ang, head);
                double a = CometProfile(d, headSigma, tailSpan);
                if (a < 0.02) continue;
                var q = _ring[(i + 1) % _ring.Count];
                var pt1 = new Point(p.X, p.Y);
                var pt2 = new Point(q.X, q.Y);
                for (int k = 0; k < th.Length; k++)
                {
                    byte aa = (byte)Math.Clamp(a * am[k] * fade * breathe * BloomGain * 255, 0, 255);
                    if (aa < 2) continue;
                    dc.DrawLine(PenFor(accent, aa, th[k] * formScale), pt1, pt2);
                }
            }
        }

        private void DrawComet(DrawingContext dc, Color accent, double head, double fade, double breathe,
            double cometW, double headSigma, double tailSpan)
        {
            for (int i = 0; i < _ring.Count; i++)
            {
                var p = _ring[i];
                double d = SignedDelta(p.Ang, head);
                double a = CometProfile(d, headSigma, tailSpan);
                if (a < 0.012) continue;
                double outA = a * fade * breathe;
                if (outA <= 0.004) continue;

                // Album이면 꼬리를 따라 색상이 돈다(다양한 색). Mono는 한 가지 색.
                Color seg = Tint == BeamTint.Album && d < 0
                    ? RotateHue(accent, -AlbumHueSpread * Math.Min(1.0, -d / tailSpan))
                    : accent;

                // 헤드 코어는 흰색에 가깝게(열감), 꼬리로 갈수록 색
                double hot = Math.Exp(-(d * d) / (2 * headSigma * headSigma)) * 1.0;
                byte r = MixChannel(seg.R, 255, hot);
                byte g = MixChannel(seg.G, 255, hot);
                byte b = MixChannel(seg.B, 255, hot);

                var q = _ring[(i + 1) % _ring.Count];
                dc.DrawLine(PenFor(Color.FromRgb(r, g, b), (int)(outA * 255), cometW),
                    new Point(p.X, p.Y), new Point(q.X, q.Y));
            }
        }

        /// <summary>헤드 근처에서 패널 안쪽으로 번지는 빛무리 — 큰 표면에서 빛이 실제로 공간을 채운다.</summary>
        private void DrawWash(DrawingContext dc, Color accent, double head, double fade, double breathe, double wash)
        {
            if (wash <= 0.01) return;

            // 헤드에 가장 가까운 링 점을 찾아 그 자리에서 안쪽으로 퍼뜨린다
            RingPt best = _ring[0]; double bd = 1.0;
            for (int i = 0; i < _ring.Count; i++)
            {
                double d = Math.Abs(SignedDelta(_ring[i].Ang, head));
                if (d < bd) { bd = d; best = _ring[i]; }
            }

            double r = Math.Min(ActualWidth, ActualHeight) * 0.60;
            double a = Math.Clamp(wash * fade * breathe, 0, 1);
            var brush = new RadialGradientBrush(
                Color.FromArgb((byte)(a * 255), accent.R, accent.G, accent.B), Colors.Transparent)
            {
                Center = new Point(0.5, 0.5), GradientOrigin = new Point(0.5, 0.5),
                RadiusX = 0.5, RadiusY = 0.5,
            };
            brush.Freeze();
            dc.DrawEllipse(brush, null, new Point(best.X, best.Y), r, r);
        }

        /// <summary>헤드로부터의 부호 있는 각도차 [−0.5, 0.5). 음수=꼬리 쪽.</summary>
        private static double SignedDelta(double ang, double head)
        {
            double d = ang - head;
            d -= Math.Floor(d + 0.5);
            return d;
        }

        /// <summary>혜성 단면 — 헤드는 좁은 코어, 뒤로는 길게 사그라드는 꼬리.</summary>
        private static double CometProfile(double d, double headSigma, double tailSpan)
        {
            double core = Math.Exp(-(d * d) / (2 * headSigma * headSigma));
            if (d >= 0) return core;
            double t = -d;
            if (t >= tailSpan) return core;
            double tail = Math.Exp(-t / TailFalloff) * (1 - t / tailSpan);
            return Math.Max(core, tail);
        }

        private static byte MixChannel(byte baseC, byte target, double t)
            => (byte)Math.Clamp(baseC + (target - baseC) * t, 0, 255);

        /// <summary>HSV에서 색상만 deg만큼 회전 — Album 모드의 색 번짐용.</summary>
        private static Color RotateHue(Color c, double deg)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            double v = max, d = max - min;
            double s = max <= 1e-6 ? 0 : d / max;
            double h = 0;
            if (d > 1e-6)
            {
                if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
                else if (max == g) h = (b - r) / d + 2;
                else h = (r - g) / d + 4;
                h /= 6;
            }
            h = (h + deg / 360.0) % 1.0;
            if (h < 0) h += 1;

            double i = Math.Floor(h * 6), f = h * 6 - i;
            double p = v * (1 - s), q = v * (1 - f * s), t = v * (1 - (1 - f) * s);
            double rr, gg, bb;
            switch ((int)i % 6)
            {
                case 0: rr = v; gg = t; bb = p; break;
                case 1: rr = q; gg = v; bb = p; break;
                case 2: rr = p; gg = v; bb = t; break;
                case 3: rr = p; gg = q; bb = v; break;
                case 4: rr = t; gg = p; bb = v; break;
                default: rr = v; gg = p; bb = q; break;
            }
            return Color.FromRgb(
                (byte)Math.Clamp(rr * 255, 0, 255),
                (byte)Math.Clamp(gg * 255, 0, 255),
                (byte)Math.Clamp(bb * 255, 0, 255));
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
