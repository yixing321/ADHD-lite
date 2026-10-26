// v0.6.1：主题装饰纹样（纯矢量，不依赖图片）。
// 松树 / 雪花 / 山 / 雾、竹 / 梅 / 叶、轨道 / 电路、简约圆环。
// 只用来做小面积点缀：侧栏、页面标题、主题色块。

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace AdhdLite
{
    public static class Deco
    {
        public static FrameworkElement Make(int style, int color, double size, string hex, double opacity)
        {
            var canvas = new Canvas();
            canvas.Width = 100;
            canvas.Height = 100;

            Brush br;
            try { br = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
            catch { br = Brushes.Gray; }

            if (style == 1)
            {
                if (color == 0) Pine(canvas, br);
                else if (color == 1) Snow(canvas, br);
                else if (color == 2) Mountain(canvas, br);
                else Fog(canvas, br);
            }
            else if (style == 2)
            {
                if (color == 0) Pot(canvas, br);
                else if (color == 1) Bamboo(canvas, br);
                else if (color == 2) Branch(canvas, br);
                else Blossom(canvas, br);
            }
            else if (style == 3)
            {
                if (color == 0) Orbit(canvas, br);
                else if (color == 1) Planet(canvas, br);
                else if (color == 2) Wave(canvas, br);
                else Circuit(canvas, br);
            }
            else if (style == 4)
            {
                if (color == 0) Minimal(canvas, br);
                else if (color == 1) Grid(canvas, br);
                else if (color == 2) Diagonal(canvas, br);
                else Dots(canvas, br);
            }
            else
            {
                if (color == 0) Leaf(canvas, br);
                else if (color == 1) Drops(canvas, br);
                else if (color == 2) Sun(canvas, br);
                else Branch(canvas, br);
            }

            var vb = new Viewbox();
            vb.Width = size;
            vb.Height = size;
            vb.Stretch = Stretch.Uniform;
            vb.Opacity = opacity;
            vb.IsHitTestVisible = false;
            vb.Child = canvas;
            return vb;
        }

        // 录入窗/设置页用的“场景纹样”：主纹样 + 两三个小点缀，比图标更完整
        // variant=0 主纹样；variant=1 同主题的另一种纹样（给背景/留白用，避免和主图标重复）
        public static FrameworkElement MakeScene(int style, int color, double size, string hex, double opacity, int variant = 0)
        {
            var canvas = new Canvas();
            canvas.Width = 100;
            canvas.Height = 100;
            Brush br;
            try { br = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
            catch { br = Brushes.Gray; }

            if (variant == 1)
            {
                AltScene(style, color, canvas, br);
            }
            else if (style == 1)
            {
                if (color == 0) { Pine(canvas, br); Dot(canvas, br, 84, 18, 4); Stroke(canvas, br, new double[] { 8, 88, 92, 88 }, 2); }
                else if (color == 1) { Snow(canvas, br); Dot(canvas, br, 16, 20, 3); Dot(canvas, br, 84, 80, 3); }
                else if (color == 2) { Mountain(canvas, br); Dot(canvas, br, 80, 18, 4); Stroke(canvas, br, new double[] { 8, 88, 92, 88 }, 2); }
                else { Fog(canvas, br); Dot(canvas, br, 18, 20, 3); Dot(canvas, br, 82, 78, 3); }
            }
            else if (style == 2)
            {
                if (color == 0) { Pot(canvas, br); Dot(canvas, br, 82, 22, 4); }
                else if (color == 1) { Bamboo(canvas, br); Dot(canvas, br, 82, 22, 4); }
                else if (color == 2) { Branch(canvas, br); Dot(canvas, br, 82, 22, 4); }
                else { Blossom(canvas, br); Dot(canvas, br, 18, 80, 3); }
            }
            else if (style == 3)
            {
                if (color == 0) { Orbit(canvas, br); Dot(canvas, br, 8, 12, 3); }
                else if (color == 1) { Planet(canvas, br); Dot(canvas, br, 16, 18, 3); }
                else if (color == 2) { Wave(canvas, br); Dot(canvas, br, 84, 18, 4); }
                else { Circuit(canvas, br); Dot(canvas, br, 18, 84, 4); }
            }
            else if (style == 4)
            {
                if (color == 0) { Minimal(canvas, br); Dot(canvas, br, 18, 18, 3); }
                else if (color == 1) { Grid(canvas, br); Ring(canvas, br, 50, 50, 8, 3); }
                else if (color == 2) { Diagonal(canvas, br); Dot(canvas, br, 18, 18, 4); }
                else { Dots(canvas, br); Ring(canvas, br, 50, 50, 10, 3); }
            }
            else
            {
                if (color == 0) { Leaf(canvas, br); Dot(canvas, br, 82, 18, 4); }
                else if (color == 1) { Drops(canvas, br); Stroke(canvas, br, new double[] { 10, 88, 90, 88 }, 2); }
                else if (color == 2) { Sun(canvas, br); Dot(canvas, br, 82, 82, 3); }
                else { Branch(canvas, br); Dot(canvas, br, 82, 18, 4); }
            }

            var vb = new Viewbox();
            vb.Width = size;
            vb.Height = size;
            vb.Stretch = Stretch.Uniform;
            vb.Opacity = opacity;
            vb.IsHitTestVisible = false;
            vb.Child = canvas;
            return vb;
        }

        // 同主题的另一种纹样：录入窗背景 / 设置留白用
        static void AltScene(int style, int color, Canvas c, Brush b)
        {
            if (style == 1)
            {
                if (color == 0) Mountain(c, b);
                else if (color == 1) Fog(c, b);
                else if (color == 2) Pine(c, b);
                else Snow(c, b);
            }
            else if (style == 2)
            {
                if (color == 0) Branch(c, b);
                else if (color == 1) Pot(c, b);
                else if (color == 2) Bamboo(c, b);
                else Branch(c, b);
            }
            else if (style == 3)
            {
                if (color == 0) Planet(c, b);
                else if (color == 1) Orbit(c, b);
                else if (color == 2) Circuit(c, b);
                else Wave(c, b);
            }
            else if (style == 4)
            {
                if (color == 0) Grid(c, b);
                else if (color == 1) Minimal(c, b);
                else if (color == 2) Dots(c, b);
                else Diagonal(c, b);
            }
            else
            {
                if (color == 0) Branch(c, b);
                else if (color == 1) Leaf(c, b);
                else if (color == 2) Drops(c, b);
                else Sun(c, b);
            }
            Dot(c, b, 84, 18, 3);
            Dot(c, b, 16, 82, 3);
        }

        static void Add(Canvas c, Shape s, double left, double top)
        {
            Canvas.SetLeft(s, left);
            Canvas.SetTop(s, top);
            c.Children.Add(s);
        }

        static void Poly(Canvas c, Brush b, double[] pts)
        {
            var p = new Polygon();
            p.Fill = b;
            var pc = new PointCollection();
            for (int i = 0; i + 1 < pts.Length; i += 2) pc.Add(new Point(pts[i], pts[i + 1]));
            p.Points = pc;
            c.Children.Add(p);
        }

        static void Stroke(Canvas c, Brush b, double[] pts, double thick)
        {
            var p = new Polyline();
            p.Stroke = b;
            p.StrokeThickness = thick;
            p.StrokeStartLineCap = PenLineCap.Round;
            p.StrokeEndLineCap = PenLineCap.Round;
            p.StrokeLineJoin = PenLineJoin.Round;
            var pc = new PointCollection();
            for (int i = 0; i + 1 < pts.Length; i += 2) pc.Add(new Point(pts[i], pts[i + 1]));
            p.Points = pc;
            c.Children.Add(p);
        }

        static void Dot(Canvas c, Brush b, double cx, double cy, double r)
        {
            var e = new Ellipse();
            e.Width = r * 2;
            e.Height = r * 2;
            e.Fill = b;
            Add(c, e, cx - r, cy - r);
        }

        static void Ring(Canvas c, Brush b, double cx, double cy, double r, double thick)
        {
            var e = new Ellipse();
            e.Width = r * 2;
            e.Height = r * 2;
            e.Stroke = b;
            e.StrokeThickness = thick;
            Add(c, e, cx - r, cy - r);
        }

        // 冷白 · 雪松
        static void Pine(Canvas c, Brush b)
        {
            Poly(c, b, new double[] { 50, 6, 18, 38, 82, 38 });
            Poly(c, b, new double[] { 50, 22, 12, 60, 88, 60 });
            Poly(c, b, new double[] { 50, 40, 6, 82, 94, 82 });
            var trunk = new Rectangle();
            trunk.Width = 8;
            trunk.Height = 18;
            trunk.RadiusX = 3;
            trunk.RadiusY = 3;
            trunk.Fill = b;
            Add(c, trunk, 46, 79);
        }

        // 冷白 · 冰蓝
        static void Snow(Canvas c, Brush b)
        {
            for (int i = 0; i < 3; i++)
            {
                double a = i * Math.PI / 3.0;
                double dx = Math.Cos(a) * 40, dy = Math.Sin(a) * 40;
                Stroke(c, b, new double[] { 50 - dx, 50 - dy, 50 + dx, 50 + dy }, 5);
                double bx = Math.Cos(a + 0.6) * 20, by = Math.Sin(a + 0.6) * 20;
                Stroke(c, b, new double[] { 50 + dx * 0.65, 50 + dy * 0.65, 50 + dx * 0.65 + bx, 50 + dy * 0.65 + by }, 3.5);
                Stroke(c, b, new double[] { 50 + dx * 0.65, 50 + dy * 0.65, 50 + dx * 0.65 - bx, 50 + dy * 0.65 - by }, 3.5);
            }
            Dot(c, b, 50, 50, 5);
        }

        // 冷白 · 石板
        static void Mountain(Canvas c, Brush b)
        {
            Stroke(c, b, new double[] { 10, 82, 34, 38, 52, 66, 68, 26, 92, 82 }, 6);
            Dot(c, b, 78, 20, 4);
        }

        // 冷白 · 雾灰
        static void Fog(Canvas c, Brush b)
        {
            Stroke(c, b, new double[] { 12, 34, 48, 30, 88, 34 }, 5);
            Stroke(c, b, new double[] { 20, 50, 58, 46, 84, 50 }, 5);
            Stroke(c, b, new double[] { 10, 66, 44, 62, 70, 66 }, 5);
        }

        // 素雅 · 苔绿
        static void Bamboo(Canvas c, Brush b)
        {
            double[] xs = { 30, 52, 72 };
            double[] hs = { 68, 76, 60 };
            for (int i = 0; i < 3; i++)
            {
                var r = new Rectangle();
                r.Width = 7;
                r.Height = hs[i];
                r.RadiusX = 3.5;
                r.RadiusY = 3.5;
                r.Fill = b;
                Add(c, r, xs[i], 88 - hs[i]);
                var leaf = new Ellipse();
                leaf.Width = 18;
                leaf.Height = 6;
                leaf.Fill = b;
                leaf.RenderTransform = new RotateTransform(i % 2 == 0 ? -28 : 28, 0, 3);
                Add(c, leaf, xs[i] + 2, 26 + i * 12);
            }
        }

        // 素雅 · 梅子
        static void Blossom(Canvas c, Brush b)
        {
            for (int i = 0; i < 5; i++)
            {
                double a = -Math.PI / 2 + i * 2 * Math.PI / 5.0;
                var e = new Ellipse();
                e.Width = 30;
                e.Height = 24;
                e.Fill = b;
                e.RenderTransform = new RotateTransform(a * 180 / Math.PI + 90, 15, 12);
                Add(c, e, 50 + Math.Cos(a) * 20 - 15, 50 + Math.Sin(a) * 20 - 12);
            }
            Dot(c, Brushes.White, 50, 50, 7);
        }

        // 柔和 / 素雅 / 冷白默认
        static void Leaf(Canvas c, Brush b)
        {
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(new Point(50, 90), false, false);
                ctx.BezierTo(new Point(48, 60), new Point(38, 34), new Point(20, 16), true, false);
            }
            var path = new Path();
            path.Data = g;
            path.Stroke = b;
            path.StrokeThickness = 5;
            path.StrokeStartLineCap = PenLineCap.Round;
            path.StrokeEndLineCap = PenLineCap.Round;
            c.Children.Add(path);

            for (int i = 0; i < 3; i++)
            {
                var e = new Ellipse();
                e.Width = 30;
                e.Height = 14;
                e.Fill = b;
                e.RenderTransform = new RotateTransform(-35, 15, 7);
                Add(c, e, 26 + i * 10, 28 + i * 16);
            }
        }

        // 科幻 · 星舰/深空/极光
        static void Orbit(Canvas c, Brush b)
        {
            Ring(c, b, 50, 50, 40, 4);
            var star = new Polygon();
            var pc = new PointCollection();
            for (int i = 0; i < 10; i++)
            {
                double r = i % 2 == 0 ? 26 : 10;
                double a = -Math.PI / 2 + i * Math.PI / 5.0;
                pc.Add(new Point(50 + Math.Cos(a) * r, 50 + Math.Sin(a) * r));
            }
            star.Points = pc;
            star.Fill = b;
            c.Children.Add(star);
            Dot(c, b, 94, 30, 4);
            Dot(c, b, 8, 66, 3);
        }

        // 科幻 · 矩阵
        static void Circuit(Canvas c, Brush b)
        {
            Stroke(c, b, new double[] { 12, 24, 40, 24, 40, 52, 70, 52, 70, 22, 90, 22 }, 4);
            Stroke(c, b, new double[] { 10, 74, 30, 74, 30, 52, 40, 52 }, 4);
            Stroke(c, b, new double[] { 60, 76, 84, 76, 84, 54, 70, 54 }, 4);
            Dot(c, b, 12, 24, 5);
            Dot(c, b, 40, 52, 5);
            Dot(c, b, 70, 22, 5);
            Dot(c, b, 84, 76, 5);
        }

        // 简约
        static void Minimal(Canvas c, Brush b)
        {
            Ring(c, b, 50, 50, 34, 4);
            Stroke(c, b, new double[] { 8, 50, 92, 50 }, 4);
            Dot(c, b, 50, 50, 7);
            Dot(c, b, 84, 50, 4);
        }

        // 柔和 · 雾蓝
        static void Drops(Canvas c, Brush b)
        {
            for (int i = 0; i < 3; i++)
            {
                var e = new Ellipse();
                e.Width = 15;
                e.Height = 22;
                e.Fill = b;
                e.RenderTransform = new RotateTransform(18, 7.5, 11);
                Add(c, e, 22 + i * 20, 26 + i * 12);
            }
            Stroke(c, b, new double[] { 30, 84, 70, 84 }, 3);
        }

        // 柔和 · 暖砂
        static void Sun(Canvas c, Brush b)
        {
            Ring(c, b, 50, 50, 22, 4);
            Dot(c, b, 50, 50, 7);
            for (int i = 0; i < 8; i++)
            {
                double a = i * Math.PI / 4;
                Stroke(c, b, new double[] {
                    50 + Math.Cos(a) * 31, 50 + Math.Sin(a) * 31,
                    50 + Math.Cos(a) * 43, 50 + Math.Sin(a) * 43 }, 3.5);
            }
        }

        // 柔和 / 素雅 · 枝
        static void Branch(Canvas c, Brush b)
        {
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(new Point(20, 86), false, false);
                ctx.BezierTo(new Point(34, 58), new Point(58, 36), new Point(82, 18), true, false);
            }
            var p = new Path();
            p.Data = g;
            p.Stroke = b;
            p.StrokeThickness = 4;
            p.StrokeStartLineCap = PenLineCap.Round;
            p.StrokeEndLineCap = PenLineCap.Round;
            c.Children.Add(p);
            Dot(c, b, 35, 63, 4);
            Dot(c, b, 51, 45, 4);
            Dot(c, b, 68, 30, 4);
        }

        // 素雅 · 豆沙
        static void Pot(Canvas c, Brush b)
        {
            Stroke(c, b, new double[] { 22, 52, 32, 80, 68, 80, 78, 52 }, 5);
            Stroke(c, b, new double[] { 18, 52, 82, 52 }, 4);
            Stroke(c, b, new double[] { 34, 38, 50, 26, 66, 38 }, 4);
            Dot(c, b, 50, 68, 4);
        }

        // 科幻 · 深空
        static void Planet(Canvas c, Brush b)
        {
            Ring(c, b, 50, 50, 26, 4);
            var e = new Ellipse();
            e.Width = 86;
            e.Height = 24;
            e.Stroke = b;
            e.StrokeThickness = 4;
            e.RenderTransform = new RotateTransform(-18, 43, 12);
            Add(c, e, 7, 38);
            Dot(c, b, 50, 50, 6);
        }

        // 科幻 · 极光
        static void Wave(Canvas c, Brush b)
        {
            Stroke(c, b, new double[] { 8, 36, 26, 26, 44, 36, 62, 46, 92, 32 }, 4);
            Stroke(c, b, new double[] { 8, 54, 26, 44, 44, 54, 62, 64, 92, 50 }, 4);
            Stroke(c, b, new double[] { 8, 72, 26, 62, 44, 72, 62, 82, 92, 68 }, 4);
        }

        // 简约 · 黑白
        static void Grid(Canvas c, Brush b)
        {
            for (int i = 0; i < 3; i++)
            {
                Stroke(c, b, new double[] { 24 + i * 26, 22, 24 + i * 26, 78 }, 3);
                Stroke(c, b, new double[] { 22, 24 + i * 26, 78, 24 + i * 26 }, 3);
            }
            Dot(c, b, 50, 50, 5);
        }

        // 简约 · 橙白
        static void Diagonal(Canvas c, Brush b)
        {
            Stroke(c, b, new double[] { 18, 74, 46, 22 }, 5);
            Stroke(c, b, new double[] { 36, 80, 70, 20 }, 5);
            Stroke(c, b, new double[] { 56, 84, 88, 30 }, 5);
            Dot(c, b, 76, 70, 5);
        }

        // 简约 · 绿白
        static void Dots(Canvas c, Brush b)
        {
            Dot(c, b, 30, 30, 7);
            Dot(c, b, 50, 50, 7);
            Dot(c, b, 70, 70, 7);
            Dot(c, b, 70, 28, 4);
            Dot(c, b, 28, 70, 4);
        }
    }
}
