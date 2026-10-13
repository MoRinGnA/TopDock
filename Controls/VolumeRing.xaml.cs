using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace TopDock.Controls
{
    /// <summary>
    /// 볼륨 계기 — 바 대신 우측 상태 점 자리를 감싸는 원형 아크다. 그 안에는 지금 나는 소리의
    /// 세 칸(저·중·고)이 산다.
    ///
    /// 노치의 "살아있는 가장자리(BorderBeam)"와 같은 언어를 약하게 빌린다:
    /// 꼬리(시작)는 흐리고 헤드(끝)로 갈수록 밝아지는 그라데이션, 뒤로 번지는 은은한 블룸,
    /// 끝점에 맺히는 작은 빛. 그래서 텅 빈 회색 고리가 아니라 '빛이 차오르는' 링으로 읽힌다.
    ///
    /// 호는 볼륨을, 안쪽 바늘은 소리를 말한다 — 두 계기가 한 자리에 겹쳐 있어도 서로 다른 것을
    /// 가리키므로 섞이지 않는다: 호는 흰색(내가 정한 값), 바늘은 곡의 색(지금 나는 소리).
    ///
    /// 진행바 자리를 빌리지 않으므로 재생 위치가 계속 보이고, 접힌 노치든 펼친 카드든
    /// 같은 자리·같은 모양이라 화면이 바뀌어도 눈이 옮겨 다니지 않는다.
    /// 음소거는 아이콘이 아니라 채움색(시스템 그레이)으로만 말한다.
    /// </summary>
    public class VolumeRing : FrameworkElement
    {
        private const double Thickness = Tokens.RingThickness;
        private const double Diameter = Tokens.RingDiameter;
        private static readonly Duration Glide = new(TimeSpan.FromMilliseconds(140));

        private static readonly Color FillColor = Color.FromRgb(0xFF, 0xFF, 0xFF);
        private static readonly Color MutedColor = Color.FromRgb(0x8E, 0x8E, 0x93);
        private static readonly Color TrackColor = Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF);
        private static readonly Color BarIdleColor = Color.FromRgb(0xF0, 0xF3, 0xF8);

        private readonly float[] _bars = new float[Tokens.RingBarCount];

        /// <summary>호가 채워지는 비율(0~100). 애니메이션이 이 값을 미끄러뜨리면 아크가 따라 그려진다.</summary>
        public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
            nameof(Level), typeof(double), typeof(VolumeRing),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public double Level { get => (double)GetValue(LevelProperty); set => SetValue(LevelProperty, value); }

        /// <summary>음소거 — 채움색만 시스템 그레이로 바꾼다(아이콘은 쓰지 않는다).</summary>
        public static readonly DependencyProperty IsMutedProperty = DependencyProperty.Register(
            nameof(IsMuted), typeof(bool), typeof(VolumeRing),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

        public bool IsMuted { get => (bool)GetValue(IsMutedProperty); set => SetValue(IsMutedProperty, value); }

        /// <summary>바늘 색 — 재생 중인 곡의 색(테두리 빛과 같은 색)을 창이 끼워 준다.</summary>
        public static readonly DependencyProperty BarsBrushProperty = DependencyProperty.Register(
            nameof(BarsBrush), typeof(Brush), typeof(VolumeRing),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public Brush? BarsBrush { get => (Brush?)GetValue(BarsBrushProperty); set => SetValue(BarsBrushProperty, value); }

        /// <summary>
        /// 바늘을 보일지. 창이 정한다 — 상태 점(배터리·AI·복사)이 자리를 쓰면 계기는 비켜선다.
        /// 호는 그대로 남는다: 자리를 양보하는 것은 바늘뿐이다.
        /// </summary>
        public static readonly DependencyProperty BarsVisibleProperty = DependencyProperty.Register(
            nameof(BarsVisible), typeof(bool), typeof(VolumeRing),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

        public bool BarsVisible { get => (bool)GetValue(BarsVisibleProperty); set => SetValue(BarsVisibleProperty, value); }

        public VolumeRing()
        {
            Width = Diameter;
            Height = Diameter;
            IsHitTestVisible = false;
        }

        /// <summary>레벨과 음소거 여부를 표시한다. 값은 짧게 미끄러지고, 음소거는 색으로만 말한다.</summary>
        public void Show(int volume, bool isMuted)
        {
            IsMuted = isMuted;

            double clamped = Math.Clamp(volume, 0, 100);
            if (Math.Abs(Level - clamped) > 0.01)
            {
                double from = Level;
                // 애니메이션을 걷어내고 기준값을 먼저 옮긴다 — 끝난 뒤 값이 되돌아 튀지 않게
                BeginAnimation(LevelProperty, null);
                Level = clamped;

                var glide = new DoubleAnimation(from, clamped, Glide)
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                };
                Timeline.SetDesiredFrameRate(glide, Tokens.FrameRate);
                BeginAnimation(LevelProperty, glide);
            }
        }

        /// <summary>세 칸의 높이(0~1)를 받는다. 다듬는 일(어택·릴리즈)은 이미 캡처 쪽에서 끝났다.</summary>
        public void SetBars(float low, float mid, float high)
        {
            // 바늘이 자리를 비켰으면(상태 점이 쓰는 중) 값을 받을 이유도 다시 그릴 이유도 없다.
            // 예전에는 이 무효화 때문에 재생 내내 30fps로 호 전체를 다시 그렸다(렌더 1회 108KB·0.39ms).
            if (!BarsVisible) return;

            _bars[0] = low;
            _bars[1] = mid;
            _bars[2] = high;
            InvalidateVisual();
        }

        /// <summary>
        /// 세 칸을 바닥으로 내린다 — SetBars와 달리 '보이는 중'인지 묻지 않는다.
        ///
        /// 재생이 멈추면 바늘을 바닥에 놓아야 한다. 이때 창은 먼저 BarsVisible을 내리므로,
        /// SetBars(0,0,0)로는 아무 일도 일어나지 않았다(조기 반환) — 값이 마지막 소리에 멈춘 채
        /// 남아, 다음에 무엇이 바늘을 되살릴 때(캡처가 아직 준비되지 않은 첫 틱 등) 낡은 값이
        /// 한 프레임 보일 수 있었다.
        /// </summary>
        public void ResetBars()
        {
            _bars[0] = 0;
            _bars[1] = 0;
            _bars[2] = 0;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = RenderSize.Width, h = RenderSize.Height;
            if (w < 6 || h < 6) return;

            double cx = w / 2, cy = h / 2;
            double radius = Math.Min(w, h) / 2 - Thickness / 2 - 2.0; // 블룸·글로우가 새지 않을 여유
            if (radius < 2) return;

            Color accent = IsMuted ? MutedColor : FillColor;
            double pct = Math.Clamp(Level, 0, 100) / 100.0;

            // 트랙 — 볼륨이 0이어도 고리의 나머지가 보이게 어둑하게 전체를 긋는다
            dc.DrawEllipse(null, PenFor(TrackColor, Thickness, false), new Point(cx, cy), radius, radius);

            if (pct > 0.0005)
            {
                DrawArc(dc, cx, cy, radius, accent, pct);
            }

            // 바늘은 호보다 나중에 그린다 — 호가 바늘을 가로지르면 계기가 아니라 장식이 된다
            if (BarsVisible) DrawBars(dc, cx, cy);
        }

        /// <summary>
        /// 호 한 층(블룸 또는 코어)을 긋는다.
        ///
        /// 세그먼트(≈2.4°) 하나마다 DrawLine + 새 Pen을 쓰면 두 가지를 잃는다:
        ///   · 이음매 — 굵은 획에 둥근 캡이 겹쳐 곡선이 도트처럼 보인다(BorderBeam이 같은 이유로
        ///     구간 폴리라인을 쓴다)
        ///   · 할당 — 실측(2026-10, %TEMP%\ringprobe): 24px 링이 프레임당 108KB를 할당하고
        ///     30fps에서 3.24MB/s를 쓰레기로 만들었다. 그 비용은 전부 이 루프였다.
        /// 그래서 밝기 버킷이 같은 연속 구간을 모아 하나의 폴리라인으로 긋는다 — 펜은 캐시에서
        /// 꺼내 쓰므로 프레임 사이에 새로 만드는 펜이 0개가 된다.
        ///
        /// 한 가지 눈에 보이는 차이가 따라온다: 예전에는 세그먼트(0.4px 간격)마다 2px 획이 겹쳐
        /// 그려져 알파가 여러 번 합성됐다 — 코드가 말하는 꼬리 알파 0.28이 실제로는 0.83으로 칠해졌다.
        /// 이제는 한 번만 칠하니 꼬리가 옅어졌다(실측: 꼬리 213→183/255, 머리는 255로 그대로).
        /// </summary>
        /// <remarks>
        /// 이 차이가 싫으면 세그먼트 단위로 긋는 예전 방식으로 되돌리면 된다 — 다만 그때는
        /// 프레임당 펜 300개를 새로 만들던 할당도 함께 돌아온다(실측 110KB/프레임).
        /// </remarks>
        private void DrawArcLayer(DrawingContext dc, double cx, double cy, double radius, double startDeg,
            double sweep, int segments, double[] alpha, Color accent, bool bloom)
        {
            int runStart = 0;
            int runBucket = ArcBucket(alpha, 0, bloom);

            for (int i = 1; i <= segments; i++)
            {
                // 마지막 점은 무조건 구간을 끊는다 — 남은 구간을 루프 밖에서 다시 긋지 않기 위해서다
                int bucket = i < segments ? ArcBucket(alpha, i, bloom) : int.MinValue;
                if (bucket == runBucket) continue;

                StrokeArcRun(dc, cx, cy, radius, startDeg, sweep, segments, alpha, runStart, i, accent, bloom);
                runStart = i;
                runBucket = bucket;
            }
        }

        /// <summary>
        /// 구간 버킷 — 눈에 안 보이는 폭으로 밝기를 묶어 구간 수를 고정한다.
        /// 블룸은 밝기 폭 자체가 1바이트라 값마다 따로 묶고(사실상 손실 없음), 코어는 8단계마다 묶는다
        /// (알파 3% 차이 — 어두운 배경 위 2px 획에서는 띠로 보이지 않는다).
        /// </summary>
        private static int ArcBucket(double[] alpha, int index, bool bloom)
        {
            int byteAlpha = (int)Math.Clamp(alpha[index] * (bloom ? BloomAlphaFactor : 1.0) * 255, 0, 255);
            if (byteAlpha < 2) return -1;                       // 이만큼 어두우면 그리지 않는다(예전과 같다)
            return byteAlpha >> (bloom ? 0 : 3);
        }

        /// <summary>구간 [start, end]를 폴리라인 하나로 — 대표값은 구간 가운데 세그먼트의 값이다.</summary>
        private void StrokeArcRun(DrawingContext dc, double cx, double cy, double radius, double startDeg,
            double sweep, int segments, double[] alpha, int start, int end, Color accent, bool bloom)
        {
            if (end <= start) return;

            int mid = start + ((end - start) / 2);
            double tMid = (double)mid / segments;
            int byteAlpha = (int)Math.Clamp(alpha[mid] * (bloom ? BloomAlphaFactor : 1.0) * 255, 0, 255);
            if (byteAlpha < 2) return;

            // 블룸은 헤드 쪽이 조금 더 넓게 번진다(약하게: 본체의 약 8%)
            double width = bloom ? Thickness * (1.5 + 0.4 * tMid) : Thickness;

            var geo = new StreamGeometry();
            using (StreamGeometryContext ctx = geo.Open())
            {
                ctx.BeginFigure(PointOn(cx, cy, radius, startDeg + sweep * start / segments), false, false);
                for (int i = start + 1; i <= end; i++)
                {
                    ctx.LineTo(PointOn(cx, cy, radius, startDeg + sweep * i / segments), true, false);
                }
            }
            geo.Freeze();
            dc.DrawGeometry(null, PenFor(WithAlpha(accent, (byte)byteAlpha), width, true), geo);
        }

        private void DrawArc(DrawingContext dc, double cx, double cy, double radius, Color accent, double pct)
        {
            // 완전한 원(360°)은 시작=끝이라 이어 그리면 안 된다 — 아주 살짝 남긴다.
            double sweep = Math.Min(359.0, 360.0 * pct);
            int segments = Math.Max(12, (int)Math.Ceiling(sweep / 2.4)); // ≈2.4° 간격
            const double startDeg = -90.0; // 12시

            // 라이팅 — 꼬리에서 헤드로 차오르는 밝기. 두 층(블룸·코어)이 같은 값을 나눠 쓴다.
            if (_arcAlpha.Length < segments + 1) _arcAlpha = new double[segments + 1];
            double[] alpha = _arcAlpha;
            for (int i = 0; i <= segments; i++)
            {
                double t = (double)i / segments;                 // 0=꼬리, 1=헤드
                double a = 0.28 + 0.72 * Math.Pow(t, 0.85);
                double head = Math.Exp(-Math.Pow((1.0 - t) / 0.12, 2)); // 헤드 코어
                alpha[i] = Math.Min(1.0, a + head * 0.35);
            }

            DrawArcLayer(dc, cx, cy, radius, startDeg, sweep, segments, alpha, accent, bloom: true);
            DrawArcLayer(dc, cx, cy, radius, startDeg, sweep, segments, alpha, accent, bloom: false);

            // 헤드 글로우 — 끝점에 맺히는 작은 빛
            Point tip = PointOn(cx, cy, radius, startDeg + sweep);
            double glowRadius = Thickness * 1.0;
            dc.DrawEllipse(GlowBrush(accent), null, tip, glowRadius, glowRadius);
        }

        /// <summary>
        /// 링 안의 세 칸. 바닥을 공통으로 두고 위로 자란다 — 이퀄라이저의 문법이다.
        /// 조용해도 바닥 높이만큼은 남겨 '꺼진 계기'가 아니라 '쉬는 계기'로 보이게 한다.
        /// </summary>
        private void DrawBars(DrawingContext dc, double cx, double cy)
        {
            double barWidth = Tokens.RingBarWidth;
            double totalWidth = barWidth * Tokens.RingBarCount + Tokens.RingBarGap * (Tokens.RingBarCount - 1);
            double left = cx - totalWidth / 2;
            double bottom = cy + Tokens.RingBarMaxHeight / 2;

            Color color = IsMuted ? MutedColor : BarsBrush is SolidColorBrush solid ? solid.Color : BarIdleColor;
            Brush brush = FrozenBrush(color);

            for (int i = 0; i < _bars.Length; i++)
            {
                double level = IsMuted ? 0.0 : Math.Clamp(_bars[i], 0f, 1f);
                double height = Tokens.RingBarMinHeight + level * (Tokens.RingBarMaxHeight - Tokens.RingBarMinHeight);
                double x = left + i * (barWidth + Tokens.RingBarGap);

                dc.DrawRoundedRectangle(brush, null,
                    new Rect(x, bottom - height, barWidth, height),
                    barWidth / 2, barWidth / 2);
            }
        }

        private static Point PointOn(double cx, double cy, double r, double degrees)
        {
            double rad = degrees * Math.PI / 180.0;
            return new Point(cx + r * Math.Cos(rad), cy + r * Math.Sin(rad));
        }

        private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

        // ── 캐시 ── 값이 아니라 '종류'로 키를 만든다. 이 컨트롤은 30fps로 다시 그려지므로,
        //    그릴 때마다 Pen·Brush를 만들면 그게 곧 쓰레기 배출이다(위 DrawArcLayer 주석의 실측).
        private readonly Dictionary<int, Pen> _penCache = new();
        private readonly Dictionary<Color, Brush> _solidBrushCache = new();
        private Brush? _glowBrush;
        private Color _glowColor;
        private double[] _arcAlpha = Array.Empty<double>();

        /// <summary>호 밝기의 블룸 비율 — 본체 빛의 약 8%.</summary>
        private const double BloomAlphaFactor = 0.08;

        /// <summary>
        /// 알파·굵기·색을 양자화한 키로 펜을 재사용한다(BorderBeam.PenFor와 같은 방식).
        /// 알파는 색이 들고 온 값을 그대로 쓴다 — 트랙(0x33)과 호(단계별)가 같은 함수를 지나가므로,
        /// 알파를 인자로 따로 받으면 둘 중 하나가 상대의 불투명도를 덮어쓴다.
        /// </summary>
        private Pen PenFor(Color color, double thickness, bool round)
        {
            int qa = color.A >> 2;                                        // 6bit
            int qr = color.R >> 3, qg = color.G >> 3, qb = color.B >> 3;   // 5bit씩
            int qt = Math.Clamp((int)Math.Round(thickness * 4), 0, 31);    // 0.25px 단위
            int key = (qt << 22) | (qa << 16) | (qr << 11) | (qg << 6) | (qb << 1) | (round ? 1 : 0);

            if (_penCache.TryGetValue(key, out Pen? cached)) return cached;

            var pen = new Pen(FrozenBrush(Color.FromArgb((byte)(qa << 2), (byte)(qr << 3), (byte)(qg << 3), (byte)(qb << 3))), thickness)
            {
                StartLineCap = round ? PenLineCap.Round : PenLineCap.Flat,
                EndLineCap = round ? PenLineCap.Round : PenLineCap.Flat,
                LineJoin = PenLineJoin.Round,
            };
            pen.Freeze();
            _penCache[key] = pen;
            return pen;
        }

        private Brush FrozenBrush(Color color)
        {
            if (_solidBrushCache.TryGetValue(color, out Brush? cached)) return cached;

            var brush = new SolidColorBrush(color);
            brush.Freeze();
            _solidBrushCache[color] = brush;
            return brush;
        }

        /// <summary>헤드 글로우 — 색은 두 가지(흰색·음소거 회색)뿐이라 색별로 한 번만 만든다.</summary>
        private Brush GlowBrush(Color accent)
        {
            if (_glowBrush != null && _glowColor == accent) return _glowBrush;

            var glow = new RadialGradientBrush(WithAlpha(accent, 0x7F), WithAlpha(accent, 0));
            glow.Freeze();
            _glowBrush = glow;
            _glowColor = accent;
            return glow;
        }
    }
}
