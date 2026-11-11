using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace FishTray;

// 大肥鱼的图标：深蓝圆底 + 冰蓝线条小鲸鱼。托盘图标、窗口图标、面板抬头都用同一个画法。
internal static class Whale
{
    internal static Bitmap Draw(int s, bool badge = true)
    {
        var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        float f = s;
        if (badge)
        {
            using var back = new LinearGradientBrush(new RectangleF(0, 0, f, f), Color.FromArgb(255, 20, 46, 108), Color.FromArgb(255, 5, 10, 32), 90f);
            g.FillEllipse(back, 0.5f, 0.5f, f - 1, f - 1);
            using var rim = new Pen(Color.FromArgb(255, 52, 92, 178), Math.Max(1f, f * 0.03f));
            g.DrawEllipse(rim, 0.75f, 0.75f, f - 1.75f, f - 1.75f);
        }
        var ice = Color.FromArgb(255, 150, 212, 255);
        float w = Math.Max(1.5f, f * 0.062f);
        PointF P(double x, double y) => new((float)(x * f), (float)(y * f));
        using var body = new GraphicsPath();
        body.AddBezier(P(.16, .62), P(.13, .42), P(.33, .33), P(.52, .38)); // 背（头更圆）
        body.AddBezier(P(.52, .38), P(.64, .41), P(.70, .46), P(.76, .47)); // 到尾柄，往上翘
        body.AddBezier(P(.76, .47), P(.82, .46), P(.85, .38), P(.90, .26)); // 尾鳍上叶（翘高）
        body.AddBezier(P(.90, .26), P(.90, .38), P(.87, .44), P(.85, .48)); // 尾鳍缺口
        body.AddBezier(P(.85, .48), P(.88, .52), P(.92, .54), P(.95, .60)); // 尾鳍下叶
        body.AddBezier(P(.95, .60), P(.86, .60), P(.80, .58), P(.74, .58)); // 回到尾柄
        body.AddBezier(P(.74, .58), P(.60, .82), P(.28, .82), P(.16, .62)); // 肚皮（更圆润）
        body.CloseFigure();
        using (var fill = new SolidBrush(Color.FromArgb(48, ice))) g.FillPath(fill, body);
        using var pen = new Pen(ice, w) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawPath(pen, body);
        using (var eye = new SolidBrush(ice)) g.FillEllipse(eye, P(.29, .56).X - w * .55f, P(.29, .56).Y - w * .55f, w * 1.1f, w * 1.1f);
        if (s >= 24)
        {
            // 喷泉：一根短柱，顶上向两边洒开
            using var spout = new Pen(ice, Math.Max(1.2f, w * .7f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(spout, P(.42, .345), P(.42, .22));
            g.DrawBezier(spout, P(.42, .22), P(.42, .17), P(.37, .15), P(.33, .19));
            g.DrawBezier(spout, P(.42, .22), P(.42, .17), P(.47, .15), P(.51, .19));
        }
        return bmp;
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

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);
}
