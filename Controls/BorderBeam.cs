using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using TopDock.Logic;

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
    ///   · 상시 림 — 평소에도 테두리 자체는 은은하게 살아 있다. 노치 가장자리의 주인은 이 빔
    ///     하나다(본체 Border는 BorderBrush를 비운다) — 두 겹으로 그리면 어느 쪽을 고쳐도
    ///     결과를 예측할 수 없다
    ///   · 운동이 언어 — Speed는 '기준 둘레(240px)에서의 초당 바퀴 수'(음수=반시계).
    ///     픽셀 속도는 240 × Speed로 표면 크기와 무관하게 일정하다 — 큰 카드에서 빛이
    ///     더 빨라지지 않는다. 둘레가 길면 한 바퀴에 걸리는 시간만 길어진다.
    ///
    /// 원본과 달리 conic 마스크 비트맵을 쓰지 않는다. 링 세그먼트마다 헤드로부터의
    /// 각도차로 알파를 직접 계산하므로 크기 변화 때 무거운 재생성이 없다(모프 끊김 원인 제거).
    /// </summary>
    public class BorderBeam : FrameworkElement
    {
        private const double TickSeconds = 1.0 / 60.0;
        private const double FadeInSec = 0.6;
        private const double FadeOutSec = 0.5;

        // ── 시계 ── 프레임 수는 빛이 스스로 정한다(BeamSpeed.FramesPerSecond):
        // 60fps는 굵은 빛·번쩍임·모프를 위한 값이고, 프레임당 1px도 못 가는 빛은 30fps,
        // 거의 멈춘 빛(대기·일시정지, 22px/s 미만)은 15fps로 내려간다. 하루 종일 켜져 있는
        // 앱에서 정지한 테두리를 30fps로 다시 그리는 값은 코어 1개의 13.5%였다(실측).

        // ── 살아있는 가장자리 상수 ──
        // 상시 림 알파. 이 가장자리의 유일한 주인이므로, 예전 두 겹(본체 그래디언트 + 빔 림)의
        // 합성값을 둘레 길이로 가중해 맞춘다 — 위 0.36 · 중간 0.30 · 아래 0.27 → 평균 0.31.
        // 그래서 위 변만 0.36→0.31로 조금 어두워지고(그래디언트의 기울기가 사라진 자리)
        // 옆·아래는 0.27~0.30→0.31로 거의 제자리다. 이제 가장자리는 상태에 따라 함께 숨쉬다.
        private const double BaseRim = 0.31;
        private const double HeadSigma = 0.026;   // 헤드 코어 반폭(둘레 비율)
        private const double TailSpan = 0.46;     // 꼬리 길이(둘레 비율)
        private const double TailFalloff = 0.115; // 꼬리 감쇠
        private const double BloomGain = 1.3;
        private const double AlbumHueSpread = 130.0; // Album 색에서 꼬리 끝까지 색상이 도는 각도(deg)

        // ── 이완(스프링) ── 상태가 바뀔 때 값이 목표로 '즉시' 튀면 빛이 손짓을 한 프레임에 갈아끼운다
        // (그게 이질감이다). 대신 스프링으로 이완한다 — 속도·밝기·형태·색이 각자 자기 무게로
        // 목표에 앉고, 살짝 넘겼다 앉는 오버슈트가 쫀득함을 만든다. 무게(정착 시간)는 값마다 다르다:
        // 운동은 빨리 따라와야 하고(0.5s), 굵기·밝기는 조금 무겁게(0.6s), 색은 가장 천천히(0.7s).
        private static readonly double SpeedOmega = BeamSpring.OmegaFor(0.50);
        private static readonly double StrengthOmega = BeamSpring.OmegaFor(0.60);
        private static readonly double WeightOmega = BeamSpring.OmegaFor(0.60);
        private static readonly double TintOmega = BeamSpring.OmegaFor(0.70);
        private static readonly double ColorOmega = BeamSpring.OmegaFor(0.70);

        /// <summary>액센트가 없을 때의 기본 빛 — 중립 화이트(앨범색이 오면 그 색으로 바뀐다).</summary>
        private static readonly Color DefaultAccent = Color.FromRgb(0xF0, 0xF3, 0xF8);

        public BorderBeam()
        {
            IsHitTestVisible = false;
            _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromSeconds(TickSeconds) };
            _timer.Tick += OnTick;
            IsVisibleChanged += (s, e) => UpdateTimer();
            SizeChanged += (s, e) => Rebuild();
            // 이완 상태의 출발점 = 기본 빛. 첫 프레임부터 색이 스프링 위에 앉도록 맞춰 둔다.
            _cR = DefaultAccent.R; _cG = DefaultAccent.G; _cB = DefaultAccent.B;
            _strengthS = 1.0;
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

        /// <summary>패널 안쪽으로 번지는 빛(물들임·색 유리 틴트)의 양(0~1).
        /// 바깥 테두리 빛(림·블룸·혜성)은 그대로 두고 '안쪽만' 끈다.
        /// 안쪽 빛은 표면 크기에 비례해 커지므로(작은 알약 ≈ 0, 확장 카드 = 최대) 자식 콘텐츠의
        /// 색을 자리마다 다르게 물들인다 — 볼륨 바처럼 색으로 상태를 말하는 요소는 잠시 끄고 그린다.</summary>
        public static readonly DependencyProperty InteriorLightProperty = DependencyProperty.Register(
            nameof(InteriorLight), typeof(double), typeof(BorderBeam),
            new PropertyMetadata(1.0, (s, e) => ((BorderBeam)s).InvalidateVisual()));
        /// <summary>0이면 안쪽 물들임·틴트가 완전히 꺼진다 (테두리 빛은 유지).</summary>
        public double InteriorLight { get => (double)GetValue(InteriorLightProperty); set => SetValue(InteriorLightProperty, value); }

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

        // ── 이완 상태(스프링) ── 값이 아니라 '지금 어디쯤인가'다. 목표(Speed·Strength·Form·
        //    Tint·AccentColor)가 바뀌면 여기서 서서히 따라간다. 렌더는 이 값을 읽는다.
        private double _speedS, _speedV;         // 지금의 운동 속도(기준 둘레 기준)
        private double _strengthS, _strengthV;   // 지금의 밝기
        private double _weightS, _weightV;       // 형태의 무게 0=Line … 1=Large
        private double _tintS, _tintV;           // 색 번짐 0=Mono … 1=Album
        private double _cR, _cG, _cB;            // 지금의 빛 색
        private double _cRV, _cGV, _cBV;

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
            // 목표값으로 이완 — 상태가 바뀌어도 빛은 손짓을 갈아끼우지 않고 넘어간다.
            // 속도·밝기·형태·틴트·색이 각자 자기 무게로 목표에 앉는다. 이 이완이 곧 가속·감속이다.
            BeamSpring.Step(ref _speedS, ref _speedV, Speed, dt, SpeedOmega);
            BeamSpring.Step(ref _strengthS, ref _strengthV, Math.Clamp(Strength, 0, 1), dt, StrengthOmega);
            BeamSpring.Step(ref _weightS, ref _weightV, Form == BeamForm.Large ? 1 : 0, dt, WeightOmega);
            BeamSpring.Step(ref _tintS, ref _tintV, Tint == BeamTint.Album ? 1 : 0, dt, TintOmega);
            Color ct = AccentColor ?? DefaultAccent;
            BeamSpring.Step(ref _cR, ref _cRV, ct.R, dt, ColorOmega);
            BeamSpring.Step(ref _cG, ref _cGV, ct.G, dt, ColorOmega);
            BeamSpring.Step(ref _cB, ref _cBV, ct.B, dt, ColorOmega);

            // 헤드는 '지금의' 속도로 돈다 — 목표가 아니라 이완된 값이라 가속·감속이 손짓이 된다.
            _head += dt * _speedS * BeamSpeed.ScaleFor(_perimeter);
            _head -= Math.Floor(_head);
            UpdateTickRate();

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
            UpdateTickRate();
        }

        /// <summary>
        /// 빛이 느리면 프레임을 줄인다 — 얼마나 줄일지는 BeamSpeed가 픽셀 이동량으로 정한다.
        ///
        /// 대기(0.09 → 21.6px/s)는 프레임당 0.72px밖에 움직이지 않는다. 같은 그림을 두 번 그리는
        /// 셈이라 15fps로도 눈에 담기는 것이 없다(실측 135 → 33ms/s). 반대로 굵은 빛(Large)과
        /// 번쩍임(Flash)은 프레임 사이가 실제로 보이므로 60fps를 지킨다.
        /// </summary>
        private void UpdateTickRate()
        {
            int fps = BeamSpeed.FramesPerSecond(_speedS, _perimeter, _weightS > 0.5 || Flash >= 0.001);
            TimeSpan want = TimeSpan.FromSeconds(1.0 / fps);
            if (_timer.Interval != want) _timer.Interval = want;
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
        private double _perimeter = BeamSpeed.RefPerimeter; // 링 둘레 — 픽셀 속도를 이 값 기준으로 맞춘다

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
            // 반지름에 맞춰 호를 촘촘히(현 길이 ≈1.6px) — 성긴 현은 굵은 빛에서 각진 마디로 보인다.
            int steps = Math.Max(8, (int)Math.Ceiling(r * Math.PI / 2 / 1.6));
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
            var pen = new Pen(brush, thickness)
            {
                // 굵은 스트로크의 정점 이음매(마이터/베벨)가 곡선에서 마디(도트)로 보이던 원인 —
                // 둥근 조인으로 이어 붙인다. 양 끝은 평평하게 두어 구간끼리 맞붙게 한다.
                LineJoin = PenLineJoin.Round,
                StartLineCap = PenLineCap.Flat,
                EndLineCap = PenLineCap.Flat,
            };
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
            double fade = _fade * Math.Clamp(_strengthS, 0, 1);
            if (fade <= 0.001) return;

            // 색도 이완값 — 흰 빛에서 앨범색으로, 앨범색에서 흰 빛으로 한 프레임에 갈아끼우지 않는다.
            Color accent = Color.FromRgb(
                (byte)Math.Clamp(_cR, 0, 255),
                (byte)Math.Clamp(_cG, 0, 255),
                (byte)Math.Clamp(_cB, 0, 255));
            // 순간 강조 — 림·블룸·혜성을 한꺼번에 올려 "빛났다"는 느낌을 만든다
            double edgeGain = 1.0 + Math.Clamp(Flash, 0, 1) * 1.5;
            double litFade = fade * edgeGain;   // 밝기 계산은 전부 여기에 턴다

            // 형태 — Large는 헤드가 크고 굵게, 꼬리도 조금 길게 돈다.
            // large는 0↔1을 이완하는 무게다 — 일시정지↔재생처럼 Line↔Large를 오갈 때
            // 굵기가 '딱' 바뀌지 않고 부풀었다 가라앉는다.
            double large = _weightS;
            double formScale = 1.0 + large;

            // 크기 보정 — 확장 노치처럼 큰 표면에선 고정 px 빛이 상대적으로 티가 안 난다.
            // 빛의 굵기·블룸·헤드를 표면 크기에 비례해 키워 존재감을 유지한다.
            double lightScale = Math.Clamp(Math.Sqrt(w * h) / 65.0, 1.0, 3.2);
            double gain = Math.Sqrt(lightScale); // 굵기·헤드는 완만하게(과하면 뭉개진다)
            // 안쪽 빛 스위치 — 색으로 상태를 말하는 콘텐츠(볼륨 바 등)가 떠 있는 동안엔 끈다.
            double interior = Math.Clamp(InteriorLight, 0, 1);
            // 안쪽 물들임 — 빛이 패널 안으로 번진다. 작은 알약(lightScale 1.0)에서도
            // 약간은 번져야 혜성이 테두리 위의 선이 아니라 '공간을 채운 빛'으로 읽힌다.
            double wash = Math.Clamp((lightScale - 0.9) * 0.17, 0, 0.24) * interior;
            // 색 유리 틴트 — 앨범색이 테두리에서 안쪽으로 스며들어 공간이 곡의 색을 띤다
            double tint = _tintS * Math.Clamp((lightScale - 0.95) * 0.09, 0, 0.15) * interior;

            // 크기 보정 — 작은 알약은 둘레가 짧아 같은 비율의 꼬리도 알약을 거의 다 감싼다.
            // 그래서 작은 노치에선 꼬리를 줄이고, 큰 노치에선 굵기를 낮춰 둔다.
            double sizeFactor = Math.Clamp(lightScale, 1.0, 3.2);

            double headSigma = HeadSigma * formScale * gain;
            double tailSpan = TailSpan * (1.0 + 0.05 * large) * (0.42 + 0.14 * sizeFactor);
            double cometW = bw * (2.0 + 0.4 * large) * (1.0 + (sizeFactor - 1.0) * 0.35);
            double bloomScale = (1.0 + 0.6 * large) * (1.0 + (sizeFactor - 1.0) * 0.30);

            // 헤드 위상 — OnTick이 적분해 둔 값을 그대로 쓴다. 여기서 _t × Speed로 다시 만들면
            // 노치가 커지는 동안 _perimeter가 바뀌는 만큼 위상이 튀어 빛이 여러 바퀴 휙 돈다.
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

            // 블룸과 혜성은 같은 '밝기 구간'을 나눠 쓴다 — 한 번만 분해한다(점마다 exp()를
            // 두 번 부르고 구간 목록을 두 번 만들던 것을 한 번으로).
            BuildRuns(head, headSigma, tailSpan, litFade * breathe);

            // 2) 블룸 — 헤드 주변의 accent 광이 테두리 안팎으로 번진다
            DrawBloom(dc, accent, bloomScale);

            // 2.5) 안쪽 물들임 — 헤드 빛이 패널 안으로 퍼진다(큰 표면 전용)
            DrawWash(dc, accent, fade, breathe, wash * edgeGain);

            // 3) 혜성 헤드 + 꼬리 — 링 세그먼트별 알파
            DrawComet(dc, accent, head, cometW, headSigma, tailSpan);

            dc.Pop(); // clip
        }

        // ── 혜성 그리기: 같은 밝기 구간을 '연속 폴리라인' 하나로 합쳐 긋는다 ──
        // 세그먼트(≈1.2px)를 하나씩 DrawLine 하면, 굵은 빛이 곡선을 만날 때 인접 조각의
        // 부채꼴 이음매가 벌어져 점(도트)처럼 보인다. 밝기 버킷별로 구간을 이어 그리면
        // 곡선에서도 매끈한 한 줄기가 되고, 그리기 횟수도 수천 → 수백으로 줄어든다.
        private const int AlphaBuckets = 48;

        // ── 한 프레임에 한 번만 하는 분해 ──
        // 블룸도 혜성도 '점마다 밝기 버킷을 구해 같은 버킷끼리 이어 긋기'를 한다. 각자 하면
        // 점마다 exp()를 두 번씩 부르고 같은 구간 목록을 두 번 만든다 — 한 번 만들어 나눠 쓴다.
        private readonly List<(int Start, int End, int Bucket)> _runs = new(96);
        /// <summary>헤드에 가장 가까운 링 점 — 물들임이 퍼져 나가는 자리(매 프레임 다시 찾지 않는다).</summary>
        private int _washIndex;

        private void BuildRuns(double head, double headSigma, double tailSpan, double gain)
        {
            _runs.Clear();
            int n = _ring.Count;
            if (n < 2) return;

            int runStart = 0;
            int runBucket = CometBucket(0, head, headSigma, tailSpan, gain);
            double near = Math.Abs(SignedDelta(_ring[0].Ang, head));
            _washIndex = 0;

            for (int i = 1; i <= n; i++)
            {
                int bucket = i < n ? CometBucket(i, head, headSigma, tailSpan, gain) : -1;
                if (i < n)
                {
                    double d = Math.Abs(SignedDelta(_ring[i].Ang, head));
                    if (d < near) { near = d; _washIndex = i; }
                }
                if (bucket == runBucket) continue;
                if (runBucket >= 0) _runs.Add((runStart, i, runBucket));
                runStart = i;
                runBucket = bucket;
            }
        }

        /// <summary>세그먼트 i의 혜성 알파(fade·breathe 반영)를 버킷으로 — 너무 어두우면 -1.</summary>
        private int CometBucket(int i, double head, double headSigma, double tailSpan, double gain)
        {
            double d = SignedDelta(_ring[i].Ang, head);
            double a = CometProfile(d, headSigma, tailSpan) * gain;
            if (a <= 0.004) return -1;
            return Math.Clamp((int)(a * AlphaBuckets), 0, AlphaBuckets - 1);
        }

        /// <summary>[start, end) 세그먼트 구간을 하나의 폴리라인으로 긋는다(곡선 이음매 제거).</summary>
        private void StrokeRingRun(DrawingContext dc, int start, int end, Pen? pen)
        {
            if (pen == null || end <= start) return;
            int n = _ring.Count;
            var geo = new StreamGeometry();
            using (StreamGeometryContext ctx = geo.Open())
            {
                ctx.BeginFigure(new Point(_ring[start % n].X, _ring[start % n].Y), false, false);
                for (int i = start + 1; i <= end; i++)
                {
                    RingPt p = _ring[i % n];
                    ctx.LineTo(new Point(p.X, p.Y), true, false);
                }
            }
            geo.Freeze();
            dc.DrawGeometry(null, pen, geo);
        }

        private void DrawBloom(DrawingContext dc, Color accent, double formScale)
        {
            double[] th = { 11.5, 6.8, 4.0, 2.2 };
            double[] am = { 0.075, 0.15, 0.27, 0.48 };

            foreach ((int start, int end, int bucket) in _runs)
            {
                double a = (bucket + 0.5) / AlphaBuckets;   // fade·breathe가 반영된 밝기
                for (int k = 0; k < th.Length; k++)
                {
                    byte aa = (byte)Math.Clamp(a * am[k] * BloomGain * 255, 0, 255);
                    if (aa < 2) continue;
                    StrokeRingRun(dc, start, end, PenFor(accent, aa, th[k] * formScale));
                }
            }
        }

        private void DrawComet(DrawingContext dc, Color accent, double head, double cometW,
            double headSigma, double tailSpan)
        {
            int n = _ring.Count;
            if (n < 2) return;

            foreach ((int start, int end, int bucket) in _runs)
            {
                // 구간 중앙 세그먼트를 대표색으로 (Album이면 꼬리를 따라 색상이 돈다)
                int mid = start + (end - start) / 2;
                double d = SignedDelta(_ring[mid % n].Ang, head);
                // 앨범 색 번짐도 이완값으로 곱한다 — Mono→Album 전환이 색을 갈아끼우지 않는다.
                Color seg = d < 0
                    ? RotateHue(accent, -AlbumHueSpread * _tintS * Math.Min(1.0, -d / tailSpan))
                    : accent;

                // 헤드 코어는 흰색에 가깝게(열감), 꼬리로 갈수록 색
                double hot = Math.Exp(-(d * d) / (2 * headSigma * headSigma));
                var c = Color.FromRgb(
                    MixChannel(seg.R, 255, hot),
                    MixChannel(seg.G, 255, hot),
                    MixChannel(seg.B, 255, hot));

                double a = (bucket + 0.5) / AlphaBuckets;   // fade·breathe가 반영된 밝기
                StrokeRingRun(dc, start, end, PenFor(c, (int)(a * 255), cometW));
            }
        }

        /// <summary>헤드 근처에서 패널 안쪽으로 번지는 빛무리 — 큰 표면에서 빛이 실제로 공간을 채운다.</summary>
        private void DrawWash(DrawingContext dc, Color accent, double fade, double breathe, double wash)
        {
            if (wash <= 0.01 || _ring.Count == 0) return;

            // 헤드에 가장 가까운 링 점은 BuildRuns가 이미 찾아 두었다 — 매 프레임 다시 훑지 않는다
            RingPt best = _ring[_washIndex];

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
