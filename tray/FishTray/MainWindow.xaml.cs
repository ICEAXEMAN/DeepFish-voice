using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace FishTray;

public partial class MainWindow : Window
{
    private static readonly string[] ShapeIds = ["small", "chibi", "full"];
    private static readonly string[] ShapeNames = ["小只", "Q 版", "全身"];
    private static readonly int[] FpsValues = [30, 60, 90, 120];
    private static readonly int[] StayValues = [0, 5, 10, 20, 60, -1];
    private static readonly string[] StayNames = ["自动", "5 秒", "10 秒", "20 秒", "60 秒", "常驻"];
    private static readonly double[] QualityValues = [0.25, 0.5, 1];
    private static readonly string[] QualityNames = ["流畅", "标准", "高清"];
    private static readonly string[] VoiceNames = ["青春、活力", "冷静、沉稳"];
    private static readonly string[] VoiceIds = ["second", "calm"];

    private bool loading;
    private bool moved;
    private bool dressBuilt;
    private Seg shapeSeg = null!, fpsSeg = null!, staySeg = null!, qualitySeg = null!, voiceSeg = null!;
    private readonly Dictionary<string, ToggleButton> chips = new();
    private readonly Dictionary<string, object> pending = new();
    private readonly Dictionary<string, DateTime> touched = new();
    private readonly System.Windows.Threading.DispatcherTimer flush = new() { Interval = TimeSpan.FromMilliseconds(350) };

    // 表情 / 动作 / 小剧场：每一块是一个可展开收起的区域，里面按小类摆着按钮
    private sealed class GroupView { public Group Group = null!; public StackPanel Box = null!; public List<(Item Item, FrameworkElement Chip)> Chips = []; }
    private sealed class SectionView { public Section Section = null!; public ToggleButton Head = null!; public StackPanel Body = null!; public List<GroupView> Groups = []; }
    private readonly List<SectionView> sections = [];
    private readonly Dictionary<string, Item> items = new();
    // 开关式的按钮（脸红、加载……）：和桌宠 get_state 里的 overlays / raw 对着显示开没开
    private readonly List<(Item Item, ToggleButton Button, bool Fav)> toggles = [];
    private readonly Dictionary<string, FrameworkElement> mainChips = new();
    private string skitSignature = "";
    private readonly Ui ui;
    private readonly System.Windows.Threading.DispatcherTimer toastTimer = new() { Interval = TimeSpan.FromSeconds(2.6) };

    internal bool? Hidden { get; private set; }

    private sealed record Seg(RadioButton[] Buttons);

    // 面板自己记住的：哪几块展开着、常用的按钮、有没有固定在最前面、“更多设置”开没开（ui.json，放在控制台文件夹里）
    private sealed class Ui
    {
        public List<string> Open { get; set; } = [];
        public List<string> Favs { get; set; } = [];
        public bool Pin { get; set; }
        public bool More { get; set; }

        private static string File_ => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "ui.json"));
        internal static Ui Load()
        {
            try { if (File.Exists(File_) && JsonSerializer.Deserialize<Ui>(File.ReadAllText(File_, Encoding.UTF8)) is { } loaded) return loaded; }
            catch { }
            return new Ui();
        }
        internal void Save()
        {
            try { File.WriteAllText(File_, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), new UTF8Encoding(false)); }
            catch { }
        }
    }

    public MainWindow()
    {
        InitializeComponent();
        Icon = Whale.WindowIcon();       // 任务栏 / Alt+Tab 用的图标
        Logo.Source = Whale.Source(136); // 抬头 logo（34 像素显示，高分屏留足像素）
        SourceInitialized += (_, _) => Taskbar.SetId(this, Taskbar.PanelId); // 在任务栏上单独占一个按钮
        DrawSky();
        ClipCorners();
        MaxHeight = Math.Min(900, SystemParameters.WorkArea.Height - 8);
        ui = Ui.Load();
        Topmost = ui.Pin; PinButton.IsChecked = ui.Pin;
        MoreToggle.IsChecked = ui.More; MorePanel.Visibility = ui.More ? Visibility.Visible : Visibility.Collapsed;

        shapeSeg = MakeSeg(ShapeHost, ShapeNames, "shape", i => Queue("shape", ShapeIds[i]));
        fpsSeg = MakeSeg(FpsHost, FpsValues.Select(v => v.ToString()).ToArray(), "fps", i => Queue("fps", FpsValues[i]));
        staySeg = MakeSeg(StayHost, StayNames, "stay", i => Queue("bubble_seconds", StayValues[i]));
        qualitySeg = MakeSeg(QualityHost, QualityNames, "quality", i => Queue("tex_scale", QualityValues[i]));
        voiceSeg = MakeSeg(VoiceHost, VoiceNames, "voice_pack", i => Queue("voice_pack", VoiceIds[i]));
        BuildSections();

        flush.Tick += async (_, _) => await Flush();
        toastTimer.Tick += (_, _) => { toastTimer.Stop(); Toast.Visibility = Visibility.Collapsed; };
        ScaleSlider.ValueChanged += (_, e) => { if (!loading) Queue("scale", Math.Round(e.NewValue, 1)); };
        BubbleSlider.ValueChanged += (_, e) => { if (!loading) Queue("bubble_scale", Math.Round(e.NewValue, 1)); };
        IdleSlider.ValueChanged += (_, e) => { if (!loading) Queue("idle_seconds", IdleStops[(int)Math.Round(e.NewValue)]); };
        // 音量（本机修，2026-11-12）：之前滑块只显示值、没有处理函数，拖了也不发出去，所以调不动。
        // 初值要在挂处理函数之前给，不然一开面板就把音量推成 80% 了。
        VolumeSlider.Value = 80;
        VolumeSlider.ValueChanged += (_, e) => { if (!loading) Queue("volume", Math.Round(e.NewValue / 100.0, 2)); };
        SetupSlider(ScaleSlider, ScaleMarks, ScaleValue, v => $"{v:0.0}×", (0.5, "0.5×"), (1, "1.0×"), (1.5, "1.5×"), (2, "2.0×"));
        SetupSlider(BubbleSlider, BubbleMarks, BubbleValue, v => $"{v:0.0}×", (0.5, "0.5×"), (1, "1.0×"), (1.5, "1.5×"), (2, "2.0×"));
        SetupSlider(VolumeSlider, VolumeMarks, VolumeValue, v => $"{Math.Round(v)}%", (0, "0"), (25, "25"), (50, "50"), (75, "75"), (100, "100"));
        SetupSlider(IdleSlider, IdleMarks, IdleValue, v => SecondsText(IdleStops[(int)Math.Round(v)]),
            (0, "15秒"), (Array.IndexOf(IdleStops, 30), "30秒"), (Array.IndexOf(IdleStops, 60), "1分"), (Array.IndexOf(IdleStops, 120), "2分"), (Array.IndexOf(IdleStops, 300), "5分"), (IdleStops.Length - 1, "10分"));
        Wire(SwMusicDance, "music_dance"); Wire(SwFollow, "follow_mouse"); Wire(SwFull, "hide_on_fullscreen"); Wire(SwStartHidden, "start_hidden"); Wire(SwKeepOn, "keep_on_screen"); Wire(SwCoaxSleep, "coax_sleep"); Wire(SwCoaxLunch, "coax_lunch"); Wire(SwTts, "tts_enabled"); Wire(SwTtsAuto, "tts_autostart");
        Wire(SwContextChat, "context_chat"); // 桌面实况总开关（本机加，2026-11-12）
        ContextSettingsButton.Click += (_, _) => OpenContextSettings();
        Wire(SwVoice, "voice_enabled");
        SwVoice.Click += (_, _) => ApplyVoiceVisibility();
        ApplyVoiceVisibility();

        ShowSwitch.Click += async (_, _) => await SetShown(ShowSwitch.IsChecked == true);
        CloseButton.Click += (_, _) => Hide();
        PinButton.Click += (_, _) => { ui.Pin = PinButton.IsChecked == true; Topmost = ui.Pin; ui.Save(); Notice(ui.Pin ? "已固定在最前面" : "取消固定"); };
        MoreToggle.Click += (_, _) => { ui.More = MoreToggle.IsChecked == true; MorePanel.Visibility = ui.More ? Visibility.Visible : Visibility.Collapsed; ui.Save(); };
        Header.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) { moved = true; try { DragMove(); } catch { } } };
        KeyDown += OnKeyDown;
        SayButton.Click += async (_, _) => await SaySomething();
        SayBox.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await SaySomething(); } };
        SayBox.TextChanged += (_, _) => SayHint.Visibility = SayBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ResetButton.Click += async (_, _) => await ResetAll();
        UpdateButton.Click += async (_, _) => await CheckUpdate();
        SearchBox.TextChanged += (_, _) => ApplyFilter();
        SearchClear.Click += (_, _) => { SearchBox.Clear(); SearchBox.Focus(); };
        SizeChanged += (_, _) => { if (!moved && IsVisible) Anchor(); else if (IsVisible) KeepOnScreen(); };
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (SearchBox.Text.Length > 0) { SearchBox.Clear(); e.Handled = true; return; }
            Hide();
        }
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
    }

    // 自检（--selftest）：像人一样点各个控件，再向桌宠要状态核对，结果写进日志
    internal async Task SelfTest(string logPath)
    {
        var log = new List<string>();
        async Task<JsonElement?> State() { await Task.Delay(1500); return await Pet.Call("get_state"); }
        void Click(ButtonBase b) => b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        void Check(string name, bool ok, string detail = "") => log.Add((ok ? "通过 " : "失败 ") + name + (detail.Length > 0 ? "  " + detail : ""));
        bool Has(JsonElement s, string key, string value) => s.TryGetProperty(key, out var list) && list.EnumerateArray().Any(e => e.GetString() == value);
        try
        {
            await Refresh();
            var s0 = await Pet.Call("get_state");
            if (s0 is not { } start) { log.Add("失败 连不上桌宠"); return; }
            bool wasHidden = start.GetProperty("hidden").GetBoolean();
            double scale0 = start.GetProperty("scale").GetDouble();
            string shape0 = start.GetProperty("shape").GetString()!;
            bool follow0 = start.GetProperty("follow_mouse").GetBoolean();
            Check("读到状态", true, $"hidden={wasHidden} scale={scale0} shape={shape0} 芯片={chips.Count} 分区={string.Join("/", sections.Select(v => v.Section.Title + v.Section.Count))}");

            ScaleSlider.Value = scale0 == 1.2 ? 1.3 : 1.2;
            var s = await State(); Check("大小滑块", s is { } a && Math.Abs(a.GetProperty("scale").GetDouble() - ScaleSlider.Value) < 0.01, $"期望 {ScaleSlider.Value} 实际 {s?.GetProperty("scale").GetDouble()}");
            ScaleSlider.Value = scale0; await State();

            int other = shape0 == "chibi" ? 0 : 1;
            var seg = shapeSeg.Buttons[other]; seg.IsChecked = true; Click(seg);
            s = await State(); Check("体型", s is { } b && b.GetProperty("shape").GetString() == ShapeIds[other], $"期望 {ShapeIds[other]} 实际 {s?.GetProperty("shape").GetString()}");
            seg = shapeSeg.Buttons[Array.IndexOf(ShapeIds, shape0)]; seg.IsChecked = true; Click(seg); await State();

            var tooth = chips["兔牙"]; tooth.IsChecked = true; Click(tooth);
            s = await State(); Check("穿兔牙", s is { } c && c.GetProperty("wearing").EnumerateArray().Any(e => e.GetString() == "兔牙"));
            tooth.IsChecked = false; Click(tooth);
            s = await State(); Check("脱兔牙", s is { } d && !d.GetProperty("wearing").EnumerateArray().Any(e => e.GetString() == "兔牙"));

            SwFollow.IsChecked = !follow0; Click(SwFollow);
            s = await State(); Check("视线跟鼠标开关", s is { } f && f.GetProperty("follow_mouse").GetBoolean() == !follow0);
            SwFollow.IsChecked = follow0; Click(SwFollow); await State();

            ShowSwitch.IsChecked = true; Click(ShowSwitch);
            await Task.Delay(1500); s = await State(); Check("出场", s is { } g && !g.GetProperty("hidden").GetBoolean());

            // 表情和动作：开关式的脸上表情、原版表情、动作词、恢复平常、搜索、收藏
            var blush = (ToggleButton)mainChips["Face:脸红"]; blush.IsChecked = true; Click(blush);
            s = await State(); Check("点开脸红", s is { } h1 && Has(h1, "overlays", "脸红"));
            Check("脸红按钮保持高亮", blush.IsChecked == true);
            blush.IsChecked = false; Click(blush);
            s = await State(); Check("再点关掉脸红", s is { } h2 && !Has(h2, "overlays", "脸红"));
            var load = (ToggleButton)mainChips["RawExp:加载"]; load.IsChecked = true; Click(load);
            s = await State(); Check("原版表情：加载", s is { } h3 && Has(h3, "raw", "加载"));
            ((Button)mainChips["Cue:歪头"]).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            ((Button)mainChips["Idle:哼歌"]).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Task.Delay(500);
            Click(ResetButton);
            s = await State(); Check("恢复平常", s is { } h4 && !Has(h4, "raw", "加载") && !Has(h4, "overlays", "脸红") && !load.IsChecked.GetValueOrDefault());
            SearchBox.Text = "脸红"; await Task.Delay(100);
            Check("搜索只剩匹配", mainChips["Face:脸红"].IsVisible && !mainChips["Cue:歪头"].IsVisible && !SettingsArea.IsVisible);
            SearchBox.Text = ""; await Task.Delay(100);
            Check("清空搜索恢复", SettingsArea.IsVisible);
            var favBefore = ui.Favs.ToList();
            ToggleFavorite(items["Cue:微笑"]); bool added = ui.Favs.Contains("Cue:微笑") && FavCard.IsVisible;
            ToggleFavorite(items["Cue:微笑"]); bool removed = !ui.Favs.Contains("Cue:微笑");
            Check("收藏与取消收藏", added && removed);
            ui.Favs = favBefore; RebuildFavs(); ui.Save();

            ShowSwitch.IsChecked = false; Click(ShowSwitch);
            await Task.Delay(4500); s = await State(); Check("收起", s is { } h && h.GetProperty("hidden").GetBoolean());
            if (!wasHidden) { ShowSwitch.IsChecked = true; Click(ShowSwitch); await Task.Delay(1500); }
        }
        catch (Exception error) { log.Add("异常 " + error); }
        finally { File.WriteAllLines(logPath, log, new UTF8Encoding(false)); }
    }

    // 把面板渲染成一张 PNG（--shot 自检用）。more：展开“更多设置”；open：三块全展开；scroll：滚到最底
    internal void Snapshot(string path, bool more, bool open = false, bool scroll = false, string search = "")
    {
        if (more) { MoreToggle.IsChecked = true; MorePanel.Visibility = Visibility.Visible; }
        if (open) foreach (var view in sections) { view.Head.IsChecked = true; view.Body.Visibility = Visibility.Visible; }
        if (search.Length > 0) SearchBox.Text = search;
        UpdateLayout();
        if (scroll) { Scroller.ScrollToBottom(); UpdateLayout(); }
        double w = Shell.ActualWidth, h = Shell.ActualHeight;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) dc.DrawRectangle(new VisualBrush(Shell), null, new Rect(0, 0, w, h));
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(w * 2), (int)Math.Ceiling(h * 2), 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    // ---- 打开 / 位置 ----

    internal void Open()
    {
        if (!IsVisible)
        {
            moved = false;
            Opacity = 0;
            if (SkitSignature() != skitSignature) BuildSections(); // skits.txt 改过：小剧场按钮跟着换
            Show();
            Anchor();
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        }
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; // 在任务栏上最小化过：点托盘/再次打开时还原
        Activate();
        _ = Refresh();
    }

    private void Anchor()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width + 8;
        Top = area.Bottom - ActualHeight + 8;
    }

    // 拖开过的窗口：展开后变高了，别让底边掉到屏幕外面
    private void KeepOnScreen()
    {
        var area = SystemParameters.WorkArea;
        if (Top + ActualHeight > area.Bottom + 8) Top = Math.Max(area.Top, area.Bottom + 8 - ActualHeight);
    }

    // ---- 星空 ----

    // 排布按平面构图来，不靠纯随机撒：
    //   · 三个层次：大量很小很淡的“尘”、少量中等的星、极少的焦点（带光晕的亮星和抬头那行的三个星芒）。越大的越少、彼此离得越远。
    //   · 用最小间距取点（泊松圆盘式：候选点离已放的点太近就重抽），不会扎堆，也不会排成整齐的格子。
    //   · 顶上是视觉重心：中等星集中在顶部 0–300，往下只剩淡淡的尘；三个星芒摆成不等边三角形，大中小错开，避开图标、名字和右上角按钮。
    // 动画都放进 twinkle 一个 Storyboard，面板收起时暂停、打开再接着闪。
    private readonly Storyboard twinkle = new();
    private readonly List<(Point At, double Gap)> placed = [];

    private void DrawSky()
    {
        var random = new Random(20261108);
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        void Animate(DependencyObject target, DependencyProperty property, double seconds, double begin, params (double At, double Value)[] frames)
        {
            var animation = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(seconds), BeginTime = TimeSpan.FromSeconds(begin), RepeatBehavior = RepeatBehavior.Forever };
            foreach (var (at, value) in frames) animation.KeyFrames.Add(new EasingDoubleKeyFrame(value, KeyTime.FromPercent(at), ease));
            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, new PropertyPath(property));
            twinkle.Children.Add(animation);
        }
        // 在 area 里找一个点：离已放的每个点都不小于两者间距里较大的那个；试 60 次找不到就放弃这一颗（宁缺勿挤）
        Point? Pick(Rect area, double gap, Func<double, double>? shapeY = null)
        {
            for (int attempt = 0; attempt < 60; attempt++)
            {
                double ry = random.NextDouble();
                var p = new Point(area.X + random.NextDouble() * area.Width, area.Y + (shapeY?.Invoke(ry) ?? ry) * area.Height);
                if (placed.All(q => (q.At - p).Length >= Math.Max(gap, q.Gap))) { placed.Add((p, gap)); return p; }
            }
            return null;
        }
        Brush Tint(bool cool) => new SolidColorBrush(cool ? Color.FromRgb(0x9C, 0xC6, 0xFF) : Color.FromRgb(0xEE, 0xF4, 0xFF));
        System.Windows.Media.Effects.DropShadowEffect Glow(double blur) => new() { Color = Color.FromRgb(0x7C, 0xC4, 0xFF), BlurRadius = blur, ShadowDepth = 0, Opacity = 1 };
        System.Windows.Shapes.Ellipse Dot(Canvas canvas, Point at, double size, Brush fill, double opacity)
        {
            var dot = new System.Windows.Shapes.Ellipse { Width = size, Height = size, Fill = fill, Opacity = opacity };
            Canvas.SetLeft(dot, at.X - size / 2); Canvas.SetTop(dot, at.Y - size / 2);
            canvas.Children.Add(dot);
            return dot;
        }

        // 先把要避开的地方占住：图标和名字、右上角两个按钮、搜索框（星芒和抬头层的星不压在字和按钮上）
        var keepOut = new[] { new Rect(10, 6, 140, 64), new Rect(292, 4, 80, 50), new Rect(26, 76, 330, 48) };

        // 焦点 1：三个星芒。最大最亮的那颗点在“大肥鱼”的“鱼”字右上角，像给名字点了一颗星，出现得最少（14 秒才亮一次）；
        // 另外三颗在名字右边的空白里：小、中，再加一颗最小的点在上方（原来最大那颗的位置），和“鱼”字那颗错落成一组
        var spots = new[] { (new Point(127, 15), 13.0), (new Point(166, 56), 7.5), (new Point(266, 48), 9.5), (new Point(204, 22), 6.0) };
        foreach (var (at, _) in spots) placed.Add((at, 34));
        for (int i = 0; i < spots.Length; i++)
        {
            var (at, size) = spots[i];
            var rotate = new RotateTransform(0);
            var scale = new ScaleTransform(0.2, 0.2);
            var sparkle = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M 0,-1 Q 0.2,-0.2 1,0 Q 0.2,0.2 0,1 Q -0.2,0.2 -1,0 Q -0.2,-0.2 0,-1 Z"),
                Fill = new SolidColorBrush(Color.FromRgb(0xF2, 0xF8, 0xFF)), Stretch = Stretch.Fill,
                Width = size * 2, Height = size * 2, Opacity = 0, Effect = Glow(10),
                RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new TransformGroup { Children = { scale, rotate } },
            };
            Canvas.SetLeft(sparkle, at.X - size); Canvas.SetTop(sparkle, at.Y - size);
            TopSky.Children.Add(sparkle);
            // 亮起来、放大、转 45°，再隐去，剩下的时间歇着。“鱼”字上那颗 14 秒一次、亮约 2 秒；另外三颗 6–8 秒一次。各自错开着来。
            // 关键帧按秒算好再折成百分比，周期变长时亮的那一下不跟着拉长
            double period = i == 0 ? 14 : i == 3 ? 7.3 : 6 + i * 0.9, begin = i == 0 ? 3.0 : i == 3 ? 1.4 : 0.8 + i * 2.3;
            double rise = 1.0 / period, fade = 2.0 / period;
            Animate(sparkle, OpacityProperty, period, begin, (0, 0), (rise, 1), (fade, 0), (1, 0));
            Animate(scale, ScaleTransform.ScaleXProperty, period, begin, (0, 0.2), (rise, 1), (fade, 0.3), (1, 0.2));
            Animate(scale, ScaleTransform.ScaleYProperty, period, begin, (0, 0.2), (rise, 1), (fade, 0.3), (1, 0.2));
            Animate(rotate, RotateTransform.AngleProperty, period, begin, (0, 0), (fade, 45), (1, 45));
        }

        // 焦点 2：四颗带光晕的亮星，整面板只有这几颗，彼此至少隔 150
        for (int i = 0; i < 4; i++)
        {
            if (Pick(new Rect(20, i == 0 ? 140 : 200, 332, i == 0 ? 160 : 640), 150) is not { } at) continue;
            var star = Dot(Sky, at, 2.4, Tint(false), 0.9);
            star.Effect = Glow(9);
            Animate(star, OpacityProperty, 4.5 + random.NextDouble() * 2, random.NextDouble() * 4, (0, 0.9), (0.5, 0.35), (1, 0.9));
        }

        // 中等星：只在顶部 0–300，12 颗，彼此至少隔 58，闪得明显（0.12 ↔ 0.9）；落在抬头那行空白里的放到顶栏上层，免得被顶栏底色压暗
        for (int i = 0; i < 12; i++)
        {
            if (Pick(new Rect(14, 6, 344, 294), 58) is not { } at) continue;
            if (keepOut.Any(r => r.Contains(at))) { placed.RemoveAt(placed.Count - 1); continue; }
            double size = 1.5 + random.NextDouble() * 0.8;
            var star = Dot(at.Y < 70 ? TopSky : Sky, at, size, Tint(i % 3 == 0), 0.12);
            double peak = 0.7 + random.NextDouble() * 0.25;
            Animate(star, OpacityProperty, 2.6 + random.NextDouble() * 2.8, random.NextDouble() * 5, (0, 0.12), (0.5, peak), (1, 0.12));
        }

        // 尘：整面板 70 颗很小很淡的点，彼此至少隔 24，上面稍多一点（shapeY 把 y 往上压），三成会轻轻闪
        for (int i = 0; i < 70; i++)
        {
            if (Pick(new Rect(8, 4, 356, 876), 24, y => Math.Pow(y, 1.25)) is not { } at) continue;
            double size = 0.6 + random.NextDouble() * 0.6, opacity = 0.15 + random.NextDouble() * 0.35;
            var dust = Dot(Sky, at, size, Tint(i % 4 == 0), opacity);
            if (random.NextDouble() < 0.3)
                Animate(dust, OpacityProperty, 3 + random.NextDouble() * 3, random.NextDouble() * 6, (0, opacity), (0.5, Math.Min(0.8, opacity + 0.3)), (1, opacity));
        }

        // 顶上的蓝光慢慢呼吸：8 秒一个来回
        Animate(TopGlow, OpacityProperty, 8, 0, (0, 0.6), (0.5, 1), (1, 0.6));

        Loaded += (_, _) => twinkle.Begin(this, true);
        IsVisibleChanged += (_, _) => { if (IsVisible) twinkle.Resume(this); else twinkle.Pause(this); };
    }

    // 内层按外框的圆角裁剪（外框 CornerRadius 18、边线 1，里面就是 17）
    private void ClipCorners()
    {
        void Clip() => Inner.Clip = new RectangleGeometry(new Rect(0, 0, Inner.ActualWidth, Inner.ActualHeight), 17, 17);
        Inner.SizeChanged += (_, _) => Clip();
    }

    // ---- 小部件 ----

    private Seg MakeSeg(Border host, string[] labels, string group, Action<int> picked)
    {
        var grid = new UniformGrid { Rows = 1, Margin = new Thickness(2) };
        var buttons = new List<RadioButton>();
        for (int i = 0; i < labels.Length; i++)
        {
            int index = i;
            var button = new RadioButton { Content = labels[i], GroupName = group, Style = (Style)FindResource("SegRadio") };
            button.Click += (_, _) => { if (!loading) { Touch(group); picked(index); } };
            buttons.Add(button); grid.Children.Add(button);
        }
        host.Child = grid;
        return new Seg(buttons.ToArray());
    }

    private static void SetSeg(Seg seg, int index) { for (int i = 0; i < seg.Buttons.Length; i++) seg.Buttons[i].IsChecked = i == index; }

    private void Wire(ToggleButton toggle, string key) => toggle.Click += (_, _) => { Touch(key); Queue(key, toggle.IsChecked == true); };

    // 语音总开关：关掉时实时语音、开机自动启动、音量调节器一起收起来；固定语音包留在原处。
    private void ApplyVoiceVisibility()
    {
        var on = SwVoice.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        TtsRow.Visibility = on;
        TtsAutoRow.Visibility = on;
        VolumePanel.Visibility = on;
    }

    private void Touch(string key) => touched[key] = DateTime.UtcNow;
    private bool Fresh(string key) => !touched.TryGetValue(key, out var at) || (DateTime.UtcNow - at).TotalSeconds > 4;

    // ---- 滑动条：刻度、数值气泡、整条按住拖 ----

    // 小动作间隔不按秒线性排（15–600 秒里常用的都挤在左边一小截），改成一档一档的常用值，短的档分得细、长的档分得粗
    private static readonly int[] IdleStops = [15, 20, 25, 30, 40, 50, 60, 75, 90, 120, 150, 180, 240, 300, 420, 600];
    private static int NearestStop(double seconds)
    {
        int best = 0;
        for (int i = 1; i < IdleStops.Length; i++) if (Math.Abs(IdleStops[i] - seconds) < Math.Abs(IdleStops[best] - seconds)) best = i;
        return best;
    }
    private static string SecondsText(int seconds) => seconds < 60 ? $"{seconds} 秒" : seconds % 60 == 0 ? $"{seconds / 60} 分钟" : $"{seconds / 60} 分 {seconds % 60} 秒";

    private const double ThumbRadius = 9; // 和 App.xaml 里 SliderDot 的半径一致：圆钮中心只走到两头各缩进 9 的地方

    private void SetupSlider(Slider slider, Grid marks, TextBlock readout, Func<double, string> text, params (double Value, string Label)[] points)
    {
        // 当前值写两处：标题行右边的数字，和拖动时圆钮上方的小气泡（模板里绑的 Slider.Tag）
        void Label() { var shown = text(slider.Value); slider.Tag = shown; readout.Text = shown; }
        Label();
        slider.ValueChanged += (_, _) => Label();

        // 刻度：一小竖线 + 数字，点一下直接跳到那一档。位置用 RenderTransform 平移（不用 Margin：Margin 会算进 DesiredSize，重排几次就越挪越偏）
        var labels = points.Select(point =>
        {
            var tick = new Border { Width = 1.5, Height = 4, CornerRadius = new CornerRadius(1), Background = (Brush)FindResource("LineBrush"),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, RenderTransform = new TranslateTransform() };
            var label = new TextBlock { Text = point.Label, FontSize = 11, Foreground = (Brush)FindResource("SubBrush"), Padding = new Thickness(3, 0, 3, 0),
                Background = Brushes.Transparent, Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 5, 0, 0), RenderTransform = new TranslateTransform() };
            label.MouseEnter += (_, _) => label.Foreground = (Brush)FindResource("TextBrush");
            label.MouseLeave += (_, _) => label.Foreground = (Brush)FindResource("SubBrush");
            label.MouseLeftButtonDown += (_, e) => { slider.Value = point.Value; e.Handled = true; };
            marks.Children.Add(tick);
            marks.Children.Add(label);
            return (tick, label, point.Value);
        }).ToArray();
        void Place()
        {
            double width = marks.ActualWidth;
            if (width <= 0) return;
            foreach (var (tick, label, value) in labels)
            {
                double center = ThumbRadius + (value - slider.Minimum) / (slider.Maximum - slider.Minimum) * (width - 2 * ThumbRadius);
                ((TranslateTransform)tick.RenderTransform).X = center - tick.Width / 2;
                label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double w = label.DesiredSize.Width; // 这里只有 Padding，没有横向 Margin，不会越算越大
                ((TranslateTransform)label.RenderTransform).X = Math.Clamp(center - w / 2, 0, Math.Max(0, width - w));
            }
        }
        marks.SizeChanged += (_, _) => Place();
        marks.Loaded += (_, _) => Place();

        // 整条都能按住拖：按下就跳到那里，接着拖不松手一路跟着走（原生 Slider 只有按在圆钮上才拖得动）
        void Follow(MouseEventArgs e)
        {
            if (slider.Template.FindName("PART_Track", slider) is not Track track || track.ActualWidth <= 2 * ThumbRadius) return;
            double ratio = Math.Clamp((e.GetPosition(track).X - ThumbRadius) / (track.ActualWidth - 2 * ThumbRadius), 0, 1);
            double raw = slider.Minimum + ratio * (slider.Maximum - slider.Minimum);
            double step = slider.TickFrequency > 0 ? slider.TickFrequency : 1;
            double snapped = Math.Round(slider.Minimum + Math.Round((raw - slider.Minimum) / step) * step, 6);
            slider.Value = Math.Clamp(snapped, slider.Minimum, slider.Maximum);
        }
        slider.PreviewMouseLeftButtonDown += (_, e) => { slider.CaptureMouse(); Follow(e); e.Handled = true; };
        slider.PreviewMouseMove += (_, e) => { if (slider.IsMouseCaptured) Follow(e); };
        slider.PreviewMouseLeftButtonUp += (_, e) => { if (slider.IsMouseCaptured) { slider.ReleaseMouseCapture(); e.Handled = true; } };
    }

    private void Queue(string key, object value)
    {
        Touch(key);
        pending[key] = value;
        flush.Stop(); flush.Start();
    }

    private async Task Flush()
    {
        flush.Stop();
        if (pending.Count == 0) return;
        var batch = new Dictionary<string, object>(pending);
        pending.Clear();
        await Pet.Call("update_settings", batch);
    }

    private async Task SetShown(bool shown)
    {
        Touch("show");
        ShowHint.Text = shown ? "她来了" : "收起中…";
        await Pet.Call(shown ? "show" : "hide");
    }

    private async Task SaySomething()
    {
        var text = SayBox.Text.Trim();
        if (text.Length == 0) return;
        SayBox.Clear();
        await Pet.Say(text);
    }

    // 底部的小提示：点了什么、有没有连上，几秒后自己消失
    private void Notice(string text)
    {
        Toast.Text = text; Toast.Visibility = Visibility.Visible;
        toastTimer.Stop(); toastTimer.Start();
    }

    // ---- 表情 / 动作 / 小剧场 ----

    private static string SkitSignature() => string.Join("|", ReadSkits());

    // 标题下一行写着“// 彩蛋”的小剧场不放按钮：只等她自己在对的时候演（生日、节日、深夜催睡这些）。
    private static string[] ReadSkits()
    {
        try
        {
            var file = Path.Combine(Pet.Root, "skits.txt");
            if (!File.Exists(file)) return [];
            var lines = File.ReadAllLines(file, Encoding.UTF8).Select(line => line.Trim()).ToArray();
            var names = new List<string>();
            for (int i = 0; i < lines.Length; i++)
            {
                if (!lines[i].StartsWith('#') || lines[i].Length < 2) continue;
                var name = lines[i].TrimStart('#').Trim();
                var at = name.IndexOf('@');
                if (at >= 0) name = name[..at].Trim();
                bool egg = i + 1 < lines.Length && lines[i + 1].StartsWith("//") && lines[i + 1].TrimStart('/').Trim().StartsWith("彩蛋");
                if (name.Length > 0 && !egg) names.Add(name);
            }
            return [.. names];
        }
        catch { }
        return [];
    }

    private void BuildSections()
    {
        var skits = ReadSkits();
        skitSignature = string.Join("|", skits);
        SectionsHost.Children.Clear(); sections.Clear(); items.Clear(); mainChips.Clear(); toggles.Clear();
        foreach (var section in Catalog.Build(skits))
        {
            var view = new SectionView { Section = section };
            bool open = ui.Open.Contains(section.Key);
            view.Head = new ToggleButton { Content = section.Title, Tag = section.Count + " 个", IsChecked = open, Style = (Style)FindResource("FoldRow"), Margin = new Thickness(0, 0, 0, 8) };
            view.Body = new StackPanel { Visibility = open ? Visibility.Visible : Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 4) };
            var card = new Border { Background = (Brush)FindResource("CardBrush"), BorderBrush = (Brush)FindResource("CardLineBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(16, 12, 16, 6), Margin = new Thickness(0, 0, 0, 8) };
            var inner = new StackPanel();
            inner.Children.Add(new TextBlock { Text = section.Blurb, Style = (Style)FindResource("Caption"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12), Opacity = .85 });
            foreach (var group in section.Groups)
            {
                var groupView = new GroupView { Group = group, Box = new StackPanel { Margin = new Thickness(0, 0, 0, 4) } };
                groupView.Box.Children.Add(new TextBlock { Text = group.Title + "  ·  " + group.Items.Length, Style = (Style)FindResource("Caption"), Margin = new Thickness(0, 0, 0, 8), FontWeight = FontWeights.SemiBold });
                var wrap = new WrapPanel();
                foreach (var item in group.Items)
                {
                    items[item.Key] = item;
                    var chip = MakeChip(item, false);
                    mainChips[item.Key] = chip; groupView.Chips.Add((item, chip)); wrap.Children.Add(chip);
                }
                groupView.Box.Children.Add(wrap);
                if (group.Hint.Length > 0) groupView.Box.Children.Add(new TextBlock { Text = group.Hint, Style = (Style)FindResource("Caption"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, -2, 0, 8), Opacity = .7, FontSize = 11.5 });
                inner.Children.Add(groupView.Box);
                view.Groups.Add(groupView);
            }
            card.Child = inner; view.Body.Children.Add(card);
            view.Head.Click += (_, _) => Fold(view);
            SectionsHost.Children.Add(view.Head); SectionsHost.Children.Add(view.Body);
            sections.Add(view);
        }
        RebuildFavs();
        ApplyFilter();
    }

    private void Fold(SectionView view)
    {
        if (SearchBox.Text.Trim().Length > 0) { view.Head.IsChecked = true; return; } // 搜索时一律展开着，点标题不改
        bool open = view.Head.IsChecked == true;
        if (open) { if (!ui.Open.Contains(view.Section.Key)) ui.Open.Add(view.Section.Key); } else ui.Open.Remove(view.Section.Key);
        ui.Save();
        view.Body.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (open) view.Body.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
    }

    // 一个按钮。开关式的（脸红、加载）是可选中的胶囊，高亮表示现在开着；其他是点一下就做。右键 = 加入 / 移出常用。
    private FrameworkElement MakeChip(Item item, bool fav)
    {
        string tip = (item.Tip.Length > 0 ? item.Tip + "\n" : "") + "右键：" + (ui.Favs.Contains(item.Key) ? "移出常用" : "加入常用");
        FrameworkElement chip;
        if (item.Kind is Kind.Face or Kind.RawExp)
        {
            var toggle = new ToggleButton { Content = item.Label, Style = (Style)FindResource("Chip") };
            toggle.Click += async (_, _) => await Run(item, toggle);
            toggles.Add((item, toggle, fav));
            chip = toggle;
        }
        else
        {
            var button = new Button { Content = item.Label, Style = (Style)FindResource("ChipButton") };
            button.Click += async (_, _) => await Run(item, button);
            chip = button;
        }
        chip.ToolTip = new TextBlock { Text = tip, TextWrapping = TextWrapping.Wrap };
        ToolTipService.SetInitialShowDelay(chip, 450);
        chip.PreviewMouseRightButtonUp += (_, e) => { e.Handled = true; ToggleFavorite(item); };
        return chip;
    }

    private async Task Run(Item item, FrameworkElement chip)
    {
        JsonElement? result = null;
        bool on = (chip as ToggleButton)?.IsChecked == true;
        switch (item.Kind)
        {
            case Kind.Cue: Notice("▶ " + item.Label); result = await Pet.Panel(new { op = "cue", cue = item.Arg }); break;
            case Kind.Pose: Notice("▶ " + item.Label); result = await Pet.Panel(new { op = "pose", cue = item.Arg }); break;
            case Kind.Face:
                Touch("tg:" + item.State); Notice((on ? "开：" : "关：") + item.Label);
                result = await Pet.Panel(new { op = "cue", cue = (on ? "" : "-") + item.Arg }); break;
            case Kind.RawExp:
                Touch("tg:" + item.State); Notice((on ? "开：" : "关：") + item.Label);
                result = await Pet.Panel(new { op = "exp", name = item.Arg, on }); break;
            case Kind.Idle: Notice("▶ " + item.Label); result = await Pet.Panel(new { op = "idle", name = item.Arg }); break;
            case Kind.RawMotion: Notice("▶ " + item.Label + "（原版动作，约 13 秒）"); result = await Pet.Panel(new { op = "motion", name = item.Arg }); break;
            case Kind.Skit:
                Notice("▶ 小剧场：" + item.Label);
                result = await Pet.Call("coax", new { stage = 6, text = item.Arg }); break;
            case Kind.SkitRandom:
                Notice("▶ 随机来一个小剧场");
                result = await Pet.Call("coax", new { stage = 6 }); break;
            case Kind.Demo: Notice("▶ 依次演全部待机小动作（约 45 秒）"); result = await Pet.Call("coax", new { stage = 5 }); break;
        }
        if (result is null)
        {
            Notice(Pet.Running ? "没做成，再点一下试试" : "桌宠没有运行");
            if (chip is ToggleButton toggle) toggle.IsChecked = !on;
            return;
        }
        if (item.Kind is Kind.Cue or Kind.Face or Kind.RawExp) SyncToggles(result.Value, force: true);
    }

    private async Task ResetAll()
    {
        var result = await Pet.Panel(new { op = "reset" });
        if (result is { } state) { SyncToggles(state, force: true); Notice("已恢复平常"); }
        else Notice(Pet.Running ? "没做成，再点一下试试" : "桌宠没有运行");
    }

    // 检查更新（用户 2026-11-12 要的）：按一下就去问 GitHub，结果直接写在按钮下面，不占用设置里的开关。
    private async Task CheckUpdate()
    {
        UpdateButton.IsEnabled = false;
        UpdateNote.Visibility = Visibility.Visible;
        UpdateNote.Text = "正在问 GitHub…";
        var result = await Pet.Call("check_update", null, 25);
        UpdateButton.IsEnabled = true;
        if (result is not { } r)
        {
            UpdateNote.Text = Pet.Running ? "没查成：桌宠那边没回话，再点一下试试" : "桌宠没有运行，先让她出来";
            return;
        }
        string Text(string key) => r.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        var state = Text("state");
        var error = Text("error");
        var behind = r.TryGetProperty("behind", out var b) && b.TryGetInt32(out var n) ? n : 0;
        string? newest = r.TryGetProperty("changes", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(e => e.GetString() ?? "").FirstOrDefault(line => line.Length > 0) : null;
        UpdateNote.Text = state switch
        {
            "latest" => "已是最新版本",
            "behind" => $"有新版本：落后 {behind} 个提交" + (newest is { Length: > 0 } ? "\n最新一条：" + newest : "") + "\n跟 Agent 说一句「更新大肥鱼插件」就行",
            "nogit" => "查不了：这份不是用 git 拉下来的副本",
            "failed" => "检查失败：" + (error.Length > 0 ? error : "原因不明"),
            _ => "检查失败：原因不明",
        };
    }

    // 桌宠说脸上现在开着哪些，按钮的高亮跟着变（刚点过的 4 秒内不被覆盖）
    private void SyncToggles(JsonElement state, bool force = false)
    {
        string[] List(string key) => state.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Select(e => e.GetString() ?? "").ToArray() : [];
        var overlays = List("overlays"); var raw = List("raw");
        foreach (var (item, button, _) in toggles)
        {
            if (!force && !Fresh("tg:" + item.State)) continue;
            button.IsChecked = (item.Kind == Kind.Face ? overlays : raw).Contains(item.State);
        }
    }

    // ---- 常用（右键收藏） ----

    private void ToggleFavorite(Item item)
    {
        if (!ui.Favs.Remove(item.Key)) ui.Favs.Add(item.Key);
        ui.Save();
        RebuildFavs();
        Notice(ui.Favs.Contains(item.Key) ? "已加入常用：" + item.Label : "已移出常用：" + item.Label);
    }

    private void RebuildFavs()
    {
        toggles.RemoveAll(t => t.Fav);
        FavPanel.Children.Clear();
        foreach (var key in ui.Favs)
            if (items.TryGetValue(key, out var item)) FavPanel.Children.Add(MakeChip(item, true));
        // 收藏夹里的开关按钮，高亮和主按钮一致
        foreach (var (item, button, fav) in toggles.Where(t => t.Fav))
            if (mainChips.TryGetValue(item.Key, out var main) && main is ToggleButton original) button.IsChecked = original.IsChecked;
        // 主区按钮的右键提示里写“加入/移出”，跟着变
        foreach (var (key, chip) in mainChips)
            if (items.TryGetValue(key, out var item)) chip.ToolTip = new TextBlock { Text = (item.Tip.Length > 0 ? item.Tip + "\n" : "") + "右键：" + (ui.Favs.Contains(key) ? "移出常用" : "加入常用"), TextWrapping = TextWrapping.Wrap };
        ApplyFilter();
    }

    // ---- 搜索 ----

    private void ApplyFilter()
    {
        if (!IsInitialized) return;
        var query = SearchBox.Text.Trim();
        bool searching = query.Length > 0;
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchClear.Visibility = SearchBox.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        SettingsArea.Visibility = MoreArea.Visibility = searching ? Visibility.Collapsed : Visibility.Visible;
        FavCard.Visibility = !searching && ui.Favs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        FavHint.Visibility = !searching && ui.Favs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        bool any = false;
        foreach (var view in sections)
        {
            bool sectionMatch = false;
            foreach (var group in view.Groups)
            {
                bool groupTitle = searching && group.Group.Title.Contains(query, StringComparison.OrdinalIgnoreCase);
                bool groupMatch = false;
                foreach (var (item, chip) in group.Chips)
                {
                    bool match = !searching || groupTitle || item.Label.Contains(query, StringComparison.OrdinalIgnoreCase) || item.Arg.Contains(query, StringComparison.OrdinalIgnoreCase) || item.Tip.Contains(query, StringComparison.OrdinalIgnoreCase);
                    chip.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
                    groupMatch |= match;
                }
                group.Box.Visibility = groupMatch ? Visibility.Visible : Visibility.Collapsed;
                sectionMatch |= groupMatch;
            }
            view.Head.Visibility = !searching || sectionMatch ? Visibility.Visible : Visibility.Collapsed;
            view.Head.IsChecked = searching ? sectionMatch : ui.Open.Contains(view.Section.Key); // 搜索时箭头也朝下，退出搜索回到原来展开的样子
            view.Body.Visibility = view.Head.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            any |= sectionMatch;
        }
        EmptyHint.Visibility = searching && !any ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BuildDress(IEnumerable<string> labels)
    {
        DressPanel.Children.Clear(); chips.Clear();
        foreach (var label in labels)
        {
            var chip = new ToggleButton { Content = label, Style = (Style)FindResource("Chip") };
            var name = label;
            chip.Click += async (_, _) =>
            {
                Touch("dress:" + name);
                bool on = chip.IsChecked == true;
                await Pet.Call("dress", on ? new { wear = new[] { name } } : (object)new { remove = new[] { name } });
            };
            chips[label] = chip; DressPanel.Children.Add(chip);
        }
        dressBuilt = true;
    }

    // ---- 和桌宠对一下状态 ----

    internal async Task Refresh()
    {
        // 不靠 Process.GetProcessesByName 判断她在不在：这台机器上它有时看不到桌宠进程，直接问她本人（本机修，2026-11-11）
        if (await Pet.Call("get_state") is not { } state) { SetStatus(null, "连不上桌宠"); return; }
        Apply(state);
    }

    private void SetStatus(bool? hidden, string text)
    {
        Hidden = hidden is null ? null : hidden;
        StatusText.Text = text;
        StatusDot.Fill = new SolidColorBrush(hidden is null ? Color.FromRgb(0x5A, 0x6A, 0x99) : hidden == true ? Color.FromRgb(0x7C, 0xC4, 0xFF) : Color.FromRgb(0x6F, 0xE0, 0xB0));
        if (hidden is null) { ShowSwitch.IsEnabled = false; }
    }

    private void Apply(JsonElement s)
    {
        bool B(string k) => s.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
        double D(string k, double d = 0) => s.TryGetProperty(k, out var v) && v.TryGetDouble(out var x) ? x : d;
        string T(string k) => s.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        string[] A(string k) => s.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Select(e => e.GetString() ?? "").ToArray() : [];

        bool hidden = B("hidden");
        SetStatus(hidden, hidden ? "收起着，在后台待命" : "在桌面上");
        loading = true;
        try
        {
            ShowSwitch.IsEnabled = true;
            if (Fresh("show")) { ShowSwitch.IsChecked = !hidden; ShowHint.Text = hidden ? "打开开关，她就出来" : "关掉就收起，她在后台待命"; }
            if (Fresh("scale")) ScaleSlider.Value = Math.Clamp(D("scale", 1.5), 0.5, 2); // 兜底默认跟 Settings.Scale 一致（1.5，2026-11-13）
            if (Fresh("shape")) SetSeg(shapeSeg, Math.Max(0, Array.IndexOf(ShapeIds, T("shape"))));
            if (Fresh("fps")) SetSeg(fpsSeg, Math.Max(0, Array.IndexOf(FpsValues, (int)D("fps", 60))));
            if (Fresh("idle_seconds")) IdleSlider.Value = NearestStop(D("idle_seconds", 40));
            if (Fresh("bubble_scale")) BubbleSlider.Value = Math.Clamp(D("bubble_scale", 1), 0.5, 2);
            if (Fresh("volume")) VolumeSlider.Value = Math.Clamp(D("volume", 0.8) * 100, 0, 100);
            if (Fresh("stay")) SetSeg(staySeg, Array.IndexOf(StayValues, (int)D("bubble_seconds")));
            if (Fresh("quality")) SetSeg(qualitySeg, Math.Max(0, Array.IndexOf(QualityValues, D("tex_scale", 1))));
            if (Fresh("music_dance")) SwMusicDance.IsChecked = B("music_dance");
            if (Fresh("context_chat")) SwContextChat.IsChecked = s.TryGetProperty("context_chat", out var cc) ? cc.ValueKind != JsonValueKind.False : true;
            if (Fresh("follow_mouse")) SwFollow.IsChecked = B("follow_mouse");            if (Fresh("hide_on_fullscreen")) SwFull.IsChecked = B("hide_on_fullscreen");
            if (Fresh("start_hidden")) SwStartHidden.IsChecked = B("start_hidden");
            if (Fresh("keep_on_screen")) SwKeepOn.IsChecked = B("keep_on_screen");
            if (Fresh("coax_sleep")) SwCoaxSleep.IsChecked = B("coax_sleep");
            if (Fresh("coax_lunch")) SwCoaxLunch.IsChecked = B("coax_lunch");
            if (Fresh("tts_enabled")) SwTts.IsChecked = B("tts_enabled");
            if (Fresh("tts_autostart")) SwTtsAuto.IsChecked = B("tts_autostart");
            if (Fresh("voice_enabled")) { SwVoice.IsChecked = B("voice_enabled"); ApplyVoiceVisibility(); }
            if (Fresh("voice_pack")) SetSeg(voiceSeg, T("voice_pack") == "calm" ? 1 : 0);
            if (!dressBuilt) { var all = A("clothes").Concat(A("accessories")).ToArray(); if (all.Length > 0) BuildDress(all); }
            var wearing = A("wearing");
            foreach (var (label, chip) in chips) if (Fresh("dress:" + label)) chip.IsChecked = wearing.Contains(label);
            SyncToggles(s);
            ApplyBond(s);
        }
        finally { loading = false; }
    }

    // 「识别当前窗口并闲聊」的详细设置窗口（本机加，2026-11-12）：只开一个，重复点就提到前面来
    // 2026-11-13：窗口被真正关掉过（Alt+F4、或系统把它关掉）之后，WPF 不许再 Show，会报
    // 「关闭窗口后，无法设置可见性，也无法调用 Show…」。所以关掉就把引用清掉，下次重建。
    private ContextChatWindow? contextWindow;
    private void OpenContextSettings()
    {
        try
        {
            if (contextWindow is null || !contextWindow.IsLoaded)
            {
                contextWindow = new ContextChatWindow();
                contextWindow.Closed += (_, _) => contextWindow = null;
            }
            contextWindow.Open();
        }
        catch (Exception error) { Notice("设置窗口打不开：" + error.Message); }
    }
}

