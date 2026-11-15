using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace FishTray;

// 大肥鱼的图标：浅到深的蓝色渐变圆底 + 深蓝虎鲸剪影（轮廓见 WhaleShape.cs）。托盘图标、窗口图标、面板抬头都用同一个画法。
internal static class Whale
{
    private const float FishSize = 0.70f;   // 鲸鱼宽度占图标的比例
    private const float FishDown = 0.02f;   // 往下挪一点，视觉上才在圆里居中（尾鳍那头偏上）
    private const float FishLeft = 0.03f;   // 往左挪一丁点，右上角的尾巴别太贴圆边（2026-11-13）
    private const float GradientAngle = 61.7f; // 渐变方向：左上浅、右下深（x 占 0.35、y 占 0.65）

    internal static Bitmap Draw(int s, bool badge = true)
    {
        var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        float f = s;
        var all = new RectangleF(-0.5f, -0.5f, f + 1, f + 1);
        if (badge)
        {
            using var back = new LinearGradientBrush(all, Color.FromArgb(255, 232, 248, 255), Color.FromArgb(255, 28, 66, 156), GradientAngle);
            g.FillEllipse(back, 0.5f, 0.5f, f - 1, f - 1);
            // 圆边：比底色深一档，细边和浅色底不会糊在一起
            float rim = Math.Max(1f, f * 0.03f);
            using var rimPen = new Pen(Color.FromArgb(255, 20, 50, 118), rim);
            float inset = 0.5f + rim / 2;
            g.DrawEllipse(rimPen, inset, inset, f - 2 * inset, f - 2 * inset);
        }
        using var path = FishPath(f);
        using var fish = new LinearGradientBrush(all, Color.FromArgb(255, 22, 50, 112), Color.FromArgb(255, 6, 12, 36), GradientAngle);
        g.FillPath(fish, path);
        return bmp;
    }

    // 把 WhaleShape 里的点连成平滑的闭合曲线：每相邻两点之间用 Catmull-Rom 换成的贝塞尔
    private static GraphicsPath FishPath(float f)
    {
        var path = new GraphicsPath(FillMode.Alternate);
        foreach (var loop in WhaleShape.Loops)
        {
            int n = loop.Length / 2;
            PointF P(int i)
            {
                i = ((i % n) + n) % n;
                return new PointF((loop[i * 2] * FishSize + 0.5f - FishLeft) * f, (loop[i * 2 + 1] * FishSize + 0.5f + FishDown) * f);
            }
            path.StartFigure();
            for (int i = 0; i < n; i++)
            {
                PointF p0 = P(i - 1), p1 = P(i), p2 = P(i + 1), p3 = P(i + 2);
                var c1 = new PointF(p1.X + (p2.X - p0.X) / 6f, p1.Y + (p2.Y - p0.Y) / 6f);
                var c2 = new PointF(p2.X - (p3.X - p1.X) / 6f, p2.Y - (p3.Y - p1.Y) / 6f);
                path.AddBezier(p1, c1, c2, p2);
            }
            path.CloseFigure();
        }
        return path;
    }

    internal static Icon Icon(int s)
    {
        using var bmp = Draw(s);
        var handle = bmp.GetHicon();
        try { using var temp = System.Drawing.Icon.FromHandle(handle); return (Icon)temp.Clone(); }
        finally { DestroyIcon(handle); }
    }

    internal static BitmapSource Source(int s)
    {
        using var bmp = Draw(s);
        using var memory = new System.IO.MemoryStream();
        bmp.Save(memory, ImageFormat.Png);
        memory.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = memory;
        image.EndInit();
        image.Freeze();
        return image;
    }

    // 窗口图标（任务栏、Alt+Tab 用）：和托盘/面板同一张画，按 64 像素渲染（本机接口，2026-11-15）
    internal static System.Windows.Media.ImageSource WindowIcon() => Source(64);

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);
}
