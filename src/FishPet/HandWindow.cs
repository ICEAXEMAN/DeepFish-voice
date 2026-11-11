using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace FishPet;

// 右键抚摸时跟着鼠标的白手（本机加，2026-11-10）：一个透明、点不到的小窗口叠在她上面，替代系统指针。
// 一只张开的手，手掌朝着头部中心：离中心越远手越斜、越被压扁，
// 远的那头略小（伪 3D 的透视）。风格是纯白的直边几何手，手指朝左上约 40°，细灰边和淡淡的投影托着它，浅色背景上也看得清。
// 窗口比她的窗口自由，手伸到她的窗口外面也不会被裁掉。

// 手的姿态。Rot：手整体转的角度（弧度）；K：大小；Cos / Sin：沿 (Ux, Uy)（从头部中心指向手的单位向量）倾斜的程度。
internal readonly record struct HandPose(double Rot, double K, double Cos, double Sin, double Ux, double Uy);

internal sealed class HandWindow : Window
{
    internal const double Size = 160;
    private readonly HandVisual visual = new();

    internal HandWindow()
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; ShowActivated = false; Topmost = true; IsHitTestVisible = false; Focusable = false;
        Width = Height = Size;
        Left = Top = -10000;
        Content = visual;
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            // 点不到（穿透）、不抢焦点、不进 Alt+Tab。
            SetWindowLongPtr(handle, -20, (nint)((long)GetWindowLongPtr(handle, -20) | 0x8000000 | 0x80 | 0x20));
        };
    }

    // 她被别的置顶窗口盖住、重新放回最上面以后，手要在她之上。
    internal void Raise() { if (IsVisible) ZOrder.Raise(new WindowInteropHelper(this).Handle); }

    // center：手掌中心，屏幕上的 DIP 坐标。
    internal void Update(Point center, HandPose pose)
    {
        Left = center.X - Size / 2; Top = center.Y - Size / 2;
        visual.Pose = pose;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
}

internal sealed class HandVisual : FrameworkElement
{
    private HandPose pose = new(-40 * Math.PI / 180, .4, 1, 0, 0, 0);
    internal HandPose Pose { get => pose; set { pose = value; InvalidateVisual(); } }

    internal const double Persp = .0065;
    private static readonly Brush Fill = Brushes.White;
    private static readonly Pen Edge = new(new SolidColorBrush(Color.FromRgb(0xb9, 0xbd, 0xc9)), 2.2) { LineJoin = PenLineJoin.Round };

    static HandVisual() { Edge.Freeze(); }

    internal HandVisual()
    {
        IsHitTestVisible = false;
        Effect = new DropShadowEffect { BlurRadius = 5, ShadowDepth = 3, Direction = 300, Opacity = .28, Color = Colors.Black };
    }

    // ---- 手的几何：局部坐标 x 向右、y 向指尖，原点在手掌中心；手掌是切了角的色块，手指是并排的直边方块 ----
    private static (double X, double Y)[] Rotate((double X, double Y)[] points, double angle, (double X, double Y) around)
    {
        double c = Math.Cos(angle), s = Math.Sin(angle);
        var result = new (double X, double Y)[points.Length];
        for (int i = 0; i < points.Length; i++)
        {
            double dx = points[i].X - around.X, dy = points[i].Y - around.Y;
            result[i] = (around.X + dx * c - dy * s, around.Y + dx * s + dy * c);
        }
        return result;
    }

    // 一根手指（或拇指）：宽 w、长 length，指尖两角切 cut；从 (bx, by) 往上，绕根部向左转 angle 弧度。
    private static (double X, double Y)[] Block(double bx, double by, double length, double w, double cut, double angle = 0)
    {
        double h = w / 2;
        return Rotate([(bx - h, by), (bx - h, by + length - cut), (bx - h + cut, by + length), (bx + h - cut, by + length), (bx + h, by + length - cut), (bx + h, by)], angle, (bx, by));
    }

    private static readonly (double X, double Y)[] Palm = [(-22, 17), (22, 17), (22, -6), (11, -17), (-11, -17), (-22, -6)];
    private static readonly double[] FingerX = [-17.25, -5.75, 5.75, 17.25];

    private static readonly List<(double X, double Y)[]> Open = BuildOpen();

    private static List<(double X, double Y)[]> BuildOpen()
    {
        double[] length = [36, 41, 37, 29];
        var shapes = new List<(double X, double Y)[]> { Palm };
        for (int i = 0; i < 4; i++) shapes.Add(Block(FingerX[i], 15, length[i], 9, 2.8));
        shapes.Add(Block(-12, -9, 36, 10.5, 3, 1.0)); // 拇指：从左下往左上斜出去，和食指隔一条缝
        return shapes;
    }

    // 先按手自己的朝向转好、缩放，再在屏幕上沿「从头部中心指向手」的方向倾斜（压扁 + 远的那头变小）。
    private Point Project((double X, double Y) p, HandPose m, double cx, double cy)
    {
        double sx = p.X, sy = -p.Y;
        double c = Math.Cos(m.Rot), s = Math.Sin(m.Rot);
        double vx = (sx * c - sy * s) * m.K, vy = (sx * s + sy * c) * m.K;
        if (m.Sin > 0)
        {
            double along = vx * m.Ux + vy * m.Uy;
            double qx = vx - along * m.Ux, qy = vy - along * m.Uy;
            double f = 1 / (1 + along * m.Sin * Persp);
            double a2 = along * m.Cos * f;
            vx = qx * f + a2 * m.Ux; vy = qy * f + a2 * m.Uy;
        }
        return new Point(cx + vx, cy + vy);
    }

    private Geometry Build(List<(double X, double Y)[]> shapes, HandPose m, double cx, double cy)
    {
        var geometry = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var context = geometry.Open())
        {
            foreach (var shape in shapes)
            {
                context.BeginFigure(Project(shape[0], m, cx, cy), true, true);
                for (int i = 1; i < shape.Length; i++) context.LineTo(Project(shape[i], m, cx, cy), true, false);
            }
        }
        geometry.Freeze();
        return geometry;
    }

    protected override void OnRender(DrawingContext dc)
    {
        Draw(dc, Build(Open, pose, ActualWidth / 2, ActualHeight / 2));
    }

    private static void Draw(DrawingContext dc, Geometry geometry)
    {
        dc.DrawGeometry(Fill, Edge, geometry);          // 先带灰边画一遍
        dc.DrawGeometry(Fill, null, geometry);          // 再用纯白盖住边的内侧，手只剩外轮廓一圈灰边，指缝里透出灰色
    }
}
