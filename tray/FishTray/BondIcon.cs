using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace FishTray;

// 羁绊的五级图标（2026-11-13）：同一条虎鲸尾巴，每升一级往上加东西——
// Lv1 只有线条；Lv2 尾根淡出、脚下一圈涟漪；Lv3 尾巴里有淡淡的颜色、涟漪两圈；
// Lv4 实心渐变、溅起两颗气泡；Lv5 身后有光晕、四周有星。
// 坐标和做预览图时的 SVG 一致（取景框从 50,36 起、250×250；没有圆底，只有尾巴），尾巴外形是照用户给的图描出来的。
internal static class BondIcon
{
    // 尾巴轮廓（从左腿底部起，经左尖、凹口、右尖，回到右腿底部）
    private const string Rest = "C113.2,232.6 116.8,216.5 123,200 C129.1,185.7 136.6,173.8 135.4,158 C128.8,145.7 120,139.5 109,131.3 C91.4,115.9 76.4,100.5 76,76 C76.4,69.8 77.5,62.9 80.1,57.6 C91.2,57.6 99.7,67.1 110,71.7 C129.3,76.9 145.1,80.9 160,95 C164.2,101.2 167.9,107.8 172,113.9 C182.8,115 193.9,113.6 205,114 C217.6,116.9 229,121.3 241,126 C250,128.1 263.3,124.7 271.1,128 C270.7,132.3 266.3,139.7 264.9,144 C258.1,152.6 251.2,158.8 242,164.7 C230,169.6 217.8,171.4 205,173 C193.6,173 183.8,173.1 173.3,177.4 C159.5,198.3 173,226.4 173,250";
    // 一级：两条腿到 250 为止，平头
    private const string Tail1 = "M111,250 " + Rest;
    // 二级以后：两条腿多往下延到 270，再用透明渐变淡出，像沉进水里
    private const string TailL = "M108.5,270 L111,250 " + Rest + " L173,270";
    private const string TailF = TailL + " Z";
    private const double StrokeW = 15;

    private static SolidColorBrush Brush(string hex, double opacity = 1)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)) { Opacity = opacity };
        b.Freeze();
        return b;
    }

    private static Path Shape(string data) => new() { Data = Geometry.Parse(data), IsHitTestVisible = false };

    private static Path Line(string data, Brush stroke, double width, PenLineCap cap)
    {
        var p = Shape(data);
        p.Stroke = stroke; p.StrokeThickness = width;
        p.StrokeStartLineCap = cap; p.StrokeEndLineCap = cap; p.StrokeLineJoin = PenLineJoin.Round;
        return p;
    }

    // 尾根下面的“微笑弧”涟漪，中心在两条腿正中间（x=142）
    private static Path Ripple(double cy, double rx, double ry, double width, double opacity) =>
        Line($"M{142 - rx},{cy} A{rx},{ry} 0 0 0 {142 + rx},{cy}", Brush("#7CC4FF", opacity), width, PenLineCap.Round);

    private static Ellipse Bubble(double cx, double cy, double r)
    {
        var e = new Ellipse { Width = 2 * r, Height = 2 * r, Stroke = Brush("#BFE8FF"), StrokeThickness = 3.2, IsHitTestVisible = false };
        Canvas.SetLeft(e, cx - r); Canvas.SetTop(e, cy - r);
        return e;
    }

    private static Path Spark(double x, double y, double scale)
    {
        var p = Shape("M0,-14 L3,-3 L14,0 L3,3 L0,14 L-3,3 L-14,0 L-3,-3 Z");
        p.Fill = Brush("#EAF8FF");
        p.RenderTransform = new TransformGroup { Children = { new ScaleTransform(scale, scale), new TranslateTransform(x, y) } };
        return p;
    }

    private static Brush Gradient(string from, double fromA, string to, double toA, Point end)
    {
        var c1 = (Color)ColorConverter.ConvertFromString(from); c1.A = (byte)(fromA * 255);
        var c2 = (Color)ColorConverter.ConvertFromString(to); c2.A = (byte)(toA * 255);
        var b = new LinearGradientBrush(c1, c2, new Point(0, 0), end);
        b.Freeze();
        return b;
    }

    // 尾巴本体：整组套一层“往下淡出”的蒙版（y 230 以上不透明，260 以下全透明）
    private static Canvas Tail(Brush? fill, string stroke)
    {
        var group = new Canvas { Width = 400, Height = 400, IsHitTestVisible = false };
        var mask = new LinearGradientBrush(Colors.White, Colors.Transparent, new Point(0, 230), new Point(0, 260)) { MappingMode = BrushMappingMode.Absolute };
        mask.Freeze();
        group.OpacityMask = mask;
        if (fill != null) { var f = Shape(TailF); f.Fill = fill; group.Children.Add(f); }
        group.Children.Add(Line(TailL, Brush(stroke), StrokeW, PenLineCap.Round));
        return group;
    }

    public static FrameworkElement Build(int level)
    {
        level = System.Math.Clamp(level, 1, 5);
        var root = new Canvas { Width = 400, Height = 400, RenderTransform = new TranslateTransform(-50, -36), IsHitTestVisible = false };
        if (level == 5)
        {
            var glow = new Ellipse { Width = 256, Height = 256, IsHitTestVisible = false };
            glow.Fill = new RadialGradientBrush { GradientStops = { new GradientStop(Color.FromArgb(0x4D, 0x7C, 0xC4, 0xFF), 0), new GradientStop(Color.FromArgb(0x1F, 0x7C, 0xC4, 0xFF), 0.6), new GradientStop(Color.FromArgb(0, 0x7C, 0xC4, 0xFF), 1) } };
            Canvas.SetLeft(glow, 180 - 128); Canvas.SetTop(glow, 152 - 128);
            root.Children.Add(glow);
        }
        if (level >= 2) root.Children.Add(Ripple(262, 64, 11, 8, 1));
        if (level >= 3) root.Children.Add(Ripple(268, 96, 18, 6.5, 0.5));
        switch (level)
        {
            case 1: root.Children.Add(Line(Tail1, Brush("#7CC4FF"), StrokeW, PenLineCap.Flat)); break;
            case 2: root.Children.Add(Tail(null, "#7CC4FF")); break;
            case 3: root.Children.Add(Tail(Gradient("#7CC4FF", 0.34, "#7CC4FF", 0.10, new Point(0, 1)), "#8FD0FF")); break;
            case 4: root.Children.Add(Tail(Gradient("#CDEEFF", 1, "#4A90D8", 1, new Point(0.35, 1)), "#CDEEFF")); break;
            default: root.Children.Add(Tail(Gradient("#CDEEFF", 1, "#4A90D8", 1, new Point(0.35, 1)), "#FFFFFF")); break;
        }
        if (level >= 4) { root.Children.Add(Bubble(298, 88, 7)); root.Children.Add(Bubble(284, 58, 4)); }
        if (level == 5) { root.Children.Add(Spark(68, 132, 1)); root.Children.Add(Spark(298, 192, 0.8)); root.Children.Add(Spark(112, 26, 0.7)); }
        var view = new Canvas { Width = 250, Height = 250 }; // 取景框：x 50–300、y 36–286，尾巴和涟漪正好居中、占满
        view.Children.Add(root);
        return new Viewbox { Stretch = Stretch.Uniform, Child = view, IsHitTestVisible = false };
    }
}
