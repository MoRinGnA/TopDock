using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace TopDock.Controls
{
    /// <summary>오브 상태 9종 — thinking-orbs v0.3.2(MIT, Jakub Antalik)의 엔진을 WPF로 포팅.
    /// 상태 → 모드 매핑도 원본과 동일: working=orbits, searching=globe, solving=rubik,
    /// listening=wave, connecting=web, weaving=braid, composing=ribbon, breathing=ring, shaping=morph.</summary>
    public enum OrbKind
    {
        Working,     // orbits  — 기울어진 궤도 위 파티클
        Searching,   // globe  — 점 구름을 훑는 스캔 자오선
        Solving,     // rubik  — 레이어가 돌아가며 풀리는 큐브 구
        Listening,   // wave   — 구면을 타고 흐르는 파동
        Connecting,  // web    — 이어지는 노드 네트워크
        Weaving,     // braid  — 3가닥 꼬임
        Composing,   // ribbon — 다중 밴드 리본
        Breathing,   // ring   — 정면 링의 은은한 물결
        Shaping,     // morph  — 원→삼각형→사각형 변신
    }

    public partial class ThinkingOrb : UserControl
    {
        private const double TickSeconds = 1.0 / 60.0;

        private readonly DispatcherTimer _timer;
        private double _t; // 원본과 동일한 애니메이션 시계(초). 프리셋 speed를 곱해 진행한다.

        private readonly List<Dot> _dots = new();
        private readonly List<Line> _lines = new();

        private readonly Brush[] _alphaCache = new Brush[16];
        private readonly Pen[] _penCache = new Pen[16];
        private readonly Brush?[] _dotCache = new Brush?[256];

        public ThinkingOrb()
        {
            InitializeComponent();
            _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromSeconds(TickSeconds) };
            _timer.Tick += (s, e) => { _t += TickSeconds * Speed; InvalidateVisual(); };
            IsVisibleChanged += (s, e) => { if (IsVisible) _timer.Start(); else _timer.Stop(); };
        }

        /// <summary>프리셋 speed 배수 — 원본 presets 테이블의 값.</summary>
        public double Speed { get; set; } = 1.0;

        public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
            nameof(Kind), typeof(OrbKind), typeof(ThinkingOrb),
            new PropertyMetadata(OrbKind.Working));

        public OrbKind Kind
        {
            get => (OrbKind)GetValue(KindProperty);
            set => SetValue(KindProperty, value);
        }

        public static readonly DependencyProperty InkProperty = DependencyProperty.Register(
            nameof(Ink), typeof(Color), typeof(ThinkingOrb),
            new PropertyMetadata(Color.FromRgb(0xE8, 0xE8, 0xEC), (s, e) => ((ThinkingOrb)s).OnInkChanged()));

        public Color Ink
        {
            get => (Color)GetValue(InkProperty);
            set => SetValue(InkProperty, value);
        }

        private sealed class Dot
        {
            public double X, Y, Z;   // 화면 좌표 + 깊이
            public double R;         // 반지름(px)
            public double White;     // 잉크 명도 (원본 white 0~1)
            public double A = 1;     // 알파
        }

        private sealed class Line
        {
            public double X1, Y1, X2, Y2;
            public double W;         // 선굵기
            public double White;
            public double A = 1;
        }

        // ── 원본 수학 유틸 (index-B8WsUNf5.js 그대로) ──

        private static double Frac(double x) => x - Math.Floor(x);

        private static double HashD(double a, double b)
        {
            double h = Math.Sin(a * 12.9898 + b * 78.233) * 43758.5453;
            return h - Math.Floor(h);
        }

        private static double VNoise(double x, double y)
        {
            double xi = Math.Floor(x), yi = Math.Floor(y);
            double fx = x - xi, fy = y - yi;
            fx = fx * fx * (3 - 2 * fx);
            fy = fy * fy * (3 - 2 * fy);
            double a = HashD(xi, yi), b = HashD(xi + 1, yi), c = HashD(xi, yi + 1), d = HashD(xi + 1, yi + 1);
            return a + (b - a) * fx + (c - a) * fy + (a - b - c + d) * fx * fy;
        }

        private static (double X, double Y, double Z) FibDir(int i, int n)
        {
            double golden = Math.PI * (3 - Math.Sqrt(5));
            double y = 1 - 2 * (i + 0.5) / n;
            double rad = Math.Sqrt(Math.Max(0, 1 - y * y));
            double a = i * golden;
            return (rad * Math.Cos(a), y, rad * Math.Sin(a));
        }

        private static double AngleDelta(double a, double b)
            => Math.Atan2(Math.Sin(a - b), Math.Cos(a - b));

        // 원본 makeProj: yaw/tilt 회전 후 원근 투영
        private readonly struct Proj
        {
            private readonly double _cx, _cy, _sy, _cyw, _st, _ct, _scale;
            private readonly bool _enabled;

            public Proj(double yaw, double tilt, double cx, double cy, double scale, bool enabled = true)
            {
                _cx = cx; _cy = cy;
                _sy = Math.Sin(yaw); _cyw = Math.Cos(yaw);
                _st = Math.Sin(tilt); _ct = Math.Cos(tilt);
                _scale = scale;
                _enabled = enabled;
            }

            public (double X, double Y, double Z) Apply(double x, double y, double z)
            {
                if (!_enabled) return (x * _scale + _cx, _cy - y * _scale, z * _scale);
                double x1 = x * _cyw + z * _sy;
                double z1 = -x * _sy + z * _cyw;
                double y1 = y * _ct - z1 * _st;
                double z2 = y * _st + z1 * _ct;
                return (_cx + x1 * _scale, _cy - y1 * _scale, z2);
            }
        }

        private static double RadiusScale(double size, double pow) => Math.Pow(size / 300.0, pow);

        private Brush BrushFor(double alpha01)
        {
            int idx = Math.Clamp((int)(alpha01 * 15), 0, 15);
            if (_alphaCache[idx] is Brush b) return b;
            var brush = new SolidColorBrush(Color.FromArgb((byte)(idx * 255 / 15), Ink.R, Ink.G, Ink.B));
            brush.Freeze();
            _alphaCache[idx] = brush;
            return brush;
        }

        /// <summary>점 브러시 캐시 — (명도 16 × 알파 16) 256개면 충분하다.
        /// 점 수가 프레임당 1,500개를 넘는 모드(ring/ribbon)가 있어, 브러시를 매번 새로 만들면
        /// 60fps에서 초당 10만 개를 할당해 GC 스터터가 생긴다.</summary>
        private Brush DotBrush(double white, double alpha01)
        {
            int wi = Math.Clamp((int)(white * 15), 0, 15);
            int ai = Math.Clamp((int)(alpha01 * 15), 0, 15);
            int key = (wi << 4) | ai;
            if (_dotCache[key] is Brush cached) return cached;

            // 원본 inkColor(dark): white가 낮을수록(뒤 점) 어둡다 — 밝은 잉크 톤으로 보간
            double w = wi / 15.0;
            byte chR = (byte)Math.Clamp(Ink.R * (0.35 + 0.65 * w), 0, 255);
            byte chG = (byte)Math.Clamp(Ink.G * (0.35 + 0.65 * w), 0, 255);
            byte chB = (byte)Math.Clamp(Ink.B * (0.35 + 0.65 * w), 0, 255);
            var brush = new SolidColorBrush(Color.FromArgb((byte)(ai * 255 / 15), chR, chG, chB));
            brush.Freeze();
            _dotCache[key] = brush;
            return brush;
        }

        private void OnInkChanged()
        {
            Array.Clear(_alphaCache, 0, _alphaCache.Length);
            Array.Clear(_penCache, 0, _penCache.Length);
            Array.Clear(_dotCache, 0, _dotCache.Length);
            InvalidateVisual();
        }

        private Pen PenFor(double alpha01, double thickness)
        {
            int idx = Math.Clamp((int)(alpha01 * 15), 0, 15);
            var pen = _penCache[idx];
            if (pen != null && pen.Thickness == thickness) return pen;
            pen = new Pen(BrushFor(alpha01), thickness);
            pen.Freeze();
            _penCache[idx] = pen;
            return pen;
        }

        // 원본 finalizeFrame: 투명 점 제거 + z-sort(앞 점이 나중에 그려짐)
        private void FinalizeFrame(List<Dot> dots, List<Line> lines, double rMin = 0.3)
        {
            _lines.Clear();
            foreach (Line l in lines)
                if (l.A >= 0.02) _lines.Add(l);

            _dots.Clear();
            foreach (Dot d in dots)
            {
                if (d.A < 0.02) continue;
                d.R = Math.Max(rMin, d.R);
                _dots.Add(d);
            }
            _dots.Sort((a, b) => a.Z.CompareTo(b.Z)); // z 오름차순 = 뒤에서부터
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);

            double size = Math.Min(ActualWidth, ActualHeight);
            if (size < 4) return;
            double half = size / 2;

            switch (Kind)
            {
                case OrbKind.Working: FrameOrbits(size); break;
                case OrbKind.Searching: FrameGlobe(size); break;
                case OrbKind.Solving: FrameRubik(size); break;
                case OrbKind.Listening: FrameWave(size); break;
                case OrbKind.Connecting: FrameWeb(size); break;
                case OrbKind.Weaving: FrameBraid(size); break;
                case OrbKind.Composing: FrameRibbon(size); break;
                case OrbKind.Breathing: FrameRing(size); break;
                case OrbKind.Shaping: FrameMorph(size); break;
            }

            // 원본 paintFrame: 선 먼저, 점은 z-sort 순서로
            foreach (Line l in _lines)
                dc.DrawLine(PenFor(l.A, Math.Max(0.6, l.W)),
                    new Point(l.X1, l.Y1), new Point(l.X2, l.Y2));

            foreach (Dot d in _dots)
            {
                dc.DrawEllipse(DotBrush(d.White, d.A), null, new Point(d.X, d.Y), d.R, d.R);
            }
        }

        // ── orbits (working) — 원본 frameOrbits ──
        private void FrameOrbits(double size)
        {
            double cx = size / 2, cy = size / 2;
            double R = size / 2 * 0.82;
            var pt = new Proj(_t * 0.12, 0.3, cx, cy, 1);
            double rs = RadiusScale(size, 0.6);

            var dots = new List<Dot>();
            const int orbitN = 12, ghostN = 40, particles = 3;

            for (int orb = 0; orb < orbitN; orb++)
            {
                double h1 = HashD(orb, 1.7), h2 = HashD(orb, 5.2), h3 = HashD(orb, 8.9);
                double ro = R * (0.45 + 0.52 * h1);
                double th = h1 * 2 * Math.PI;
                double phi = Math.Acos(2 * h2 - 1);
                double nx = Math.Sin(phi) * Math.Cos(th);
                double ny = Math.Cos(phi);
                double nz = Math.Sin(phi) * Math.Sin(th);
                double ux = -ny, uy = nx, uz = 0;
                double ul = Math.Max(1e-6, Math.Sqrt(ux * ux + uy * uy));
                ux /= ul; uy /= ul;
                double vx = ny * uz - nz * uy;
                double vy = nz * ux - nx * uz;
                double vz = nx * uy - ny * ux;
                double speed = (0.25 + 0.55 * h3) * (h3 > 0.5 ? 1 : -1);

                for (int k = 0; k < ghostN; k++)
                {
                    double a = k / (double)ghostN * 2 * Math.PI;
                    var (px, py, z) = pt.Apply(
                        (ux * Math.Cos(a) + vx * Math.Sin(a)) * ro,
                        (uy * Math.Cos(a) + vy * Math.Sin(a)) * ro,
                        (uz * Math.Cos(a) + vz * Math.Sin(a)) * ro);
                    double depth = (z / ro + 1) / 2;
                    dots.Add(new Dot { X = px, Y = py, Z = z, R = 0.9 * rs, White = 0.72, A = 0.5 * (0.4 + 0.6 * depth) });
                }
                for (int m = 0; m < particles; m++)
                {
                    double a = _t * speed + m / (double)particles * 2 * Math.PI + h2 * 6;
                    var (px, py, z) = pt.Apply(
                        (ux * Math.Cos(a) + vx * Math.Sin(a)) * ro,
                        (uy * Math.Cos(a) + vy * Math.Sin(a)) * ro,
                        (uz * Math.Cos(a) + vz * Math.Sin(a)) * ro);
                    double depth = (z / ro + 1) / 2;
                    dots.Add(new Dot { X = px, Y = py, Z = z, R = (1.2 + 1.6 * depth) * rs, White = 0.3 - 0.22 * depth });
                }
            }
            FinalizeFrame(dots, new List<Line>());
        }

        // ── globe (searching) — 원본 frameGlobe ──
        private void FrameGlobe(double size)
        {
            const double spin = 0.5;
            double cx = size / 2, cy = size / 2;
            double radius = size / 2 * 0.82;
            double tilt = 0.4 + 0.06 * Math.Sin(_t * 0.35);
            var pt = new Proj(_t * spin, tilt, cx, cy, radius);
            double scan = _t * (spin + (1.7 - 0) * 4.08); // scanMul 4.08
            double rs = RadiusScale(size, 0.6);

            var dots = new List<Dot>();
            const int latRings = 17, lonDensity = 44; // count 프리셋 64: 0.42 → 17*0.648≈11? 원본 scaleCounts 후 값 유지 위해 원본 프로파일 유지
            const double dimBase = 0.45;

            for (int li = 0; li <= latRings; li++)
            {
                double lat = -Math.PI / 2 + li / (double)latRings * Math.PI;
                double cosLat = Math.Cos(lat), sinLat = Math.Sin(lat);
                int lonCount = Math.Max(1, (int)Math.Round(Math.Abs(cosLat) * lonDensity));
                for (int lj = 0; lj < lonCount; lj++)
                {
                    double lon = lj / (double)lonCount * 2 * Math.PI;
                    var (px, py, z) = pt.Apply(cosLat * Math.Cos(lon), sinLat, cosLat * Math.Sin(lon));
                    double depth = (z + 1) / 2;
                    double d = AngleDelta(lon + _t * spin, scan);
                    double boost = Math.Exp(-(d * d) / 0.18) * Math.Max(0, z);
                    dots.Add(new Dot
                    {
                        X = px, Y = py, Z = z,
                        R = (0.6 + 1.7 * depth + 1.0 * boost) * rs,
                        White = 0.62 - 0.54 * depth,
                        A = dimBase + (1 - dimBase) * Math.Min(1, boost),
                    });
                }
            }
            FinalizeFrame(dots, new List<Line>());
        }

        // ── rubik (solving) — 원본 frameRubik ──
        private readonly struct Move
        {
            public readonly int Axis;
            public readonly double Lo, Hi, Ang;
            public Move(int axis, double lo, double hi, double ang) { Axis = axis; Lo = lo; Hi = hi; Ang = ang; }
        }

        private static Move[] MakeMoves(int count)
        {
            var moves = new Move[count];
            for (int i = 0; i < count; i++)
            {
                int axis = Math.Min(2, (int)(HashD(i, 2.3) * 3));
                double lo = -1 + 0.5 * Math.Min(3, Math.Floor(HashD(i, 5.9) * 4));
                double dir = HashD(i, 7.7) < 0.5 ? 1 : -1;
                moves[i] = new Move(axis, lo, lo + 0.5, dir * Math.PI / 2);
            }
            return moves;
        }

        private (double[] Amount, int Active) SolveCycle(double time, int count, double slotDur, double rest)
        {
            double cyc = 2 * count * slotDur + rest;
            double tc = time % cyc;
            var amount = new double[count];
            int active = -1;
            if (tc < 2 * count * slotDur)
            {
                int slot = (int)(tc / slotDur);
                double p = (tc - slot * slotDur) / slotDur;
                double cl = Math.Min(1, p / 0.7);
                double ep = 1 - Math.Pow(1 - cl, 3);
                if (slot < count)
                {
                    for (int i = 0; i < slot; i++) amount[i] = 1;
                    amount[slot] = ep;
                    active = slot;
                }
                else
                {
                    int u = 2 * count - 1 - slot;
                    for (int i = 0; i < u; i++) amount[i] = 1;
                    amount[u] = 1 - ep;
                    active = u;
                }
            }
            return (amount, active);
        }

        private void FrameRubik(double size)
        {
            double cx = size / 2, cy = size / 2;
            double R = size / 2 * 0.82;
            var pt = new Proj(_t * 0.55, 0.35 + 0.1 * Math.Sin(_t * 0.9), cx, cy, R);
            double rs = RadiusScale(size, 0.6);

            const int moveCount = 14;
            var moves = MakeMoves(moveCount);
            var (amount, active) = SolveCycle(_t, moveCount, 0.42, 1.2);

            var dots = new List<Dot>();
            const int latRings = 15, lonDensity = 40;

            for (int li = 0; li <= latRings; li++)
            {
                double lat = -Math.PI / 2 + li / (double)latRings * Math.PI;
                double cosLat = Math.Cos(lat), sinLat = Math.Sin(lat);
                int lonCount = Math.Max(1, (int)Math.Round(Math.Abs(cosLat) * lonDensity));
                for (int lj = 0; lj < lonCount; lj++)
                {
                    double lon = lj / (double)lonCount * 2 * Math.PI;
                    double x = cosLat * Math.Cos(lon), y = sinLat, z = cosLat * Math.Sin(lon);
                    bool inActive = false;
                    for (int i = 0; i < moves.Length; i++)
                    {
                        if (amount[i] <= 0) continue;
                        var mv = moves[i];
                        double coord = mv.Axis == 0 ? x : mv.Axis == 1 ? y : z;
                        if (coord < mv.Lo || coord >= mv.Hi) continue;
                        if (i == active) inActive = true;
                        double a = mv.Ang * amount[i];
                        double ca = Math.Cos(a), sa = Math.Sin(a);
                        if (mv.Axis == 0)
                        {
                            double y2 = y * ca - z * sa; z = y * sa + z * ca; y = y2;
                        }
                        else if (mv.Axis == 1)
                        {
                            double x2 = x * ca + z * sa; z = -x * sa + z * ca; x = x2;
                        }
                        else
                        {
                            double x2 = x * ca - y * sa; y = x * sa + y * ca; x = x2;
                        }
                    }
                    var (px, py, zr) = pt.Apply(x, y, z);
                    double depth = (zr + 1) / 2;
                    dots.Add(new Dot
                    {
                        X = px, Y = py, Z = zr,
                        R = (0.6 + 1.7 * depth + (inActive ? 0.3 : 0)) * rs,
                        White = 0.62 - 0.54 * depth - (inActive ? 0.14 : 0),
                    });
                }
            }
            FinalizeFrame(dots, new List<Line>());
        }

        // ── wave (listening) — 원본 frameWave ──
        private void FrameWave(double size)
        {
            double cx = size / 2, cy = size / 2;
            double R = size / 2 * 0.874;
            var pt = new Proj(_t * 0.18, 0.38, cx, cy, 1);
            double rs = RadiusScale(size, 0.6);

            var dots = new List<Dot>();
            const int rings = 15, lonDensity = 40;

            for (int ri = 0; ri <= rings; ri++)
            {
                double lat = -Math.PI / 2 + ri / (double)rings * Math.PI;
                double cosLat = Math.Cos(lat), sinLat = Math.Sin(lat);
                double w = 0.62 * Math.Sin(_t * 2.1 - ri * 0.52) + 0.38 * Math.Sin(_t * 1.27 + ri * 0.83);
                double rr = R * (0.88 + 0.105 * w);
                int lonCount = Math.Max(1, (int)Math.Round(Math.Abs(cosLat) * lonDensity));
                for (int lj = 0; lj < lonCount; lj++)
                {
                    double lon = lj / (double)lonCount * 2 * Math.PI;
                    var (px, py, z) = pt.Apply(cosLat * Math.Cos(lon) * rr, sinLat * rr, cosLat * Math.Sin(lon) * rr);
                    double depth = (z / R + 1) / 2;
                    double crest = Math.Max(0, w);
                    dots.Add(new Dot
                    {
                        X = px, Y = py, Z = z,
                        R = (0.6 + 1.7 * depth) * (1 + 0.4 * crest) * rs,
                        White = 0.66 - 0.56 * depth - 0.1 * crest,
                    });
                }
            }
            FinalizeFrame(dots, new List<Line>());
        }

        // ── web (connecting) — 원본 frameWeb ──
        private void FrameWeb(double size)
        {
            double cx = size / 2, cy = size / 2;
            double R = size / 2 * 0.8;
            var pt = new Proj(_t * 0.12, 0.32, cx, cy, R);
            double rs = RadiusScale(size, 0.6);

            const int nodeN = 30;
            const double thr = 0.72;
            var nodes = new List<(double X, double Y, double Z)>();
            for (int i = 0; i < nodeN; i++)
            {
                var d = FibDir(i, nodeN);
                double x = d.X + 0.3 * (VNoise(i * 0.31 + 9, _t * 0.24) - 0.5) * 2;
                double y = d.Y + 0.3 * (VNoise(i * 0.53 + 27, _t * 0.21) - 0.5) * 2;
                double z = d.Z + 0.3 * (VNoise(i * 0.77 + 55, _t * 0.27) - 0.5) * 2;
                double l = Math.Sqrt(x * x + y * y + z * z);
                nodes.Add((x / l, y / l, z / l));
            }

            var lines = new List<Line>();
            var dots = new List<Dot>();
            for (int i = 0; i < nodeN; i++)
            {
                for (int j = i + 1; j < nodeN; j++)
                {
                    double dx = nodes[i].X - nodes[j].X, dy = nodes[i].Y - nodes[j].Y, dz = nodes[i].Z - nodes[j].Z;
                    double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    if (dist >= thr) continue;
                    var p1 = pt.Apply(nodes[i].X, nodes[i].Y, nodes[i].Z);
                    var p2 = pt.Apply(nodes[j].X, nodes[j].Y, nodes[j].Z);
                    double depth = ((p1.Z + p2.Z) / 2 + 1) / 2;
                    lines.Add(new Line
                    {
                        X1 = p1.X, Y1 = p1.Y, X2 = p2.X, Y2 = p2.Y,
                        White = 0.42,
                        A = (1 - dist / thr) * (0.3 + 0.55 * depth),
                        W = Math.Max(0.6, 0.8 * rs),
                    });
                }
            }
            for (int i = 0; i < nodeN; i++)
            {
                var (px, py, z) = pt.Apply(nodes[i].X, nodes[i].Y, nodes[i].Z);
                double depth = (z + 1) / 2;
                double pulse = 1 + 0.25 * Math.Sin(_t * 1.4 + i * 2.7);
                dots.Add(new Dot { X = px, Y = py, Z = z, R = (1.4 + 1.8 * depth) * pulse * rs, White = 0.55 - 0.45 * depth });
            }
            const int signals = 5;
            for (int s = 0; s < signals; s++)
            {
                int seg = (int)(_t * 0.55 + s * 7.31);
                int a = (int)(HashD(seg, s * 3.1 + 1.7) * nodeN);
                int b = (int)(HashD(seg, s * 5.7 + 4.2) * nodeN);
                if (a == b) continue;
                double f = Frac(_t * 0.55 + s * 7.31);
                double x = nodes[a].X + (nodes[b].X - nodes[a].X) * f;
                double y = nodes[a].Y + (nodes[b].Y - nodes[a].Y) * f;
                double z = nodes[a].Z + (nodes[b].Z - nodes[a].Z) * f;
                double l = Math.Max(1e-6, Math.Sqrt(x * x + y * y + z * z));
                var (px, py, zr) = pt.Apply(x / l, y / l, z / l);
                double depth = (zr + 1) / 2;
                dots.Add(new Dot { X = px, Y = py, Z = zr, R = (1.4 * 1.5 + 1.8 * depth) * rs, White = 0.05, A = 0.5 + 0.5 * depth });
            }
            FinalizeFrame(dots, lines);
        }

        // ── braid (weaving) — 원본 frameBraid ──
        private void FrameBraid(double size)
        {
            double cx = size / 2, cy = size / 2;
            double R = size / 2 * 0.76;
            var pt = new Proj(_t * 0.4, 0.3, cx, cy, 1);
            double rs = RadiusScale(size, 0.6);

            var dots = new List<Dot>();
            const int ghostN = 150;
            for (int i = 0; i < ghostN; i++)
            {
                var d = FibDir(i, ghostN);
                var (px, py, z) = pt.Apply(d.X * R, d.Y * R, d.Z * R);
                double depth = (z / R + 1) / 2;
                dots.Add(new Dot { X = px, Y = py, Z = z, R = 0.8 * rs, White = 0.78, A = 0.1 + 0.22 * depth });
            }

            const int strandN = 52;
            const int turns = 3;
            for (int s = 0; s < 3; s++)
            {
                double phase = s / 3.0 * 2 * Math.PI;
                for (int i = 0; i < strandN; i++)
                {
                    double u = (Frac(i / (double)strandN + _t * 0.045) * 2 - 1) * 0.96;
                    double surf = Math.Sqrt(Math.Max(0, 1 - u * u));
                    double endFade = Math.Min(1, (1 - Math.Abs(u)) / 0.1);
                    double a = u * Math.PI * turns + phase;
                    double weave = 1 + 0.075 * Math.Sin(u * Math.PI * turns * 2 + phase * 2 + _t * 0.8);
                    double rr = surf * R * weave;
                    var (px, py, zr) = pt.Apply(Math.Cos(a) * rr, u * R * weave, Math.Sin(a) * rr);
                    double depth = (zr / R + 1) / 2;
                    dots.Add(new Dot
                    {
                        X = px, Y = py, Z = zr,
                        R = (1.2 + 1.8 * depth) * rs,
                        White = 0.55 - 0.45 * depth,
                        A = endFade * (0.45 + 0.55 * depth),
                    });
                }
            }
            FinalizeFrame(dots, new List<Line>());
        }

        // ── ribbon (composing) / ring (breathing) — 원본 frameRibbon ──
        private void FrameRibbon(double size, bool faceOn = false, double bandMul = 3.9, double wobMul = 1.0)
        {
            double cx = size / 2, cy = size / 2;
            double R = size / 2 * 0.78;
            double camTilt = 0.3;
            var pt = new Proj(_t * 0.1, camTilt, cx, cy, 1);
            double rs = RadiusScale(size, 0.6);

            var dots = new List<Dot>();
            // ring(정면) 프리셋은 유령 구체 없이 밴드만 그린다 — 원본의 깨끗한 정면 링.
            int ghostN = faceOn ? 0 : 150;
            for (int i = 0; i < ghostN; i++)
            {
                var d = FibDir(i, ghostN);
                var (px, py, z) = pt.Apply(d.X * R, d.Y * R, d.Z * R);
                double depth = (z / R + 1) / 2;
                dots.Add(new Dot { X = px, Y = py, Z = z, R = 0.8 * rs, White = 0.78, A = 0.1 + 0.22 * depth });
            }

            double ya = _t * 0.24;
            double ta = faceOn ? -camTilt : 0.55 + 0.3 * Math.Sin(_t * 0.18);
            double ux = Math.Cos(ya), uy = 0, uz = Math.Sin(ya);
            double vx = -uz * Math.Sin(ta), vy = Math.Cos(ta), vz = ux * Math.Sin(ta);
            double nx = uy * vz - uz * vy;
            double ny = uz * vx - ux * vz;
            double nz2 = ux * vy - uy * vx;
            double wobAmp = 0.23 * wobMul;
            double baseR = faceOn ? R / (1 + 0.85 * wobAmp) : R;
            const int lanes = 5, segs = 88;
            int realLanes = Math.Max(1, (int)Math.Round(lanes * bandMul));

            for (int w = 0; w < realLanes; w++)
            {
                double laneOff = (w - (realLanes - 1) / 2.0) * 0.075;
                double edge = Math.Abs(w - (realLanes - 1) / 2.0) / Math.Max(1, (realLanes - 1) / 2.0);
                for (int k = 0; k < segs; k++)
                {
                    double a = k / (double)segs * 2 * Math.PI;
                    double wob = (0.16 * Math.Sin(a * 3 - _t * 1.7 + w * 0.22) + 0.07 * Math.Sin(a * 5 + _t * 1.1)) * wobMul;
                    double radial = faceOn ? 1 + wob : 1;
                    double off = faceOn ? laneOff : laneOff + wob;
                    double x = ux * Math.Cos(a) + vx * Math.Sin(a) + nx * off;
                    double y = uy * Math.Cos(a) + vy * Math.Sin(a) + ny * off;
                    double z = uz * Math.Cos(a) + vz * Math.Sin(a) + nz2 * off;
                    double l = Math.Sqrt(x * x + y * y + z * z);
                    double rr = baseR * radial;
                    var (px, py, zr) = pt.Apply(x / l * rr, y / l * rr, z / l * rr);
                    double depth = (zr / R + 1) / 2;
                    dots.Add(new Dot
                    {
                        X = px, Y = py, Z = zr,
                        R = (1.1 + 1.7 * depth) * (1 - 0.25 * edge) * rs,
                        White = 0.52 - 0.44 * depth + 0.18 * edge,
                        A = 0.4 + 0.6 * depth,
                    });
                }
            }
            FinalizeFrame(dots, new List<Line>());
        }

        private void FrameRibbon(double size) => FrameRibbon(size, faceOn: false, bandMul: 3.9, wobMul: 1.0);
        private void FrameRing(double size) => FrameRibbon(size, faceOn: true, bandMul: 3.627, wobMul: 0.368);

        // ── morph (shaping) — 원본 frameMorph: 원→삼각형→사각형 (1.4초 홀드 + 0.9초 모프) ──
        private const double HoldSec = 1.4, MorphSec = 0.9;

        private static (double X, double Y) PathCircle(double f)
        {
            double a = -Math.PI / 2 + f * 2 * Math.PI;
            return (Math.Cos(a) * 0.24, Math.Sin(a) * 0.24);
        }

        private static Func<double, (double, double)> PolyPath((double, double)[] verts)
        {
            int V = verts.Length;
            var L = new double[V];
            double total = 0;
            for (int i = 0; i < V; i++)
            {
                var a = verts[i]; var b = verts[(i + 1) % V];
                L[i] = Math.Sqrt(Math.Pow(b.Item1 - a.Item1, 2) + Math.Pow(b.Item2 - a.Item2, 2));
                total += L[i];
            }
            return f =>
            {
                double target = f * total;
                int i = 0;
                while (target > L[i] && i < V - 1) { target -= L[i]; i++; }
                double ff = L[i] > 0 ? Math.Min(1, target / L[i]) : 0;
                return (verts[i].Item1 + (verts[(i + 1) % V].Item1 - verts[i].Item1) * ff,
                        verts[i].Item2 + (verts[(i + 1) % V].Item2 - verts[i].Item2) * ff);
            };
        }

        private static readonly Func<double, (double, double)> ShapeTriangle = PolyPath(new[]
        {
            (0.0, -0.26), (0.24, 0.16), (-0.24, 0.16),
        });
        private static readonly Func<double, (double, double)> ShapeSquare = PolyPath(new[]
        {
            (0.0, -0.2), (0.2, -0.2), (0.2, 0.2), (-0.2, 0.2), (-0.2, -0.2),
        });

        private static double SmoothE(double x)
        {
            x = Math.Clamp(x, 0, 1);
            return x * x * (3 - 2 * x);
        }

        private void FrameMorph(double size)
        {
            int K = 3;
            double seg = HoldSec + MorphSec;
            double tc = _t % (seg * K);
            int k = (int)(tc / seg);
            double local = tc - k * seg;
            double m = local > HoldSec ? SmoothE((local - HoldSec) / MorphSec) : 0;

            Func<double, (double, double)> pA = k == 0 ? PathCircle : k == 1 ? ShapeTriangle : ShapeSquare;
            int pBIdx = (k + 1) % K;
            Func<double, (double, double)> pB = pBIdx switch { 0 => PathCircle, 1 => ShapeTriangle, _ => ShapeSquare };

            const int M = 160;
            var pts = new (double X, double Y)[M];
            for (int i = 0; i < M; i++)
            {
                double f = i / (double)M;
                var a = pA(f);
                var b = pB(f);
                pts[i] = (a.Item1 + (b.Item1 - a.Item1) * m, a.Item2 + (b.Item2 - a.Item2) * m);
            }

            var L = new double[M];
            double total = 0;
            for (int i = 0; i < M; i++)
            {
                var a = pts[i]; var b = pts[(i + 1) % M];
                L[i] = Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2));
                total += L[i];
            }

            int n = 34;
            double re = 0.021 * 1.35;
            double pulse = 1 + 0.02 * Math.Sin(local * 3.1);
            double c2 = size / 2;

            var dots = new List<Dot>();
            int segIdx = 0;
            double acc = 0;
            for (int k2 = 0; k2 < n; k2++)
            {
                double target = k2 / (double)n * total;
                while (acc + L[segIdx] < target && segIdx < M - 1)
                {
                    acc += L[segIdx];
                    segIdx++;
                }
                var a = pts[segIdx];
                var b = pts[(segIdx + 1) % M];
                double f = L[segIdx] > 0 ? Math.Min(1, (target - acc) / L[segIdx]) : 0;
                double x = (a.X + (b.X - a.X) * f) * pulse;
                double y = (a.Y + (b.Y - a.Y) * f) * pulse;
                dots.Add(new Dot
                {
                    X = c2 + x * size,
                    Y = c2 + y * size,
                    Z = 0,
                    R = Math.Max(0.35, re * size),
                    White = 0.1,
                });
            }
            FinalizeFrame(dots, new List<Line>());
        }
    }
}
