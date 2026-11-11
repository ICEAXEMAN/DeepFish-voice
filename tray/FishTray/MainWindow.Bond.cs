using System;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace FishTray;

// 面板上的“陪伴”卡片（本机加，2026-11-08）：亲密度的心形进度条、番茄钟的圆环倒计时。数据来自 get_state 的 affection、focus。
public partial class MainWindow
{
    private static readonly string[] LevelNames = ["初识", "熟悉", "亲近", "黏人", "最喜欢"];
    private readonly System.Windows.Threading.DispatcherTimer focusClock = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private string focusPhase = "none";
    private DateTime focusEnds;
    private double focusTotal = 25 * 60;
    private double heartRatio;
    private int shownLevel;
    private bool bondWired;

    private void WireBond()
    {
        if (bondWired) return;
        bondWired = true;
        // 五个等级的小刻度
        for (int i = 0; i < LevelNames.Length; i++)
        {
            var dot = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = i == 0 ? HorizontalAlignment.Left : i == LevelNames.Length - 1 ? HorizontalAlignment.Right : HorizontalAlignment.Center };
            dot.Children.Add(new System.Windows.Shapes.Ellipse { Width = 5, Height = 5, Margin = new Thickness(0, 1, 4, 0), VerticalAlignment = VerticalAlignment.Center, Fill = (Brush)FindResource("LineBrush") });
            dot.Children.Add(new TextBlock { Text = LevelNames[i], FontSize = 10.5, Foreground = (Brush)FindResource("SubBrush") });
            LevelDots.Children.Add(dot);
        }
        ((FrameworkElement)HeartFill.Parent).SizeChanged += (_, _) => PlaceHeart(false);
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
        // 心轻轻地跳：1.6 秒一次，很小
        var beat = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(1.6), RepeatBehavior = RepeatBehavior.Forever };
        foreach (var (at, value) in new[] { (0.0, 1.0), (0.12, 1.14), (0.24, 1.0), (0.36, 1.09), (0.55, 1.0), (1.0, 1.0) })
            beat.KeyFrames.Add(new EasingDoubleKeyFrame(value, KeyTime.FromPercent(at), new SineEase { EasingMode = EasingMode.EaseInOut }));
        HeartBeat.BeginAnimation(ScaleTransform.ScaleXProperty, beat);
        HeartBeat.BeginAnimation(ScaleTransform.ScaleYProperty, beat);
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
            string todayText = today > 0 ? $"今天 +{today}" : today < 0 ? $"今天 {today}" : "今天还没变";
            LevelHint.Text = nextName == null ? (points >= 200 ? "满满的喜欢 ♥" : "已经是最喜欢啦 ♥") + " · " + todayText : $"再 {Math.Max(0, next - points)} 分到「{nextName}」 · {todayText}";
            HeartRow.ToolTip = "摸头、聊天、按时睡觉、陪她专注会加；戳裙子、连点她、熬夜过 1 点、好几天不来会减。\n到了「熟悉」以后，最多掉回「熟悉」。";
            double ratio = nextName == null ? 1 : next > from ? Math.Clamp((points - from) / (double)(next - from), 0, 1) : 0;
            bool grew = shownLevel != 0 && level > shownLevel;
            shownLevel = level;
            if (Math.Abs(ratio - heartRatio) > 0.0001 || grew) { heartRatio = ratio; PlaceHeart(true); }
            for (int i = 0; i < LevelDots.Children.Count; i++)
            {
                var dot = (StackPanel)LevelDots.Children[i];
                bool reached = i < level;
                ((System.Windows.Shapes.Ellipse)dot.Children[0]).Fill = reached ? new SolidColorBrush(Color.FromRgb(0xFF, 0x8F, 0xB8)) : (Brush)FindResource("LineBrush");
                ((TextBlock)dot.Children[1]).Foreground = i == level - 1 ? new SolidColorBrush(Color.FromRgb(0xFF, 0xC2, 0xD9)) : (Brush)FindResource("SubBrush");
            }
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

    // 心形进度条：宽度跟着这一级走了多少，变化时滑过去
    private void PlaceHeart(bool animate)
    {
        var host = (FrameworkElement)HeartFill.Parent;
        double width = host.ActualWidth * heartRatio;
        if (width > 0 && width < 8) width = 8; // 有一点也露个圆头
        if (!animate || host.ActualWidth <= 0) { HeartFill.BeginAnimation(WidthProperty, null); HeartFill.Width = width; return; }
        HeartFill.BeginAnimation(WidthProperty, new DoubleAnimation(width, TimeSpan.FromMilliseconds(600)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
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
