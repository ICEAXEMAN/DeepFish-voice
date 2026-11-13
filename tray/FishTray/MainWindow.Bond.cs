using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace FishTray;

// 面板上的“陪伴”卡片（本机加，2026-11-08）：羁绊（原“亲密度”，2026-11-13 改名改版）的竖条纹进度条和五个节点、番茄钟的圆环倒计时。数据来自 get_state 的 affection、focus。
public partial class MainWindow
{
    private static readonly string[] LevelNames = ["初识", "磨合", "同频", "默契", "羁绊"];
    private readonly System.Windows.Threading.DispatcherTimer focusClock = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private string focusPhase = "none";
    private DateTime focusEnds;
    private double focusTotal = 25 * 60;
    private double bondRatio;   // 整条走了多少：(已到的等级 − 1 + 这一级里的进度) / 4
    private int shownLevel;
    private bool bondWired;

    // 条纹一格的宽（和 xaml 里 BondStripes 的 Viewport 宽一致），条按整格走，不露半截条纹
    private const double StripePitch = 6;
    // 条里面离卡片边的距离：边框 1.4 + 内边距 7（和 xaml 里 BondTrack、BondInner 对得上），节点要和条的两头对齐
    private const double BondInset = 8.4;
    private readonly List<(Grid Marker, System.Windows.Shapes.Ellipse Halo, System.Windows.Shapes.Ellipse Dot, TextBlock Label)> bondNodes = [];

    private static SolidColorBrush Rgb(byte r, byte g, byte b) => new(Color.FromRgb(r, g, b));

    private void WireBond()
    {
        if (bondWired) return;
        bondWired = true;
        // 五个节点：外圈（当前级才亮）、圆点、名字
        for (int i = 0; i < LevelNames.Length; i++)
        {
            var halo = new System.Windows.Shapes.Ellipse { Width = 15, Height = 15, Stroke = Rgb(0x7C, 0xC4, 0xFF), StrokeThickness = 1.2, Opacity = 0, IsHitTestVisible = false };
            var dot = new System.Windows.Shapes.Ellipse { Width = 9, Height = 9, StrokeThickness = 1.3, IsHitTestVisible = false };
            var label = new TextBlock { Text = LevelNames[i], FontSize = 10.5, IsHitTestVisible = false };
            // 外环和圆点装进同一个 15×15 的格子里各自居中，所以一定同心（以前各摆各的，会差半个像素）
            var marker = new Grid { Width = 15, Height = 15, IsHitTestVisible = false };
            marker.Children.Add(halo); marker.Children.Add(dot);
            BondNodes.Children.Add(marker); BondNodes.Children.Add(label);
            bondNodes.Add((marker, halo, dot, label));
        }
        BondNodes.UseLayoutRounding = true;
        BondNodes.SizeChanged += (_, _) => LayoutNodes();
        BondInner.SizeChanged += (_, _) => PlaceBond(false);
        StyleNodes(1);
        SetBondIcon(1, false);
        FocusButton.Click += async (_, _) =>
        {
            bool start = focusPhase == "none";
            FocusButton.IsEnabled = false;
            Notice(start ? "▶ 开始专注 25 分钟" : "专注结束");
            var result = await Pet.Panel(new { op = "focus", action = start ? "start" : "stop" });
            FocusButton.IsEnabled = true;
            if (result is null) Notice(Pet.Running ? "没做成，再点一下试试" : "桌宠没有运行");
            await Refresh();
        };
        focusClock.Tick += (_, _) => DrawFocus();
        IsVisibleChanged += (_, _) => { if (IsVisible) focusClock.Start(); else focusClock.Stop(); };
        DrawFocus();
    }

    private void ApplyBond(JsonElement s)
    {
        WireBond();
        if (s.TryGetProperty("affection", out var a) && a.ValueKind == JsonValueKind.Object)
        {
            int I(string k) => a.TryGetProperty(k, out var v) && v.TryGetInt32(out var x) ? x : 0;
            int points = I("points"), level = Math.Clamp(I("level"), 1, 5), from = I("from"), next = I("next"), today = I("today");
            string name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            string? nextName = a.TryGetProperty("next_name", out var nn) && nn.ValueKind == JsonValueKind.String ? nn.GetString() : null;
            LevelName.Text = name;
            LevelBadge.Text = "Lv" + level;
            PointsText.Text = points.ToString();
            LevelHint.Text = nextName == null ? (points >= 200 ? "羁绊 · 已满格，满满的喜欢 ♥" : "羁绊 · 已经到最高阶啦 ♥") : $"羁绊 · 再 {Math.Max(0, next - points)} 点到「{nextName}」";
            // 右边的小标签：今天涨了绿、掉了红、没变灰
            TodayText.Text = today > 0 ? $"今天 ▲ +{today}" : today < 0 ? $"今天 ▼ {today}" : "今天还没变";
            TodayText.Foreground = today > 0 ? Rgb(0x6F, 0xE0, 0xB0) : today < 0 ? Rgb(0xFF, 0x9F, 0xB0) : (Brush)FindResource("SubBrush");
            TodayChip.Background = today > 0 ? new SolidColorBrush(Color.FromArgb(0x26, 0x6F, 0xE0, 0xB0)) : today < 0 ? new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0x9F, 0xB0)) : new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
            double within = nextName == null ? 1 : next > from ? Math.Clamp((points - from) / (double)(next - from), 0, 1) : 0;
            double ratio = nextName == null ? 1 : ((level - 1) + within) / (LevelNames.Length - 1);
            bool grew = shownLevel != 0 && level > shownLevel;
            shownLevel = level;
            if (Math.Abs(ratio - bondRatio) > 0.0001 || grew) { bondRatio = ratio; PlaceBond(true); }
            StyleNodes(level);
            SetBondIcon(level, true);
        }
        if (s.TryGetProperty("focus", out var f) && f.ValueKind == JsonValueKind.Object)
        {
            focusPhase = f.TryGetProperty("phase", out var p) ? p.GetString() ?? "none" : "none";
            int left = f.TryGetProperty("seconds_left", out var l) && l.TryGetInt32(out var x) ? x : 0;
            focusTotal = f.TryGetProperty("seconds_total", out var t) && t.TryGetInt32(out var y) && y > 0 ? y : 25 * 60;
            int rounds = f.TryGetProperty("rounds_today", out var r) && r.TryGetInt32(out var z) ? z : 0;
            var ends = DateTime.UtcNow.AddSeconds(left);
            if (focusPhase == "none" || Math.Abs((ends - focusEnds).TotalSeconds) > 1.5) focusEnds = ends; // 小于 1.5 秒的差不追，免得数字来回跳
            FocusTitle.Text = focusPhase switch { "work" => "专注中", "break" => "休息一下", _ => "专注陪伴" };
            FocusHint.Text = focusPhase switch
            {
                "work" => "她戴着眼镜安静陪你" + (rounds > 0 ? $" · 今天第 {rounds + 1} 轮" : ""),
                "break" => "起来动一动，5 分钟后叫你",
                _ => rounds > 0 ? $"今天专注了 {rounds} 轮，再来一轮？" : "她戴上眼镜陪你认真 25 分钟",
            };
            FocusButton.Content = focusPhase == "none" ? (rounds > 0 ? "再来一轮" : "开始") : "结束";
            FocusButton.Tag = focusPhase == "none" ? null : "stop";
            DrawFocus();
        }
    }

    // 左边的图标：等级变了才重画，升级时淡入一下
    private int iconLevel;
    private void SetBondIcon(int level, bool fade)
    {
        if (level == iconLevel) return;
        bool first = iconLevel == 0;
        iconLevel = level;
        BondIconHost.Child = BondIcon.Build(level);
        if (fade && !first) BondIconHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(500)));
    }

    // 羁绊条：宽度跟着整条（五个阶段）走了多少，变化时滑过去；按整格条纹取宽，有一点也至少亮一格
    private void PlaceBond(bool animate)
    {
        double inner = BondInner.ActualWidth;
        double width = inner * bondRatio;
        if (width > 0) width = Math.Min(inner, Math.Max(StripePitch, Math.Round(width / StripePitch) * StripePitch));
        if (!animate || inner <= 0) { BondFill.BeginAnimation(WidthProperty, null); BondFill.Width = width; return; }
        BondFill.BeginAnimation(WidthProperty, new DoubleAnimation(width, TimeSpan.FromMilliseconds(600)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    // 节点摆在条的下面，第 i 个在整条的 i/4 处；名字在节点下面居中，头尾的不超出卡片
    private void LayoutNodes()
    {
        double w = BondNodes.ActualWidth;
        if (w <= 0) return;
        double span = w - 2 * BondInset;
        for (int i = 0; i < bondNodes.Count; i++)
        {
            double x = BondInset + span * i / (bondNodes.Count - 1);
            var (marker, _, _, label) = bondNodes[i];
            Canvas.SetLeft(marker, x - marker.Width / 2); Canvas.SetTop(marker, 9 - marker.Height / 2);
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, Math.Clamp(x - label.DesiredSize.Width / 2, 0, Math.Max(0, w - label.DesiredSize.Width)));
            Canvas.SetTop(label, 19);
        }
    }

    // 到过的节点实心点亮；当前这一级再加一圈外环、名字最亮；下一级的轮廓亮一点；更远的都暗着
    private void StyleNodes(int level)
    {
        int current = Math.Clamp(level, 1, bondNodes.Count) - 1;
        for (int i = 0; i < bondNodes.Count; i++)
        {
            var (_, halo, dot, label) = bondNodes[i];
            if (i < current) { dot.Fill = Rgb(0x7C, 0xC4, 0xFF); dot.Stroke = Rgb(0x7C, 0xC4, 0xFF); }
            else if (i == current) { dot.Fill = Rgb(0xD6, 0xEE, 0xFF); dot.Stroke = Rgb(0xD6, 0xEE, 0xFF); }
            else { dot.Fill = Rgb(0x0B, 0x16, 0x40); dot.Stroke = i == current + 1 ? Rgb(0x7C, 0xC4, 0xFF) : Rgb(0x3A, 0x4F, 0x8C); }
            halo.Opacity = i == current ? 0.9 : 0;
            label.Foreground = i == current ? (Brush)FindResource("TextBrush") : i == current + 1 ? (Brush)FindResource("SubBrush") : Rgb(0x5A, 0x6A, 0x9C);
            label.FontWeight = i == current ? FontWeights.SemiBold : FontWeights.Normal;
        }
        LayoutNodes();
    }

    // 圆环：专注是蓝色、休息是绿色，走掉的部分缩短；中间写剩下的时间
    private void DrawFocus()
    {
        double left = focusPhase == "none" ? focusTotal : Math.Max(0, (focusEnds - DateTime.UtcNow).TotalSeconds);
        double ratio = focusPhase == "none" ? 1 : Math.Clamp(left / focusTotal, 0, 1);
        var color = focusPhase == "break" ? Color.FromRgb(0x6F, 0xE0, 0xB0) : Color.FromRgb(0x7C, 0xC4, 0xFF);
        FocusArc.Stroke = new SolidColorBrush(focusPhase == "none" ? Color.FromArgb(0x88, 0x7C, 0xC4, 0xFF) : color);
        FocusGlow.Color = color;
        FocusGlow.Opacity = focusPhase == "none" ? 0.15 : 0.6;
        FocusArc.Data = Arc(25, 23, ratio);
        int seconds = (int)Math.Ceiling(left);
        FocusTime.Text = focusPhase == "none" ? "25′" : $"{seconds / 60:00}:{seconds % 60:00}";
        FocusTime.FontSize = focusPhase == "none" ? 14 : 12;
    }

    // 从 12 点方向顺时针画 ratio 那么长的一段圆弧
    private static Geometry Arc(double center, double radius, double ratio)
    {
        if (ratio <= 0.0001) return Geometry.Empty;
        if (ratio >= 0.9999) return new EllipseGeometry(new Point(center, center), radius, radius);
        double angle = ratio * 2 * Math.PI;
        var start = new Point(center, center - radius);
        var end = new Point(center + radius * Math.Sin(angle), center - radius * Math.Cos(angle));
        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments.Add(new ArcSegment(end, new Size(radius, radius), 0, ratio > 0.5, SweepDirection.Clockwise, true));
        return new PathGeometry([figure]);
    }
}
