using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using System.Windows.Threading;

namespace FishTray;

// 任务栏右下角的大肥鱼图标：左键开调控面板，右键菜单（出场/收起、打开面板、退出控制台）。
// 跟着桌宠走：桌宠一起来它就在，桌宠退场（Agent 断开约 1 分钟后）它也退。
public partial class App : System.Windows.Application
{
    private const string SingleName = "FishTray.Single.7c1f";
    private const string ShowSignalName = "FishTray.ShowPanel.7c1f";

    private Mutex? single;
    private EventWaitHandle? showSignal;
    private NotifyIcon? tray;
    private MainWindow? panel;
    private ToolStripMenuItem? toggleItem;
    private readonly DispatcherTimer poll = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime startedAt = DateTime.UtcNow;
    private DateTime? petGoneSince;
    private bool petSeen;

    internal static void Log(string text)
    {
        try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "..", "tray.log"), DateTime.Now.ToString("HH:mm:ss") + " " + text + "\n"); } catch { }
    }

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);
        Pet.Init();
        Taskbar.Register(); // 两个窗口在任务栏上各占一个按钮：先登记名字和图标（HKCU，重复写没关系）

        // 画图标预览：FishTray.exe --preview 目标.png
        var args = e.Args;
        int preview = Array.IndexOf(args, "--preview");
        if (preview >= 0 && preview + 1 < args.Length)
        {
            using (var big = Whale.Draw(256)) big.Save(args[preview + 1]);
            using (var small = Whale.Draw(24)) small.Save(Path.ChangeExtension(args[preview + 1], ".24.png"));
            Shutdown();
            return;
        }

        // 截图预览：FishTray.exe --shot 目标.png [--more]（不进托盘、不占“只留一个”，渲染面板成图后退出，给开发自检用）
        int shot = Array.IndexOf(args, "--shot");
        if (shot >= 0 && shot + 1 < args.Length)
        {
            var target = args[shot + 1];
            bool more = Array.IndexOf(args, "--more") >= 0, open = Array.IndexOf(args, "--open") >= 0, scroll = Array.IndexOf(args, "--scroll") >= 0;
            int find = Array.IndexOf(args, "--search");
            string search = find >= 0 && find + 1 < args.Length ? args[find + 1] : "";
            panel = new MainWindow();
            panel.Open();
            var wait = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            wait.Tick += (_, _) =>
            {
                wait.Stop();
                try { panel.Snapshot(target, more, open, scroll, search); } catch (Exception error) { File.WriteAllText(target + ".err.txt", error.ToString()); }
                Shutdown();
            };
            wait.Start();
            return;
        }

        // 截图预览：FishTray.exe --context-shot 目标.png [--scroll 像素]（「识别当前窗口并闲聊」子窗口，开发自检用）
        int shotCtx = Array.IndexOf(args, "--context-shot");
        if (shotCtx >= 0 && shotCtx + 1 < args.Length)
        {
            var target = args[shotCtx + 1];
            int scrollAt = Array.IndexOf(args, "--scroll");
            double scroll = scrollAt >= 0 && scrollAt + 1 < args.Length && double.TryParse(args[scrollAt + 1], out var value) ? value : 0;
            var window = new ContextChatWindow();
            window.Open();
            if (Array.IndexOf(args, "--demo-try") >= 0) window.StartTryNote(); // 只演示倒数条，不真让她说话
            // --wait 毫秒：等久一点再截（图标是后台找的，等它填完再看）
            int waitAt = Array.IndexOf(args, "--wait");
            var waitMs = waitAt >= 0 && waitAt + 1 < args.Length && int.TryParse(args[waitAt + 1], out var waited) ? waited : 2500;
            var waitCtx = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(waitMs) };
            waitCtx.Tick += (_, _) =>
            {
                waitCtx.Stop();
                try
                {
                    window.Snapshot(target, scroll);
                    File.WriteAllText(target + ".measure.txt", window.Measure());
                }
                catch (Exception error) { File.WriteAllText(target + ".err.txt", error.ToString()); }
                Shutdown();
            };
            waitCtx.Start();
            return;
        }

        bool wantPanel = Array.IndexOf(args, "--show") >= 0;

        // 自检：FishTray.exe --selftest 日志.txt（点各个控件，核对桌宠状态，写日志后退出）
        int self = Array.IndexOf(args, "--selftest");
        if (self >= 0 && self + 1 < args.Length)
        {
            var logPath = args[self + 1];
            panel = new MainWindow();
            panel.Open();
            _ = Dispatcher.InvokeAsync(async () => { await System.Threading.Tasks.Task.Delay(1500); await panel.SelfTest(logPath); Shutdown(); });
            return;
        }
        single = new Mutex(true, SingleName, out bool first);
        if (!first)
        {
            // 已经有一个在跑：要开面板就通知它，然后自己退出
            if (wantPanel) { try { EventWaitHandle.OpenExisting(ShowSignalName).Set(); } catch { } }
            Shutdown();
            return;
        }
        showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
        var waiter = new Thread(() =>
        {
            while (showSignal.WaitOne()) Dispatcher.BeginInvoke(new Action(OpenPanel));
        }) { IsBackground = true };
        waiter.Start();

        panel = new MainWindow();
        BuildTray();
        poll.Tick += async (_, _) => { try { await Tick(); } catch (Exception error) { Log("ERR " + error); } };
        poll.Start();

        // 自己打开（start.bat 之类）而桌宠没开：把桌宠也带起来（她默认收着）
        if (wantPanel) { Pet.StartPet(); OpenPanel(); }
    }

    private void BuildTray()
    {
        toggleItem = new ToolStripMenuItem("出场");
        toggleItem.Click += async (_, _) =>
        {
            bool? hidden = panel?.Hidden;
            if (hidden is null) return;
            await Pet.Call(hidden == true ? "show" : "hide");
            await Tick();
        };
        var open = new ToolStripMenuItem("打开面板");
        open.Click += (_, _) => OpenPanel();
        var exit = new ToolStripMenuItem("退出控制台");
        exit.Click += (_, _) => Quit();

        var menu = new ContextMenuStrip { ShowImageMargin = false, Renderer = new DarkRenderer(), Font = new Font("Microsoft YaHei UI", 9.5f), Padding = new Padding(4) };
        menu.Items.AddRange([toggleItem, open, new ToolStripSeparator(), exit]);
        menu.Opening += (_, _) =>
        {
            bool? hidden = panel?.Hidden;
            toggleItem.Enabled = hidden is not null;
            toggleItem.Text = hidden == false ? "收起" : "出场";
        };

        tray = new NotifyIcon { Icon = Whale.Icon(SystemInformation.SmallIconSize.Width), Text = "大肥鱼", Visible = false, ContextMenuStrip = menu };
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) OpenPanel(); };
    }

    private void OpenPanel()
    {
        if (panel is null) return;
        if (panel.IsVisible)
        {
            if (panel.WindowState == System.Windows.WindowState.Minimized) panel.WindowState = System.Windows.WindowState.Normal;
            panel.Activate();
            return;
        }
        panel.Open();
    }

    private async System.Threading.Tasks.Task Tick()
    {
        if (panel is null) return;
        // 用接口判断她在不在（本机修，2026-11-11）：Process.GetProcessesByName("FishPet") 在这台机器上一直返回空，
        // 会误判“桌宠没运行”，40 秒后把控制台和桌宠一起关掉，托盘图标就消失了。
        bool running = panel.Hidden is not null;
        if (running) { petSeen = true; petGoneSince = null; }
        else
        {
            petGoneSince ??= DateTime.UtcNow;
            // 桌宠退场了（或者一直没起来）：控制台也退，不留个空图标
            if ((petSeen && DateTime.UtcNow - petGoneSince > TimeSpan.FromSeconds(2)) || (!petSeen && DateTime.UtcNow - startedAt > TimeSpan.FromSeconds(40)))
            { Quit(); return; }
        }
        await panel.Refresh();
        if (tray is not null)
        {
            bool? hidden = panel.Hidden;
            tray.Text = hidden is null ? "大肥鱼（连接中）" : hidden == true ? "大肥鱼 · 收起着" : "大肥鱼 · 在桌面上";
            // 她收着的时候托盘图标也不露面；她出场（或面板还开着）才出现，默认落在“显示隐藏的图标”里
            tray.Visible = hidden == false || panel.IsVisible;
        }
    }

    private void Quit()
    {
        poll.Stop();
        if (tray is not null) { tray.Visible = false; tray.Dispose(); tray = null; }
        panel?.Close();
        Shutdown();
    }

    // 暗色右键菜单
    private sealed class DarkRenderer : ToolStripProfessionalRenderer
    {
        public DarkRenderer() : base(new DarkColors()) { RoundedEdges = false; }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Color.FromArgb(232, 240, 255) : Color.FromArgb(110, 125, 165);
            base.OnRenderItemText(e);
        }
    }

    private sealed class DarkColors : ProfessionalColorTable
    {
        private static readonly Color Back = Color.FromArgb(10, 20, 56), Hover = Color.FromArgb(38, 102, 182), Line = Color.FromArgb(42, 63, 122);
        public override Color ToolStripDropDownBackground => Back;
        public override Color MenuBorder => Line;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color ImageMarginGradientBegin => Back;
        public override Color ImageMarginGradientMiddle => Back;
        public override Color ImageMarginGradientEnd => Back;
        public override Color SeparatorDark => Line;
        public override Color SeparatorLight => Back;
    }
}
