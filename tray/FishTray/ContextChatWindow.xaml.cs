using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace FishTray;

// 「识别当前窗口并闲聊」的子窗口（本机加，2026-11-12）：
// 直接改 DSH 那边 dafeiyu-context 插件的 settings.json（它每 4 秒自己重新读），
// 状态从插件写的 status.json 里读，「现在试一句」写一个 trigger.json 给插件接。
public partial class ContextChatWindow : Window
{
    private readonly DispatcherTimer saveTimer;
    private readonly DispatcherTimer statusTimer;
    private readonly Prefs prefs;
    private JsonObject? root;
    private bool loading;
    // 「现在试一句」的进度：点完按 12 秒倒数，插件真说了就停
    private DateTime? tryStartedAt;
    private long tryAtMs; // 这次点「现在试一句」写进 trigger.json 的时间（毫秒），插件按它回报结果
    private DateTime? noteHideAt;
    private const double EstimateSeconds = 12;

    internal ContextChatWindow()
    {
        InitializeComponent();
        Icon = Whale.WindowIcon(); // 任务栏 / Alt+Tab 用的图标（和控制面板同一个 logo）
        SourceInitialized += (_, _) => Taskbar.SetId(this, Taskbar.ContextId); // 在任务栏上单独占一个按钮
        saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); Save(); };
        statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        statusTimer.Tick += (_, _) => RefreshStatus();

        Header.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) { try { DragMove(); } catch { } } };
        CloseButton.Click += (_, _) => Hide();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Hide(); };

        // 跟主控制台一样：右上角的图钉，固定在别的窗口前面，记住选择
        prefs = Prefs.Load();
        PinButton.IsChecked = prefs.Pin;
        Topmost = prefs.Pin;
        PinButton.Click += (_, _) =>
        {
            prefs.Pin = PinButton.IsChecked == true;
            Topmost = prefs.Pin;
            prefs.Save();
            SaveHint.Text = prefs.Pin ? "已固定在最前面" : "取消固定";
        };

        TryButton.Click += (_, _) => TryNow();
        LogButton.Click += (_, _) => OpenLog();

        // 节奏
        GapSlider.ValueChanged += (_, e) => { if (!loading) { GapRun.Text = $"  {e.NewValue:0} 分钟"; Touch("minGapMs", (int)e.NewValue * 60000); } };
        DaySlider.ValueChanged += (_, e) => { if (!loading) { DayRun.Text = $"  {e.NewValue:0} 句"; Touch("dailyMax", (int)e.NewValue); } };
        SceneSlider.ValueChanged += (_, e) => { if (!loading) { SceneRun.Text = $"  {e.NewValue:0} 秒"; Touch("sceneDelayMs", (int)e.NewValue * 1000); } };
        OfficeSlider.ValueChanged += (_, e) => { if (!loading) { OfficeRun.Text = $"  {e.NewValue:0} 分钟"; TouchOffice((int)e.NewValue); } };
        DupeSlider.ValueChanged += (_, e) => { if (!loading) { DupeRun.Text = "  " + DupeText(e.NewValue); Touch("sceneDedupeMs", (int)e.NewValue * 60000); } };
        BuildQuietRange();

        QuietButton.Click += (_, _) => Preset("quiet");
        NormalButton.Click += (_, _) => Preset("normal");
        ChattyButton.Click += (_, _) => Preset("chatty");

        // 风格
        StyleBox.TextChanged += (_, _) => { if (!loading) Touch("stylePrompt", StyleBox.Text.Trim()); };
        TplSharp.Click += (_, _) => FillStyle(Templates[0]);
        TplSoft.Click += (_, _) => FillStyle(Templates[1]);
        TplGenki.Click += (_, _) => FillStyle(Templates[2]);
        TplCool.Click += (_, _) => FillStyle(Templates[3]);
        TplClingy.Click += (_, _) => FillStyle(Templates[4]);
        TplReset.Click += (_, _) => FillStyle("");
        LinesSlider.ValueChanged += (_, e) => { if (!loading) { LinesRun.Text = $"  {e.NewValue:0} 行"; Touch("maxLines", (int)e.NewValue); } };
        CharsSlider.ValueChanged += (_, e) => { if (!loading) { CharsRun.Text = $"  {e.NewValue:0} 字"; Touch("maxLineChars", (int)e.NewValue); } };

        Wire(SwActions, "withActions");
        Wire(SwAsk, "askQuestion");
        Wire(SwSpeech, "speech");
        Wire(SwInject, "injectContext");
        Wire(SwRequireList, "requireList");

        // 软件名单
        AddHereButton.Click += (_, _) => AddHere();
        AddTypedButton.Click += (_, _) => AddTyped();
        PickExeButton.Click += (_, _) => PickExe();
        PrivateHereButton.Click += (_, _) => AddPrivate(StatusJson()?["proc"]?.ToString() ?? "");
        PrivateAddButton.Click += (_, _) => AddPrivate(PrivateInput.Text);
        PrivatePickButton.Click += (_, _) =>
        {
            var picked = PickExePath();
            if (picked != null) AddPrivate(ProcessNameOf(picked));
        };
        PrivateInput.TextChanged += (_, _) => PrivateHint.Visibility = PrivateInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        PrivateInput.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            AddPrivate(PrivateInput.Text);
        };
        NewAppBox.TextChanged += (_, _) => NewAppHint.Visibility = NewAppBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        NewAppBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; AddTyped(); } };
        BlockBox.TextChanged += (_, _) => { if (!loading) TouchList("titleBlockWords", BlockBox.Text); };

        Loaded += (_, _) => { statusTimer.Start(); TuneTooltips(this); };
        Closed += (_, _) => statusTimer.Stop();
        IsVisibleChanged += (_, e) => { if (e.NewValue is true) { Reload(); RefreshStatus(); } };
    }

    // 这个窗口自己记住的东西（放在控制台文件夹里，和 ui.json 挨着）
    private sealed class Prefs
    {
        public bool Pin { get; set; }

        private static string FilePath => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "context-ui.json"));

        internal static Prefs Load()
        {
            try
            {
                if (File.Exists(FilePath) && JsonSerializer.Deserialize<Prefs>(File.ReadAllText(FilePath, Encoding.UTF8)) is { } loaded) return loaded;
            }
            catch { }
            return new Prefs();
        }

        internal void Save()
        {
            try
            {
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            }
            catch { }
        }
    }

    private static readonly string[] Templates =
    [
        "说话带点毒舌，爱损人，但句句都在关心；别客气，别用敬语，偶尔翻旧账。",
        "说话温柔、慢一点，先肯定再提建议；不许凶，最多轻轻叹口气。",
        "元气一点，语速快、句子短，多用感叹，看到什么都想凑热闹。",
        "话少、冷静、语气平；最多一句，点到为止，不随便夸人。",
        "黏人一点，爱撒娇，喜欢问你在干嘛，偶尔闹小脾气，但很快就好了。",
    ];

    // ---- 找插件目录、读设置 ----
    private static string? Dir()
    {
        try
        {
            var pointer = Path.Combine(Pet.Root, "context_chat.json");
            if (File.Exists(pointer))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(pointer, Encoding.UTF8));
                if (doc.RootElement.TryGetProperty("dir", out var dir) && dir.GetString() is { Length: > 0 } path && Directory.Exists(path)) return path;
            }
        }
        catch { }
        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh", "plugins", "dafeiyu-context");
        return Directory.Exists(fallback) ? fallback : null;
    }

    private static string? SettingsFile() => Dir() is { } dir ? Path.Combine(dir, "settings.json") : null;

    internal void Open()
    {
        Reload();
        RefreshStatus();
        Show();
        WindowState = WindowState.Normal;
        Activate();
        if (prefs.Pin) Topmost = true;            // 钉住了就一直保持在前面
        else { Topmost = true; Topmost = false; } // 没钉就只顶到前面一下，不占着最上层
    }

    private void Reload()
    {
        loading = true;
        try
        {
            var file = SettingsFile();
            if (file == null || !File.Exists(file))
            {
                SaveHint.Text = "没找到插件设置：先把 DSH 跑起来一次（插件会写 context_chat.json 指路）";
                return;
            }
            root = JsonNode.Parse(File.ReadAllText(file, Encoding.UTF8)) as JsonObject ?? new JsonObject();
            SaveHint.Text = "改完自动保存，4 秒内生效";

            GapSlider.Value = Math.Clamp(Int("minGapMs", 420000) / 60000.0, 1, 60);
            DaySlider.Value = Math.Clamp(Int("dailyMax", 40), 0, 200);
            SceneSlider.Value = Math.Clamp(Int("sceneDelayMs", 30000) / 1000.0, 5, 120);
            OfficeSlider.Value = Math.Clamp(Int("officeDelayMinMs", 120000) / 60000.0, 1, 15);
            DupeSlider.Value = Math.Clamp(Int("sceneDedupeMs", 7200000) / 60000.0, 30, 720);
            // 从几点到几点：「从」可以比「到」大（23 点 – 7 点 = 跨过午夜）；两个一样 = 没有安静时段
            quietLow = Math.Clamp(Int("quietFrom", 0), 0, 24);
            quietHigh = Math.Clamp(Int("quietTo", 8), 0, 24);
            LayoutQuiet();
            GapRun.Text = $"  {GapSlider.Value:0} 分钟";
            DayRun.Text = $"  {DaySlider.Value:0} 句";
            SceneRun.Text = $"  {SceneSlider.Value:0} 秒";
            OfficeRun.Text = $"  {OfficeSlider.Value:0} 分钟";
            DupeRun.Text = "  " + DupeText(DupeSlider.Value);

            StyleBox.Text = Str("stylePrompt", "");
            LinesSlider.Value = Math.Clamp(Int("maxLines", 2), 1, 3);
            CharsSlider.Value = Math.Clamp(Int("maxLineChars", 28), 12, 60);
            LinesRun.Text = $"  {LinesSlider.Value:0} 行";
            CharsRun.Text = $"  {CharsSlider.Value:0} 字";

            SwActions.IsChecked = Bool("withActions", true);
            SwAsk.IsChecked = Bool("askQuestion", false);
            SwSpeech.IsChecked = Bool("speech", true);
            SwInject.IsChecked = Bool("injectContext", true);
            SwRequireList.IsChecked = Bool("requireList", true);

            BlockBox.Text = string.Join("，", Strings("titleBlockWords"));
            ForgetMissingIcons(); // 重新打开时，之前没找到的再找一遍（程序可能刚启动）；找到的保留
            BuildApps();
            BuildPrivate();
        }
        finally { loading = false; }
    }

    private static string DupeText(double minutes) => minutes >= 60 ? $"{minutes / 60:0.#} 小时" : $"{minutes:0} 分钟";

    // ---- JSON 小工具 ----
    private bool? BoolNode(string key) => root?[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
    private bool Bool(string key, bool fallback) => BoolNode(key) ?? fallback;
    private int Int(string key, int fallback) => root?[key] is JsonValue v && v.TryGetValue<int>(out var n) ? n : fallback;
    private string Str(string key, string fallback) => root?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : fallback;

    private List<string> Strings(string key)
    {
        var list = new List<string>();
        if (root?[key] is JsonArray array)
            foreach (var node in array)
                if (node?.ToString() is { Length: > 0 } text) list.Add(text);
        return list;
    }

    private void Touch(string key, object value)
    {
        if (root == null) return;
        JsonNode? node = value switch
        {
            int i => JsonValue.Create(i),
            bool b => JsonValue.Create(b),
            double d => JsonValue.Create(d),
            _ => JsonValue.Create(value.ToString()),
        };
        root[key] = node;
        Schedule();
    }

    private void TouchList(string key, string text)
    {
        if (root == null) return;
        var array = new JsonArray();
        // 逗号（中英文）、顿号、分号、换行都算分隔：提示写的是"用逗号隔开"，但换行也照收
        foreach (var line in text.Split(new[] { '\n', '，', ',', '、', '；', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct())
            array.Add(line);
        root[key] = array;
        Schedule();
    }

    private void Wire(ToggleButton toggle, string key) =>
        toggle.Click += (_, _) => { if (!loading) Touch(key, toggle.IsChecked == true); };

    private void Schedule()
    {
        saveTimer.Stop();
        saveTimer.Start();
        SaveHint.Text = "保存中…";
    }

    private void Save()
    {
        try
        {
            var file = SettingsFile();
            if (file == null || root == null) { SaveHint.Text = "没找到插件设置，没存"; return; }
            var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            // 先写临时文件再换名：插件 4 秒一次读这个文件，不会读到写了一半的
            var temp = file + ".tmp";
            File.WriteAllText(temp, json + Environment.NewLine, new UTF8Encoding(false));
            File.Move(temp, file, true);
            SaveHint.Text = $"已保存 {DateTime.Now:HH:mm:ss}（她那边 4 秒内生效）";
        }
        catch (Exception error) { SaveHint.Text = "存不下来：" + error.Message; }
    }

    // ---- 安静时段：一个滑动条、两个圆圈 ----
    // 外观直接照 StarSlider：同一条轨道（6 高、1px 边框）、同一段渐变、同一个圆钮样式（SliderDot），
    // 区别只是有两个圆钮，各拖各的"从几点 / 到几点"。
    private const double QuietPad = 9; // 圆钮半径（SliderDot 是 18×18）
    private Thumb quietLo = null!, quietHi = null!;
    private Border quietFill = null!, quietFill2 = null!; // 跨午夜时要画两段：0 点到「到」、「从」到 24 点
    private double quietLow, quietHigh = 8; // 内部用小数，拖起来顺；显示和保存时取整

    private void BuildQuietRange()
    {
        QuietTrack.Children.Clear();
        QuietTrack.Children.Add(new Border
        {
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Margin = new Thickness(QuietPad, 0, QuietPad, 0),
            Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x2A, 0x5C)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x3F, 0x7A)),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
        });
        quietFill = new Border
        {
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Background = QuietGradient(),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };
        QuietTrack.Children.Add(quietFill);
        quietFill2 = new Border
        {
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Background = QuietGradient(),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        QuietTrack.Children.Add(quietFill2);

        quietLo = QuietThumb();
        quietHi = QuietThumb();
        QuietTrack.Children.Add(quietLo);
        QuietTrack.Children.Add(quietHi);
        quietLo.DragDelta += (_, e) => MoveQuiet(true, e.HorizontalChange);
        quietHi.DragDelta += (_, e) => MoveQuiet(false, e.HorizontalChange);

        // 点轨道空白处：把离得近的那个圆移过去（点圆钮本身时交给它自己拖）
        QuietTrack.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (Inside(e.OriginalSource, quietLo) || Inside(e.OriginalSource, quietHi)) return;
            var value = QuietFromX(e.GetPosition(QuietTrack).X);
            SetQuiet(Math.Abs(value - quietLow) <= Math.Abs(value - quietHigh), value);
            e.Handled = true;
        };

        QuietTrack.SizeChanged += (_, _) => LayoutQuiet();
        QuietTrack.Loaded += (_, _) => LayoutQuiet();
    }

    private static Brush QuietGradient()
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x2F, 0x6F, 0xD0), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x7C, 0xC4, 0xFF), 1));
        return brush;
    }

    private static bool Inside(object? source, DependencyObject target)
    {
        var current = source as DependencyObject;
        while (current != null)
        {
            if (ReferenceEquals(current, target)) return true;
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private Thumb QuietThumb() => new()
    {
        Style = (Style)FindResource("SliderDot"),
        Width = 18,
        Height = 18,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Center,
        Cursor = Cursors.Hand,
        RenderTransform = new TranslateTransform(),
    };

    private double QuietFromX(double x)
    {
        var width = QuietTrack.ActualWidth;
        var inner = Math.Max(1, width - 2 * QuietPad);
        var ratio = Math.Clamp((x - QuietPad) / inner, 0, 1);
        return Math.Round(ratio * 24);
    }

    private void MoveQuiet(bool low, double dx)
    {
        var inner = Math.Max(1, QuietTrack.ActualWidth - 2 * QuietPad);
        SetQuiet(low, (low ? quietLow : quietHigh) + dx / inner * 24);
    }

    private void SetQuiet(bool low, double value)
    {
        // 两个圆可以互相越过：「从」比「到」大就是跨午夜；重合 = 没有安静时段（插件里 from == to 就是不设）
        if (low) quietLow = Math.Clamp(value, 0, 24);
        else quietHigh = Math.Clamp(value, 0, 24);
        LayoutQuiet();
        if (loading) return;
        Touch("quietFrom", (int)Math.Round(quietLow));
        Touch("quietTo", (int)Math.Round(quietHigh));
    }

    private void LayoutQuiet()
    {
        if (quietLo is null) return;
        int from = (int)Math.Round(quietLow), to = (int)Math.Round(quietHigh);
        QuietRun.Text = from == to ? "  没有（两个圆重合了）"
            : from < to ? $"  {from} 点 – {to} 点"
            : $"  {from} 点 – 次日 {to} 点";
        var width = QuietTrack.ActualWidth;
        var inner = Math.Max(1, width - 2 * QuietPad);
        double X(double hour) => QuietPad + hour / 24.0 * inner;
        ((TranslateTransform)quietLo.RenderTransform).X = X(quietLow) - QuietPad;
        ((TranslateTransform)quietHi.RenderTransform).X = X(quietHigh) - QuietPad;
        if (from == to)
        {
            quietFill.Width = 0;
            quietFill2.Visibility = Visibility.Collapsed;
        }
        else if (from < to)
        {
            quietFill.Margin = new Thickness(X(quietLow), 0, 0, 0);
            quietFill.Width = Math.Max(0, X(quietHigh) - X(quietLow));
            quietFill2.Visibility = Visibility.Collapsed;
        }
        else
        {
            // 跨午夜：左边 0 点到「到」，右边「从」到 24 点
            quietFill.Margin = new Thickness(X(0), 0, 0, 0);
            quietFill.Width = Math.Max(0, X(quietHigh) - X(0));
            quietFill2.Margin = new Thickness(X(quietLow), 0, 0, 0);
            quietFill2.Width = Math.Max(0, X(24) - X(quietLow));
            quietFill2.Visibility = Visibility.Visible;
        }
    }

    // ---- 节奏预设 ----
    private void Preset(string which)
    {
        if (root == null) return;
        var (gap, day, scene, office) = which switch
        {
            "quiet" => (20, 10, 60, 5),
            "chatty" => (3, 80, 15, 1),
            _ => (7, 40, 30, 3),
        };
        root["minGapMs"] = gap * 60000;
        root["dailyMax"] = day;
        root["sceneDelayMs"] = scene * 1000;
        OfficeSet(office);
        loading = true;
        try
        {
            GapSlider.Value = gap; DaySlider.Value = day; SceneSlider.Value = scene; OfficeSlider.Value = office;
            GapRun.Text = $"  {gap} 分钟"; DayRun.Text = $"  {day} 句"; SceneRun.Text = $"  {scene} 秒"; OfficeRun.Text = $"  {office} 分钟";
        }
        finally { loading = false; }
        Schedule();
    }

    private void TouchOffice(int minutes)
    {
        if (root == null) return;
        OfficeSet(minutes);
        Schedule();
    }

    private void OfficeSet(int minutes)
    {
        if (root == null) return;
        root["officeDelayMinMs"] = minutes * 60000;
        root["officeDelayMaxMs"] = (int)(minutes * 60000 * 1.6);
    }

    private void FillStyle(string text)
    {
        loading = true;
        try { StyleBox.Text = text; }
        finally { loading = false; }
        Touch("stylePrompt", text.Trim());
    }

    // ---- 软件名单 ----
    private JsonArray AppsArray()
    {
        if (root == null) return new JsonArray();
        if (root["apps"] is JsonArray array) return array;
        var fresh = new JsonArray();
        root["apps"] = fresh;
        return fresh;
    }

    private static string ModeOf(JsonNode? app) => app?["mode"]?.ToString() switch
    {
        "watch" => "watch",
        "ignore" => "ignore",
        _ => "speak",
    };

    // 插件在 status.json 里写的「名单里说不了的软件」：{ 进程名: 原因 }
    private Dictionary<string, string> warnings = new();
    private string warningsKey = "";

    private void ReadWarnings(JsonNode? status)
    {
        var next = new Dictionary<string, string>();
        if (status?["appWarnings"] is JsonObject obj)
            foreach (var (name, value) in obj)
                if (value?.ToString() is { Length: > 0 } text) next[name.ToLowerInvariant()] = text;
        warnings = next;
        warningsKey = string.Join("|", next.OrderBy(pair => pair.Key).Select(pair => pair.Key + "=" + pair.Value));
    }

    private void BuildApps()
    {
        ReadWarnings(StatusJson());
        AppsPanel.Children.Clear();
        foreach (var node in AppsArray())
        {
            if (node is not JsonObject app) continue;
            var proc = app["proc"]?.ToString() ?? "";
            if (proc.Length == 0) continue;
            var mode = ModeOf(app);

            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // 程序图标（在后台找，别卡住界面：找开始菜单快捷方式那一步偶尔会慢）
            var image = new Image { Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(image, 0);
            row.Children.Add(image);
            var forIcon = proc;
            var forPath = app["path"]?.ToString();
            RunOnIconThread(() =>
            {
                var icon = IconOf(forIcon, forPath);
                if (icon is null) return;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try { image.Source = icon; } catch { }
                }));
            });

            var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
            label.Inlines.Add(new System.Windows.Documents.Run(proc));
            // 设成「说话」其实说不了的（私密软件、系统界面、DSH 没开 debugDsh……），插件会写原因，这里提醒一下
            if (mode == "speak" && warnings.TryGetValue(proc.ToLowerInvariant(), out var warn))
            {
                label.Inlines.Add(new System.Windows.Documents.Run("  ⚠ " + warn) { FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xB4, 0x54)) });
                label.ToolTip = warn;
            }
            Grid.SetColumn(label, 1);
            row.Children.Add(label);

            var modes = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            foreach (var (id, text) in new[] { ("speak", "说话"), ("watch", "只看"), ("ignore", "不看") })
            {
                var button = new Button
                {
                    Style = (Style)FindResource(id == mode ? "PillAccent" : "ChipButton"),
                    Content = text,
                    Margin = new Thickness(0, 0, 4, 0),
                    Padding = new Thickness(10, 3, 10, 3),
                };
                var wanted = id;
                button.Click += (_, _) => SetMode(proc, wanted);
                modes.Children.Add(button);
            }
            Grid.SetColumn(modes, 2);
            row.Children.Add(modes);

            var remove = new Button { Style = (Style)FindResource("Ghost"), Content = "✕", Padding = new Thickness(7, 1, 7, 1), Margin = new Thickness(6, 0, 0, 0) };
            remove.Click += (_, _) => RemoveApp(proc);
            Grid.SetColumn(remove, 3);
            row.Children.Add(remove);

            AppsPanel.Children.Add(row);
        }
        if (AppsPanel.Children.Count == 0)
            AppsPanel.Children.Add(new TextBlock { Text = "名单还是空的：下面加一个软件，或者把现在这个窗口加进来", FontSize = 12, Foreground = (Brush)FindResource("SubBrush"), TextWrapping = TextWrapping.Wrap });
    }

    private void SetMode(string proc, string mode)
    {
        foreach (var node in AppsArray())
        {
            if (node is not JsonObject app) continue;
            if ((app["proc"]?.ToString() ?? "") != proc) continue;
            app["mode"] = mode;
            break;
        }
        BuildApps();
        Schedule();
    }

    private void RemoveApp(string proc)
    {
        var array = AppsArray();
        var target = array.FirstOrDefault(node => node is JsonObject app && (app["proc"]?.ToString() ?? "") == proc);
        if (target != null) array.Remove(target);
        BuildApps();
        Schedule();
    }

    private void AddApp(string proc, string? path = null)
    {
        proc = proc.Trim().ToLowerInvariant();
        if (proc.Length == 0) return;
        var array = AppsArray();
        var existing = array.FirstOrDefault(node => node is JsonObject app && (app["proc"]?.ToString() ?? "") == proc);
        if (existing is JsonObject hit)
        {
            hit["mode"] = "speak";
            if (!string.IsNullOrWhiteSpace(path)) hit["path"] = path;
        }
        else
        {
            var entry = new JsonObject { ["proc"] = proc, ["mode"] = "speak" };
            if (!string.IsNullOrWhiteSpace(path)) entry["path"] = path;
            array.Add(entry);
        }
        BuildApps();
        Schedule();
        NewAppBox.Clear();
    }

    private void AddTyped() => AddApp(NewAppBox.Text);

    // 「现在这个窗口」取插件状态里记的那个前台进程（别取到自己身上）
    private void AddHere()
    {
        var proc = StatusJson()?["proc"]?.ToString() ?? "";
        if (proc.Length == 0)
        {
            SaveHint.Text = "这会儿没认出前台窗口，手动输进程名吧";
            return;
        }
        AddApp(proc);
    }

    // ---- 状态 ----
    private JsonNode? StatusJson()
    {
        try
        {
            var dir = Dir();
            if (dir == null) return null;
            var file = Path.Combine(dir, "status.json");
            if (!File.Exists(file)) return null;
            return JsonNode.Parse(File.ReadAllText(file, Encoding.UTF8));
        }
        catch { return null; }
    }

    private void RefreshStatus()
    {
        if (!IsVisible) return;
        var status = StatusJson();
        if (status == null)
        {
            NowText.Text = "还没收到她的状态";
            WhyText.Text = "观察器没在跑：在 DSH 里开一次会话，或者在 Claude 里发一条消息，就会起来";
            StateBadge.Text = "离线";
            UpdateTryNote(null);
            return;
        }
        // 插件在跑的话每 5 秒会刷一次状态；太久没刷，就是插件没开（DSH 没开、插件被卸载），
        // 里面的窗口、计数、「看着呢」都是旧的，不能当现在的状态显示
        if (StatusAge(status) is { } age && age > StaleAfter)
        {
            var last = StatusTime(status);
            NowText.Text = last is { } when ? $"插件没在跑（最后一次状态是 {when.ToLocalTime():HH:mm:ss}）" : "插件没在跑";
            WhyText.Text = "窗口是 DeepSeek Harness 里的插件、或者 Claude 的 hook 拉起的观察器在看；两边都没在用的时候，她不会看也不会说。在 DSH 开个会话或给 Claude 发条消息就起来了";
            StateBadge.Text = "离线";
            UpdateTryNote(status);
            return;
        }
        var app = status["app"]?.ToString() ?? "";
        var title = status["title"]?.ToString() ?? "";
        var rule = status["rule"]?.ToString() ?? "";
        var contextChat = Flag(status["contextChat"], true);
        var watching = Flag(status["watching"]);
        var priv = Flag(status["private"]);
        var today = Num(status["todayCount"], 0);
        var max = Num(status["dailyMax"], 40);
        var reason = status["reason"]?.ToString() ?? "";
        var nextAt = status["nextAt"]?.ToString() ?? "";
        var lastSaid = System.Text.RegularExpressions.Regex.Replace(status["lastSaid"]?.ToString() ?? "", "〔[^〕]*〕", "").Replace(" /  / ", " / ").Trim();

        var who = status["mode"]?.ToString() == "claude" ? "Claude" : "DSH";
        StateBadge.Text = !contextChat ? "总开关关着" : watching ? $"看着呢（{who}）" : "观察器没跑";
        if (app.Length == 0) NowText.Text = "这会儿没在看她";
        else if (priv) NowText.Text = $"{app}（黑名单窗口，标题不看）";
        else NowText.Text = title.Length > 0 ? $"{app}：{title}" : app;

        var parts = new List<string>();
        if (rule is "none") parts.Add("不在白名单里");
        else if (rule is "watch") parts.Add("名单里设成只看不说");
        else if (rule is "ignore") parts.Add("这个软件设成完全不看");
        if (reason.Length > 0) parts.Add(reason);
        parts.Add($"今天 {today}/{max} 句");
        if (nextAt.Length > 0 && DateTimeOffset.TryParse(nextAt, out var next)) parts.Add($"下次最早 {next.ToLocalTime():HH:mm}");
        if (lastSaid.Length > 0) parts.Add($"上次：「{lastSaid}」");
        WhyText.Text = string.Join(" · ", parts);
        UpdateTryNote(status);
        SyncWarnings(status);
    }

    private void SyncWarnings(JsonNode? status)
    {
        var before = warningsKey;
        ReadWarnings(status);
        if (warningsKey != before) BuildApps();
    }

    // 状态文件多久没刷就算插件没在跑（插件每 5 秒写一次，留足余量）
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(20);

    private static DateTimeOffset? StatusTime(JsonNode? status) =>
        status?["at"]?.ToString() is { Length: > 0 } text && DateTimeOffset.TryParse(text, out var at) ? at : null;

    private static TimeSpan? StatusAge(JsonNode? status) =>
        StatusTime(status) is { } at ? DateTimeOffset.UtcNow - at : null;

    private static long LongOf(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<long>(out var number) ? number : 0;

    private static bool Flag(JsonNode? node, bool fallback = false) =>
        node is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : fallback;

    private static int Num(JsonNode? node, int fallback) =>
        node is JsonValue value && value.TryGetValue<int>(out var number) ? number : fallback;

    private void TryNow()
    {
        try
        {
            var dir = Dir();
            if (dir == null) { SaveHint.Text = "没找到插件目录"; return; }
            // 插件没在跑就别写请求：写了没人接，等 DSH 开起来又会冷不丁说一句
            var current = StatusJson();
            if (current == null || StatusAge(current) is not { } age || age > StaleAfter)
            {
                EndTryNote("观察器没在跑，这句说不了（在 DSH 里开个会话，或给 Claude 发条消息）");
                return;
            }
            if (current["mode"]?.ToString() == "claude")
            {
                EndTryNote("现在是 Claude 在看窗口（DSH 没开）：不会自己写台词。要试一句请先开 DSH");
                return;
            }
            var file = Path.Combine(dir, "trigger.json");
            tryAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var json = new JsonObject { ["at"] = tryAtMs }.ToJsonString();
            File.WriteAllText(file, json, new UTF8Encoding(false));
            StartTryNote();
        }
        catch (Exception error) { SaveHint.Text = "没戳动：" + error.Message; }
    }

    // 倒数条：点完立刻显示，插件真说了就换成「说完了」
    internal void StartTryNote()
    {
        tryStartedAt = DateTime.Now;
        noteHideAt = null;
        TryNote.Visibility = Visibility.Visible;
        TryNote.Text = "正在生成… 大约 12 秒";
    }

    // 倒数结束：显示一句结果，10 秒后收起
    private void EndTryNote(string text)
    {
        tryStartedAt = null;
        TryNote.Visibility = Visibility.Visible;
        TryNote.Text = text;
        noteHideAt = DateTime.Now.AddSeconds(10);
    }

    // 「现在试一句」的进度：点完按估计时间倒数；插件说了就改成「说完了」，
    // 没说成（她收着、正在说话、插件没在跑……）就把原因显示出来，别一直转圈
    private void UpdateTryNote(JsonNode? status)
    {
        if (tryStartedAt is { } start)
        {
            var elapsed = (DateTime.Now - start).TotalSeconds;

            // 插件亲口回报的结果（at 对得上这次点的时间才算）
            if (status?["manual"] is JsonObject manual && LongOf(manual["at"]) >= tryAtMs && tryAtMs > 0)
            {
                var state = manual["state"]?.ToString() ?? "";
                if (state == "failed")
                {
                    var why = manual["reason"]?.ToString() ?? "";
                    EndTryNote(why.Length > 0 ? $"没说成：{why}" : "没说成");
                    return;
                }
            }

            var saidAt = status?["lastSaidAt"]?.ToString() ?? "";
            if (saidAt.Length > 0 && DateTimeOffset.TryParse(saidAt, out var said) && said.ToLocalTime().DateTime > start)
            {
                EndTryNote($"说完了（{elapsed:0.#} 秒）");
                return;
            }

            // 请求发出去之后插件掉线了
            if (elapsed > 3 && (status == null || StatusAge(status) is { } gone && gone > StaleAfter))
            {
                EndTryNote("插件没在跑（DSH 没开？），这句说不了");
                return;
            }
            // 兜底：一分钟还没动静（插件是旧版本，不会回报，或者卡住了）
            if (elapsed > 60)
            {
                EndTryNote("等了一分钟她还没说：看看「看日志」里写了什么");
                return;
            }

            var left = EstimateSeconds - elapsed;
            TryNote.Visibility = Visibility.Visible;
            TryNote.Text = left > 0
                ? $"正在生成… 还剩 {Math.Ceiling(left):0} 秒（写台词 + 合成语音）"
                : $"还在合成…（已 {elapsed:0} 秒，通常十秒左右）";
            return;
        }
        if (noteHideAt is { } hideAt && DateTime.Now > hideAt)
        {
            noteHideAt = null;
            TryNote.Visibility = Visibility.Collapsed;
        }
    }

    private void OpenLog()
    {
        try
        {
            var dir = Dir();
            if (dir == null) return;
            var log = Path.Combine(dir, "context.log");
            if (File.Exists(log)) Process.Start(new ProcessStartInfo(log) { UseShellExecute = true });
        }
        catch { }
    }

    // 提示框：多停一会儿，而且离开再回来能马上再弹（系统默认 5 秒就收，再放上去要重新等，像「只显示一次」）
    private static void TuneTooltips(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is FrameworkElement element && element.ToolTip != null)
            {
                ToolTipService.SetShowDuration(element, 120000);
                ToolTipService.SetInitialShowDelay(element, 250);
                ToolTipService.SetBetweenShowDelay(element, 100);
            }
            TuneTooltips(child);
        }
    }

    // 手动绑定电脑里的程序：选一个 .exe，认它的进程名
    private void PickExe()
    {
        var path = PickExePath();
        if (path == null) return;
        var name = ProcessNameOf(path);
        AddApp(name, path);
        SaveHint.Text = $"加上了「{name}」；要是她没反应，去任务管理器看看它的进程名是不是这个";
    }

    private string? PickExePath()
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选一个要让她认得的程序",
                Filter = "程序 (*.exe)|*.exe|所有文件 (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false,
            };
            return dialog.ShowDialog(this) == true ? dialog.FileName : null;
        }
        catch (Exception error) { SaveHint.Text = "选不了：" + error.Message; return null; }
    }

    // ---- 黑名单：和上面的白名单一样，一行一个（图标 + 名字 + ✕） ----
    private JsonArray PrivateArray()
    {
        if (root == null) return new JsonArray();
        if (root["privateApps"] is JsonArray array) return array;
        var fresh = new JsonArray();
        root["privateApps"] = fresh;
        return fresh;
    }

    private void BuildPrivate()
    {
        PrivatePanel.Children.Clear();
        foreach (var node in PrivateArray())
        {
            var proc = node?.ToString() ?? "";
            if (proc.Length == 0) continue;

            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var image = new Image { Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(image, 0);
            row.Children.Add(image);
            var forIcon = proc;
            RunOnIconThread(() =>
            {
                var icon = IconOf(forIcon, null);
                if (icon is null) return;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try { image.Source = icon; } catch { }
                }));
            });

            var label = new TextBlock { Text = proc, VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5 };
            Grid.SetColumn(label, 1);
            row.Children.Add(label);

            var remove = new Button { Style = (Style)FindResource("Ghost"), Content = "✕", Padding = new Thickness(7, 1, 7, 1), Margin = new Thickness(6, 0, 0, 0) };
            remove.Click += (_, _) => RemovePrivate(proc);
            Grid.SetColumn(remove, 2);
            row.Children.Add(remove);

            PrivatePanel.Children.Add(row);
        }
        if (PrivatePanel.Children.Count == 0)
            PrivatePanel.Children.Add(new TextBlock
            {
                Text = "还没有：下面加一个软件，它的标题就不会被读了",
                FontSize = 12,
                Foreground = (Brush)FindResource("SubBrush"),
                TextWrapping = TextWrapping.Wrap,
            });
    }

    // 黑名单也支持"从电脑里选"和"把当前窗口加进来"
    private void AddPrivate(string proc)
    {
        proc = (proc ?? "").Trim().ToLowerInvariant();
        if (proc.Length == 0) { SaveHint.Text = "这会儿没认出前台窗口，手动输进程名吧"; return; }
        var array = PrivateArray();
        if (array.Any(node => string.Equals(node?.ToString(), proc, StringComparison.OrdinalIgnoreCase)))
        {
            SaveHint.Text = $"「{proc}」已经在黑名单里了";
            return;
        }
        array.Add(proc);
        BuildPrivate();
        Schedule();
        PrivateInput.Clear();
        SaveHint.Text = $"加进黑名单了：{proc}（这个软件的标题不会再读）";
    }

    private void RemovePrivate(string proc)
    {
        var array = PrivateArray();
        var target = array.FirstOrDefault(node => string.Equals(node?.ToString(), proc, StringComparison.OrdinalIgnoreCase));
        if (target != null) array.Remove(target);
        BuildPrivate();
        Schedule();
    }

    // 进程图标：先找正在跑的同名进程的 exe，再看名单里记过的路径，
    // 最后去开始菜单/桌面的快捷方式里按名字找（没在跑的程序也能有图标）
    // 图标缓存（线程安全：界面线程会读、会清；查找在专用线程里写）。值为 null = 这次没找到
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ImageSource?> icons = new();
    private Dictionary<string, string>? shortcuts; // 只在图标线程里建、用；建好才整体换上，没有半成品

    // 所有图标查找都排到这一条 STA 线程上，一个接一个做：
    // 以前每行名单各开一个线程池线程，同时读写同一个 Dictionary（偶尔抛异常、被吞掉、图标就缓存成「没有」），
    // 而且外壳取图标（SHGetFileInfo 解析 .lnk）要求 STA，线程池线程是 MTA，也不稳。
    private readonly System.Collections.Concurrent.BlockingCollection<Action> iconQueue = new();
    private System.Threading.Thread? iconThread;

    private void RunOnIconThread(Action work)
    {
        if (iconThread == null)
        {
            iconThread = new System.Threading.Thread(() =>
            {
                foreach (var job in iconQueue.GetConsumingEnumerable())
                {
                    try { job(); } catch { }
                }
            }) { IsBackground = true, Name = "icon-lookup" };
            iconThread.SetApartmentState(System.Threading.ApartmentState.STA);
            iconThread.Start();
        }
        iconQueue.Add(work);
    }

    private void ForgetMissingIcons()
    {
        foreach (var key in icons.Where(pair => pair.Value is null).Select(pair => pair.Key).ToList())
            icons.TryRemove(key, out _);
    }

    // 找到过的程序路径记在本地（没在运行的软件，下次不用再靠猜开始菜单快捷方式）
    private static string IconPathsFile => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "icon-paths.json"));
    private Dictionary<string, string>? knownPaths; // 只在图标线程里用

    private string? KnownPath(string proc)
    {
        try
        {
            knownPaths ??= File.Exists(IconPathsFile)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(IconPathsFile, Encoding.UTF8)) ?? new()
                : new();
        }
        catch { knownPaths = new(); }
        return knownPaths.TryGetValue(proc, out var path) && File.Exists(path) && !path.EndsWith("_proxy.exe", StringComparison.OrdinalIgnoreCase) ? path : null;
    }

    private void RememberPath(string proc, string path)
    {
        try
        {
            knownPaths ??= new();
            if (knownPaths.TryGetValue(proc, out var old) && old == path) return;
            knownPaths[proc] = path;
            File.WriteAllText(IconPathsFile, JsonSerializer.Serialize(knownPaths, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        }
        catch { }
    }

    // 用 shell 的 SHGetFileInfo 取小图标：比 Icon.ExtractAssociatedIcon 靠谱，
    // 尤其是快捷方式（.lnk）——后台线程里也能解析出真正的程序图标
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr Icon;
        public int Index;
        public uint Attributes;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 80)] public string Type;
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref ShellFileInfo info, uint size, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    private static ImageSource? ShellIconOf(string path)
    {
        try
        {
            var info = new ShellFileInfo();
            var size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<ShellFileInfo>();
            if (SHGetFileInfo(path, 0, ref info, size, 0x100 /*SHGFI_ICON*/ | 0x1 /*SHGFI_SMALLICON*/) == IntPtr.Zero || info.Icon == IntPtr.Zero) return null;
            try
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally { DestroyIcon(info.Icon); }
        }
        catch { return null; }
    }

    // 自带图标：tray\icons\<进程名>.png（64×64，各软件官方站点的图标）。装了的软件优先用它自己的真图标，
    // 没装的（微信、QQ、钉钉、飞书、Telegram、Discord……）才用这里的，这样名单里不会一排空白
    private static string BundledIconsDir => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "icons"));

    private static ImageSource? BundledIconOf(string proc)
    {
        try
        {
            var name = proc.Trim().ToLowerInvariant();
            if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null; // 名字里不能带路径符号
            var file = Path.Combine(BundledIconsDir, name + ".png");
            if (!File.Exists(file)) return null;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad; // 读完就放掉文件，不占着
            image.DecodePixelWidth = 48;
            image.UriSource = new Uri(file);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch { return null; }
    }

    private ImageSource? IconOf(string proc, string? recordedPath)
    {
        if (icons.TryGetValue(proc, out var cached)) return cached;
        ImageSource? source = null;
        try
        {
            string? path = null;
            foreach (var process in Process.GetProcessesByName(proc))
            {
                try { path = process.MainModule?.FileName; } catch { }
                finally { process.Dispose(); }
                if (!string.IsNullOrEmpty(path)) break;
            }
            if (string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(recordedPath) && File.Exists(recordedPath)) path = recordedPath;
            if (string.IsNullOrEmpty(path)) path = KnownPath(proc);
            path ??= ShortcutFor(proc);
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                source = ShellIconOf(path); // 快捷方式（.lnk）只有它能解析出真图标
                if (source is null)
                {
                    // 有的 exe 拿不到 shell 图标，退回直接读 exe 自带的
                    try
                    {
                        using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                        if (icon != null)
                        {
                            source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                            source.Freeze();
                        }
                    }
                    catch { }
                }
                // 图标取到了，而且路径是真 exe（不是 .lnk 快捷方式）：记下来，以后没在运行也能找到
                if (source != null && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) RememberPath(proc, path);
            }
        }
        catch { }
        source ??= BundledIconOf(proc); // 本机没装、也没在运行：用自带的图标（tray\icons\进程名.png）
        icons[proc] = source;
        return source;
    }

    // 开始菜单（公共 + 个人）和桌面上的 .lnk 建两个索引：
    //  ① 快捷方式指向的 exe 文件名（qq.exe → qq）——最准，不会因为快捷方式名字相近认错；
    //  ② 快捷方式的名字（只认字母数字）——兜底，规则收得很紧，免得「line」认成「Python Online Documentation」。
    private long indexMs = -1; // 建索引用了多久（毫秒，自检用）
    private Dictionary<string, string>? exeTargets; // exe 文件名（无扩展名、小写）→ exe 完整路径

    private void BuildShortcutIndex()
    {
        if (shortcuts != null) return;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var built = new Dictionary<string, string>();
        var targets = new Dictionary<string, string>();
        dynamic? shell = null;
        try { shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!); } catch { }
        try
        {
            var dirs = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"),
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            };
            foreach (var dir in dirs)
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var file in Directory.EnumerateFiles(dir, "*.lnk", SearchOption.AllDirectories))
                {
                    var key = ShortcutKey(Path.GetFileNameWithoutExtension(file));
                    if (key.Length >= 3 && !built.ContainsKey(key)) built[key] = file;
                    if (shell is null) continue;
                    try
                    {
                        string target = shell.CreateShortcut(file).TargetPath ?? "";
                        if (target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        {
                            var exe = Path.GetFileNameWithoutExtension(target).ToLowerInvariant();
                            if (exe.Length > 0 && !targets.ContainsKey(exe)) targets[exe] = target;
                        }
                    }
                    catch { }
                }
            }
        }
        catch { }
        exeTargets = targets;
        indexMs = clock.ElapsedMilliseconds;
        shortcuts = built; // 建完才换上；中途出错也有一个能用的（可能不全）
    }

    private static string ShortcutKey(string text) =>
        new string(text.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    // 快捷方式（.lnk）指向的 exe；解析不出（商店应用、指向网址或脚本）就返回 null，不用 .lnk 本身取图标：
    // 系统拿不到真图标时会给一个白色的「快捷方式」通用图标，比不显示还难看
    private object? lnkShell;

    private string? ExeOfShortcut(string lnk)
    {
        try
        {
            lnkShell ??= Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
            string target = ((dynamic)lnkShell!).CreateShortcut(lnk).TargetPath ?? "";
            // 网页应用（Apple Music 这类 Edge/Chrome 的 PWA）的快捷方式指向 msedge_proxy.exe：图标是浏览器的，不是它自己的，不要
            if (target.EndsWith("_proxy.exe", StringComparison.OrdinalIgnoreCase)) return null;
            return target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(target) ? target : null;
        }
        catch { return null; }
    }

    private string? ShortcutFor(string proc)
    {
        BuildShortcutIndex();
        var name = proc.Trim().ToLowerInvariant();
        // ① 快捷方式指向的就是这个进程的 exe：最准
        if (exeTargets != null && exeTargets.TryGetValue(name, out var exePath) && File.Exists(exePath)) return exePath;
        if (shortcuts is null || shortcuts.Count == 0) return null;
        var key = ShortcutKey(proc);
        if (key.Length == 0) return null;
        // ② 名字和进程名完全一样（「cloudmusic」→「CloudMusic」）
        if (shortcuts.TryGetValue(key, out var exact)) return ExeOfShortcut(exact);
        // ③ 兜底的模糊匹配：太短的进程名（qq、line）不模糊，不然会认成别的软件；候选按名字长度排（最接近的优先），取第一个能解析出 exe 的
        if (key.Length >= 4)
            foreach (var file in shortcuts.Where(pair => pair.Key.StartsWith(key, StringComparison.Ordinal))
                         .OrderBy(pair => pair.Key.Length).ThenBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Value))
                if (ExeOfShortcut(file) is { } near) return near;
        if (key.Length >= 6)
            foreach (var file in shortcuts.Where(pair => pair.Key.Contains(key, StringComparison.Ordinal))
                         .OrderBy(pair => pair.Key.Length).ThenBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Value))
                if (ExeOfShortcut(file) is { } inside) return inside;
        return null;
    }

    // 从 exe 路径推进程名：先用正在跑的同路径进程（最准），再退回文件名（msedge.exe → msedge），
    // 最后才用 exe 内部名——内部名经常是「Microsoft Edge」这种带空格的，跟进程名对不上
    private static string ProcessNameOf(string path)
    {
        try
        {
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    if (string.Equals(process.MainModule?.FileName, path, StringComparison.OrdinalIgnoreCase))
                        return process.ProcessName.ToLowerInvariant();
                }
                catch { }
                finally { process.Dispose(); }
            }
        }
        catch { }
        var file = Path.GetFileNameWithoutExtension(path).Trim().ToLowerInvariant();
        if (file.Length > 0) return file;
        try
        {
            var name = (FileVersionInfo.GetVersionInfo(path).InternalName ?? "").Trim();
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
            if (name.Length > 0) return name.ToLowerInvariant();
        }
        catch { }
        return file;
    }

    // 开发自检用：量一下光标和提示字的横向位置（对不齐时看这个）
    internal string Measure()
    {
        try
        {
            var caret = NewAppBox.GetRectFromCharacterIndex(0, false);
            var hint = NewAppHint.TranslatePoint(new Point(0, 0), NewAppBox);
            // 顺手测一下安静时段那两个圆钮能不能被点中（拖不动的 bug 就是在这儿查的）
            var result = $"caretX={caret.X:0.##} caretTop={caret.Top:0.##} hintX={hint.X:0.##} hintTop={hint.Y:0.##} padding={NewAppBox.Padding}";
            try
            {
                var trackWidth = QuietTrack.ActualWidth;
                var middle = QuietTrack.ActualHeight / 2;
                var loX = ((TranslateTransform)quietLo.RenderTransform).X + QuietPad;
                var hiX = ((TranslateTransform)quietHi.RenderTransform).X + QuietPad;
                string NameOf(double x)
                {
                    var hit = QuietTrack.InputHitTest(new Point(x, middle)) as DependencyObject;
                    if (hit is null) return "null";
                    if (Inside(hit, quietLo)) return "thumbLo";
                    if (Inside(hit, quietHi)) return "thumbHi";
                    return hit.GetType().Name;
                }
                result += $" | track={trackWidth:0} loX={loX:0} hiX={hiX:0} hitLo={NameOf(loX)} hitHi={NameOf(hiX)} hitMid={NameOf(trackWidth / 2)}";
            }
            catch (Exception probe) { result += " | probe ERR " + probe.Message; }
            try
            {
                var names = new[] { "wps", "cloudmusic", "msedge", "photoshop", "excel", "potplayer" };
                result += " | icons: " + string.Join(",", names.Select(name =>
                    $"{name}={(icons.TryGetValue(name, out var got) ? (got is null ? "null" : "ok") : "none")}"));
                result += " | shortcuts=" + (shortcuts?.Count ?? -1) + " exeTargets=" + (exeTargets?.Count ?? -1) + " indexMs=" + indexMs;
                // 名单里每个软件的图标有没有找到（自检用：多跑几次对比是否稳定）
                result += " | allIcons: " + string.Join(",", icons.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}={(pair.Value is null ? "NONE" : "ok")}"));
            }
            catch { }
            return result;
        }
        catch (Exception error) { return "ERR " + error.Message; }
    }

    // 开发自检用：把窗口渲染成一张 PNG（FishTray.exe --context-shot x.png [--scroll 像素]）
    internal void Snapshot(string path, double scroll = 0)
    {
        if (scroll > 0) { Scroller.ScrollToVerticalOffset(scroll); }
        UpdateLayout();
        double width = Shell.ActualWidth, height = Shell.ActualHeight;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) dc.DrawRectangle(new VisualBrush(Shell), null, new Rect(0, 0, width, height));
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)Math.Ceiling(width * 2), (int)Math.Ceiling(height * 2), 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
