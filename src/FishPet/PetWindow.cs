using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace FishPet;

// The pet: a transparent topmost window with her in it, her speech bubble above her head, her reflexes when
// clicked, dragged or left alone (Behavior), her own body language from what the agent has her say (Body,
// Line), and her mirror. Everything the agent does goes through Execute (Tools.cs for what each tool means).
internal sealed partial class PetWindow : Window
{
    private readonly Settings settings;
    private readonly string? model;
    private readonly bool test;
    private readonly SpriteImage image = new() { Stretch = Stretch.Fill };
    private readonly BubbleWindow bubble;
    private readonly Speech speech;
    private readonly Behavior behavior;
    private readonly Body body;
    private readonly BubbleWindow mutter; // 待机自言自语的小气泡
    private readonly Speech mutterSpeech; // 嘀咕/小剧场/事件也用 Speech 打字，好让嘴跟着字动（2026-11-06）
    private Voice? voicePack; // 固定台词的录音（voice\ 或 voice_calm\）；没有这个文件夹就照旧只说话不上声
    private static readonly HttpClient Http = new();
    private readonly Random random = new();
    private readonly Dictionary<string, string> textCues = []; // 固定嘀咕文本 → 表情动作
    private readonly Dictionary<string, List<(string Text, int Level)>> touchLines = []; // 触碰反应的台词：反应名 → (台词, 至少要几级亲密度)
    // 摸头彩蛋的五句用抽签袋：一轮五句各一次，跨轮也尽量不连着同一句（本机加，2026-11-11）
    private readonly List<string> meltBag = [];
    private string? lastMeltLine;
    private readonly Updates updates;
    private readonly DispatcherTimer timer;
    // Out of the way while another program fills the screen (settings: hide_on_fullscreen), looked at twice a second.
    private bool hiddenForFullscreen;
    // 收起待命：程序和接口都开着，她不露面（窗口藏起、渲染暂停、待机表演停掉）；show / say / coax 会叫她出场。
    private bool standby, ready, hiding, coaxWoke, restarting;
    private int hideToken;
    private DateTime nextFullscreenLook;
    // 本机加（2026-11-11）：她的置顶偶尔会被人从外面撤掉（别的“总在最前”的程序、系统切全屏之类），
    // 普通窗口就压到她头上了。每秒看一次，掉了补回来；她被点、被摸、被拖时也抬到置顶层最上面
    // （WS_EX_NOACTIVATE 的窗口点了不会自己浮上来）。
    private DateTime nextTopmostLook;
    private DateTime topmostLostLogged;
    // 本机加（2026-11-11，2026-11-13 从合并前的备份挪回来）：上面那个“每秒看一次标记”不够。实测她可以在
    // 置顶标记还挂着的时候就被排到普通窗口（Edge、Claude）后面，那时只有真的再调一次“置顶”才会跳回最上层。
    // 所以每隔 0.7 秒看一眼 Z 序，谁压着她就把她和气泡一起抬上去。
    private DateTime nextRaise;
    private DateTime coveredLogged;
    private Player? player;
    // 哄睡演示（coax）：记住她原来的位置和大小，好归位。
    private double? coaxLeft, coaxTop;
    private double coaxScale;
    private string? coaxShape;
    private bool coaxFollowMouse;
    private bool coaxAutoReturn, coaxSpeechSeen;
    private DateTime? coaxReturnAt;
    // 自言自语让位：大肥鱼开口说话（或哄睡演出）时，嘀咕立刻收起，说完后再安静一小会儿。
    private DateTime mutterQuietUntil;
    private int mutterToken;
    private bool textIdleActive;
    private bool coaxHoldPose;
    private string? coaxStage4Text; // 第四层台词（由 AI 实时生成传入）
    private ControlServer? server;
    private HttpApi? api;
    private string? trouble;
    private bool exiting, exitReady;
    private long shapeChange;
    private Shape Shape => Shape.Find(settings.Shape);

    // Dragging her about.
    private bool dragging, dragged;
    private Point dragStart, windowStart;

    // What the window does to her: moving pushes her body, the taskbar hides what reaches it.
    private double lastLeft, lastTop, lookX = 9, lookY = 9;
    // 待机时鼠标停在同一个地方超过 5 秒，就暂时关掉视线跟随；鼠标一动就恢复。
    private NativePoint lastCursor;
    private DateTime lastCursorAt = DateTime.UtcNow;
    private bool mouseIdle;
    // 鼠标停住后视线回正：0 没在回、1 眼睛在回（头还没动）、2 头也在回。头比眼睛晚 HeadReturnDelay 秒开始，转得慢（速率越小越慢，平时是 10）。
    private int returnStage;
    private DateTime returnAt;
    private const double HeadReturnDelay = .5, HeadReturnRate = 1.6, EyeReturnRate = 5;
    private Rect open;

    // Her lines: one at a time, in order; a line of nothing but 〔〕 plays with no bubble.
    private Task lines = Task.CompletedTask;
    private Line? silent;
    private readonly Stopwatch silentClock = new();
    private DateTime? relaxAt;
    private const double LingerSeconds = 3.5;

    // Her recorded voice, and her mouth.
    private readonly MediaPlayer voicePlayer = new();
    // 用户嫌吵（2026-11-10）：把原来的 25% 响度当作新的满档，控制台 100% = 以前 25%
    private const double VoiceGain = 0.25;
    private float[]? voice;
    private DateTime voiceUntil;
    private double mouth;
    private const int VoiceStepMilliseconds = 33;

    // Every line she says is filmed, for looking back at afterwards; kept half an hour.
    private sealed record Photo(DateTime At, string Said, string Picture);
    private readonly List<Photo> photos = [];
    private List<BitmapSource>? film;
    private string filmSaid = "";
    private DateTime filmShot;
    private DateTime? filmEnded;
    private const int PhotosKept = 20, FilmFrames = 120;
    private static readonly TimeSpan PhotoLife = TimeSpan.FromMinutes(30), FilmEvery = TimeSpan.FromMilliseconds(150), FilmAfter = TimeSpan.FromMilliseconds(600);
    private const int MirrorHeight = 520, MirrorGridHeight = 360, MirrorShots = 6, MirrorColumns = 3;
    private const double MirrorSpan = .5;

    // What the user did to her, for the agent (recent_events).
    private sealed record Happening(long Sequence, DateTime At, string Kind, object Detail);
    private readonly List<Happening> happenings = [];
    private long sequence;

    internal PetWindow(Settings settings, string? model, bool test, string? pipe = null)
    {
        this.settings = settings; this.model = model; this.test = test;
        Condition.Birthday = settings.Birthday; // 生日那天的台词、小剧场（mutter.txt、skits.txt 里的 @生日）
        standby = settings.StartHidden && !test;
        Title = "大肥鱼";
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; ShowActivated = false; Topmost = true;
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        image.Opacity = 0;
        Content = new Grid { Children = { image } };
        bubble = new BubbleWindow(this, () => Height * Shape.HeadTop) { RecordAs = test ? null : "speech" };
        bubble.Quiet = test;
        bubble.Scale = settings.BubbleScale;
        mutter = new BubbleWindow(this, () => Height * Shape.HeadTop) { Scale = 0.85, RecordAs = test ? null : "mutter" };
        mutter.Quiet = test;
        LoadMutterFile();
        LoadSkits();
        LoadTouchFile();
        speech = new Speech(bubble) { Seconds = settings.BubbleSeconds };
        mutterSpeech = new Speech(mutter) { Seconds = -1 }; // 停留由嘀咕/小剧场/事件自己控制，Speech 不自动收
        voicePack = null;
        LoadVoicePack();
        voicePlayer.Volume = settings.Volume * VoiceGain; // 语音音量（settings: volume，控制台里能调；VoiceGain 是总衰减）
        _ = Tts.Warm(settings); // 提前把实时语音的第一次冷启动跑掉
        body = new Body(Send);
        updates = new Updates(Places.Root, Places.Log);
        behavior = new Behavior(Send, () => [.. settings.Outfit, .. body.Overlays], new Random()) { HandBusy = () => body.HandUp, Ended = body.Restore, IdleStarted = name => OnIdleStarted(name), TextIdle = text => PlayTextIdle(text), TextIdleMoods = textCues.Keys.ToArray(), SkitIdle = name => PlaySkit(name), SkitIdleMoods = skits.Keys.ToArray(),
            TextWeight = (name, local) => WeightOf(textConds, name, local), SkitWeightOf = (name, local) => WeightOf(skitConds, name, local),
            ChimeTexts = ChimeNow, ChimeBlocked = () => MutterBlocked || textIdleActive, Smitten = () => affection.Level >= Affection.Levels.Length };
        ApplySize();
        Place();
        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Background, (_, _) => Tick(), Dispatcher);
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            // Never takes the focus from what the user is doing, not in Alt+Tab.
            SetWindowLongPtr(handle, -20, (nint)((long)GetWindowLongPtr(handle, -20) | 0x8000080));
            // 本机修（2026-11-06）：Windows 默认不让窗口比屏幕大太多（最大跟踪尺寸≈屏幕+边框）。全身体型放到 2 倍时窗口比屏幕还高，
            // 高度被系统压矮约 20%，而画面还是按原高度渲染、被硬塞进矮窗口里，人就显得横向被拉扁。这里把上限放开。
            HwndSource.FromHwnd(handle)?.AddHook(NoWindowSizeLimit);
        };
        Loaded += (_, _) =>
        {
            Start();
            StartMusic(); // 跟着电脑里的音乐跳（PetWindow.Dance.cs）
            LaunchTray();
            if (!test) // 开发用透明帧导出：插件\record\on.json 在就录（Recorder.cs，本机加 2026-11-08）
            {
                Recorder.BoostChanged += ApplySize;
                Recorder.Init(Dispatcher, RecordLayout);
            }
            server = new ControlServer(Execute, pipe);
            try { api = HttpApi.Start(Places.ApiFile, test ? 0 : HttpApi.DefaultPort, (name, args, token) => { if (!HttpApi.Passive.Value) lastHttp = DateTime.UtcNow; return Execute(name, args, token); }); }
            catch (Exception error) { Places.Log("网页接口开不起来：" + error.Message); }
            // A newer plugin on GitHub, looked for now and twice a day; the agent hears of it with its next tool (Execute).
            if (!test) _ = Task.Run(async () =>
            {
                while (true)
                {
                    if (settings.CheckUpdates) await updates.Check();
                    await Task.Delay(TimeSpan.FromHours(12));
                }
            });
        };
        PreviewMouseLeftButtonDown += Down;
        PreviewMouseMove += Move;
        PreviewMouseLeftButtonUp += Up;
        PreviewMouseRightButtonDown += StrokeDown; // 右键抚摸、悬停反应、拖拽惯性和头晕（PetWindow.Touch.cs）
        PreviewMouseRightButtonUp += StrokeUp;
        PreviewMouseMove += HoverMove;
        MouseLeave += (_, _) => hoverZone = null;
        LostMouseCapture += (_, _) => { if (dragging) { dragging = false; if (dragged) Dropped(); } if (stroking) EndStroke(); };
        Closing += Leaving;
        Closed += (_, _) => { StopMusic(); TouchClosed(); Recorder.Stop(wait: true); server?.Dispose(); api?.Dispose(); player?.Dispose(); bubble.Close(); };
    }

    private void Send(string command) => player?.Send(command);

    // 录制的版面信息，都是帧像素：气泡尖儿该对着哪儿（窗口中线、头顶往下 6 DIP），气泡的样式参数。
    private object RecordLayout()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        double k = dpi.DpiScaleX * Recorder.Boost;
        return new
        {
            dpiScale = dpi.DpiScaleX, supersample = Recorder.Boost, windowDip = new { width = Width, height = Height },
            shape = settings.Shape, scale = settings.Scale, texScale = settings.TexScale,
            bubbleTip = new { x = Math.Round(Width / 2 * k, 1), y = Math.Round((Height * Shape.HeadTop + 6) * k, 1) },
            bubbleStyle = new { scale = settings.BubbleScale, mutterScale = 0.85, font = "Microsoft YaHei UI", fontSizeDip = 15, lineHeightDip = 22, maxWidthDip = 300,
                padding = new[] { 18, 14, 14, 14 + Bubble.PointerHeight }, cornerRadius = 24, border = 1.5, borderColor = "#506098", background = "#FFFBF4", textColor = "#323854", pointerHalfWidth = 8, pointerHeight = Bubble.PointerHeight },
        };
    }

    // 本机加的（2026-11-06）：桌宠起来时顺手带起任务栏托盘里的大肥鱼控制台。先找插件里编好的（插件\tray\app\FishTray.exe，setup.ps1 编），
    // 再找老位置（文档\大肥鱼控制台\app\FishTray.exe）。它自己只留一个、桌宠退场后自己也退；没装它就什么也不做。
    private void LaunchTray()
    {
        if (test) return;
        try
        {
            var exe = new[] { Path.Combine(Places.Root, "tray", "app", "FishTray.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "大肥鱼控制台", "app", "FishTray.exe") }.FirstOrDefault(File.Exists);
            if (exe != null)
            {
                // 插件文件夹常被沙箱打上“低完整性”标签，桌宠因此低权限运行，直接拉起的托盘会继承低权限：写不了文件、图标也出不来。
                // 所以交给计划任务 FishTray（普通权限，装控制台时建的）来拉；没有这个任务才退回直接拉起。
                using var ask = Process.Start(new ProcessStartInfo("schtasks.exe", "/run /tn FishTray") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
                if (ask is null || !ask.WaitForExit(5000) || ask.ExitCode != 0)
                    Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! })?.Dispose();
            }
        }
        catch (Exception error) { Places.Log("托盘没起来：" + error.Message); }
    }

    // ---- Her renderer ----

    private void Start()
    {
        if (model == null) { Trouble("没找到模型：把模型文件夹放进插件的 model 文件夹里（里面要有 " + Places.ModelFile + "）。"); return; }
        if (!File.Exists(Places.Renderer)) { Trouble("没找到渲染程序：先运行插件里的 setup.ps1 编译它。"); return; }
        timeBeginPeriod(1); // 摸头那只手要更高帧：把系统计时精度提到 1ms（关闭时恢复）
        var started = new Player(image);
        started.Ready += Ready;
        started.Failed += message => Trouble("渲染出问题了：" + message);
        player = started;
        danceSent.Clear(); // 新渲染器不知道上一轮按住过什么，跳舞的参数要重新发一遍（PetWindow.Dance.cs）
        lastLeft = Left; lastTop = Top;
        var dpi = VisualTreeHelper.GetDpi(this);
        var scale = dpi.DpiScaleX * Recorder.Boost; // 宽高用同一系数，保证渲染画面比例始终与窗口一致（不会变形）；录制 supersample 时按几倍出图
        Recorder.Expect((int)(Width * scale), (int)(Height * scale));
        started.Start(model, Shape, settings.Outfit, (int)(Width * scale), (int)(Height * scale), settings.Fps, settings.TexScale);
        body.Forget(); silent = null; relaxAt = null;
        danceSent.Clear(); // 新渲染器不知道上一轮按住过什么，跳舞的参数要重新发一遍（PetWindow.Dance.cs）
        behavior.Resume(DateTime.UtcNow, settings.IdleSeconds);
        timer.Start();
        open = default;
        UpdateCut();
    }

    // 换清晰度档位后重启渲染器：贴图按新的 texscale 重新加载（表情回到默认，衣服配饰和体型会重新穿上）。
    private void RestartRenderer()
    {
        restarting = true;
        player?.Dispose();
        Start();
    }

    private void Trouble(string message)
    {
        trouble = message;
        Places.Log(message);
        bubble.Say(message);
    }

    // She comes in and greets once her body type has settled.
    private void Ready()
    {
        ready = true;
        if (standby) return; // 收着待命：先不露面、不问候
        if (restarting) { restarting = false; return; } // 换清晰度重启渲染：不重新问候
        image.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)));
        behavior.Show("问候", DateTime.UtcNow);
        body.Apply("抚胸");
        WelcomeSoon(); // 新装第一次出场：自己说一段欢迎（Welcome.cs）
    }

    private void ApplySize()
    {
        var shape = Shape;
        // Her feet stay where they are, not the window's corner, so resizing does not lift her out of the taskbar.
        double middle = Left + Width / 2, feet = Top + Height * shape.FootAt;
        Width = Math.Round(shape.Width * settings.Scale);
        Height = Math.Round(shape.Height * settings.Scale);
        if (double.IsFinite(middle) && double.IsFinite(feet)) { Left = middle - Width / 2; Top = feet - Height * shape.FootAt; }
        if (PresentationSource.FromVisual(this) != null)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var scale = dpi.DpiScaleX * Recorder.Boost; // 宽高用同一系数，避免非整数缩放造成轻微变形；录制 supersample 时按几倍出图
            Recorder.Expect((int)(Width * scale), (int)(Height * scale));
            Send($"resize {(int)(Width * scale)} {(int)(Height * scale)}");
        }
        UpdateCut();
    }

    // Where she was, or the bottom right corner half sunk behind the taskbar. In a test, out of sight.
    private void Place()
    {
        if (test) { Left = -6000; Top = 100; return; }
        var area = SystemParameters.WorkArea;
        Left = settings.Left ?? area.Right - Width - 8;
        Top = settings.Top ?? area.Bottom - Height * .5;
        Clamp();
    }

    // She may sink behind the taskbar, but her face stays out.
    private void Clamp()
    {
        if (test) return;
        var area = SystemParameters.WorkArea;
        var shape = Shape;
        var bounds = new Rect(Width * shape.Side, Height * shape.HeadTop, Width * (1 - 2 * shape.Side), Height * (shape.HeadAt + .12 - shape.HeadTop));
        if (!settings.KeepOnScreen)
        {
            // 能拖出屏幕：整块桌面（所有显示器）外面最多藏到只剩 60 像素的身子，免得找不回来
            const double keep = 60;
            double x = SystemParameters.VirtualScreenLeft, y = SystemParameters.VirtualScreenTop;
            double r = x + SystemParameters.VirtualScreenWidth, b = y + SystemParameters.VirtualScreenHeight;
            Left = Math.Clamp(Left, x - bounds.Right + keep, Math.Max(x - bounds.Right + keep, r - bounds.Left - keep));
            Top = Math.Clamp(Top, y - bounds.Bottom + keep, Math.Max(y - bounds.Bottom + keep, b - bounds.Top - keep));
            return;
        }
        Left = Math.Clamp(Left, area.Left - bounds.Left, Math.Max(area.Left - bounds.Left, area.Right - bounds.Right));
        Top = Math.Clamp(Top, area.Top - bounds.Top, Math.Max(area.Top - bounds.Top, area.Bottom - bounds.Bottom));
    }

    private void SavePlace()
    {
        if (test) return;
        settings.Left = Left; settings.Top = Top;
        settings.Save();
    }

    // 哄睡演示（coax）：把窗口摆到屏幕各处，让 Agent 快速看三层递进的动作效果。
    private object Coax(JsonElement args)
    {
        int stage = args.GetProperty("stage").GetInt32();
        if (stage != 0 && Wake()) coaxWoke = stage is >= 1 and <= 4; // 收着时被叫出来演哄睡：演完再自己收回去
        YieldMutter(); // 哄睡演出也优先：嘀咕让位
        coaxStage4Text = args.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null; // 第四层台词（AI 实时生成）
        if (stage == 0)
        {
            CoaxReturn();
            return Tools.Text(new { stage = 0, restored = true });
        }
        if (stage == 5) { DemoIdle(); return Tools.Text(new { stage = 5, demo = "待机小动作" }); }
        if (stage == 6) // 演示待机小剧场：text 给小剧场名，不给就随机一个
        {
            if (skits.Count == 0) return Tools.Error("skits.txt 里还没有小剧场。");
            var pick = coaxStage4Text != null && skits.ContainsKey(coaxStage4Text) ? coaxStage4Text : skits.Keys.ElementAt(random.Next(skits.Count));
            PlaySkit(pick, force: true);
            return Tools.Text(new { stage = 6, skit = pick, all = skits.Keys.ToArray() });
        }
        RememberPlace();
        var pos = RunStage(stage);
        if (pos == null) return Tools.Error("stage 要 0–3。");
        return Tools.Text(new { stage, left = pos.Value.Left, top = pos.Value.Top, scale = settings.Scale });
    }

    // 演哄睡前记下原来的体型、大小、位置，演完归位用。
    private void RememberPlace()
    {
        if (coaxScale != 0) return;
        coaxShape = settings.Shape;
        coaxScale = settings.Scale;
        coaxLeft = Left; coaxTop = Top;
        coaxFollowMouse = settings.FollowMouse;
    }

    // 记下了位置但这一层没演成（被收起、退出打断）：把记录丢掉，等于没记过。
    private void ForgetPlace()
    {
        coaxShape = null;
        coaxScale = 0;
        coaxLeft = coaxTop = null;
    }

    // 摆好某一层的体型、大小和位置并出场。返回出场位置；stage 非法返回 null。
    private (double Left, double Top)? RunStage(int stage, int fadeInMs = 220)
    {
        coaxEpoch++; // 正在归位的话作废
        var area = SystemParameters.WorkArea;
        double targetLeft, targetTop;
        switch (stage)
        {
            case 1: // 第一层：屏幕正中，温柔关心
                if (Shape.Id != "full") { settings.Shape = "full"; player?.SetShape(Shape); }
                settings.Scale = 2;
                settings.FollowMouse = false;
                ApplySize();
                targetLeft = area.Left + (area.Width - Width) / 2;
                targetTop = area.Top + area.Height * 0.40 - Height * Shape.HeadTop;
                coaxAutoReturn = true; coaxSpeechSeen = false; coaxReturnAt = null;
                break;
            case 2: // 第二层：屏幕正中，严肃
                if (Shape.Id != "full") { settings.Shape = "full"; player?.SetShape(Shape); }
                settings.Scale = 2;
                settings.FollowMouse = false;
                ApplySize();
                targetLeft = area.Left + (area.Width - Width) / 2;
                targetTop = area.Top + area.Height * 0.40 - Height * Shape.HeadTop;
                coaxAutoReturn = true; coaxSpeechSeen = false; coaxReturnAt = null;
                break;
            case 3: // 第三层：屏幕正中，屑屑眼「时间到了」
                if (Shape.Id != "full") { settings.Shape = "full"; player?.SetShape(Shape); }
                settings.Scale = 2;
                settings.FollowMouse = false;
                ApplySize();
                targetLeft = area.Left + (area.Width - Width) / 2;
                targetTop = area.Top + area.Height * 0.40 - Height * Shape.HeadTop;
                coaxAutoReturn = true; coaxSpeechSeen = false; coaxReturnAt = null;
                break;
            case 4: // 第四层：屏幕正中，黑脸质问
                if (Shape.Id != "full") { settings.Shape = "full"; player?.SetShape(Shape); }
                settings.Scale = 2;
                settings.FollowMouse = false;
                ApplySize();
                targetLeft = area.Left + (area.Width - Width) / 2;
                targetTop = area.Top + area.Height * 0.40 - Height * Shape.HeadTop;
                coaxAutoReturn = true; coaxSpeechSeen = false; coaxReturnAt = null;
                coaxHoldPose = true; // 说完后保持黑脸盯人，直到归位
                body.Apply("闭眼 黑脸"); // 出场就是闭眼 + 黑脸
                Stage4Sequence(); // 1 秒后睁眼，再 1 秒后慢慢质问
                break;
            default:
                return null;
        }
        AppearAt(targetLeft, targetTop, fadeInMs: fadeInMs);
        return (targetLeft, targetTop);
    }

    // 第四层演出：出场时已经闭眼+黑脸，1 秒后突然睁眼盯前方，再大前倾，再 1 秒后一个字一个字质问（每字 0.5 秒）。
    private async void Stage4Sequence()
    {
        await Task.Delay(1200); // 出场淡入 + 闭眼黑脸停留约 1 秒
        body.Apply("睁大 正视"); // 突然睁眼，直勾勾盯前方
        await Task.Delay(350); // 睁眼后停顿一下
        body.Apply("大前倾"); // 身子猛地向前倾
        await Task.Delay(1000);
        SayLine(coaxStage4Text ?? "为 什 么 还 没 睡 ？", null, 2); // 每字 0.5 秒（台词由 AI 实时生成）
    }

    // 归位（用户 2026-11-13 改）：先快速鞠躬淡出退场，趁看不见恢复原来的体型、大小、视线跟随，
    // 再在上一次的位置快速淡入。不再是正中的她一下变小、一下跳回去。
    private int coaxEpoch; // 归位的路上又来了新的一层（RunStage）就把这次归位作废，别两边抢
    private async void CoaxReturn()
    {
        coaxAutoReturn = false; coaxSpeechSeen = false; coaxReturnAt = null; // 别每帧再触发
        int epoch = ++coaxEpoch;
        try
        {
            bool visible = await QuickLeave();
            if (epoch != coaxEpoch) return;
            if (coaxShape != null) { settings.Shape = coaxShape; player?.SetShape(Shape); coaxShape = null; }
            settings.FollowMouse = coaxFollowMouse;
            if (coaxScale > 0) { settings.Scale = coaxScale; ApplySize(); }
            double left = coaxLeft ?? Left, top = coaxTop ?? Top;
            coaxLeft = coaxTop = null; coaxScale = 0;
            coaxHoldPose = false;
            if (visible) AppearAt(left, top, () => body.Relax(), EnterFadeMs); // 在看不见的时候收起黑脸盯人的样子
            else { body.Relax(); Left = left; Top = top; } // 被收起/退出打断：她们自己管淡出，这里只把状态放回去
        }
        catch (Exception error) { Places.Log("归位出错：" + error.Message); }
        if (epoch == coaxEpoch && coaxWoke) { coaxWoke = false; _ = HideAfterCoax(); }
    }

    private async Task HideAfterCoax()
    {
        await Task.Delay(1000);
        await HideAway();
    }

    // ---- 收起待命 / 出场 ----

    // 出场：窗口显出来、渲染恢复。返回是不是真的从收起状态出来的（本来就在就是 false）。
    private bool Wake(bool greet = false)
    {
        hideToken++; // 正在收起的话，作废
        if (hiding) { hiding = false; image.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200))); }
        if (!standby) return false;
        standby = false;
        alarmLast = DateTime.Now; // 收着的时候主计时器不转，错过的哄睡/午饭提醒不补（否则出来后 2 分钟内会被补演一遍，用户 2026-11-13 不要）
        StartMusic(); // 出来了才开始听歌（收着的时候不白开采集进程）。要在 standby 落下之后：StartMusic 自己会看 standby
        nextFullscreenLook = default;
        if (player is { } current) UpdateFullscreen(current);
        RaiseAboveAll(); // 从收起里出来：窗口离开的这段时间可能已经被别的窗口压住了，先回到最上层
        if (ready) image.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)));
        if (greet) { behavior.Show("问候", DateTime.UtcNow); body.Apply("抚胸"); }
        WelcomeSoon(); // 新装、而且一开就收着：被叫出来的第一次也说欢迎（Welcome.cs）
        return true;
    }

    // 收起：等话说完，鞠躬，淡出，再藏起窗口、停掉渲染；程序和接口都还开着。中途被叫出来（Wake）就作废。
    private async Task HideAway()
    {
        if (standby || hiding || exiting || test) return;
        int token = ++hideToken;
        hiding = true;
        YieldMutter();
        for (int wait = 0; wait < 150 && speech.IsSaying && token == hideToken; wait++) await Task.Delay(200);
        if (token != hideToken) return;
        behavior.Show("告别", DateTime.UtcNow);
        await Task.Delay(1600);
        if (token != hideToken) return;
        image.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(500)));
        await Task.Delay(600);
        if (token != hideToken || exiting) return;
        hiding = false; standby = true;
        nextFullscreenLook = default;
        mutterSpeech.Stop();
        StopMusic(); // 收进待命就别再听了，省一个常驻的采集进程
    }

    // 演示全部待机小动作（临时给用户看）。
    private async void DemoIdle()
    {
        string[] moods = ["张望", "哼歌", "害羞", "得意", "思考", "打哈欠", "打瞌睡", "歪头", "发呆", "叹气", "玩狗尾草"];
        foreach (var name in moods)
        {
            behavior.Show(name, DateTime.UtcNow, 1, force: true);
            await Task.Delay(4200);
        }
        behavior.ClearActivities();
        body.Relax();
    }

    // ───────── 待机自言自语：嘀咕、固定文本、小剧场、临时道具 ─────────
    // 说话优先：只要大肥鱼开口或在哄睡演出，这里的一切都让位（见 YieldMutter / MutterBlocked）。

    private sealed record SkitStep(string Text, string? Cue, double Seconds);
    private sealed record Skit(string[] Props, SkitStep[] Steps);
    private readonly Dictionary<string, Skit> skits = [];
    private readonly Dictionary<string, string[]> textProps = []; // 固定嘀咕文本 → 临时道具
    private readonly HashSet<string> tempProps = [];               // 现在临时戴着的配饰（模型里的表情名）
    private DateTime propsUntil;

    // 待机原版动作配的临时道具：动作名 → (概率, 道具)。
    private static readonly Dictionary<string, (double Chance, string Prop)> IdlePropTable = new()
    {
        ["思考"] = (.4, "单片眼镜"), ["张望"] = (.25, "单片眼镜"), ["得意"] = (.35, "兔牙"),
        ["叹气"] = (.2, "钢盆"), ["哼歌"] = (.15, "兔牙"), ["发呆"] = (.2, "黑框眼镜"),
    };

    // 台词、小剧场的条件（时段、星期、日期、整点……，见 Conditions.cs）：名字 → 条件；没写条件的不在表里，永远可用。
    private readonly Dictionary<string, Condition> textConds = [];
    private readonly Dictionary<string, Condition> skitConds = [];

    // 抽签权重：没条件 1；整点专用的不参加平时抽签 0；条件符合 Boost，不符合 0。
    private int WeightOf(Dictionary<string, Condition> table, string name, DateTime local) =>
        !table.TryGetValue(name, out var condition) ? 1 : condition.Chime ? 0 : condition.Matches(local, affection.Level) ? condition.Boost : 0;

    // 这个钟点该报的台词：整点专用、条件符合（几点、时段、星期、日期）的。
    private string[] ChimeNow(DateTime local) =>
        textConds.Where(item => item.Value.Chime && item.Value.Matches(local, affection.Level)).Select(item => item.Key).ToArray();

    // mutter.txt：每行「文本|动作|道具|条件」，后三项可省（没有道具但有条件时，道具那栏留空：「文本|动作||深夜」）。
    private void LoadMutterFile()
    {
        try
        {
            foreach (var line in File.ReadAllLines(Path.Combine(Places.Root, "mutter.txt")))
            {
                if (line.TrimStart().StartsWith("//")) continue;
                var parts = line.Split('|', 4);
                if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[0])) continue;
                if (parts.Length == 4)
                {
                    var condition = Condition.Parse(parts[3], out var bad);
                    if (condition == null) { Places.Log($"mutter.txt 的条件读不懂「{bad}」，这行先不用：{parts[0]}"); continue; }
                    if (!condition.Plain) textConds[parts[0]] = condition;
                }
                textCues[parts[0]] = parts[1].Trim();
                if (parts.Length >= 3 && parts[2].Trim().Length > 0) textProps[parts[0]] = ParseProps(parts[2]);
            }
        }
        catch { }
    }

    private static string[] ParseProps(string text) =>
        text.Split([' ', '　', '、', '，', ','], StringSplitOptions.RemoveEmptyEntries).Where(name => PetProps.Names.Contains(name)).Distinct().ToArray();

    // 固定台词的录音包（本机加，2026-11-10）：second = voice\，calm = voice_calm\。切换时重新读索引，不用重启。
    private void LoadVoicePack()
    {
        var folder = Path.Combine(Places.Root, settings.VoicePack == "calm" ? "voice_calm" : "voice");
        if (settings.VoicePack == "calm" && !Directory.Exists(folder)) folder = Path.Combine(Places.Root, "voice");
        voicePack = Voice.Load(folder);
        Places.Log($"语音包：{(voicePack == null ? "没有（只说话）" : $"{voicePack.Count} 条")}（{settings.VoicePack}）");
    }

    // touch.txt：「反应|台词」或「反应|台词|亲密3」，点她身体不同位置时随机念一句（反应见 Behavior.ClickMood：摸头/戳脸/开心/护裙/震惊/心虚，连点 5 下是「挨打」，
    // 亲密度满档时戳裙子是「娇羞」）。写了「亲密N」的那句要到第 N 级才会说，而且级别越高的句子越常被挑中（本机加，2026-11-08）。
    private void LoadTouchFile()
    {
        touchLines.Clear();
        // 冷静、沉稳包有三条触碰台词换成 语音参考9/10/11 原素材，字幕也跟着换（touch_calm.txt）。
        var touchFile = settings.VoicePack == "calm" && File.Exists(Path.Combine(Places.Root, "touch_calm.txt")) ? "touch_calm.txt" : "touch.txt";
        // touch.txt 之外，右键抚摸、悬停、头晕的台词（stroke.txt / hover.txt / dizzy.txt）格式一样，一起读进来；哪个文件没有就跳过。
        foreach (var file in new[] { touchFile, "stroke.txt", "hover.txt", "dizzy.txt" })
        try
        {
            foreach (var line in File.ReadAllLines(Path.Combine(Places.Root, file)))
            {
                if (line.TrimStart().StartsWith("//")) continue;
                var parts = line.Split('|', 3);
                if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1])) continue;
                int level = 0;
                if (parts.Length == 3 && parts[2].Trim().Length > 0)
                {
                    var condition = Condition.Parse(parts[2], out var bad);
                    if (condition == null) { Places.Log($"{file} 的条件读不懂「{bad}」，这行先不用：{parts[1]}"); continue; }
                    level = condition.MinLevel;
                }
                var key = parts[0].Trim();
                if (!touchLines.TryGetValue(key, out var list)) touchLines[key] = list = [];
                list.Add((parts[1].Trim(), level));
            }
        }
        catch { }
    }

    // skits.txt：「# 名字」开头一个小剧场（名字后面可以加「@条件」，如「# 生日快乐 @10月21日」）；「@道具 单片眼镜 兔牙」可选；之后每行「台词|动作|停留秒数」，台词可空（只做动作）、秒数可省（3.5）。
    private void LoadSkits()
    {
        try
        {
            string? name = null; Condition? when = null; var props = new List<string>(); var steps = new List<SkitStep>();
            void Flush()
            {
                if (name != null && steps.Count > 0)
                {
                    skits[name] = new Skit([.. props], [.. steps]);
                    if (when is { Plain: false }) skitConds[name] = when;
                }
                props.Clear(); steps.Clear();
            }
            foreach (var raw in File.ReadAllLines(Path.Combine(Places.Root, "skits.txt")))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("//")) continue;
                if (line.StartsWith('#'))
                {
                    Flush();
                    var title = line.TrimStart('#').Trim();
                    var at = title.IndexOf('@');
                    name = (at >= 0 ? title[..at] : title).Trim();
                    string? bad = null;
                    when = at >= 0 ? Condition.Parse(title[(at + 1)..], out bad) : Condition.Always;
                    if (when == null) { Places.Log($"skits.txt 的条件读不懂「{bad}」，这个小剧场先不用：{name}"); name = null; }
                    continue;
                }
                if (line.StartsWith("@道具")) { props.AddRange(ParseProps(line[3..])); continue; }
                var parts = line.Split('|', 3);
                var cue = parts.Length > 1 && Body.CueReadable(parts[1].Trim(), 8) ? parts[1].Trim() : null;
                double seconds = parts.Length > 2 && double.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s) ? Math.Clamp(s, 1, 10) : 3.5;
                if (parts[0].Trim().Length > 0 || cue != null) steps.Add(new SkitStep(parts[0].Trim(), cue, seconds));
            }
            Flush();
        }
        catch { }
    }

    // 戴上临时道具：只戴她现在没戴的；眼镜不叠（主人戴着的、读东西时戴的都算）。seconds 后自动摘。
    private void PutOn(IEnumerable<string> labels, double seconds)
    {
        foreach (var label in labels)
        {
            var expression = Outfit.Accessories.FirstOrDefault(item => item.Label == label || item.Expression == label).Expression;
            if (expression == null || settings.Outfit.Contains(expression) || tempProps.Contains(expression)) continue;
            if (expression is "黑框眼镜" or "单片眼镜")
            {
                bool glassesOn = behavior.GlassesOnForWork || tempProps.Contains("黑框眼镜") || tempProps.Contains("单片眼镜")
                    || settings.Outfit.Contains("黑框眼镜") || settings.Outfit.Contains("单片眼镜");
                if (glassesOn) continue;
            }
            tempProps.Add(expression);
            Send($"exp {expression} 1");
        }
        if (tempProps.Count > 0) { var until = DateTime.UtcNow.AddSeconds(seconds); if (until > propsUntil) propsUntil = until; }
    }

    // 摘掉临时道具；主人用 dress 戴着的不动。
    private void TakeOffProps()
    {
        if (tempProps.Count == 0) return;
        foreach (var expression in tempProps)
            if (!settings.Outfit.Contains(expression)) Send($"exp {expression} 0");
        tempProps.Clear();
    }

    // 待机原版动作开始时：有概率顺手戴个临时道具。
    private void IdlePropsFor(string action)
    {
        if (MutterBlocked || !IdlePropTable.TryGetValue(action, out var item) || random.NextDouble() > item.Chance) return;
        PutOn([item.Prop], Behavior.Moods[action].Seconds + 1);
    }

    // 待机小动作（原版11个）开始时：顺手可能戴个道具，再按概率现问模型嘀咕一句（连动作、道具一起配）。
    private void OnIdleStarted(string action)
    {
        IdlePropsFor(action);
        MutterFor(action);
    }

    private async void MutterFor(string action)
    {
        if (FocusWork) return; // 专注陪伴期间不嘀咕（本机加，2026-11-09）
        if (string.IsNullOrWhiteSpace(settings.LlmApiKey)) return;
        if (random.NextDouble() > settings.MutterChance) return; // 随机概率，避免太吵
        if (MutterBlocked) return;
        int token = ++mutterToken;
        try
        {
            var said = await Mutter.Ask(Http, settings.LlmBaseUrl, settings.LlmApiKey, settings.LlmModel, action);
            // 等模型回话的这段时间里，她可能已经开口说话了：那就丢掉这句嘀咕。
            if (said == null || token != mutterToken || MutterBlocked) return;
            if (said.Cue != null) { body.Apply(said.Cue); textIdleActive = true; }
            if (said.Props.Length > 0) PutOn(said.Props, 5);
            mutterSpeech.Say(new Line(said.Text, body.Apply));
            await Task.Delay(4000);
            if (token != mutterToken) return;
            mutterSpeech.Stop();
            if (said.Cue != null) { textIdleActive = false; body.Relax(); }
        }
        catch { /* 网络或解析失败就静默 */ }
    }

    // 抽到固定文本：显示文本 + 按文本配的表情动作 + 临时道具。
    private async void PlayTextIdle(string text)
    {
        if (FocusWork) return; // 专注时不嘀咕
        if (MutterBlocked) return;
        int token = ++mutterToken;
        textIdleActive = true;
        if (textCues.TryGetValue(text, out var cue)) body.Apply(cue);
        if (textProps.TryGetValue(text, out var props)) PutOn(props, 6);
        var voiceSeconds = Speak(text);
        mutterSpeech.Say(new Line(text, body.Apply));
        await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, (voiceSeconds ?? 0) + 0.4)));
        if (token != mutterToken) return; // 中途被说话打断了，收尾已经做过
        textIdleActive = false;
        mutterSpeech.Stop();
        TakeOffProps();
        body.Relax();
    }

    // 小剧场：几句话 + 动作 + 道具连着演。force：演示用，不受「刚说完要安静」限制。
    private async void PlaySkit(string name, bool force = false)
    {
        if (FocusWork && !force) return; // 专注陪伴期间不演小剧场（连语音一起停）
        if (!skits.TryGetValue(name, out var skit)) return;
        if (!force && MutterBlocked)
        {
            // 正说着话、或刚说完要安静：这次先让过去，45 秒后再试（别白白等满一个待机间隔）
            behavior.SkitMissed(DateTime.UtcNow);
            return;
        }
        behavior.SkitPlayed(DateTime.UtcNow);
        Places.Log("小剧场：" + name);
        int token = ++mutterToken;
        skitToken = token; // 演出期间暂时不跟鼠标（Tick 里看 SkitOn）；被打断时 mutterToken 一变就自动算演完
        textIdleActive = true;
        PutOn(skit.Props, skit.Steps.Sum(step => step.Seconds) + 1);
        foreach (var step in skit.Steps)
        {
            if (step.Cue != null) body.Apply(step.Cue);
            // 台词里用全角「｜」隔开几句，每次随机演其中一句（2026-11-06）。
            var said = step.Text.Contains('｜') ? step.Text.Split('｜')[random.Next(step.Text.Split('｜').Length)].Trim() : step.Text;
            var voiceSeconds = said.Length > 0 ? Speak(said) : null;
            if (said.Length > 0) mutterSpeech.Say(new Line(said, body.Apply)); else mutterSpeech.Stop();
            // 小剧场有录音的句子按录音长度接下一句，只留 0.45 秒气口；没录音 / 纯动作才用脚本里的停留秒数。
            var wait = said.Length > 0 && voiceSeconds != null
                ? Math.Max(0.9, voiceSeconds.Value + 0.45)
                : Math.Max(step.Seconds, said.Length * 0.12 + 0.3); // 没录音时按字数留足气泡出完的时间（上游 2026-11-12）
            await Task.Delay(TimeSpan.FromSeconds(wait));
            if (token != mutterToken || exiting) return; // 被说话打断了，收尾已经做过
        }
        textIdleActive = false;
        skitToken = 0;
        mutterSpeech.Stop();
        TakeOffProps();
        body.Relax();
    }

    private int skitToken;
    private bool SkitOn => skitToken != 0 && skitToken == mutterToken;

    // 嘀咕现在能不能出来：她在说话、放语音、做哄睡演出，或刚说完不久，都不行。
    private bool MutterBlocked =>
        exiting || speech.IsSaying || voice != null || silent != null || coaxAutoReturn || textIdleActive || DateTime.UtcNow < mutterQuietUntil || (panelHold && body.Posing);

    // 她要开口了：正在演的嘀咕/小剧场马上收起，在途的作废，临时道具摘掉，说话的优先权给她。
    private void YieldMutter()
    {
        mutterToken++;
        mutterQuietUntil = DateTime.UtcNow.AddSeconds(10);
        mutterSpeech.Stop();
        TakeOffProps();
        foreach (var overlay in poseOverlays) body.Apply("-" + overlay); // 组合神情被打断：临时加的脸上表情也收回
        poseOverlays.Clear();
        if (textIdleActive) { textIdleActive = false; body.Relax(); }
    }

    // 出场/换位：先淡出消失，再到新位置淡入（去掉滑动效果）。mid 在淡出后、换位前做。
    private async void AppearAt(double left, double top, Action? mid = null, int fadeInMs = 220)
    {
        image.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(120)));
        await Task.Delay(140);
        mid?.Invoke();
        Left = left; Top = top;
        RaiseAboveAll(); // 换位/出场后先回到最上层（哄睡把她摆到屏幕正中时，正好压在别的窗口上）
        image.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(fadeInMs)));
    }

    // A topmost window draws over the taskbar: whatever of her reaches it is not drawn, and clicks there
    // pass through, so she stands behind it.
    private void UpdateCut()
    {
        if (!IsVisible || test) return;
        var work = SystemParameters.WorkArea;
        var monitor = new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
        var window = new Rect(Left, Top, Width, Height);
        double left = work.Left > monitor.Left ? Math.Clamp(work.Left - window.Left, 0, window.Width) : 0;
        double top = work.Top > monitor.Top ? Math.Clamp(work.Top - window.Top, 0, window.Height) : 0;
        double right = work.Right < monitor.Right ? Math.Clamp(work.Right - window.Left, left, window.Width) : window.Width;
        double bottom = work.Bottom < monitor.Bottom ? Math.Clamp(work.Bottom - window.Top, top, window.Height) : window.Height;
        var uncovered = new Rect(left, top, right - left, bottom - top);
        // 本机改（2026-11-12）：按住左键拖动时不挡，全身盖过任务栏好看全貌；放手后再躲回任务栏后面。
        if (dragging && dragged) uncovered = new Rect(0, 0, Width, Height);
        if (uncovered == open) return;
        open = uncovered;
        image.Clip = uncovered == new Rect(0, 0, Width, Height) ? null : new RectangleGeometry(uncovered);
    }

    // Her body takes a moment to change while the window changes at once: she goes out, changes unseen, and comes back.
    private async void SetShape(string id)
    {
        settings.Shape = Shape.Find(id).Id;
        settings.Save();
        if (player is not { } current) return;
        long change = ++shapeChange;
        image.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(120)));
        await Task.Delay(130);
        if (change != shapeChange) return;
        ApplySize();
        current.SetShape(Shape);
        Clamp();
        long seen = current.Frames;
        for (int wait = 0; wait < 40 && current.Frames < seen + 3; wait++) await Task.Delay(20);
        if (change == shapeChange) image.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)));
    }

    private void Tick()
    {
        GoWithAgent();
        if (player is not { } current) return;
        if (DateTime.UtcNow >= nextFullscreenLook)
        {
            nextFullscreenLook = DateTime.UtcNow.AddMilliseconds(500);
            UpdateFullscreen(current);
        }
        if (hiddenForFullscreen) return;
        // 别的程序的窗口（包括刚被点到的普通窗口）跑到她上面时，把她和气泡重新放回最上面（见 ZOrder.cs）：
        // WPF 的 Topmost 只在设置那一刻生效，标记还挂着、人却已经在后面了，只看标记的 KeepTopmost 发现不了。
        if (!test && !standby && IsVisible && DateTime.UtcNow >= nextRaise)
        {
            nextRaise = DateTime.UtcNow.AddMilliseconds(700);
            var me = new WindowInteropHelper(this).Handle;
            if (ZOrder.CoveredByOthers(me))
            {
                RaiseAboveAll(me);
                bubble.Raise(); mutter.Raise(); handWindow?.Raise();
                if (DateTime.UtcNow - coveredLogged > TimeSpan.FromMinutes(1))
                {
                    coveredLogged = DateTime.UtcNow;
                    Places.Log("有窗口压在她上面，已经把她重新置顶" + Above(me));
                }
            }
        }
        KeepTopmost();
        // Moving the window pushes her body; physics carries that into her hair, skirt and tail.
        // 拖动时按抓点压低这个整体平移：抓点那一头才贴在手上（用户 2026-11-12）
        double swayScale = dragging ? GrabbedSway : 1;
        double swayX = (Left - lastLeft) * 30 * swayScale, swayY = (Top - lastTop) * 30 * swayScale;
        lastLeft = Left; lastTop = Top;
        if (swayX != 0 || swayY != 0) current.Send(FormattableString.Invariant($"sway {swayX:0} {swayY:0}"));
        UpdateCut();
        UpdateMouth(current);
        // When the last agent goes, nobody is left to say its work ended. A program on the HTTP interface has no
        // connection to lose: what it starts stays until it says it is done.
        int agents = server?.Connections ?? 0;
        if (agents == 0 && agentsBefore > 0) behavior.ClearActivities();
        agentsBefore = agents;
        UpdateBody();
        MusicTick(); // 有音乐就把跳舞带起来（PetWindow.Dance.cs）
        HoverTick();
        BondTick();
        AlarmTick();
        UpdateFilm();
        speech.Tick();
        mutterSpeech.Tick();
        // 哄睡演示：句子说完、动作演完 3 秒后自动归位。
        if (coaxAutoReturn)
        {
            if (speech.IsSaying) coaxSpeechSeen = true;
            else if (coaxSpeechSeen && coaxReturnAt == null) coaxReturnAt = DateTime.UtcNow.AddSeconds(5);
            if (coaxReturnAt is { } at && DateTime.UtcNow >= at) CoaxReturn();
        }
        // Nothing of her own while she says something or still holds the look she said it with.
        if (voice != null || speech.IsSaying || silent != null) mutterQuietUntil = DateTime.UtcNow.AddSeconds(10); // 说完后嘀咕再等 10 秒
        if (tempProps.Count > 0 && DateTime.UtcNow >= propsUntil) TakeOffProps(); // 临时道具到点自动摘
        behavior.Tick(DateTime.UtcNow, voice != null || speech.IsSaying || silent != null || body.Posing || textIdleActive || Dancing, settings.IdleSeconds); // 嘀咕/小剧场/事件演着的时候，待机抽签和小动作也别插进来（本机修，2026-11-06）；跟着音乐跳的时候也一样（2026-11-10）
        GetCursorPos(out var cursor);
        if (cursor.X != lastCursor.X || cursor.Y != lastCursor.Y)
        {
            lastCursor = cursor;
            lastCursorAt = DateTime.UtcNow;
            mouseIdle = false; // 鼠标动了，恢复互动
        }
        else if (!mouseIdle && DateTime.UtcNow - lastCursorAt > TimeSpan.FromSeconds(5))
        {
            mouseIdle = true; // 鼠标停在同一处超过 5 秒，暂时关掉视线跟随
        }
        // 鼠标停住以后的回正（用户 2026-11-12 要的：更慢更自然，眼睛先回正，头再跟着）：先让眼睛回到正中（渲染器里眼睛有自己的目标和速度），
        // 隔一小会儿再让头慢慢转回来。别的原因的回正（关了跟随、摸头、小剧场）还是原来那样一下回去。
        bool slowReturn = settings.FollowMouse && mouseIdle && !test && !stroking && !(settings.SkitPausesFollow && SkitOn);
        if (slowReturn)
        {
            if (returnStage == 0 && (lookX is 9 || (lookX == 0 && lookY == 0))) return; // 本来就是正的
            var at = DateTime.UtcNow;
            if (returnStage == 0)
            {
                returnStage = 1; returnAt = at;
                current.Send(FormattableString.Invariant($"lookrate {HeadReturnRate} {EyeReturnRate}"));
                current.Send(FormattableString.Invariant($"look {lookX:0.00} {lookY:0.00} 0 0")); // 眼睛先回正，头先不动
            }
            else if (returnStage == 1 && (at - returnAt).TotalSeconds >= HeadReturnDelay)
            {
                returnStage = 2; lookX = lookY = 0;
                current.Send("look 0 0 0 0"); // 头再慢慢转回来
            }
            return;
        }
        if (returnStage != 0)
        {
            // 回正做到一半鼠标又动了（或者别的原因打断）：快慢还原，让下面重新发一次目标（眼睛重新跟着头）
            returnStage = 0;
            current.Send("lookrate 10 10");
            lookX = lookY = 9;
        }
        if (!settings.FollowMouse || mouseIdle || test || stroking || (settings.SkitPausesFollow && SkitOn))
        {
            if (lookX == 0 && lookY == 0) return;
            lookX = lookY = 0;
            current.Send("look 0 0");
            return;
        }
        var dpi = VisualTreeHelper.GetDpi(this);
        double headX = (Left + Width / 2) * dpi.DpiScaleX, headY = (Top + Height * Shape.HeadAt) * dpi.DpiScaleY;
        double x = Math.Clamp((cursor.X - headX) / 500, -1, 1), y = Math.Clamp((headY - cursor.Y) / 400, -1, 1);
        if (Math.Abs(x - lookX) + Math.Abs(y - lookY) < .01) return;
        lookX = x; lookY = y;
        current.Send(FormattableString.Invariant($"look {x:0.00} {y:0.00}"));
    }

    // She comes and goes with the agent (the owner, 2026-11-03: the agent closed, she was still there). Once an agent
    // has been connected, she bows out after a while with none, long enough for one that reconnects or restarts its
    // servers. A program on the HTTP interface keeps her while it uses her; started by hand with no agent, she stays.
    private static readonly TimeSpan AloneFor = TimeSpan.FromSeconds(5), HttpUse = TimeSpan.FromSeconds(15); // 本机改（2026-11-12）：最后一个 Agent 一退出就走，缓冲从 1 分钟缩到 5 秒，只够 Agent 重启 MCP 重连
    // 桥接正常关掉时（Agent 自己退了）会先说一声再见：那就不用等满一分钟，几秒就跟着退（用户 2026-11-12 要的）。
    private static readonly TimeSpan FarewellFor = TimeSpan.FromSeconds(3), FarewellFresh = TimeSpan.FromSeconds(20);
    private bool hadAgent;
    private int agentsBefore;
    private DateTime? aloneSince;
    private DateTime lastHttp = DateTime.MinValue;

    private void GoWithAgent()
    {
        if (test || exiting) return;
        // 本机改（2026-11-11）：DSH 每次工具调用可能重开一次 MCP，桥接一断就退场的话托盘小图标也会跟着消失。
        // 留一分钟缓冲：真的把 Agent 关了才会走，只是重连或者闲一会儿不算。还有程序在用网页接口时也留着。
        if ((server?.Connections ?? 0) > 0) { hadAgent = true; aloneSince = null; return; }
        if (!hadAgent) return; // 手动打开、没接过 Agent 的，一直留着
        if (DateTime.UtcNow - lastHttp < HttpUse) { aloneSince = null; return; }
        // 刚说过再见的（20 秒内）：等 3 秒就走，不用等满一分钟
        bool goodbye = server?.FarewellAt is { } said && DateTime.UtcNow - said < FarewellFresh;
        aloneSince ??= DateTime.UtcNow;
        if (DateTime.UtcNow - aloneSince < (goodbye ? FarewellFor : AloneFor)) return;
        Places.Log(goodbye ? "Agent 正常退出了，她跟着退下" : "接她的 Agent 都断开了，她先退下");
        Close();
    }

    // While another program fills the screen she is hidden and her renderer draws nothing; her words still play on,
    // unseen, and she is back on top once it is gone. Not in a test, which is out of sight anyway.
    private void UpdateFullscreen(Player current)
    {
        string who = "";
        bool overlay = !standby && !test && Fullscreen.OverlayInFront(out who); // 截图/录屏浮层：不管开关，她都让开
        bool covered = standby || overlay || (settings.HideOnFullscreen && !test && Fullscreen.InFront());
        if (covered == hiddenForFullscreen) return;
        if (covered && overlay) Places.Log("有铺满全屏的截图/录屏类浮层在最前（" + who + "），她先让开");
        hiddenForFullscreen = covered;
        current.Send($"pause {(covered ? 1 : 0)}");
        bubble.Quiet = covered || test;
        if (covered) { bubble.Hide(); Hide(); return; }
        Show();
        Topmost = false; Topmost = true;
    }

    // 置顶标记可能在她不知情的时候被拿走（别的“总在最前”工具、系统切全屏、换显示器），
    // 一旦没了，普通窗口就能一直压着她。每秒看一眼，掉了就补回来（补过写一行日志，最多每分钟一行）。
    private void KeepTopmost()
    {
        if (test || standby || !IsVisible || exiting) return;
        if (DateTime.UtcNow < nextTopmostLook) return;
        nextTopmostLook = DateTime.UtcNow.AddSeconds(1);
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == 0 || ((long)GetWindowLongPtr(handle, -20) & 0x8) != 0) return;
        RaiseAboveAll(handle);
        if (DateTime.UtcNow - topmostLostLogged > TimeSpan.FromMinutes(1))
        {
            topmostLostLogged = DateTime.UtcNow;
            Places.Log("窗口的置顶被去掉了（别的程序或系统），已经重新置顶" + Above(handle));
        }
    }

    // 丢置顶的时候，压在她上面的是哪个程序（只写日志，好查出是谁撤的）。
    private static string Above(nint handle)
    {
        var above = GetWindow(handle, 3); // GW_HWNDPREV
        if (above == 0) return "";
        GetWindowThreadProcessId(above, out uint id);
        try { return "；刚才压在上面的是 " + Process.GetProcessById((int)id).ProcessName; }
        catch { return ""; }
    }

    // 她从不抢焦点（WS_EX_NOACTIVATE），点了也不会像普通窗口那样自己浮上来；置顶的一群窗口里又是最后点到的在最上面。
    // 所以用户碰她的时候，手动把她抬到置顶层的最前面。
    private void RaiseAboveAll()
    {
        if (test || standby || !IsVisible || exiting) return;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != 0) RaiseAboveAll(handle);
    }

    private static void RaiseAboveAll(nint handle) =>
        SetWindowPos(handle, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);

    // ---- The user's hands on her ----

    private void Down(object sender, MouseButtonEventArgs e)
    {
        if (exiting || e.ChangedButton != MouseButton.Left) return;
        e.Handled = true;
        RaiseAboveAll();
        dragging = true; dragged = false;
        GetCursorPos(out var p);
        dragStart = new Point(p.X, p.Y);
        windowStart = new Point(Left, Top);
        CaptureMouse();
    }

    private void Move(object sender, MouseEventArgs e)
    {
        if (!dragging || e.LeftButton != MouseButtonState.Pressed) return;
        GetCursorPos(out var p);
        var dpi = VisualTreeHelper.GetDpi(this);
        double dx = (p.X - dragStart.X) / dpi.DpiScaleX, dy = (p.Y - dragStart.Y) / dpi.DpiScaleY;
        if (Math.Abs(dx) + Math.Abs(dy) > 4 && !dragged)
        {
            dragged = true;
            behavior.DragStart();
            TouchDragStarted();
            Happened("drag_start", new { });
        }
        if (dragged) { Left = windowStart.X + dx; Top = windowStart.Y + dy; }
        e.Handled = true;
    }

    private void Up(object sender, MouseButtonEventArgs e)
    {
        if (!dragging) return;
        e.Handled = true;
        dragging = false;
        ReleaseMouseCapture();
        if (dragged) { Dropped(); return; }
        // The window also gets clicks on her faint edges, which count as missing her.
        var at = e.GetPosition(this);
        if (VisualTreeHelper.HitTest(image, at) == null) return;
        var zone = Behavior.Zone(Shape.Id, at.X / Width, at.Y / Height, settings.Outfit);
        YieldMutter(); // 点她时嘀咕/小剧场让位，别跟触碰台词抢气泡
        behavior.Click(zone, DateTime.UtcNow, at.X / Width < .5 ? 1 : -1);
        BondTouched(behavior.Showing);
        SayTouch(behavior.Showing);
        Happened("click", new { zone, reaction = behavior.Showing });
    }

    // 点她时除了身体反应，再念一句对应台词（touch.txt）。用嘀咕的小气泡，嘴跟着字动，按字数停一会儿后收起。
    private async void SayTouch(string? reaction)
    {
        // 主语音（尤其是项目报告的实时语音）正在合成/播放时，触碰只做身体反应，不抢语音和台词气泡。
        if (speech.IsSaying || voice != null || Tts.Busy) return;
        if (reaction?.StartsWith("娇羞") == true) reaction = "娇羞"; // 满档戳裙子的三种反应共用 touch.txt 里「娇羞」的台词
        if (reaction == null || !touchLines.TryGetValue(reaction, out var all)) return;
        var lines = all.Where(line => line.Level <= affection.Level).ToArray();
        if (lines.Length == 0) return;
        int token = mutterToken; // 有新的触碰台词、或她开口了，就别让旧的一句乱收气泡
        // 摸头彩蛋的五句：抽完一轮再重抽，尽量不连续重复（用户定的，2026-11-11）
        string said;
        if (reaction == "抚摸头4")
            said = NextMeltLine(lines.Select(line => line.Text).ToArray());
        else
        {
            // 级别要求越高的句子权重越大：到了高等级，多半说黏人的那些
            int pick = random.Next(lines.Sum(line => 1 + 2 * line.Level));
            said = lines[^1].Text;
            foreach (var line in lines) { pick -= 1 + 2 * line.Level; if (pick < 0) { said = line.Text; break; } }
        }
        var voiceSeconds = Speak(said);
        mutterSpeech.Say(new Line(said, body.Apply));
        var wait = Math.Max(Math.Clamp(said.Length * 250 + 1200, 2500, 6000) / 1000.0, (voiceSeconds ?? 0) + 0.4);
        await Task.Delay(TimeSpan.FromSeconds(wait));
        if (token != mutterToken || speech.IsSaying) return;
        mutterSpeech.Stop();
    }

    // 摸头彩蛋五句的抽签袋：五句一轮，每句各出一次；新一轮尽量避免第一句和上一轮最后一句相同。
    private string NextMeltLine(string[] texts)
    {
        meltBag.RemoveAll(text => !texts.Contains(text));
        if (meltBag.Count == 0)
        {
            meltBag.AddRange(texts);
            for (int i = meltBag.Count - 1; i > 0; i--) // Fisher-Yates
            {
                int j = random.Next(i + 1);
                (meltBag[i], meltBag[j]) = (meltBag[j], meltBag[i]);
            }
            if (meltBag.Count > 1 && meltBag[0] == lastMeltLine)
            {
                int j = 1 + random.Next(meltBag.Count - 1);
                (meltBag[0], meltBag[j]) = (meltBag[j], meltBag[0]);
            }
        }
        var said = meltBag[0];
        meltBag.RemoveAt(0);
        lastMeltLine = said;
        return said;
    }

    private void Dropped()
    {
        TouchDragEnded();
        Clamp();
        SavePlace();
        BondSeen();
        double level = dizzy;
        bool dazed = level >= DizzyLow;
        behavior.DragEnd(DateTime.UtcNow, dazed);
        dragMeasured = false; dizzy = 0; dizzyAngle = null;
        AimAllZero();
        springK = 120; springC = 11;
        if (dazed) StartDizzy(level); // 转晕了：放下后晕一会儿，时间随晕的程度（PetWindow.Touch.cs）
        Happened("drag_end", new { dizzy = (int)level });
    }

    private void Happened(string kind, object detail)
    {
        var happening = new Happening(++sequence, DateTime.UtcNow, kind, detail);
        happenings.Add(happening);
        Recorder.Event(kind, detail);
        if (happenings.Count > 100) happenings.RemoveAt(0);
        server?.Publish(kind, detail);
    }

    // ---- What she says ----

    // A new line waits for the one before to be played out, or her body would be cut off mid-move. At most as long as
    // its words take and 20 s more, so a long line or recording plays out but nothing stuck holds her up for good.
    private async Task WaitForLine(CancellationToken token)
    {
        var waited = Stopwatch.StartNew();
        while ((silent != null || speech.IsSaying) && waited.Elapsed.TotalSeconds < 20 + speech.Spoken) await Task.Delay(50, token);
    }

    // In the order they came, each once the one before has been acted out.
    private async Task InTurn(Func<Task> act, CancellationToken token)
    {
        var turn = new TaskCompletionSource();
        var before = lines;
        lines = turn.Task;
        try { await before; await WaitForLine(token); await act(); }
        finally { turn.SetResult(); }
    }

    private void SayLine(string text, double? voiceSeconds, double charactersPerSecond = Line.CharactersPerSecond)
    {
        YieldMutter();
        relaxAt = null;
        panelHold = false; // 她自己开口了：控制台点出来的样子不再强留
        var line = new Line(text, body.Apply, voiceSeconds, charactersPerSecond);
        if (line.Plain.Trim().Length == 0) { PlaySilently(text); return; }
        StartFilming(line.Plain);
        speech.Say(line);
    }

    // film: false for what she tries in the mirror, which she sees there already.
    private void PlaySilently(string text, bool filmed = true)
    {
        relaxAt = null;
        silent = new Line(text, body.Apply);
        if (filmed) StartFilming(silent.Plain);
        silentClock.Restart();
        silent.Advance(0);
    }

    // Her own look stays a while after her words, then she relaxes; not in the middle of one of her reflexes.
    private void UpdateBody()
    {
        if (silent is { } line)
        {
            line.Advance(silentClock.Elapsed.TotalSeconds);
            if (line.Done) silent = null;
        }
        if (!body.Posing || silent != null || speech.IsSaying) { relaxAt = null; return; }
        if (coaxHoldPose || panelHold || textIdleActive) { relaxAt = null; return; } // 第三层：保持黑脸盯人；控制台点出来的样子保持到点“恢复”；嘀咕/小剧场/事件演着时由它们自己收尾（以前一句停够 3.5 秒就被这里放松掉，爱心眼刚做出来就没了，本机修 2026-11-06）
        relaxAt ??= DateTime.UtcNow.AddSeconds(LingerSeconds);
        if (DateTime.UtcNow < relaxAt || behavior.Showing != null) return;
        body.Relax();
        relaxAt = null;
    }

    // 这句台词有录音就放出来（返回录音长度），没有就返回 null、照旧只打字冒泡。
    private double? Speak(string text)
    {
        if (!settings.VoiceEnabled) return null; // 语音总开关关着：预录也不出声
        var file = voicePack?.Find(text);
        if (file == null) return null;
        try { return PlayVoice(file); }
        catch { return null; }
    }

    // Returns how long the recording is, for spreading what she does over it.
    private double? PlayVoice(string? file)
    {
        if (!settings.VoiceEnabled) return null; // 语音总开关关着：audio 和预录都不放
        if (string.IsNullOrWhiteSpace(file) || !Path.IsPathRooted(file) || !file.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) || !File.Exists(file))
            throw new ArgumentException("audio 要是本机 wav 文件的完整路径，而且文件要在。");
        voicePlayer.Stop();
        voicePlayer.Close(); // 放完/换一条时松开文件，不然录音文件一直被占着没法替换
        voicePlayer.Open(new Uri(file));
        voicePlayer.Volume = settings.Volume * VoiceGain; // Close() 会把音量打回默认值，每次打开后要重设
        voicePlayer.Play();
        try { voice = Behavior.VoiceLevels(File.ReadAllBytes(file), VoiceStepMilliseconds); }
        catch (IOException) { voice = null; }
        // A voice that never starts playing must not hold her mouth for ever.
        if (voice != null) voiceUntil = DateTime.UtcNow.AddMilliseconds(voice.Length * VoiceStepMilliseconds + 2000);
        return voice?.Length * VoiceStepMilliseconds / 1000.0;
    }

    // 语音总开关关掉时，把正在放的录音也停掉。
    private void StopVoice()
    {
        try { voicePlayer.Stop(); voicePlayer.Close(); } catch { /* 已经关了 */ }
        voice = null;
    }

    // Her recorded voice when there is one; otherwise her mouth moves while the words come out.
    private void UpdateMouth(Player current)
    {
        double level = 0;
        if (voice is { } levels)
        {
            int step = (int)(voicePlayer.Position.TotalMilliseconds / VoiceStepMilliseconds);
            if (step < levels.Length && DateTime.UtcNow < voiceUntil) level = levels[step];
            else { voice = null; try { voicePlayer.Close(); } catch { /* 已经关了 */ } }
        }
        if (voice == null && (speech.IsTalking || mutterSpeech.IsTalking)) level = .2 + .35 * Math.Abs(Math.Sin(Environment.TickCount64 / 70.0));
        if (level == mouth) return;
        mouth = level;
        current.Send(FormattableString.Invariant($"mouth {level:0.00}"));
    }

    // ---- Her mirror and her photos ----

    // What she looks like right now, head to waist so her face is big enough to read, on white.
    private BitmapSource? Snapshot(int most = MirrorHeight)
    {
        if (image.Source is not BitmapSource frame || frame.PixelWidth == 0) return null;
        int top = (int)(frame.PixelHeight * Math.Max(0, Shape.HeadTop - .03)), span = Math.Min(frame.PixelHeight - top, (int)(frame.PixelHeight * MirrorSpan));
        var upper = new CroppedBitmap(frame, new Int32Rect(0, top, frame.PixelWidth, span));
        double scale = Math.Min(1, most / (double)span);
        int width = (int)Math.Round(frame.PixelWidth * scale), height = (int)Math.Round(span * scale);
        var visual = new DrawingVisual();
        using (var draw = visual.RenderOpen())
        {
            draw.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            draw.DrawImage(upper, new Rect(0, 0, width, height));
        }
        var flat = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        flat.Render(visual);
        flat.Freeze();
        return flat;
    }

    // Looks in rows of three, left to right and down in the order they were seen, as a PNG data URL.
    private static string Picture(IReadOnlyList<BitmapSource> shots)
    {
        int columns = Math.Min(MirrorColumns, shots.Count), rows = (shots.Count + columns - 1) / columns;
        int cellWidth = shots.Max(shot => shot.PixelWidth), cellHeight = shots.Max(shot => shot.PixelHeight);
        var visual = new DrawingVisual();
        using (var draw = visual.RenderOpen())
        {
            draw.DrawRectangle(Brushes.White, null, new Rect(0, 0, cellWidth * columns, cellHeight * rows));
            for (int i = 0; i < shots.Count; i++)
                draw.DrawImage(shots[i], new Rect(i % columns * cellWidth, i / columns * cellHeight, shots[i].PixelWidth, shots[i].PixelHeight));
        }
        var strip = new RenderTargetBitmap(cellWidth * columns, cellHeight * rows, 96, 96, PixelFormats.Pbgra32);
        strip.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(strip));
        using var bytes = new MemoryStream();
        encoder.Save(bytes);
        return "data:image/png;base64," + Convert.ToBase64String(bytes.ToArray());
    }

    private static IReadOnlyList<BitmapSource> Moments(IReadOnlyList<BitmapSource> seen) => seen.Count <= MirrorShots ? seen
        : Enumerable.Range(0, MirrorShots).Select(i => seen[(int)Math.Round(i * (seen.Count - 1) / (double)(MirrorShots - 1))]).ToList();

    // She tries something in front of the mirror: done for real, watched all through, six moments given back.
    private async Task<IReadOnlyList<BitmapSource>> TryInMirror(string pose, CancellationToken token)
    {
        var seen = new List<BitmapSource>();
        await InTurn(async () =>
        {
            PlaySilently(pose.Contains('〔') ? pose : "〔" + pose + "〕", filmed: false);
            var line = silent!;
            var clock = Stopwatch.StartNew();
            // Until it has all played and settled a moment, never longer than six seconds.
            while (clock.Elapsed.TotalSeconds < 6 && !(line.Done && clock.Elapsed.TotalSeconds > line.Until + .3))
            {
                if (Snapshot(MirrorGridHeight) is { } shot) seen.Add(shot);
                await Task.Delay(120, token);
            }
        }, token);
        return Moments(seen);
    }

    private void StartFilming(string said)
    {
        FinishFilming();
        film = []; filmSaid = said; filmEnded = null; filmShot = default;
    }

    // A look every FilmEvery while she says it and a moment after; too long a line keeps every other one.
    private void UpdateFilm()
    {
        if (film is not { } shots) return;
        var now = DateTime.UtcNow;
        if (speech.IsSaying || silent != null) filmEnded = null; else filmEnded ??= now;
        if (now - filmShot >= FilmEvery && Snapshot(MirrorGridHeight) is { } shot)
        {
            shots.Add(shot); filmShot = now;
            if (shots.Count >= FilmFrames) { var kept = shots.Where((_, i) => i % 2 == 0).ToList(); shots.Clear(); shots.AddRange(kept); }
        }
        if (filmEnded is { } ended && now - ended >= FilmAfter) FinishFilming();
    }

    private void FinishFilming()
    {
        var shots = film;
        film = null;
        if (shots is not { Count: > 0 }) return;
        photos.Add(new(DateTime.UtcNow, filmSaid, Picture(Moments(shots))));
        TrimPhotos();
    }

    private void TrimPhotos()
    {
        photos.RemoveAll(photo => DateTime.UtcNow - photo.At > PhotoLife);
        if (photos.Count > PhotosKept) photos.RemoveRange(0, photos.Count - PhotosKept);
    }

    // ---- Leaving ----

    // She bows and fades out while still bowed, rather than vanishing halfway down.
    private async void Leaving(object? sender, CancelEventArgs e)
    {
        if (exitReady) return;
        e.Cancel = true;
        if (exiting) return;
        exiting = true;
        speech.Stop();
        if (player != null && image.Opacity > 0)
        {
            behavior.Show("告别", DateTime.UtcNow);
            await Task.Delay(1600);
            image.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(500)));
            await Task.Delay(600);
        }
        await Task.Yield(); // 收起待命时上面一个 await 都没有，不让出的话 Close() 会落在关闭事件里面而报错（本机修，2026-11-06）
        exitReady = true;
        timeEndPeriod(1);
        Close();
        Application.Current.Shutdown();
    }

    // ---- The agent's tools ----

    // A newer plugin found on GitHub rides along with whatever the agent asks next, once, for it to tell the user.
    private async Task<object> Execute(string name, JsonElement args, CancellationToken token)
    {
        var result = await Dispatcher.InvokeAsync(() => Run(name, args, token)).Task.Unwrap();
        return updates.Unannounced() is { } found ? Tools.WithNotice(result, UpdateNotice(found)) : result;
    }

    // What her body could not read in what she said, and while her lines move only before their words, a reminder to
    // think of her body as she speaks (the owner, 2026-11-05). Her last ten lines since she started.
    private readonly List<bool> acting = [];
    private string Heard(IReadOnlyList<string> lines)
    {
        acting.AddRange(lines.Select(Body.ActsWithin));
        if (acting.Count > 10) acting.RemoveRange(0, acting.Count - 10);
        var unread = Body.Unread(string.Join("\n", lines));
        var missed = unread.Count == 0 ? "" : "身体没跟上：" + string.Join("、", unread.Select(item => "〔" + item.Word + "〕" + (item.Like != null ? "（想写的是〔" + item.Like + "〕？）" : ""))) +
            "，" + (unread.Count > 1 ? "这几个" : "这个") + "没做出来。";
        return missed + (Body.NeedsActing(acting) ? "下次说之前，在心里把这几句演一遍：说到哪儿脸和身子怎么变，就在哪儿写〔〕。" : "");
    }

    private static string UpdateNotice(Updates.Found found) =>
        "【插件有新版本】GitHub 上比现在这份多了 " + found.Behind + " 处改动（新的在前）：" + string.Join("；", found.Changes) +
        "。" + (found.Guide ? "这次身体说明也改了，更新完要把你人设里【你的身体】到【你的身体·完】那段换成新版（“更新插件”第 6 步）。" : "") +
        "找个合适的时候，用一两句话告诉用户有更新、大概改了什么，问他要不要更新；他说要，就照插件 AGENTS.md 最后的“更新插件”做。" +
        "还没更新的话，过一天会再提醒一次；get_state 的 update 里随时能看到。";

    private async Task<object> Run(string name, JsonElement args, CancellationToken token)
    {
        if (exiting) throw new InvalidOperationException("你正在告别，收起之后再叫你出来。");
        if (Recorder.Active && name is not ("get_state" or "recent_events" or "look_back" or "recall"))
            Recorder.Event(name, args.ValueKind == JsonValueKind.Object ? args.Clone() : null); // 录制时记下工具调用（say、dress、show……）
        if (name is not ("get_state" or "quit" or "recent_events") && trouble != null && player == null) throw new InvalidOperationException(trouble);
        switch (name)
        {
            case "say":
                Wake(); // 收着时一开口就出场
                YieldMutter(); // 说话优先：收起正在显示的嘀咕，等待排队的这段时间里也不再嘀咕
                var text = Tools.RequiredText(args, "text");
                var audio = Tools.OptionalText(args, "audio");
                // 字幕说中文、声音说日语（本机加，2026-11-09）：speech 是要念的日文，现场合成；text 只当字幕。
                var spoken = Tools.OptionalText(args, "speech");
                if (audio == null && spoken != null)
                {
                    var live = await Tts.Speak(spoken, settings, token);
                    var single = text.Replace('\n', ' ').Trim();
                    await InTurn(() => { SayLine(single, live != null ? PlayVoice(live) : null); return Task.CompletedTask; }, token);
                    var heard = Heard([single]);
                    return Tools.Text(heard.Length == 0 ? new { started = true, bubbles = 1, said = Body.Strip(text) }
                        : (object)new { started = true, bubbles = 1, said = Body.Strip(text), note = heard });
                }
                // 实时语音（本机加，2026-11-09）：一句一句来。第一句先说，边听边合成后面的；
                // 有预录就用预录，没有才现场合成，服务没开就只打字。
                var bubbles = audio != null ? [text] : Line.Split(text);
                async Task<string?> VoiceFor(string piece)
                {
                    if (audio != null) return audio;
                    var preset = voicePack?.Find(Tts.Plain(piece));
                    if (preset != null) return preset;
                    return await Tts.Speak(piece, settings, CancellationToken.None);
                }
                var firstVoice = await VoiceFor(bubbles[0]);
                var later = bubbles.Skip(1).Select(piece => VoiceFor(piece)).ToArray(); // 先把后面的排上队
                // A line each, in turn; the call returns once the first has begun, the rest wait their turn whoever
                // asked. A recording is one utterance, so with one it is a single bubble.
                await InTurn(() => { SayLine(bubbles[0], firstVoice != null ? PlayVoice(firstVoice) : null); return Task.CompletedTask; }, token);
                foreach (var (next, voice) in bubbles.Skip(1).Select((piece, i) => (piece, later[i])))
                    _ = InTurn(async () =>
                    {
                        if (exiting) return;
                        var file = await voice;
                        if (!exiting) SayLine(next, file != null ? PlayVoice(file) : null);
                    }, CancellationToken.None);
                var note = Heard(bubbles);
                return Tools.Text(note.Length == 0 ? new { started = true, bubbles = bubbles.Count, said = Body.Strip(text) }
                    : (object)new { started = true, bubbles = bubbles.Count, said = Body.Strip(text), note });
            case "look_in_mirror":
                if (standby) throw new InvalidOperationException("你现在收起着，先用 show 出场再照镜子。");
                if (hiddenForFullscreen) throw new InvalidOperationException("别的程序正全屏，你藏着没在画，镜子里只会是旧样子；等全屏退出再照（或者先 update_settings hide_on_fullscreen 设成 false）。"); // 本机修，2026-11-08
                var pose = Tools.OptionalText(args, "try");
                IReadOnlyList<BitmapSource> shots = pose != null ? await TryInMirror(pose, token) : Snapshot() is { } now ? [now] : [];
                if (shots.Count == 0) throw new InvalidOperationException("镜子里还什么都看不到，等你出现了再照。");
                return Tools.Image(Picture(shots), new { shots = shots.Count, tried = pose });
            case "look_back":
                int which = args.TryGetProperty("which", out var value) && value.TryGetInt32(out var number) ? number : 1;
                if (film != null && !speech.IsSaying && silent == null) FinishFilming();
                TrimPhotos();
                if (photos.Count == 0) throw new InvalidOperationException("这半小时里还没有拍下你说话的样子。");
                if (which < 1 || which > photos.Count) throw new ArgumentException("只留着最近 " + photos.Count + " 句的样子。");
                var photo = photos[^which];
                int Ago(DateTime at) => (int)(DateTime.UtcNow - at).TotalSeconds;
                return Tools.Image(photo.Picture, new { said = photo.Said, secondsAgo = Ago(photo.At),
                    recent = Enumerable.Range(1, photos.Count).Select(n => new { which = n, said = photos[^n].Said, secondsAgo = Ago(photos[^n].At) }).ToArray() });
            case "set_activity":
                var kind = Tools.RequiredText(args, "kind", 40);
                if (!args.TryGetProperty("active", out var active) || active.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException("要给 active（true 或 false）。");
                behavior.Activity(kind, active.GetBoolean());
                return Tools.Text(new { kind, active = active.GetBoolean() });
            case "dress":
                foreach (var label in Tools.Texts(args, "wear").Concat(Tools.Texts(args, "remove")))
                    if (Outfit.Expression(label) == null) throw new ArgumentException("没有“" + label + "”。衣服和配饰见 dress 的说明。");
                foreach (var label in Tools.Texts(args, "wear")) Dress(Outfit.Expression(label)!, true);
                foreach (var label in Tools.Texts(args, "remove")) Dress(Outfit.Expression(label)!, false);
                return Tools.Text(new { wearing = Wearing() });
            case "get_state":
                return Tools.Text(State());
            // 托盘底部的「检查更新」按钮走这里（用户 2026-11-12 要的）：不进 MCP 工具表，只给本机控制台用。
            case "check_update":
                var checkedNow = await updates.Check();
                return Tools.Text(new { state = checkedNow.State, behind = checkedNow.Behind, changes = checkedNow.Changes, error = checkedNow.Error });
            case "update_settings":
                if (args.TryGetProperty("fps", out var fps) && fps.TryGetInt32(out var rate))
                {
                    if (!Settings.FpsChoices.Contains(rate)) throw new ArgumentException("fps 只能是 30、60、90、120。");
                    settings.Fps = rate; Send($"fps {rate}");
                }
                if (args.TryGetProperty("scale", out var scale) && scale.TryGetDouble(out var size))
                {
                    settings.Scale = Math.Clamp(size, .5, 2); ApplySize(); Clamp();
                }
                if (args.TryGetProperty("bubble_scale", out var bubbleScale) && bubbleScale.TryGetDouble(out var bubbleSize))
                {
                    settings.BubbleScale = Math.Clamp(bubbleSize, .5, 2); bubble.Scale = settings.BubbleScale;
                }
                if (args.TryGetProperty("bubble_seconds", out var stay) && stay.TryGetInt32(out var stays))
                {
                    settings.BubbleSeconds = Settings.StayFor(stays); speech.Seconds = settings.BubbleSeconds;
                }
                if (args.TryGetProperty("voice_enabled", out var voiceOn) && voiceOn.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    settings.VoiceEnabled = voiceOn.GetBoolean();
                    if (!settings.VoiceEnabled)
                    {
                        // 语音总开关：所有声音静音，同时把实时语音/开机自动启动一起关掉，并把后台 GPT-SoVITS 停掉。
                        settings.TtsEnabled = false;
                        settings.TtsAutostart = false;
                        StopVoice();
                        Tts.Stop(settings);
                    }
                    else
                    {
                        // 打开总开关：恢复实时语音和自动启动，并让后台 GPT-SoVITS 起来（有服务目录时）。
                        settings.TtsEnabled = true;
                        settings.TtsAutostart = true;
                        voicePlayer.Volume = settings.Volume * VoiceGain;
                        _ = Tts.Warm(settings); // 先启动服务并预热一句，之后实时语音才能马上生成
                    }
                }
                if (args.TryGetProperty("volume", out var volume) && volume.TryGetDouble(out var level))
                {
                    settings.Volume = Math.Clamp(level, 0, 1);
                    voicePlayer.Volume = settings.Volume * VoiceGain;
                }
                if (args.TryGetProperty("tts_enabled", out var ttsOn) && ttsOn.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    var wasOn = settings.TtsEnabled;
                    settings.TtsEnabled = ttsOn.GetBoolean();
                    // 关掉实时语音就顺手停掉语音服务，省内存/显存；再打开时提前把它热起来（2026-11-10 用户要的）
                    if (wasOn && !settings.TtsEnabled) Tts.Stop(settings);
                    else if (!wasOn && settings.TtsEnabled) _ = Tts.Ensure(settings, CancellationToken.None);
                }
                if (args.TryGetProperty("tts_autostart", out var ttsAuto) && ttsAuto.ValueKind is JsonValueKind.True or JsonValueKind.False) settings.TtsAutostart = ttsAuto.GetBoolean();
        if (args.TryGetProperty("tts_dir", out var ttsDir) && ttsDir.ValueKind == JsonValueKind.String) settings.TtsDir = (ttsDir.GetString() ?? "").Trim();
        if (args.TryGetProperty("tts_url", out var ttsUrl) && ttsUrl.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(ttsUrl.GetString()))
                    settings.TtsUrl = ttsUrl.GetString()!.Trim();
                if (args.TryGetProperty("tts_speed", out var ttsSpeed) && ttsSpeed.TryGetDouble(out var speed))
                    settings.TtsSpeed = Math.Clamp(speed, .7, 1.5);
                // 固定台词语音包（本机加，2026-11-10）：second = voice\，calm = voice_calm\；切完立刻换索引和触碰台词。
                if (args.TryGetProperty("voice_pack", out var voicePackArg) && voicePackArg.ValueKind == JsonValueKind.String)
                {
                    var id = string.Equals(voicePackArg.GetString(), "calm", StringComparison.OrdinalIgnoreCase) ? "calm" : "second";
                    if (id != settings.VoicePack)
                    {
                        settings.VoicePack = id;
                        voice = null;
                        try { voicePlayer.Close(); } catch { /* 已经关了 */ }
                        LoadVoicePack();
                        LoadTouchFile();
                    }
                }
                // Taken up at once: hidden behind a fullscreen program she comes back if it is turned off.
                if (args.TryGetProperty("hide_on_fullscreen", out var hide) && hide.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    settings.HideOnFullscreen = hide.GetBoolean(); nextFullscreenLook = default;
                }
                if (args.TryGetProperty("check_updates", out var check) && check.ValueKind is JsonValueKind.True or JsonValueKind.False) settings.CheckUpdates = check.GetBoolean();
                if (args.TryGetProperty("follow_mouse", out var follow) && follow.ValueKind is JsonValueKind.True or JsonValueKind.False) settings.FollowMouse = follow.GetBoolean();
                if (args.TryGetProperty("start_hidden", out var startHidden) && startHidden.ValueKind is JsonValueKind.True or JsonValueKind.False) settings.StartHidden = startHidden.GetBoolean();
                if (args.TryGetProperty("keep_on_screen", out var keepOn) && keepOn.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    settings.KeepOnScreen = keepOn.GetBoolean();
                    if (settings.KeepOnScreen) { Clamp(); settings.Left = Left; settings.Top = Top; }
                }
                if (args.TryGetProperty("birthday", out var birth) && birth.ValueKind == JsonValueKind.String)
                {
                    var day = birth.GetString();
                    if (string.IsNullOrWhiteSpace(day)) settings.Birthday = "";
                    else settings.Birthday = Condition.NormalBirthday(day) ?? throw new ArgumentException("birthday 写成 10-21 这样（月-日），空字符串是清掉。");
                    Condition.Birthday = settings.Birthday;
                }
                if (args.TryGetProperty("coax_sleep", out var sleep) && sleep.ValueKind is JsonValueKind.True or JsonValueKind.False) settings.CoaxSleep = sleep.GetBoolean();
                if (args.TryGetProperty("coax_lunch", out var lunch) && lunch.ValueKind is JsonValueKind.True or JsonValueKind.False) settings.CoaxLunch = lunch.GetBoolean();
                // 听到音乐就跳（本机加，2026-11-10）：关掉时顺手把耳朵也收起来，别白跑一个 PowerShell。
                if (args.TryGetProperty("music_apps", out var apps) && apps.ValueKind == JsonValueKind.String) settings.MusicApps = (apps.GetString() ?? "").Trim();
        if (args.TryGetProperty("music_dance", out var dance) && dance.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    settings.MusicDance = dance.GetBoolean();
                    if (!settings.MusicDance) StopMusic();
                    else if (music == null) StartMusic();
                }
                // 桌面实况（本机加，2026-11-12）：总开关，DSH 那边的 dafeiyu-context 插件每十几秒读一次。
                if (args.TryGetProperty("context_chat", out var contextChat) && contextChat.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    settings.ContextChat = contextChat.GetBoolean();
                if (args.TryGetProperty("idle_seconds", out var idle) && idle.TryGetInt32(out var seconds)) settings.IdleSeconds = Math.Clamp(seconds, 15, 600);
                bool texChanged = false;
                if (args.TryGetProperty("tex_scale", out var texScale) && texScale.TryGetDouble(out var tex))
                {
                    var next = tex >= 1 ? 1 : tex >= 0.5 ? 0.5 : 0.25;
                    if (next != settings.TexScale) { settings.TexScale = next; texChanged = true; }
                }
                settings.Save();
                if (args.TryGetProperty("shape", out var shape) && shape.ValueKind == JsonValueKind.String) SetShape(shape.GetString()!);
                if (texChanged) RestartRenderer();
                return Tools.Text(State());
            case "coax":
                return Coax(args);
            case "recent_events":
                long after = args.TryGetProperty("after", out var since) && since.TryGetInt64(out var mark) ? mark : 0;
                return Tools.Text(happenings.Where(item => item.Sequence > after)
                    .Select(item => new { sequence = item.Sequence, secondsAgo = (int)(DateTime.UtcNow - item.At).TotalSeconds, kind = item.Kind, detail = item.Detail }).ToArray());
            case "quit":
                _ = Dispatcher.BeginInvoke(new Action(Close));
                return Tools.Text(new { leaving = true });
            case "show":
                coaxWoke = false;
                bool came = Wake(greet: true);
                if (!came) RaiseAboveAll(); // 已经在场：把压在她上面的置顶窗口让开
                return Tools.Text(new { hidden = false, came });
            case "hide":
                _ = HideAway();
                return Tools.Text(new { hiding = !standby });
            case "focus":
                return FocusCommand(Tools.RequiredText(args, "action", 10));
            case "remember": case "recall": case "forget": // 记忆小本本（本机加，2026-11-08）
                return MemoryCommand(name, args);
            case "event": // Claude Code hooks 等外面的事件（本机加）：收着不叫她出场，演不演由 events.txt 和她当下忙不忙决定
                return Event(args);
            case "panel":
                Wake(); // 控制台点互动时，收着也出场（和说话一样）；空动作（reset）不会误唤出，因此下面单独处理
                return Panel(args);
            default:
                throw new ArgumentException("没有这个工具。");
        }
    }

    // ───────── 外面发来的事件（本机加，2026-11-06）─────────
    // POST /call/event {"name": "授权"}：Claude Code 的 hooks 一类的外部程序告诉她“发生了什么”，她按 events.txt 演一小下。
    // 不进工具列表（Agent 看不到，也不用 Agent 调）；不花 token；她收着、被全屏盖住、在说话、在演别的时候就不演（返回里写原因）。
    private readonly EventBook events = new();

    private object Event(JsonElement args)
    {
        string name = Tools.RequiredText(args, "name", 20);
        // auto：定时任务自己开的会话发来的（hook 脚本认出来的），不算用户本人在，不记动静、不加亲密度
        bool auto = args.TryGetProperty("auto", out var autoValue) && autoValue.ValueKind == JsonValueKind.True;
        bool restDue = BondEvent(name, auto);
        events.Load(Path.Combine(Places.Root, "events.txt"));
        if (!events.TryGet(name, out var def)) return Tools.Text(new { played = false, reason = "events.txt 里没有这个事件：" + name });
        var now = DateTime.UtcNow;
        string? why =
            standby || hiding || hiddenForFullscreen || test ? "她收着或被全屏盖住，不叫她出场" :
            focus.Now == FocusTimer.Phase.Work && !def.Priority && !FocusEvent(name) ? "在专注，不打扰" :
            events.Cooling(def, now) ? "冷却中" :
            // 在说话、放语音、哄睡演出、被面板摆着姿势：谁都让位
            exiting || speech.IsSaying || voice != null || silent != null || coaxAutoReturn || (panelHold && body.Posing) ? "她在说话或被摆着姿势" :
            // 平常的事件只在她闲着时演；“优先”的可以打断闲时的嘀咕、小剧场、被碰的反应
            !def.Priority && (MutterBlocked || textIdleActive || behavior.Showing != null) ? "她在忙别的" : null;
        if (why != null) return Tools.Text(new { played = false, reason = why });
        // 连续工作满 2 小时：这一下换成提醒起来动一动
        if (restDue && PlayOwn("该休息")) { streak.Reminded(now); return Tools.Text(new { played = true, instead = "该休息" }); }
        events.Played(def, now);
        PlayEvent(events.Choose(def, random));
        return Tools.Text(new { played = true });
    }

    private async void PlayEvent(EventLine line)
    {
        YieldMutter(); // 收掉正在演的嘀咕/小剧场、摘临时道具，并让后面 10 秒别再冒新的
        int token = ++mutterToken;
        textIdleActive = true;
        if (line.Cue != null) body.Apply(line.Cue);
        var voiceSeconds = line.Text.Length > 0 ? Speak(line.Text) : null;
        if (line.Text.Length > 0) mutterSpeech.Say(new Line(line.Text, body.Apply));
        await Task.Delay(TimeSpan.FromSeconds(Math.Max(line.Seconds, (voiceSeconds ?? 0) + 0.4)));
        if (token != mutterToken || exiting) return; // 中途被说话打断了，收尾已经做过
        textIdleActive = false;
        mutterSpeech.Stop();
        body.Relax();
    }

    // ───────── 托盘控制台的互动（本机加，2026-11-06）─────────
    // 控制台（FishTray）点表情、动作、待机小动作时走这里：直接做出来，不经过“说话”，也不进工具列表（Agent 看不到）。
    // 做出来的样子一直保持，直到点“恢复”或她自己开口说话（SayLine 里放开）。
    private bool panelHold;
    private readonly HashSet<string> panelRaw = [];
    private readonly HashSet<string> poseOverlays = []; // 组合神情临时加的脸上表情，get_state 里先不算，免得托盘高亮跟着乱跳
    // 模型自带、但词表里没有对应的原版表情和动作：加载（脸上转圈的小图标）、前倾（满）、竖食指开；脱捕（循环的原版动作）。
    private static readonly HashSet<string> RawExpressions = ["加载", "前倾", "竖食指开"];
    private static readonly HashSet<string> RawMotions = ["脱捕"];

    private object Panel(JsonElement args)
    {
        string op = Tools.RequiredText(args, "op", 20);
        BondSeen(); // 点托盘也算用户在
        switch (op)
        {
            case "focus": // 番茄钟：开始 / 提前结束（本机加，2026-11-08）
                return FocusCommand(Tools.RequiredText(args, "action", 10));
            case "cue":
                var cue = Tools.RequiredText(args, "cue", 120);
                if (!Body.CueReadable(cue, 12)) throw new ArgumentException("读不懂这几个动作词：" + cue);
                Wake(); YieldMutter(); relaxAt = null; panelHold = true;
                body.Apply(cue);
                break;
            case "pose": // 组合神情：临时摆一下，约 3.5 秒自己恢复，不影响脸上表情开关（本机加 2026-11-06）
                var pose = Tools.RequiredText(args, "cue", 120);
                if (!Body.CueReadable(pose, 12)) throw new ArgumentException("读不懂这几个动作词：" + pose);
                Wake(); YieldMutter(); relaxAt = null;
                var kept = body.Overlays.ToArray();
                body.Apply(pose);
                foreach (var overlay in body.Overlays.Except(kept)) poseOverlays.Add(overlay);
                AutoRelaxPose();
                break;
            case "idle": // 待机小动作、被碰时的反应（Behavior.Moods）
                var mood = Tools.RequiredText(args, "name", 20);
                if (!Behavior.Moods.ContainsKey(mood)) throw new ArgumentException("没有这个小动作：" + mood);
                Wake(); YieldMutter(); relaxAt = null;
                behavior.Show(mood, DateTime.UtcNow, random.Next(2) == 0 ? 1 : -1, force: true);
                break;
            case "exp": // 原版表情开关
                var face = Tools.RequiredText(args, "name", 20);
                if (!RawExpressions.Contains(face)) throw new ArgumentException("没有这个原版表情：" + face);
                bool on = args.TryGetProperty("on", out var wanted) && wanted.ValueKind == JsonValueKind.True;
                Wake(); relaxAt = null; panelHold = true;
                if (on) panelRaw.Add(face); else panelRaw.Remove(face);
                Send($"exp {face} {(on ? 1 : 0)}");
                break;
            case "motion": // 原版动作
                var motion = Tools.RequiredText(args, "name", 20);
                if (!RawMotions.Contains(motion)) throw new ArgumentException("没有这个原版动作：" + motion);
                Wake(); YieldMutter();
                Send($"motion {motion}");
                break;
            case "reset": // 恢复平常：身体、原版表情、正在演的小剧场/嘀咕都收起来
                YieldMutter();
                foreach (var face2 in panelRaw) Send($"exp {face2} 0");
                panelRaw.Clear(); panelHold = false; relaxAt = null;
                body.Relax();
                break;
            default:
                throw new ArgumentException("没有这个操作：" + op);
        }
        return Tools.Text(new { ok = true, overlays = body.Overlays.Except(poseOverlays).ToArray(), raw = panelRaw.ToArray(), hand = body.HandUp });
    }

    // 组合神情临时摆一下的收尾：只收回这次临时加的脸上表情和会保持的动作，之前开着的脸上表情不动。
    private async void AutoRelaxPose()
    {
        await Task.Delay(3600);
        if (exiting) return;
        foreach (var overlay in poseOverlays) body.Apply("-" + overlay);
        poseOverlays.Clear();
        body.RelaxChannels();
    }

    private void Dress(string expression, bool worn)
    {
        settings.Outfit = worn ? settings.Outfit.Append(expression).Distinct().ToArray() : settings.Outfit.Where(item => item != expression).ToArray();
        Send($"exp {expression} {(worn ? 1 : 0)}");
        behavior.UpdateGlasses();
        settings.Save();
    }

    private string[] Wearing() => Outfit.Clothes.Concat(Outfit.Accessories).Where(item => settings.Outfit.Contains(item.Expression)).Select(item => item.Label).ToArray();

    private object State() => new
    {
        shape = settings.Shape, shapeLabel = Shape.Label, wearing = Wearing(), fps = settings.Fps, scale = settings.Scale, tex_scale = settings.TexScale, bubble_scale = settings.BubbleScale, bubble_seconds = settings.BubbleSeconds, volume = settings.Volume,
        voice_enabled = settings.VoiceEnabled,
        voice_pack = settings.VoicePack, voice_count = voicePack?.Count ?? 0,
        tts_enabled = settings.TtsEnabled, tts_url = settings.TtsUrl, tts_speed = settings.TtsSpeed, tts_autostart = settings.TtsAutostart, tts_dir = settings.TtsDir,
        hide_on_fullscreen = settings.HideOnFullscreen, hidden_for_fullscreen = hiddenForFullscreen && !standby, hidden = standby, start_hidden = settings.StartHidden, keep_on_screen = settings.KeepOnScreen, follow_mouse = settings.FollowMouse,
        check_updates = settings.CheckUpdates, coax_sleep = settings.CoaxSleep, coax_lunch = settings.CoaxLunch, birthday = settings.Birthday,
        update = updates.Latest is { } found ? new { current = found.Current, latest = found.Latest, behind = found.Behind, changes = found.Changes, guide = found.Guide } : null,
        idle_seconds = settings.IdleSeconds, position = new { Left, Top }, saying = speech.IsSaying, reaction = behavior.Showing, trouble,
        // 跟着音乐跳（本机加，2026-11-10）：开关和这会儿跳没跳。
        music_dance = settings.MusicDance, music_apps = settings.MusicApps, dancing = Dancing,
        context_chat = settings.ContextChat, // 桌面实况总开关（本机加，2026-11-12）：DSH 的插件读它决定要不要看她前台、要不要搭话
        clothes = Outfit.Clothes.Select(item => item.Label).ToArray(), accessories = Outfit.Accessories.Select(item => item.Label).ToArray(),
        overlays = body.Overlays.Except(poseOverlays).ToArray(), raw = panelRaw.ToArray(), hand = body.HandUp, // 托盘控制台用：脸上开着哪些表情、原版表情、手举着没有
        affection = AffectionState(), focus = FocusState(), memory = MemoryState(), // 亲密度、番茄钟、记忆条数（本机加，2026-11-08）
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo { public NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }

    private static nint NoWindowSizeLimit(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != 0x0024) return 0; // WM_GETMINMAXINFO
        var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        info.MaxSize = new NativePoint { X = 16384, Y = 16384 };
        info.MaxTrackSize = new NativePoint { X = 16384, Y = 16384 };
        Marshal.StructureToPtr(info, lParam, true);
        // 本机修（2026-11-07）：不能 handled = true。WPF 自己也处理这条消息，并从里面读出最大尺寸，排版时用它限制窗口内容的大小。
        // 拦掉以后，系统窗口确实放大了，但 WPF 排版仍按屏幕高度封顶，画面被 Stretch.Fill 压进较矮的区域，看起来就扁了
        // （小只 2 倍、150% 缩放、1600 高的屏幕上约扁 12%）。放行后，WPF 读到的是这里改过的上限。
        return 0;
    }
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    // 重新置顶（本机加，2026-11-11）：不改位置大小、不抢焦点，只把窗口放回置顶层的最上面。
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    private static readonly nint HwndTopmost = -1;
    private const uint SwpNoMove = 0x0001, SwpNoSize = 0x0002, SwpNoActivate = 0x0010, SwpNoOwnerZOrder = 0x0200;
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint process);
    // 摸头的手要更高帧（本机加，2026-11-11）：把计时精度提到 1ms，关闭时恢复
    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint period);
}
