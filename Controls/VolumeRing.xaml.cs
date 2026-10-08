using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace TopDock.Controls
{
    /// <summary>
    /// 볼륨 계기 — 바 대신 우측 상태 점 자리를 감싸는 원형 아크다.
    ///
    /// 노치의 "살아있는 가장자리(BorderBeam)"와 같은 언어를 약하게 빌린다:
    /// 꼬리(시작)는 흐리고 헤드(끝)로 갈수록 밝아지는 그라데이션, 뒤로 번지는 은은한 블룸,
    /// 끝점에 맺히는 작은 빛. 그래서 텅 빈 회색 고리가 아니라 '빛이 차오르는' 링으로 읽힌다.
    ///
    /// 진행바 자리를 빌리지 않으므로 재생 위치가 계속 보이고, 접힌 노치든 펼친 카드든
    /// 같은 자리·같은 모양이라 화면이 바뀌어도 눈이 옮겨 다니지 않는다.
    /// 음소거는 아이콘이 아니라 채움색(시스템 그레이)으로만 말한다.
    /// </summary>
    public class VolumeRing : FrameworkElement
    {
        private const double Thickness = 2.0;
        private const double Diameter = 24.0;
        private static readonly Duration Glide = new(TimeSpan.FromMilliseconds(140));

        private static readonly Color FillColor = Color.FromRgb(0xFF, 0xFF, 0xFF);
        private static readonly Color MutedColor = Color.FromRgb(0x8E, 0x8E, 0x93);
        private static readonly Color TrackColor = Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF);

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
                Timeline.SetDesiredFrameRate(glide, 60);
                BeginAnimation(LevelProperty, glide);
            }
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
            dc.DrawEllipse(null, MakePen(TrackColor, Thickness, false), new Point(cx, cy), radius, radius);

            if (pct <= 0.0005) return;

            // 완전한 원(360°)은 시작=끝이라 이어 그리면 안 된다 — 아주 살짝 남긴다.
            double sweep = Math.Min(359.0, 360.0 * pct);
            int segments = Math.Max(12, (int)Math.Ceiling(sweep / 2.4)); // ≈2.4° 간격
            const double startDeg = -90.0; // 12시

            Point prev = PointOn(cx, cy, radius, startDeg);
            for (int i = 1; i <= segments; i++)
            {
                double t = (double)i / segments;                 // 0=꼬리, 1=헤드
                Point cur = PointOn(cx, cy, radius, startDeg + sweep * t);

                // 라이팅 — 꼬리에서 헤드로 차오르는 밝기
                double alpha = 0.28 + 0.72 * Math.Pow(t, 0.85);
                double head = Math.Exp(-Math.Pow((1.0 - t) / 0.12, 2)); // 헤드 코어
                alpha = Math.Min(1.0, alpha + head * 0.35);

                // 블룸 — 헤드 쪽이 조금 더 넓게 번진다(약하게: 본체의 약 8%)
                byte bloomAlpha = (byte)Math.Clamp(alpha * 0.08 * 255, 0, 255);
                if (bloomAlpha >= 2)
                {
                    double bloomWidth = Thickness * (1.5 + 0.4 * t);
                    dc.DrawLine(MakePen(WithAlpha(accent, bloomAlpha), bloomWidth, true), prev, cur);
                }

                byte coreAlpha = (byte)Math.Clamp(alpha * 255, 0, 255);
                dc.DrawLine(MakePen(WithAlpha(accent, coreAlpha), Thickness, true), prev, cur);

                prev = cur;
            }

            // 헤드 글로우 — 끝점에 맺히는 작은 빛
            Point tip = PointOn(cx, cy, radius, startDeg + sweep);
            double glowRadius = Thickness * 1.0;
            var glow = new RadialGradientBrush(
                WithAlpha(accent, (byte)(0.5 * 255)),
                WithAlpha(accent, 0));
            glow.Freeze();
            dc.DrawEllipse(glow, null, tip, glowRadius, glowRadius);
        }

        private static Point PointOn(double cx, double cy, double r, double degrees)
        {
            double rad = degrees * Math.PI / 180.0;
            return new Point(cx + r * Math.Cos(rad), cy + r * Math.Sin(rad));
        }

        private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

        private static Pen MakePen(Color color, double thickness, bool round)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            var pen = new Pen(brush, thickness)
            {
                StartLineCap = round ? PenLineCap.Round : PenLineCap.Flat,
                EndLineCap = round ? PenLineCap.Round : PenLineCap.Flat,
                LineJoin = PenLineJoin.Round,
            };
            pen.Freeze();
            return pen;
        }
    }
}
